using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class AsusSideButton : IDisposable {
    const uint CommandCenterButton=95;
    readonly Control dispatcher;
    readonly LumaFlyout panel;
    readonly Action preparePanel,showOverview;
    readonly AsusCompanionGuard companionGuard;
    readonly ManagementEventWatcher watcher;
    readonly Timer settle=new Timer{Interval=40};
    readonly Timer overviewDelay=new Timer{Interval=35};
    readonly HoldKeyCallback holdKeyCallback;
    IntPtr holdKeyHook;
    bool swallowingHold;
    readonly Stopwatch clock=Stopwatch.StartNew();
    long lastPress=-1000;
    bool openAfterPress,disposed;
    int hiddenWindows;

    internal AsusSideButton(Control owner,LumaFlyout flyout,Action prepare,Action overview){
        dispatcher=owner;panel=flyout;preparePanel=prepare;showOverview=overview;
        settle.Tick+=FinishPress;
        watcher=new ManagementEventWatcher(@"root\wmi","SELECT * FROM AsusAtkWmiEvent");
        watcher.EventArrived+=OnAsusEvent;
        try{watcher.Start();}
        catch{watcher.Dispose();settle.Dispose();overviewDelay.Dispose();throw;}
        try{
            companionGuard=AsusCompanionGuard.Start();
            Safety.Audit("asus.command-center-oem",null,companionGuard!=null?"blocked":"unsupported","user-session launcher");
        }catch(Exception ex){try{Safety.Audit("asus.command-center-oem",null,"failed",ex.Message);}catch{}}
        holdKeyCallback=OnHoldKey;
        holdKeyHook=SetWindowsHookEx(13,holdKeyCallback,GetModuleHandle(null),0);
        overviewDelay.Tick+=(sender,args)=>{
            overviewDelay.Stop();
            if(disposed||Safety.Safe)return;
            showOverview();
            try{Safety.Audit("asus.command-center-hold",null,"overview","hardware shortcut");}catch{}
        };
    }

    internal bool HoldReady {get{return holdKeyHook!=IntPtr.Zero;}}
    internal bool StoreLaunchBlocked {get{return companionGuard!=null;}}
    delegate IntPtr HoldKeyCallback(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")]static extern IntPtr SetWindowsHookEx(int kind,HoldKeyCallback callback,IntPtr module,uint thread);
    [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string module);
    IntPtr OnHoldKey(int code,IntPtr message,IntPtr data){
        // This model emits the dedicated Copilot chord on hold. Pass all other keys through.
        if(code>=0&&data!=IntPtr.Zero&&Marshal.ReadInt32(data)==0x86&&Marshal.ReadInt32(data,4)==0x6e){
            int flags=Marshal.ReadInt32(data,8);
            if((flags&0x10)==0){
                if((flags&0x80)!=0&&swallowingHold){
                    swallowingHold=false;
                    if(!Safety.Safe){overviewDelay.Stop();overviewDelay.Start();}
                    return new IntPtr(1);
                }
                if((flags&0x80)==0&&!Safety.Safe&&KeyDown(0x5b)&&KeyDown(0xa0)&&!KeyDown(0x11)&&!KeyDown(0x12)){
                    swallowingHold=true;
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero,code,message,data);
    }
    static bool KeyDown(int key){return (GetAsyncKeyState(key)&0x8000)!=0;}

    void OnAsusEvent(object sender,EventArrivedEventArgs e){
        if(Convert.ToUInt32(e.NewEvent["EventID"])!=CommandCenterButton)return;
        try{dispatcher.BeginInvoke(new Action(BeginPress));}catch(InvalidOperationException){}
    }

    void BeginPress(){
        if(disposed||Safety.Safe||clock.ElapsedMilliseconds-lastPress<400)return;
        lastPress=clock.ElapsedMilliseconds;
        openAfterPress=!panel.EdgeIsOpen;
        if(!openAfterPress)panel.Dismiss();
        hiddenWindows=0;
        if(companionGuard!=null||!ScreenXpertPopup.IsRunning()){CompletePress();return;}
        settle.Start();
    }

    void FinishPress(object sender,EventArgs e){
        if(Safety.Safe){settle.Stop();return;}
        hiddenWindows+=ScreenXpertPopup.Hide();
        if(clock.ElapsedMilliseconds-lastPress<280)return;
        settle.Stop();
        CompletePress();
    }

    void CompletePress(){
        if(openAfterPress&&!panel.EdgeIsOpen){preparePanel();panel.OpenAtScreenEdge();}
        try{Safety.Audit("asus.command-center",null,openAfterPress?"open":"close","ScreenXpert popups hidden: "+hiddenWindows);}catch{}
    }

    public void Dispose(){
        if(disposed)return;
        disposed=true;
        if(holdKeyHook!=IntPtr.Zero){UnhookWindowsHookEx(holdKeyHook);holdKeyHook=IntPtr.Zero;}
        overviewDelay.Stop();overviewDelay.Dispose();
        settle.Stop();settle.Dispose();
        watcher.EventArrived-=OnAsusEvent;
        watcher.Dispose();
        if(companionGuard!=null)companionGuard.Dispose();
    }
}

// Keep the ASUS system service and driver. The verified user-session launcher exits
// when its own single-instance event already exists. No Store window is intercepted.
internal sealed class AsusCompanionGuard : IDisposable {
    const string AgentName="AsusOptimizationStartupTask";
    const string AgentHash="D9E21A747FAB60C57CC11155ACD59F18BA38D6A5B037F594618E102FA662EC0F";
    const string InstanceEvent="ATK Package AsMonStartupTask Application Event";
    readonly string path;
    readonly int session;
    System.Threading.EventWaitHandle instance;
    bool restore;

    AsusCompanionGuard(string agentPath,int ownSession){
        path=agentPath;session=ownSession;
        if(!System.Threading.EventWaitHandle.TryOpenExisting(InstanceEvent,out instance))
            instance=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.ManualReset,InstanceEvent);
    }

    internal static AsusCompanionGuard Start(){
        const string serviceKey=@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\ASUSOptimization";
        string service=Convert.ToString(Registry.GetValue(serviceKey,"ImagePath",null)).Trim('"');
        if(String.IsNullOrEmpty(service))return null;
        string folder=Path.GetDirectoryName(service);
        string driverStore=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\DriverStore\FileRepository\asussci2.inf_amd64_");
        if(!service.StartsWith(driverStore,StringComparison.OrdinalIgnoreCase)||
            !String.Equals(Path.GetFileName(service),"AsusOptimization.exe",StringComparison.OrdinalIgnoreCase))return null;
        string path=Path.Combine(folder,AgentName+".exe");
        if(!File.Exists(path))return null;
        using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))
            if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")!=AgentHash)return null;
        int session;
        using(var self=Process.GetCurrentProcess())session=self.SessionId;
        var guard=new AsusCompanionGuard(path,session);
        try{
            foreach(var process in Process.GetProcessesByName(AgentName))using(process){
                if(process.SessionId!=session)continue;
                if(!String.Equals(process.MainModule.FileName,path,StringComparison.OrdinalIgnoreCase)||!IsCurrentUser(process.Id))
                    throw new InvalidOperationException("Обнаружен другой пользовательский процесс ASUS");
                process.Kill();
                guard.restore=true;
                if(!process.WaitForExit(1700))throw new InvalidOperationException("Пользовательский процесс ASUS ещё работает");
            }
            guard.restore=true;
            return guard;
        }catch{guard.Dispose();throw;}
    }
    static bool IsCurrentUser(int processId){
        using(var identity=WindowsIdentity.GetCurrent())
        using(var process=new ManagementObject("Win32_Process.Handle='"+processId+"'"))
        using(var owner=process.InvokeMethod("GetOwnerSid",null,null)){
            return Convert.ToUInt32(owner["ReturnValue"])==0&&String.Equals(Convert.ToString(owner["Sid"]),identity.User.Value,StringComparison.Ordinal);
        }
    }
    public void Dispose(){
        if(instance==null)return;
        instance.Dispose();instance=null;
        if(!restore)return;
        bool running=false;
        foreach(var process in Process.GetProcessesByName(AgentName))using(process){if(process.SessionId==session)running=true;}
        if(!running)using(var started=Process.Start(new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(path)})){}
    }
}

internal static class ScreenXpertPopup {
    delegate bool WindowVisitor(IntPtr window,IntPtr context);
    [DllImport("user32.dll")]static extern bool EnumWindows(WindowVisitor visitor,IntPtr context);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]static extern bool ShowWindowAsync(IntPtr window,int command);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder text,int capacity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder text,int capacity);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);

    internal static bool IsRunning(){
        var processes=Process.GetProcessesByName("AsusScreenXpertReunion");
        foreach(var process in processes)process.Dispose();
        return processes.Length!=0;
    }

    internal static int Hide(){
        int count=0;
        var text=new StringBuilder(128);
        EnumWindows((window,context)=>{
            if(!IsWindowVisible(window)||GetWindowText(window,text,text.Capacity)==0)return true;
            string title=text.ToString();
            if(title!="AsusScreenXpertReunionQuickAccessPanel"&&title!="QuickAccessExpand")return true;
            GetClassName(window,text,text.Capacity);
            if(!text.ToString().StartsWith("HwndWrapper[AsusScreenXpertReunion.exe;",StringComparison.Ordinal))return true;
            uint pid;
            GetWindowThreadProcessId(window,out pid);
            try{
                using(var process=Process.GetProcessById((int)pid)){
                    if(process.ProcessName=="AsusScreenXpertReunion"&&ShowWindowAsync(window,0))count++;
                }
            }catch(ArgumentException){}
            return true;
        },IntPtr.Zero);
        return count;
    }
}
