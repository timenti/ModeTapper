using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Only paired settings in the active Windows plan are changed. Temporary overrides
// use the existing safety journal so they can be released after a restart.
internal static class PlanSettings {
    const string LidBaseline="plan.lid-default.", LidHold="plan.lid-keep.", IdleHold="plan.idle-keep.", Saver="plan.energy-saver.", BoostHold="plan.boost-off.";
    const string SaverAC="max-power.ac", SaverDC="max-power.dc";
    const string SaverOverride="plan.energy-saver-switch";
    static readonly Guid Buttons=new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
    static readonly Guid Lid=new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");
    static readonly Guid Sleep=new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    static readonly Guid IdleTimeout=new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    static readonly Guid Energy=new Guid("de830923-a562-41af-a086-e3a2c6bad2da");
    static readonly Guid Threshold=new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");
    static readonly Guid Processor=new Guid("54533251-82be-4824-96c1-47b60b740d00");
    static readonly Guid BoostMode=new Guid("be337238-0d82-4146-a960-4f3749d470c7");
    [DllImport("powrprof.dll")]static extern uint PowerGetActiveScheme(IntPtr root,out IntPtr scheme);
    [DllImport("powrprof.dll")]static extern uint PowerSetActiveScheme(IntPtr root,ref Guid scheme);
    [DllImport("powrprof.dll")]static extern uint PowerReadACValueIndex(IntPtr root,ref Guid scheme,ref Guid group,ref Guid setting,out uint value);
    [DllImport("powrprof.dll")]static extern uint PowerReadDCValueIndex(IntPtr root,ref Guid scheme,ref Guid group,ref Guid setting,out uint value);
    [DllImport("powrprof.dll")]static extern uint PowerWriteACValueIndex(IntPtr root,ref Guid scheme,ref Guid group,ref Guid setting,uint value);
    [DllImport("powrprof.dll")]static extern uint PowerWriteDCValueIndex(IntPtr root,ref Guid scheme,ref Guid group,ref Guid setting,uint value);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
    [StructLayout(LayoutKind.Sequential)]internal struct PowerStatus {
        public byte ACLineStatus,BatteryFlag,BatteryPercent,SaverEnabled;
        public uint BatteryLifeTime,BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")]static extern bool GetSystemPowerStatus(out PowerStatus status);
    internal static PowerStatus Status(){
        PowerStatus status;
        return GetSystemPowerStatus(out status)?status:new PowerStatus{ACLineStatus=255,BatteryFlag=255,BatteryPercent=255};
    }
    internal static bool EnergySaverOn {get{return WindowsEnergySaver.Active??(Status().SaverEnabled==1);}}

    internal static Guid Active(){
        IntPtr address;PowerModes.Check(PowerGetActiveScheme(IntPtr.Zero,out address));
        try{return (Guid)Marshal.PtrToStructure(address,typeof(Guid));}finally{LocalFree(address);}
    }
    static bool GetPlanSetting(string key,out Guid scheme,out Guid group,out Guid setting){
        setting=Lid;group=Buttons;scheme=Guid.Empty;
        string prefix=key.StartsWith(LidHold,StringComparison.Ordinal)?LidHold:key.StartsWith(LidBaseline,StringComparison.Ordinal)?LidBaseline:null;
        if(prefix==null&&key.StartsWith(IdleHold,StringComparison.Ordinal)){prefix=IdleHold;group=Sleep;setting=IdleTimeout;}
        if(prefix==null&&key.StartsWith(Saver,StringComparison.Ordinal)){prefix=Saver;group=Energy;setting=Threshold;}
        if(prefix==null&&key.StartsWith(BoostHold,StringComparison.Ordinal)){prefix=BoostHold;group=Processor;setting=BoostMode;}
        return prefix!=null&&Guid.TryParse(key.Substring(prefix.Length),out scheme);
    }
    internal static bool IsManaged(string key){return key==SaverAC||key==SaverDC||key.StartsWith("plan.",StringComparison.Ordinal);}
    internal static string Read(string key){
        if(key==SaverAC||key==SaverDC)return PowerModes.ReadSide(key==SaverAC);
        if(key==SaverOverride)return WindowsEnergySaver.Read();
        Guid scheme,group,setting;
        if(!GetPlanSetting(key,out scheme,out group,out setting))throw new InvalidOperationException("Unknown power setting");
        uint ac,dc;PowerModes.Check(PowerReadACValueIndex(IntPtr.Zero,ref scheme,ref group,ref setting,out ac));
        PowerModes.Check(PowerReadDCValueIndex(IntPtr.Zero,ref scheme,ref group,ref setting,out dc));
        return ac.ToString(CultureInfo.InvariantCulture)+","+dc.ToString(CultureInfo.InvariantCulture);
    }
    internal static void Write(string key,string value){
        if(key==SaverAC||key==SaverDC){PowerModes.WriteSide(key==SaverAC,value);return;}
        if(key==SaverOverride){WindowsEnergySaver.Write(value);return;}
        Guid scheme,group,setting;
        if(!GetPlanSetting(key,out scheme,out group,out setting))throw new InvalidOperationException("Unknown power setting");
        var parts=value.Split(',');uint ac=UInt32.Parse(parts[0],CultureInfo.InvariantCulture),dc=UInt32.Parse(parts[1],CultureInfo.InvariantCulture);
        PowerModes.Check(PowerWriteACValueIndex(IntPtr.Zero,ref scheme,ref group,ref setting,ac));
        PowerModes.Check(PowerWriteDCValueIndex(IntPtr.Zero,ref scheme,ref group,ref setting,dc));
        if(Active()==scheme)PowerModes.Check(PowerSetActiveScheme(IntPtr.Zero,ref scheme));
    }
    static void Change(string key,string value){Safety.Change(key,()=>Read(key),text=>Write(key,text),value);}
    static void Restore(string key){if(!Safety.RestoreOne(key,()=>Read(key),text=>Write(key,text),true))throw new InvalidOperationException("Настройка питания была изменена вне ModeTapper");}
    internal static void RecoverOverrides(){
        foreach(string key in Safety.TrackedKeys())
            if(key.StartsWith(LidHold,StringComparison.Ordinal)||key.StartsWith(IdleHold,StringComparison.Ordinal)||key.StartsWith(Saver,StringComparison.Ordinal)||key.StartsWith(BoostHold,StringComparison.Ordinal)||key==SaverAC||key==SaverDC||key==SaverOverride)
                try{Safety.RestoreOne(key,()=>Read(key),text=>Write(key,text),true);}
                catch(Exception ex){Safety.Pause("Power recovery failed: "+key+": "+ex.Message);}
    }
    internal static void EnableWindowsLidSleep(){Change(LidBaseline+Active(),"1,1");}
    internal static string KeepLidOpen(string previous){
        string scheme=Active().ToString();
        if(scheme==previous)return scheme;
        ReleaseLid(previous);
        try{Change(IdleHold+scheme,"0,0");Change(LidHold+scheme,"0,0");return scheme;}
        catch{ReleaseLid(scheme);throw;}
    }
    internal static void ReleaseLid(string scheme){if(scheme!=null)try{Restore(LidHold+scheme);}finally{Restore(IdleHold+scheme);}}
    internal static string SetEnergySaver(string previous,bool? enabled){
        if(!enabled.HasValue){if(previous!=null)Restore(Saver+previous);return null;}
        string scheme=Active().ToString();
        if(previous!=null&&previous!=scheme)Restore(Saver+previous);
        Change(Saver+scheme,enabled.Value?"100,100":"0,0");
        return scheme;
    }
    internal static void SetEnergySaverOverride(bool? enabled){
        if(!enabled.HasValue){Restore(SaverOverride);return;}
        if(WindowsEnergySaver.Available)Change(SaverOverride,enabled.Value?"1":"2");
    }
    internal static string StartMaxSaver(string scheme){
        try{
            Change(BoostHold+scheme,"0,0");
            Change(SaverAC,PowerModes.Economy.ToString());Change(SaverDC,PowerModes.Economy.ToString());
            return scheme;
        }catch{StopMaxSaver(scheme);throw;}
    }
    internal static void StopMaxSaver(string scheme){
        try{Restore(BoostHold+scheme);}finally{try{Restore(SaverDC);}finally{Restore(SaverAC);}}
    }
}

// Windows 11's Always use energy saver setting is independent of the battery threshold.
// Its Settings handler publishes this WNF preference. Windows owns and persists it.
internal static class WindowsEnergySaver {
    const string PowerKey=@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power";
    const ulong OverrideNotification=0x41c6013da3bc3075UL;
    static readonly Guid StatusNotification=new Guid("550e8400-e29b-41d4-a716-446655440000");

    [DllImport("ntdll.dll")]
    static extern int RtlPublishWnfStateData(ulong name,IntPtr type,ref uint data,uint size,IntPtr scope);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate uint SettingCallback(IntPtr context,uint type,IntPtr setting);
    [StructLayout(LayoutKind.Sequential)]
    struct Subscription {public SettingCallback Callback;public IntPtr Context;}
    [DllImport("powrprof.dll")]
    static extern uint PowerSettingRegisterNotification(ref Guid setting,uint flags,ref Subscription subscription,out IntPtr handle);
    [DllImport("powrprof.dll")]
    static extern uint PowerSettingUnregisterNotification(IntPtr handle);

    static readonly SettingCallback statusChanged=OnStatusChanged;
    static IntPtr statusHandle;
    static int effective=-1;
    internal static bool Available {get{return Environment.OSVersion.Version.Build>=26100&&Read()!=null;}}
    internal static bool? Active {get{int value=Volatile.Read(ref effective);return value<0?(bool?)null:value!=0;}}

    internal static string Read(){
        object value=Registry.GetValue(PowerKey,"EnergySaverState",null);
        if(value is int&&(int)value>=0&&(int)value<=2)return ((int)value).ToString(CultureInfo.InvariantCulture);
        return null;
    }
    internal static void Write(string value){
        if(Read()==value)return;
        uint requested=UInt32.Parse(value,CultureInfo.InvariantCulture);
        if(requested>2)throw new InvalidOperationException("Неизвестный режим энергосбережения Windows");
        int result=RtlPublishWnfStateData(OverrideNotification,IntPtr.Zero,ref requested,4,IntPtr.Zero);
        if(result<0)throw new InvalidOperationException("Windows отклонила переключение энергосбережения (0x"+result.ToString("X8")+")");
        // The service updates the stored preference asynchronously.
        for(int attempt=0;attempt<20&&Read()!=value;attempt++)Thread.Sleep(15);
    }
    static uint OnStatusChanged(IntPtr context,uint type,IntPtr setting){
        if(setting!=IntPtr.Zero&&Marshal.ReadInt32(setting,16)==4){
            int value=Marshal.ReadInt32(setting,20);
            if(value>=0&&value<=2)Volatile.Write(ref effective,value);
        }
        return 0;
    }
    internal static void Observe(){
        if(Environment.OSVersion.Version.Build<26100||statusHandle!=IntPtr.Zero)return;
        Guid setting=StatusNotification;
        var subscription=new Subscription{Callback=statusChanged};
        if(PowerSettingRegisterNotification(ref setting,2,ref subscription,out statusHandle)!=0)statusHandle=IntPtr.Zero;
    }
    internal static void StopObserving(){
        if(statusHandle!=IntPtr.Zero){PowerSettingUnregisterNotification(statusHandle);statusHandle=IntPtr.Zero;}
        Volatile.Write(ref effective,-1);
    }
}

internal enum PowerPreset { Economy, Balance, Maximum }

internal sealed class PowerSelection : IDisposable {
    string saverScheme,boostScheme;
    Guid? appliedPlan;
    internal PowerSelection(){Current=PowerPreset.Balance;}
    internal PowerPreset Current {get;private set;}

    internal void Select(PowerPreset preset){
        Guid plan=PlanSettings.Active();
        if(boostScheme!=null&&(preset!=PowerPreset.Economy||boostScheme!=plan.ToString()))ReleaseMaxSavings();
        if(preset==PowerPreset.Economy){
            boostScheme=PlanSettings.StartMaxSaver(plan.ToString());
            try{saverScheme=PlanSettings.SetEnergySaver(saverScheme,true);PlanSettings.SetEnergySaverOverride(true);}
            catch{
                try{saverScheme=PlanSettings.SetEnergySaver(saverScheme,null);}finally{ReleaseMaxSavings();}
                throw;
            }
        }else{
            PlanSettings.SetEnergySaverOverride(preset==PowerPreset.Maximum?(bool?)false:null);
            if(preset==PowerPreset.Maximum)PowerModes.SetPair(PowerModes.Best,PowerModes.Best);
            else PowerModes.Defaults();
            saverScheme=PlanSettings.SetEnergySaver(saverScheme,preset==PowerPreset.Maximum?(bool?)false:null);
        }
        Current=preset;
        appliedPlan=plan;
    }

    internal void RefreshPlan(){if(appliedPlan.HasValue&&PlanSettings.Active()!=appliedPlan.Value)Select(Current);}

    void ReleaseMaxSavings(){
        if(boostScheme==null)return;
        PlanSettings.StopMaxSaver(boostScheme);
        boostScheme=null;
    }

    public void Dispose(){
        try{ReleaseMaxSavings();}
        finally{
            try{saverScheme=PlanSettings.SetEnergySaver(saverScheme,null);}
            finally{PlanSettings.SetEnergySaverOverride(null);}
        }
    }
}

internal sealed class LidMonitor : NativeWindow,IDisposable {
    static readonly Guid LidState=new Guid("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
    [DllImport("user32.dll",SetLastError=true)]static extern IntPtr RegisterPowerSettingNotification(IntPtr window,ref Guid setting,uint flags);
    [DllImport("user32.dll")]static extern bool UnregisterPowerSettingNotification(IntPtr registration);
    [DllImport("powrprof.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.U1)]static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)]bool hibernate,[MarshalAs(UnmanagedType.U1)]bool force,[MarshalAs(UnmanagedType.U1)]bool disableWake);
    [StructLayout(LayoutKind.Sequential)]struct Luid {public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]struct TokenPrivilege {public uint Count;public Luid Id;public uint Attributes;}
    [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool LookupPrivilegeValue(string system,string name,out Luid id);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool AdjustTokenPrivileges(IntPtr token,bool disable,ref TokenPrivilege state,int size,out TokenPrivilege previous,out int needed);

    readonly IntPtr registration;
    internal bool Closed {get;private set;}
    internal LidMonitor(){
        CreateHandle(new CreateParams());
        Guid setting=LidState;registration=RegisterPowerSettingNotification(Handle,ref setting,0);
        if(registration==IntPtr.Zero){int error=Marshal.GetLastWin32Error();DestroyHandle();throw new Win32Exception(error);}
    }
    protected override void WndProc(ref Message message){
        if(message.Msg==0x218&&message.WParam.ToInt32()==0x8013&&message.LParam!=IntPtr.Zero&&
           (Guid)Marshal.PtrToStructure(message.LParam,typeof(Guid))==LidState&&Marshal.ReadInt32(message.LParam,16)>=4)
            Closed=Marshal.ReadInt32(message.LParam,20)==0;
        base.WndProc(ref message);
    }
    internal static void SleepNow(){
        IntPtr token;
        if(!OpenProcessToken(GetCurrentProcess(),0x28,out token))throw new Win32Exception(Marshal.GetLastWin32Error());
        try{
            Luid id;if(!LookupPrivilegeValue(null,"SeShutdownPrivilege",out id))throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested=new TokenPrivilege{Count=1,Id=id,Attributes=2};TokenPrivilege previous;int size;
            bool changed=AdjustTokenPrivileges(token,false,ref requested,Marshal.SizeOf(typeof(TokenPrivilege)),out previous,out size);
            int error=Marshal.GetLastWin32Error();if(!changed||error!=0)throw new Win32Exception(error);
            try{if(!SetSuspendState(false,false,false))throw new Win32Exception(Marshal.GetLastWin32Error());}
            finally{
                TokenPrivilege ignored;int ignoredSize;
                AdjustTokenPrivileges(token,false,ref previous,Marshal.SizeOf(typeof(TokenPrivilege)),out ignored,out ignoredSize);
            }
        }finally{CloseHandle(token);}
    }
    public void Dispose(){UnregisterPowerSettingNotification(registration);DestroyHandle();}
}
