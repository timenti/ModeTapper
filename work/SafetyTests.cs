using System;
using System.IO;
class SafetyTests {
 static int checks;static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
 static int Main(){string folder=Path.Combine(Path.GetTempPath(),"LumaSafetyTest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);Safety.Init(folder);string state="before";int calls=0;
  Safety.Change("fake",()=>state,v=>{calls++;state=v;},"after");Check(state=="after"&&calls==1,"first apply");Safety.Change("fake",()=>state,v=>{calls++;state=v;},"after");Check(calls==1,"repeat apply does not rewrite");
  Safety.Change("fake",()=>state,v=>state=v,"next");Check(File.ReadAllText(Path.Combine(folder,"safety","settings.json")).Contains("before"),"original baseline survives later changes");
  bool failed=false;try{Safety.Change("fake",()=>state,v=>{if(v=="fail"){state=v;throw new IOException("injected failure");}state=v;},"fail");}catch(IOException){failed=true;}Check(failed&&state=="next","partial write is compensated");
  Check(!File.ReadAllText(Path.Combine(folder,"safety","settings.json")).Contains("true"),"compensated transaction is not pending");
  Safety.Atomic(Path.Combine(folder,"safety","settings.json"),"{\"fake\":{\"Original\":\"before\",\"Expected\":\"next\",\"Pending\":true}}");Safety.Init(folder);Check(Safety.Safe,"interrupted transaction forces safe mode");bool blocked=false;try{Safety.Change("fake",()=>state,v=>state=v,"bad");}catch(InvalidOperationException){blocked=true;}Check(blocked&&state=="next","safe mode prevents writes");
  Check(File.Exists(Path.Combine(folder,"safety","settings.json.bak")),"atomic file has recovery backup");Check(File.ReadAllText(Path.Combine(folder,"safety","actions.jsonl")).Contains("compensated"),"audit records failure and compensation");
  string clean=Path.Combine(folder,"session-only");Safety.Init(clean);Safety.PauseForSession();
  Check(Safety.Safe&&!File.Exists(Path.Combine(clean,"safety","safe-mode.flag")),"test pause applies to the process only");
  Safety.Init(clean);Check(!Safety.Safe,"normal launch following carousel test is usable");
  Safety.Pause("explicit persistent pause");Safety.PauseForSession();Safety.Init(clean);Check(Safety.Safe,"carousel test does not clear an existing persistent pause");
  int reads=0,waits=0;
  Check(DisplayLight.WaitForLevel(()=>++reads<4?86:100,100,()=>waits++)&&waits==3,"delayed display confirmation does not trigger rollback");
  waits=0;Check(!DisplayLight.WaitForLevel(()=>86,100,()=>waits++)&&waits==9,"unchanged display fails after a bounded wait");
  Safety.Init(Path.Combine(folder,"audio"));string sound="0.0600000024,0";
  Safety.Change("sound",()=>sound,value=>sound=value=="0.04,0"?"0.0400000028,0":value,"0.04,0");
  Check(!Safety.Safe&&sound=="0.0400000028,0","audio taper readback preserves a successful change");
  Check(!SoundLevel.SameSnapshot("0.04,0","0.05,0")&&!SoundLevel.SameSnapshot("0.04,1","0.04,0"),"real volume and mute mismatches still fail verification");
  Check(Safety.RestoreOne("sound",()=>sound,value=>sound=value)&&sound=="0.0600000024,0","audio rollback accepts the equivalent readback");
  const string before="0.8031502,0",wanted="0.86,0",deviceStep="0.8504,0";
  Safety.Init(Path.Combine(folder,"stepped-audio"));sound=before;waits=0;
  Safety.Change("sound",()=>sound,value=>sound=value==wanted?deviceStep:value,wanted,
   (target,previous)=>SoundLevel.WaitForSnapshot(()=>waits<2?previous:sound,target,previous,()=>waits++));
  string stateFile=Path.Combine(folder,"stepped-audio","safety","settings.json");
  Check(!Safety.Safe&&waits==2&&File.ReadAllText(stateFile).Contains("\"Expected\":\""+deviceStep+"\""),"audio waits for delayed hardware and journals the observed device step");
  Check(SoundLevel.WaitForSnapshot(()=>"0.79112494,0",before,wanted,()=>{})=="0.79112494,0","a nearby Bluetooth step confirms restoration");
  failed=false;try{SoundLevel.WaitForSnapshot(()=>"0.85,1",wanted,before,()=>{});}catch(IOException){failed=true;}
  Check(failed,"a volume match never masks a different mute state");
  Safety.Atomic(stateFile,"{\"sound\":{\"Original\":\"0.8031502,0\",\"Expected\":\"0.86,0\",\"Pending\":true}}");
  Safety.Init(Path.Combine(folder,"stepped-audio"));Safety.CheckCanResume();
  Check(Safety.Safe,"an interrupted audio transaction still enters safe mode");
  Safety.RecoverInterruptedSound(()=>"0.79112494,0");
  Check(!Safety.TrackedKeys().Contains("sound")&&File.Exists(stateFile+".bak"),"audio recovery preserves the current setting and retains journal history");
  Console.WriteLine(checks+" journal, interrupted restart and test-mode checks PASS; isolated data: "+folder);return 0;
 }
}
