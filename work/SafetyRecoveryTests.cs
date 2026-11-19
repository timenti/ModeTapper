using System;
using System.IO;
using System.Threading;
class SafetyRecoveryTests {
 static int checks;static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
 static string NewFolder(){string path=Path.Combine(Path.GetTempPath(),"LumaSafetyRecovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);Safety.Init(path);return path;}
 static int Main(){
  string folder=NewFolder(),value="factory";Safety.Change("test",()=>value,v=>value=v,"app-first");value="user-manual";Safety.Change("test",()=>value,v=>value=v,"app-second");Check(Safety.RestoreOne("test",()=>value,v=>value=v)&&value=="user-manual","restore preserves external baseline rather than replacing it with factory value");
  int writes=0;Check(Safety.RestoreOne("test",()=>value,v=>{writes++;value=v;})&&writes==0,"repeat restore is idempotent");
  Safety.Change("test",()=>value,v=>value=v,"app-third");value="other-software";Check(!Safety.RestoreOne("test",()=>value,v=>value=v)&&value=="other-software","external changes after app write are not overwritten");
  folder=NewFolder();value="old";bool failed=false;try{Safety.Change("test",()=>value,v=>value=v=="wanted"?"clamped":v,"wanted");}catch(IOException){failed=true;}Check(failed&&value=="old","mismatched device readback is compensated");
  folder=NewFolder();string journal=Path.Combine(folder,"safety","settings.json");File.WriteAllText(journal,"broken-journal");Safety.Init(folder);Check(Safety.Safe,"corrupt journal fails closed");Check(Safety.Rollback()==2&&File.ReadAllText(journal)=="broken-journal","rollback refuses unreadable journal without overwriting evidence");failed=false;try{Safety.Resume();}catch(InvalidOperationException){failed=true;}Check(failed,"resume refuses corrupt journal");
  folder=NewFolder();value="old";File.Move(Path.Combine(folder,"safety","actions.jsonl"),Path.Combine(folder,"safety","actions.saved"));Directory.CreateDirectory(Path.Combine(folder,"safety","actions.jsonl"));writes=0;failed=false;try{Safety.Change("test",()=>value,v=>{writes++;value=v;},"new");}catch(IOException){failed=true;}catch(UnauthorizedAccessException){failed=true;}Check(failed&&writes==0&&value=="old","failed intent logging prevents native write");Check(Safety.Safe,"journal write failure pauses changes");
  Check(WindowsProxy.Canonical(new ProxySnapshot{Flags=9})==WindowsProxy.Canonical(new ProxySnapshot{Flags=9,Server="",Bypass="",AutoUrl=""}),"null and empty WinINet strings compare identically");
  Console.WriteLine(checks+" external change, corrupt journal, intent failure and proxy canonicalization checks PASS");return 0;
 }
}
