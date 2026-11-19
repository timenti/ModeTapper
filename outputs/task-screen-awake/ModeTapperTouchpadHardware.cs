using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

internal sealed class KeyboardLight {
 internal KeyboardLight(string folder){}
 RegistryKey OpenLighting(bool writable){using(var devices=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Lighting\Devices")){if(devices!=null)foreach(string name in devices.GetSubKeyNames()){if(name.IndexOf("VID_0B05&PID_1A30&MI_04",StringComparison.OrdinalIgnoreCase)<0)continue;var device=devices.OpenSubKey(name,writable);if(device==null)continue;if(Convert.ToInt32(device.GetValue("AmbientLightingEnabled",0))!=1){device.Dispose();throw new InvalidOperationException("Включи динамическое освещение клавиатуры в настройках Windows");}return device;}}throw new InvalidOperationException("Настройки подсветки клавиатуры не найдены в Windows");}
 internal int Level {get{using(var device=OpenLighting(false))return Math.Max(0,Math.Min(100,Convert.ToInt32(device.GetValue("Brightness",100))));}}
 internal int BeginGesture(){Safety.Require();return Level;}
 internal void Set(int value){Safety.Change("keyboard.brightness",()=>Level.ToString(),text=>SetRaw(Int32.Parse(text)),Math.Max(0,Math.Min(100,value)).ToString());}
 internal void SetRaw(int value){value=Math.Max(0,Math.Min(100,value));using(var device=OpenLighting(true)){if(Convert.ToInt32(device.GetValue("Brightness",100))!=value)device.SetValue("Brightness",value,RegistryValueKind.DWord);}}
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
