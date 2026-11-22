using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal sealed class SafetyEntry {public string Original,Expected,RestoreTarget;public bool Pending;}
internal static class Safety {
 static readonly object sync=new object();static readonly JavaScriptSerializer json=new JavaScriptSerializer();
 static Dictionary<string,SafetyEntry> entries=new Dictionary<string,SafetyEntry>();
 static string folder;static bool readable=true;internal static bool Safe {get;private set;}
 internal static void Init(string path){folder=path;Safe=false;readable=true;entries=new Dictionary<string,SafetyEntry>();Directory.CreateDirectory(Path.Combine(folder,"safety"));string state=Path.Combine(folder,"safety","settings.json");if(File.Exists(state))try{entries=json.Deserialize<Dictionary<string,SafetyEntry>>(File.ReadAllText(state));if(entries==null)throw new InvalidDataException("Null journal");foreach(var value in entries.Values)if(value==null)throw new InvalidDataException("Null journal entry");}catch(Exception ex){entries=new Dictionary<string,SafetyEntry>();readable=false;Pause("Journal unreadable; original preserved: "+ex.GetType().Name);}string build=Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","CurrentBuild",""))+"."+Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","UBR",""));string approved=Path.Combine(folder,"safety","windows-build.txt");if(!File.Exists(approved))Atomic(approved,build);else if(File.ReadAllText(approved)!=build)Pause("Windows build changed to "+build);foreach(var entry in entries.Values)if(entry.Pending)Pause("Interrupted setting transaction");Safe=Safe||File.Exists(Path.Combine(folder,"safety","safe-mode.flag"));Audit("startup",null,build,Safe?"safe":"normal");}
 internal static void Atomic(string path,string body){string temp=path+".tmp";using(var f=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){byte[] bytes=Encoding.UTF8.GetBytes(body);f.Write(bytes,0,bytes.Length);f.Flush(true);}if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);}
 static void Save(){Atomic(Path.Combine(folder,"safety","settings.json"),json.Serialize(entries));}
 internal static List<string> TrackedKeys(){lock(sync)return new List<string>(entries.Keys);}
 internal static void Audit(string key,string oldValue,string next,string result){if(folder==null)return;lock(sync){string file=Path.Combine(folder,"safety","actions.jsonl");if(File.Exists(file)&&new FileInfo(file).Length>4*1024*1024){string prior=file+".1";if(File.Exists(prior))File.Delete(prior);File.Move(file,prior);}using(var f=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.Read)){byte[] body=Encoding.UTF8.GetBytes(json.Serialize(new{utc=DateTime.UtcNow.ToString("o"),operation=key,oldValue,newValue=next,result})+"\n");f.Write(body,0,body.Length);f.Flush(true);}}}
 internal static void Require(){lock(sync){if(folder==null)throw new InvalidOperationException("Safety journal is not initialized");if(!readable)throw new InvalidOperationException("Safety journal is unreadable");foreach(var entry in entries.Values)if(entry.Pending){Pause("Unresolved transaction");throw new InvalidOperationException("Unresolved transaction");}if(Safe)throw new InvalidOperationException("ModeTapper в безопасном режиме. Изменения Windows приостановлены.");}}
 internal static void Pause(string reason){Safe=true;Atomic(Path.Combine(folder,"safety","safe-mode.flag"),reason);Audit("safe-mode",null,"on",reason);}
 internal static void PauseForSession(){Safe=true;Audit("safe-mode",null,"session-only","Carousel test");}
 internal static void CheckCanResume(){
  lock(sync){
   if(!readable)throw new InvalidOperationException("Журнал ModeTapper повреждён. Безопасный режим остаётся включённым.");
   foreach(var item in entries)if(item.Key!="sound"&&item.Value.Pending)throw new InvalidOperationException("Сначала выполните --rollback для незавершённых операций");
  }
 }
 internal static void Resume(){
  lock(sync){
   CheckCanResume();
   RecoverInterruptedSound(()=>Read("sound"));
   string approved=Path.Combine(folder,"safety","windows-build.txt");string build=Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","CurrentBuild",""))+"."+Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","UBR",""));Atomic(approved,build);
   string flag=Path.Combine(folder,"safety","safe-mode.flag");if(File.Exists(flag))File.Delete(flag);Safe=false;Audit("safe-mode","on","off","explicit resume");
  }
 }
 internal static void RecoverInterruptedSound(Func<string> read){
  lock(sync){
   SafetyEntry entry;
   if(!entries.TryGetValue("sound",out entry)||!entry.Pending)return;
   // The interrupted record has no output device ID. Preserve the user's current volume.
   string actual=read();
   Audit("sound",entry.Expected,actual,"interrupted audio change discarded; current volume preserved");
   entries.Remove("sound");
   try{Save();}catch{entries.Add("sound",entry);throw;}
  }
 }
 static bool SameValue(string key,string first,string second){return first==second||(key=="sound"&&SoundLevel.SameSnapshot(first,second));}
 internal static void Change(string key,Func<string> read,Action<string> write,string next,Func<string,string,string> confirm=null){
  lock(sync){
   Require();string old=read();if(SameValue(key,old,next))return;
   SafetyEntry entry;
   if(!entries.TryGetValue(key,out entry)){entry=new SafetyEntry{Original=old,Expected=old,RestoreTarget=old};entries.Add(key,entry);}
   else if(!SameValue(key,old,entry.Expected)){Audit(key,entry.Expected,old,"external change; new restoration baseline");entry.RestoreTarget=old;}
   entry.Expected=next;entry.Pending=true;
   try{Save();Audit(key,old,next,"intent");}catch{Safe=true;throw;}
   try{
    write(next);
    entry.Expected=ConfirmValue(key,read,confirm,next,old);
    entry.Pending=false;Save();Audit(key,old,entry.Expected,"success");
   }catch(Exception ex){
    try{
     write(old);
     entry.Expected=ConfirmValue(key,read,confirm,old,next);
     entry.Pending=false;Save();Audit(key,next,entry.Expected,"compensated: "+ex.Message);
    }catch(Exception rollback){Pause("Rollback failed: "+key);Audit(key,next,old,"FAILED: "+rollback.Message);}
    throw;
   }
  }
 }
 static string ConfirmValue(string key,Func<string> read,Func<string,string,string> confirm,string target,string previous){
  if(confirm!=null)return confirm(target,previous);
  if(!SameValue(key,read(),target))throw new IOException("Readback mismatch: "+key);
  return target;
 }
 internal static bool RestoreOne(string key,Func<string> read,Action<string> write,bool temporary=false,Func<string,string,string> confirm=null){
  lock(sync){
   if(!readable)throw new InvalidOperationException("Unreadable journal; original preserved");
   SafetyEntry entry;if(!entries.TryGetValue(key,out entry))return true;
   string baseline=entry.RestoreTarget??entry.Original,current=read();
   if(!SameValue(key,current,entry.Expected)&&!SameValue(key,current,baseline)){Audit(key,current,baseline,"conflict: external change preserved");return false;}
   if(!SameValue(key,current,baseline)){Audit(key,current,baseline,"rollback intent");write(baseline);}
   string actual=ConfirmValue(key,read,confirm,baseline,current);
   if(temporary)entries.Remove(key);else{entry.Expected=actual;entry.Pending=false;}
   Save();Audit(key,current,actual,"restored");return true;
  }
 }
 internal static int Rollback(){Pause("Explicit rollback");if(!readable)return 2;int failures=0;foreach(string key in new List<string>(entries.Keys))try{if(!RestoreTracked(key))failures++;}catch(Exception ex){failures++;Audit(key,null,null,"restore failed: "+ex.Message);}return failures==0?0:2;}
 static bool RestoreTracked(string key){
  if(key=="sound")using(var sound=new SoundLevel())return RestoreOne(key,sound.Snapshot,sound.Restore,confirm:sound.Confirm);
  if(key.StartsWith(MonitorBrightness.Prefix,StringComparison.Ordinal))
   using(var monitor=MonitorBrightness.Find(key))return RestoreOne(key,monitor.Read,monitor.Write);
  return RestoreOne(key,()=>Read(key),value=>Write(key,value));
 }
 static string Read(string key){if(PlanSettings.IsManaged(key))return PlanSettings.Read(key);if(key==NativeWindowAnimation.Setting)return NativeWindowAnimation.Read();if(key=="power.ac"||key=="power.dc")return PowerModes.ReadSide(key=="power.ac");if(key=="proxy")return WindowsProxy.Canonical(WindowsProxy.Query());if(key=="keyboard.brightness")return new KeyboardLight(folder).Level.ToString();if(key=="display.brightness")return DisplayLight.Get().ToString();if(key=="sound")using(var sound=new SoundLevel())return sound.Snapshot();throw new InvalidOperationException("Unknown rollback key "+key);}
 static void Write(string key,string value){if(PlanSettings.IsManaged(key))PlanSettings.Write(key,value);else if(key==NativeWindowAnimation.Setting)NativeWindowAnimation.Write(value);else if(key=="power.ac"||key=="power.dc")PowerModes.WriteSide(key=="power.ac",value);else if(key=="proxy")WindowsProxy.ApplyRaw(json.Deserialize<ProxySnapshot>(value));else if(key=="keyboard.brightness")new KeyboardLight(folder).SetRaw(Int32.Parse(value));else if(key=="display.brightness")using(var display=new DisplayLight())display.SetRaw(Int32.Parse(value));else throw new InvalidOperationException("Unknown rollback key "+key);}
}
