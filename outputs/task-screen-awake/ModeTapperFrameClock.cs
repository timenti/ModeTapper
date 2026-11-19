using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Windows messages from a multimedia clock, coalesced to one pending UI frame.
// The clock exists only while the carousel moves or a finger is held down.
internal sealed class CarouselFrameClock : IDisposable {
 delegate void TimerCallback(uint id,uint message,UIntPtr user,UIntPtr first,UIntPtr second);
 [DllImport("winmm.dll")]static extern uint timeSetEvent(uint delay,uint resolution,TimerCallback callback,UIntPtr user,uint flags);
 [DllImport("winmm.dll")]static extern uint timeKillEvent(uint id);
 readonly Control owner;readonly TimerCallback callback;readonly Action deliver;
 readonly System.Windows.Forms.Timer fallback=new System.Windows.Forms.Timer{Interval=8};
 uint timer;int pending;bool disposed;
 internal bool Enabled {get;private set;}
 internal event EventHandler Tick;
 internal CarouselFrameClock(Control target){owner=target;callback=Queue;deliver=Deliver;fallback.Tick+=(s,e)=>{if(Enabled&&Tick!=null)Tick(this,EventArgs.Empty);};}
 void Queue(uint id,uint message,UIntPtr user,UIntPtr first,UIntPtr second){if(!Enabled||disposed||Interlocked.CompareExchange(ref pending,1,0)!=0)return;try{owner.BeginInvoke(deliver);}catch(InvalidOperationException){Interlocked.Exchange(ref pending,0);}}
 void Deliver(){
  try{if(Enabled&&!disposed&&Tick!=null)Tick(this,EventArgs.Empty);}
  finally{Interlocked.Exchange(ref pending,0);}
 }
 internal void Start(){if(Enabled||disposed)return;Enabled=true;timer=timeSetEvent(8,1,callback,UIntPtr.Zero,0x101);if(timer==0)fallback.Start();}
 internal void Stop(){Enabled=false;fallback.Stop();uint old=timer;timer=0;if(old!=0)timeKillEvent(old);}
 public void Dispose(){if(disposed)return;Stop();disposed=true;fallback.Dispose();}
}
