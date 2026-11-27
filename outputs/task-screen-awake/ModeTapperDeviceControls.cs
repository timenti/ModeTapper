using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
#if !LUMA_CAMERA_WORKER
using System.Windows.Forms;
#else
[assembly: System.Reflection.AssemblyTitle("Управление камерами ModeTapper")]
[assembly: System.Reflection.AssemblyProduct("ModeTapper")]
[assembly: System.Reflection.AssemblyCompany("TIMENTI")]
[assembly: System.Reflection.AssemblyVersion("2.0.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.2.0")]
#endif

internal enum CameraState { Unknown, Available, Blocked, Mixed, Missing }

internal static class CameraDevices {
    const uint DisabledProblem=22, Started=8, DisableWithoutPrompt=4, DisablePersist=8;
    static readonly Guid CameraClass=new Guid("ca3e7ab9-b4c3-4ae6-8251-579ef933890f");
    [StructLayout(LayoutKind.Sequential)]struct Node {
        public int Size;
        public Guid Class;
        public uint Device;
        public UIntPtr Reserved;
    }
    sealed class Camera {
        internal uint Node,Status,Problem;
        internal string Id;
        internal bool Blocked {get{return Problem==DisabledProblem&&(Status&Started)==0;}}
        internal bool Running {get{return Problem==0&&(Status&Started)!=0;}}
    }
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid kind,string enumerator,IntPtr parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr info,uint index,ref Node node);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool SetupDiGetDeviceInstanceId(IntPtr info,ref Node node,StringBuilder id,int length,IntPtr required);
    [DllImport("setupapi.dll")]static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);
    [DllImport("cfgmgr32.dll")]static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint device,uint flags);
    [DllImport("cfgmgr32.dll")]static extern uint CM_Disable_DevNode(uint device,uint flags);
    [DllImport("cfgmgr32.dll")]static extern uint CM_Enable_DevNode(uint device,uint flags);

    internal static string ControlDirectory {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Luma Camera");}}
    static string Journal(string directory){return Path.Combine(directory,"camera-disabled.json");}
    static string Result(string operation){return Path.Combine(ControlDirectory,operation+"-result.txt");}
    static List<Camera> Read(){
        var kind=CameraClass;
        IntPtr list=SetupDiGetClassDevs(ref kind,null,IntPtr.Zero,2);
        if(list==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
        var result=new List<Camera>();
        try{
            for(uint index=0;;index++){
                var node=new Node{Size=Marshal.SizeOf(typeof(Node))};
                if(!SetupDiEnumDeviceInfo(list,index,ref node)){
                    int error=Marshal.GetLastWin32Error();
                    if(error==259)return result;
                    throw new Win32Exception(error);
                }
                var id=new StringBuilder(512);
                if(!SetupDiGetDeviceInstanceId(list,ref node,id,id.Capacity,IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                uint status,problem;
                if(CM_Get_DevNode_Status(out status,out problem,node.Device,0)!=0)
                    throw new IOException("Windows не вернула состояние камеры.");
                result.Add(new Camera{Node=node.Device,Id=id.ToString(),Status=status,Problem=problem});
            }
        }finally{SetupDiDestroyDeviceInfoList(list);}
    }

    internal static CameraState State(){
        try{
            var cameras=Read();
            if(cameras.Count==0)return CameraState.Missing;
            if(cameras.TrueForAll(c=>c.Blocked))return CameraState.Blocked;
            if(cameras.TrueForAll(c=>c.Running))return CameraState.Available;
            return CameraState.Mixed;
        }catch(Win32Exception){return CameraState.Unknown;}
        catch(IOException){return CameraState.Unknown;}
    }

    internal static bool HasLock(){return File.Exists(Journal(ControlDirectory));}
    static void WriteAtomic(string path,string body){
        string temp=path+".tmp";
        using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){
            var bytes=Encoding.UTF8.GetBytes(body);
            file.Write(bytes,0,bytes.Length);file.Flush(true);
        }
        if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    }
    static void Save(string path,HashSet<string> ids){
        if(ids.Count==0){if(File.Exists(path))File.Delete(path);return;}
        WriteAtomic(path,new JavaScriptSerializer().Serialize(new List<string>(ids)));
    }
    static void Set(Camera camera,bool blocked){
        uint result=blocked?CM_Disable_DevNode(camera.Node,DisablePersist|DisableWithoutPrompt):CM_Enable_DevNode(camera.Node,0);
        if(result!=0)throw new IOException("Windows отклонила изменение камеры ("+result+").");
    }
    static bool Confirm(List<Camera> changed,bool blocked){
        for(int attempt=0;attempt<12;attempt++){
            var actual=Read();
            bool confirmed=changed.TrueForAll(expected=>actual.Exists(current=>
                String.Equals(current.Id,expected.Id,StringComparison.OrdinalIgnoreCase)&&
                (blocked?current.Blocked:current.Running)));
            if(confirmed)return true;
            Thread.Sleep(100);
        }
        return false;
    }

    internal static int RunWorker(string operation){
        if(operation!="block"&&operation!="enable")return 2;
        using(var identity=WindowsIdentity.GetCurrent())
            if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return 5;
        using(var gate=new Mutex(false,@"Local\LumaCameraDevices")){
            bool acquired=false;
            try{
                try{acquired=gate.WaitOne(3000);}catch(AbandonedMutexException){acquired=true;}
                if(!acquired)return 3;
                int result;
                try{PrepareControlDirectory();}catch(Exception){return 4;}
                try{result=Change(operation=="block",ControlDirectory);}catch(Exception){result=4;}
                WriteAtomic(Result(operation),Guid.NewGuid().ToString("N")+":"+result);
                return result;
            }catch(IOException){return 4;}
            finally{if(acquired)gate.ReleaseMutex();}
        }
    }

    static void PrepareControlDirectory(){
        string path=ControlDirectory;
        var security=new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");
        if(!Directory.Exists(path))Directory.CreateDirectory(path,security);
        var directory=new DirectoryInfo(path);
        if((directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Некорректная папка камер.");
        string owner=directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier)).Value;
        if(owner!="S-1-5-32-544"&&owner!="S-1-5-18")throw new IOException("Некорректный владелец папки камер.");
        directory.SetAccessControl(security);
        foreach(var item in directory.EnumerateFileSystemInfos())
            if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Некорректный файл в папке камер.");
    }

    static int Change(bool block,string directory){
        var cameras=Read();
        if(cameras.Count==0)return 3;
        string path=Journal(directory);
        var saved=File.Exists(path)?new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(path)):new string[0];
        if(saved==null)throw new InvalidDataException("Повреждён список камер ModeTapper.");
        var original=new HashSet<string>(saved,StringComparer.OrdinalIgnoreCase);
        var recorded=new HashSet<string>(original,StringComparer.OrdinalIgnoreCase);
        var targets=cameras.FindAll(camera=>block?!camera.Blocked:
            camera.Blocked&&(original.Count==0||original.Contains(camera.Id)));
        if(block){
            foreach(var target in targets)recorded.Add(target.Id);
            Save(path,recorded);
        }
        var changed=new List<Camera>();
        try{
            foreach(var target in targets){Set(target,block);changed.Add(target);}
            if(!Confirm(changed,block)||block&&State()!=CameraState.Blocked)
                throw new IOException("Изменение камер не подтверждено Windows.");
            if(!block){
                foreach(var camera in cameras)recorded.Remove(camera.Id);
                Save(path,recorded);
            }
            return 0;
        }catch{
            bool recovered=true;
            changed.Reverse();
            foreach(var current in changed){
                try{Set(current,!block);}catch(IOException){recovered=false;}
            }
            if(recovered&&Confirm(changed,!block))Save(path,original);
            throw;
        }
    }

#if !LUMA_CAMERA_WORKER
    static string ReadResult(string path){
        try{return File.ReadAllText(path);}
        catch(IOException){return null;}
    }
    internal static int Request(bool block){
        string operation=block?"block":"enable";
        string worker=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ModeTapper-camera-control.exe");
        if(File.Exists(worker)){
            var elevation=new ProcessStartInfo(worker,operation){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
            try{
                using(var process=Process.Start(elevation)){
                    if(process==null)return 7;
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }catch(Win32Exception ex){if(ex.NativeErrorCode==1223)return 1223;throw;}
        }
        string resultPath=Result(operation);
        string previous=ReadResult(resultPath);
        var launch=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"schtasks.exe"),
            "/Run /TN \\Luma-Camera-"+operation){UseShellExecute=false,CreateNoWindow=true};
        using(var task=Process.Start(launch)){
            if(task==null||!task.WaitForExit(5000)||task.ExitCode!=0)return 6;
        }
        for(int attempt=0;attempt<90;attempt++){
            string current=ReadResult(resultPath);
            int number;
            if(current!=null&&current!=previous&&current.Length>=34&&current[32]==':'&&
               Int32.TryParse(current.Substring(33),out number))return number;
            Thread.Sleep(100);
        }
        return 7;
    }
    internal static void LaunchCamera(){
        using(var process=Process.Start(new ProcessStartInfo{
            FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),
            Arguments=@"shell:AppsFolder\Microsoft.WindowsCamera_8wekyb3d8bbwe!App",UseShellExecute=true
        })){}
    }
#endif
}

#if LUMA_CAMERA_WORKER
internal static class CameraWorker {
    static int Main(string[] args){
        return args.Length==1?CameraDevices.RunWorker(args[0]):2;
    }
}
#else
internal static class RotationLock {
    const uint Disabled=1, Suppressed=2, RemoteSession=4, MultipleDisplays=8, NoSensor=16, Unsupported=32, Docked=64;
    [DllImport("user32.dll")]static extern bool GetAutoRotationState(out uint state);
    // Windows applies this preference even while its Quick Settings tile is unavailable.
    [DllImport("win32u.dll",EntryPoint="NtUserSetAutoRotation")]
    static extern bool SetAutoRotation([MarshalAs(UnmanagedType.Bool)]bool enabled);
    internal static bool? Current(){
        uint state;
        return GetAutoRotationState(out state)&&(state&(RemoteSession|NoSensor|Unsupported))==0?(bool?)((state&Disabled)!=0):null;
    }
    internal static bool Toggle(){
        var locked=Current();
        if(!locked.HasValue)return false;
        try{return SetAutoRotation(locked.Value)&&Current()==!locked.Value;}
        catch(EntryPointNotFoundException){return false;}
    }
    internal static bool AllowsSensorCorrection(){
        uint state;
        return GetAutoRotationState(out state)&&
            (state&(Disabled|Suppressed|RemoteSession|MultipleDisplays|NoSensor|Unsupported|Docked))==0;
    }
    internal static int ReadSensor(){
        try{
            var type=Type.GetType("Windows.Devices.Sensors.SimpleOrientationSensor, Windows, ContentType=WindowsRuntime",true);
            var sensor=type.GetMethod("GetDefault",Type.EmptyTypes).Invoke(null,null);
            if(sensor==null)return -1;
            int sample=Convert.ToInt32(type.GetMethod("GetCurrentOrientation",Type.EmptyTypes).Invoke(sensor,null));
            // Face-up and face-down do not identify an upright direction of the screen.
            return sample>=0&&sample<4?sample:-1;
        }catch(Exception){return -1;}
    }
}

internal sealed class CameraStatusToast : Form {
    [DllImport("user32.dll")]static extern uint GetDpiForSystem();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=16};
    readonly Stopwatch time=new Stopwatch();
    string label;
    Color signal;
    int restTop,travel;
    internal CameraStatusToast(){
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;
        BackColor=Color.FromArgb(28,28,28);DoubleBuffered=true;
        timer.Tick+=(sender,args)=>DrawFrame();
    }
    protected override bool ShowWithoutActivation {get{return true;}}
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x080800a0;return p;}}

    internal void Display(CameraState state){
        if(state!=CameraState.Blocked&&state!=CameraState.Available&&state!=CameraState.Mixed)return;
        label=state==CameraState.Blocked?"Камеры выключены":state==CameraState.Available?"Камеры включены":"Камеры включены частично";
        signal=state==CameraState.Blocked?Color.FromArgb(235,125,125):state==CameraState.Available?Color.FromArgb(110,211,157):Color.FromArgb(245,197,87);
        var area=Screen.PrimaryScreen.Bounds;
        float scale=Math.Max(96,GetDpiForSystem())/96f;
        ClientSize=new Size((int)(214*scale),(int)(37*scale));
        var old=Region;
        using(var shape=TrayApp.RoundRect(new RectangleF(0,0,Width,Height),9*scale))Region=new Region(shape);
        if(old!=null)old.Dispose();
        restTop=area.Top+(int)(10*scale);travel=(int)(8*scale);
        Left=area.Left+(area.Width-Width)/2;
        time.Restart();DrawFrame();Invalidate();if(!Visible)Show();timer.Start();
    }
    void DrawFrame(){
        double elapsed=time.Elapsed.TotalSeconds;
        if(elapsed>=1.65){timer.Stop();Hide();return;}
        double amount=LumaFlyout.AnimationsEnabled()?Math.Min(1,Math.Min(elapsed/.14,(1.65-elapsed)/.20)):1;
        Top=restTop-(int)(travel*(1-amount)*(1-amount));Opacity=amount;
    }
    protected override void OnPaint(PaintEventArgs e){
        float scale=Height/37f;
        using(var font=new Font("Segoe UI",13*scale,FontStyle.Regular,GraphicsUnit.Pixel)){
            var flags=TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine;
            int width=TextRenderer.MeasureText(e.Graphics,label,font,Size.Empty,flags).Width;
            int x=(Width-width-(int)(17*scale))/2;
            e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using(var brush=new SolidBrush(signal))e.Graphics.FillEllipse(brush,x,(Height-7*scale)/2,7*scale,7*scale);
            TextRenderer.DrawText(e.Graphics,label,font,new Rectangle(x+(int)(17*scale),0,width+4,Height),Color.White,flags);
        }
    }
    protected override void Dispose(bool disposing){if(disposing)timer.Dispose();base.Dispose(disposing);}
}
#endif
