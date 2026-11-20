using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using System.Windows.Forms;

internal sealed class KeyboardLight {
 static readonly object hardwareLock=new object();
 static int hardwareRevision;
 internal struct Snapshot {internal int Level,Revision;}
 internal KeyboardLight(string folder){}
 RegistryKey OpenLighting(bool writable){using(var devices=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Lighting\Devices")){if(devices!=null)foreach(string name in devices.GetSubKeyNames()){if(name.IndexOf("VID_0B05&PID_1A30&MI_04",StringComparison.OrdinalIgnoreCase)<0)continue;var device=devices.OpenSubKey(name,writable);if(device==null)continue;if(Convert.ToInt32(device.GetValue("AmbientLightingEnabled",0))!=1){device.Dispose();throw new InvalidOperationException("Включи динамическое освещение клавиатуры в настройках Windows");}return device;}}throw new InvalidOperationException("Настройки подсветки клавиатуры не найдены в Windows");}
 internal int Level {get{using(var device=OpenLighting(false))return Math.Max(0,Math.Min(100,Convert.ToInt32(device.GetValue("Brightness",100))));}}
 internal int BeginGesture(){Safety.Require();return Level;}
 internal void WakeHardware(){
  if(!AsusCooling.IsSupportedModel())return;
  lock(hardwareLock){
   ApplyHardwareLevel(3,"keyboard.vendor-hid");
   hardwareRevision++;
  }
 }
 internal Snapshot CaptureHardware(){
  lock(hardwareLock){
   if(Safety.Safe||!WindowsLightingEnabled()||Level==0)return new Snapshot();
   // Aura can replace the readable report with an effect. In that case Windows supplies the on/off preference.
   return new Snapshot{Level=AsusKeyboardBacklight.ReadLevel()??3,Revision=hardwareRevision};
  }
 }
 internal void RestoreHardware(Snapshot saved){
  lock(hardwareLock){
   if(saved.Level==0||saved.Revision!=hardwareRevision||Safety.Safe||!WindowsLightingEnabled()||Level==0)return;
   ApplyHardwareLevel(saved.Level,"keyboard.resume");
  }
 }
 static bool WindowsLightingEnabled(){
  return Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Lighting","AmbientLightingEnabled",0))==1;
 }
 static void ApplyHardwareLevel(int level,string operation){
  Safety.Require();
  string value=level.ToString(CultureInfo.InvariantCulture);
  Safety.Audit(operation,null,value,"enable intent");
  try{
   AsusKeyboardBacklight.SetLevel(level);
  }catch(Exception ex){
   Safety.Audit(operation,null,value,ex.Message);
   throw;
  }
  Safety.Audit(operation,null,value,"driver accepted enable");
 }
 internal void Set(int value){Safety.Change("keyboard.brightness",()=>Level.ToString(),text=>SetRaw(Int32.Parse(text)),Math.Max(0,Math.Min(100,value)).ToString());}
 internal void SetRaw(int value){value=Math.Max(0,Math.Min(100,value));using(var device=OpenLighting(true)){if(Convert.ToInt32(device.GetValue("Brightness",100))!=value)device.SetValue("Brightness",value,RegistryValueKind.DWord);}}
}

internal static class AsusKeyboardBacklight {
 const string KeyboardControlInterface=@"\\?\HID#VID_0B05&PID_1A30&MI_00&COL01#";
 const uint DeviceName=0x20000007, PreparsedData=0x20000005;
 const ushort VendorUsage=0x76, VendorUsagePage=0xFF31;

 [StructLayout(LayoutKind.Sequential)]
 struct Device {public IntPtr Handle;public uint Type;}

 [DllImport("user32.dll",SetLastError=true)]
 static extern uint GetRawInputDeviceList([Out]Device[] devices,ref uint count,uint entrySize);
 [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
 static extern uint GetRawInputDeviceInfo(IntPtr device,uint command,IntPtr data,ref uint size);
 [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
 static extern uint GetRawInputDeviceInfo(IntPtr device,uint command,StringBuilder data,ref uint size);
 [DllImport("hid.dll")]
 static extern int HidP_GetCaps(IntPtr data,[Out]byte[] caps);
 [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
 static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
 [DllImport("hid.dll",SetLastError=true)]
 static extern bool HidD_SetFeature(SafeFileHandle device,byte[] report,int length);
 [DllImport("hid.dll",SetLastError=true)]
 static extern bool HidD_GetFeature(SafeFileHandle device,[In,Out]byte[] report,int length);

 internal static int? ReadLevel(){
  return WithController<int?>((handle,report)=>{
   report[0]=0x5A;
   if(!HidD_GetFeature(handle,report,report.Length))throw new Win32Exception(Marshal.GetLastWin32Error());
   // The returned feature report may be for an unrelated Aura command.
   if(report[0]!=0x5A||report[1]!=0xBA||report[2]!=0xC5||report[3]!=0xC4||report[4]>3)return null;
   return report[4];
  });
 }

 internal static void SetLevel(int level){
  WithController((handle,report)=>{
   // Windows Dynamic Lighting still supplies the user's brightness and effect.
   report[0]=0x5A;
   report[1]=0xBA;
   report[2]=0xC5;
   report[3]=0xC4;
   report[4]=(byte)level;
   if(!HidD_SetFeature(handle,report,report.Length))throw new Win32Exception(Marshal.GetLastWin32Error());
   return true;
  });
 }

 static T WithController<T>(Func<SafeFileHandle,byte[],T> operation){
  uint count=0,entrySize=(uint)Marshal.SizeOf(typeof(Device));
  if(GetRawInputDeviceList(null,ref count,entrySize)==UInt32.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
  var devices=new Device[count];
  uint found=GetRawInputDeviceList(devices,ref count,entrySize);
  if(found==UInt32.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
  for(int index=0;index<found;index++){
   var device=devices[index];
   if(device.Type!=2)continue;
   string path=ReadPath(device.Handle);
   if(path==null||!path.StartsWith(KeyboardControlInterface,StringComparison.OrdinalIgnoreCase))continue;
   int length=FeatureLength(device.Handle);
   if(length<5)continue;

   using(var handle=CreateFile(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero)){
    if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
    return operation(handle,new byte[length]);
   }
  }
  throw new Win32Exception(21,"Контроллер подсветки клавиатуры ASUS недоступен");
 }

 static string ReadPath(IntPtr device){
  uint size=0;
  if(GetRawInputDeviceInfo(device,DeviceName,IntPtr.Zero,ref size)==UInt32.MaxValue||size==0)return null;
  var path=new StringBuilder((int)size);
  return GetRawInputDeviceInfo(device,DeviceName,path,ref size)==UInt32.MaxValue?null:path.ToString();
 }

 static int FeatureLength(IntPtr device){
  uint size=0;
  if(GetRawInputDeviceInfo(device,PreparsedData,IntPtr.Zero,ref size)==UInt32.MaxValue||size==0)return 0;
  IntPtr buffer=Marshal.AllocHGlobal((int)size);
  try{
   if(GetRawInputDeviceInfo(device,PreparsedData,buffer,ref size)==UInt32.MaxValue)return 0;
   var caps=new byte[64];
   if(HidP_GetCaps(buffer,caps)<0||BitConverter.ToUInt16(caps,0)!=VendorUsage||BitConverter.ToUInt16(caps,2)!=VendorUsagePage)return 0;
   return BitConverter.ToUInt16(caps,8);
  }finally{Marshal.FreeHGlobal(buffer);}
 }
}

internal sealed class KeyboardLightResume : NativeWindow,IDisposable {
 const int SessionChange=0x02B1, SessionLock=0x7, SessionUnlock=0x8;
 [DllImport("wtsapi32.dll",SetLastError=true)]
 static extern bool WTSRegisterSessionNotification(IntPtr window,uint flags);
 [DllImport("wtsapi32.dll")]
 static extern bool WTSUnRegisterSessionNotification(IntPtr window);

 readonly KeyboardLight keyboard;
 readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
 KeyboardLight.Snapshot? saved;
 bool locked;
 int attempts;

 internal KeyboardLightResume(string folder){
  keyboard=new KeyboardLight(folder);
  CreateHandle(new CreateParams());
  if(!WTSRegisterSessionNotification(Handle,0)){
   int error=Marshal.GetLastWin32Error();
   DestroyHandle();
   timer.Dispose();
   throw new Win32Exception(error);
  }
  timer.Tick+=Restore;
 }

 protected override void WndProc(ref Message message){
  if(message.Msg==SessionChange)OnSessionChange(message.WParam.ToInt32());
  base.WndProc(ref message);
 }

 void OnSessionChange(int reason){
  if(reason==SessionLock){
   if(locked)return;
   timer.Stop();
   Capture();
   locked=true;
  }else if(reason==SessionUnlock){
   locked=false;
   if(saved.HasValue&&saved.Value.Level>0){
    attempts=0;
    timer.Interval=350;
    timer.Start();
   }else saved=null;
  }
 }

 void Capture(){
  saved=new KeyboardLight.Snapshot();
  try{saved=keyboard.CaptureHardware();}
  catch(Exception ex){Safety.Audit("keyboard.resume",null,null,ex.Message);}
 }

 void Restore(object sender,EventArgs args){
  timer.Stop();
  if(!saved.HasValue||locked)return;
  try{keyboard.RestoreHardware(saved.Value);}
  catch(Exception ex){
   if(ex is Win32Exception&&++attempts<3){
    timer.Interval=750;
    timer.Start();
    return;
   }
   Safety.Audit("keyboard.resume",null,null,ex.Message);
  }
  saved=null;
 }

 public void Dispose(){
  timer.Dispose();
  WTSUnRegisterSessionNotification(Handle);
  DestroyHandle();
 }
}

internal sealed class DisplayLight : IDisposable {
 readonly ManagementObject target;
 internal DisplayLight(){using(var search=new ManagementObjectSearcher("root\\WMI","SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE"))using(var list=search.Get()){foreach(ManagementObject item in list){target=new ManagementObject(item.Path.Path);break;}}if(target==null)throw new InvalidOperationException("Регулировка яркости экрана недоступна");}
 internal static int Get(){using(var search=new ManagementObjectSearcher("root\\WMI","SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE"))using(var list=search.Get()){foreach(ManagementObject item in list)using(item)return Convert.ToInt32(item["CurrentBrightness"]);}throw new InvalidOperationException("Яркость экрана недоступна");}
 internal void Set(int level){Safety.Change("display.brightness",()=>Get().ToString(),text=>SetRaw(Int32.Parse(text)),Math.Max(0,Math.Min(100,level)).ToString());}
 internal static bool WaitForLevel(Func<int> read,int level,Action wait){
  for(int attempt=0;attempt<10;attempt++){
   if(read()==level)return true;
   if(attempt<9)wait();
  }
  return false;
 }
 internal void SetRaw(int level){
  level=Math.Max(0,Math.Min(100,level));
  uint result=Convert.ToUInt32(target.InvokeMethod("WmiSetBrightness",new object[]{(uint)0,(byte)level}));
  if(result!=0)throw new InvalidOperationException("Windows отклонила яркость экрана (WMI "+result+")");
  // The WMI provider can return before the panel reports the new value.
  if(!WaitForLevel(Get,level,()=>Thread.Sleep(30)))throw new InvalidOperationException("Экран не подтвердил яркость "+level+"%");
 }
 public void Dispose(){target.Dispose();}
}
[ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]internal class AudioDevices {}
[ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]internal interface IAudioDevices {
 [PreserveSig]int EnumAudioEndpoints(int flow,int state,out IntPtr devices);
 [PreserveSig]int GetDefaultAudioEndpoint(int flow,int role,out IAudioDevice device);
}
[ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]internal interface IAudioDevice {
 [PreserveSig]int Activate(ref Guid id,int context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)]out object result);
}
[ComImport,Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]internal interface IEndpointVolume {
 [PreserveSig]int RegisterControlChangeNotify(IntPtr notify);
 [PreserveSig]int UnregisterControlChangeNotify(IntPtr notify);
 [PreserveSig]int GetChannelCount(out uint count);
 [PreserveSig]int SetMasterVolumeLevel(float value,ref Guid context);
 [PreserveSig]int SetMasterVolumeLevelScalar(float value,ref Guid context);
 [PreserveSig]int GetMasterVolumeLevel(out float value);
 [PreserveSig]int GetMasterVolumeLevelScalar(out float value);
 [PreserveSig]int SetChannelVolumeLevel(uint channel,float value,ref Guid context);
 [PreserveSig]int SetChannelVolumeLevelScalar(uint channel,float value,ref Guid context);
 [PreserveSig]int GetChannelVolumeLevel(uint channel,out float value);
 [PreserveSig]int GetChannelVolumeLevelScalar(uint channel,out float value);
 [PreserveSig]int SetMute([MarshalAs(UnmanagedType.Bool)]bool value,ref Guid context);
 [PreserveSig]int GetMute([MarshalAs(UnmanagedType.Bool)]out bool value);
}
internal sealed class SoundLevel : IDisposable {
 IEndpointVolume endpoint;
 internal SoundLevel(){var devices=(IAudioDevices)new AudioDevices();IAudioDevice device=null;try{Marshal.ThrowExceptionForHR(devices.GetDefaultAudioEndpoint(0,1,out device));Guid id=typeof(IEndpointVolume).GUID;object value;Marshal.ThrowExceptionForHR(device.Activate(ref id,23,IntPtr.Zero,out value));endpoint=(IEndpointVolume)value;}finally{if(device!=null)Marshal.ReleaseComObject(device);Marshal.ReleaseComObject(devices);}}
 internal int Get(){float value;Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out value));return (int)Math.Round(value*100);}
 internal string Snapshot(){float value;bool mute;Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out value));Marshal.ThrowExceptionForHR(endpoint.GetMute(out mute));return value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+","+(mute?"1":"0");}
 internal static bool SameSnapshot(string first,string second){return MatchingSnapshot(first,second,.00001f);}
 static bool MatchingSnapshot(string first,string second,float tolerance){
  if(first==null||second==null)return false;
  string[] a=first.Split(','),b=second.Split(',');
  float from,to;
  return a.Length==2&&b.Length==2&&a[1]==b[1]&&
   Single.TryParse(a[0],NumberStyles.Float,CultureInfo.InvariantCulture,out from)&&
   Single.TryParse(b[0],NumberStyles.Float,CultureInfo.InvariantCulture,out to)&&Math.Abs(from-to)<=tolerance;
 }
 internal static string WaitForSnapshot(Func<string> read,string target,string previous,Action wait){
  const float maxStepError=.015f;
  string actual=null;
  for(int attempt=0;attempt<9;attempt++){
   actual=read();
   if(SameSnapshot(actual,target))return actual;
   if(MatchingSnapshot(actual,target,maxStepError)&&!SameSnapshot(actual,previous))return actual;
   if(attempt<8)wait();
  }
  if(MatchingSnapshot(actual,target,maxStepError))return actual;
  throw new IOException("Readback mismatch: sound");
 }
 internal string Confirm(string target,string previous){return WaitForSnapshot(Snapshot,target,previous,()=>Thread.Sleep(20));}
 internal void Restore(string text){
  string[] parts=text.Split(',');Guid context=Guid.Empty;
  bool mute;
  Marshal.ThrowExceptionForHR(endpoint.GetMute(out mute));
  Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(Single.Parse(parts[0],CultureInfo.InvariantCulture),ref context));
  if(mute!=(parts[1]=="1"))Marshal.ThrowExceptionForHR(endpoint.SetMute(parts[1]=="1",ref context));
 }
 internal void Set(int value,bool increase){string old=Snapshot();string next=(Math.Max(0,Math.Min(100,value))/100f).ToString("R",CultureInfo.InvariantCulture)+","+(increase&&value>0?"0":old.Split(',')[1]);Safety.Change("sound",Snapshot,Restore,next,Confirm);}

 public void Dispose(){if(endpoint!=null){Marshal.ReleaseComObject(endpoint);endpoint=null;}}
}
