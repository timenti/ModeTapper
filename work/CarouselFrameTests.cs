using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
class CarouselFrameTests {
 [DllImport("winmm.dll")]static extern uint timeBeginPeriod(uint period);
 [DllImport("winmm.dll")]static extern uint timeEndPeriod(uint period);
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 [STAThread]static void Main(){
  timeBeginPeriod(1);
  try{using(var owner=new Control())using(var clock=new CarouselFrameClock(owner)){
   IntPtr handle=owner.Handle;int frames=0;clock.Tick+=(s,e)=>frames++;clock.Start();
   var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<1100){Application.DoEvents();Thread.Sleep(1);}
   double fps=frames/watch.Elapsed.TotalSeconds;Check(fps>75,"frame clock is not limited to legacy 60 Hz: "+fps);
   clock.Stop();Application.DoEvents();int stopped=frames;watch.Restart();while(watch.ElapsedMilliseconds<100){Application.DoEvents();Thread.Sleep(1);}Check(frames==stopped,"no idle frames after stopping");
   clock.Start();watch.Restart();while(watch.ElapsedMilliseconds<100){Application.DoEvents();Thread.Sleep(1);}Check(frames>stopped+5,"clock restarts after settled overview");clock.Stop();
   Console.WriteLine("Frame clock: "+fps.ToString("F1")+" callbacks/s, stop and restart PASS");
  }}finally{timeEndPeriod(1);}
 }
}
