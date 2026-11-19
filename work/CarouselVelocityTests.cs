using System;
class CarouselVelocityTests {
 static int checks;static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
 static double Stroke(double interval,bool batched){var tracker=new CarouselVelocity();tracker.Reset(0,1000,0);for(int i=1;i<=200;i++){double t=i*interval,arrival=batched?Math.Floor(t/.008)*.008:t;tracker.Add(arrival,1000-5000*t,1000*t);}double x,y;tracker.Read(200*interval,out x,out y);Check(x<-4000&&x>-6000,"fast motion survives dense and batched HID delivery: "+x);Check(y>800&&y<1200,"vertical speed preserves scale");tracker.Read(200*interval+.2,out x,out y);Check(x==0&&y==0,"pause cancels carry velocity");return -x;}
 static void Main(){Stroke(.001,false);Stroke(.001,true);Stroke(.008,false);
  var tracker=new CarouselVelocity();tracker.Reset(0,0,0);for(int i=1;i<=30;i++)tracker.Add(i*.008,i*2,0);double x,y;tracker.Read(.24,out x,out y);Check(Math.Abs(x-250)<1,"slow drag retains low speed");
  tracker.Reset(0,0,0);tracker.Add(.05,250,0);tracker.Add(.10,500,0);tracker.Add(.15,500,0);tracker.Add(.20,500,0);tracker.Read(.20,out x,out y);Check(x==0,"holding before release stops fling");
  tracker.Reset(0,0,0);for(int i=1;i<=30;i++)tracker.Add(i*.030,i*3,0);
  double first=tracker.InterpolatedX(.900),next=tracker.InterpolatedX(.908);
  Check(next>first&&next-first<1,"a presentation frame between sparse input reports advances a fractional position");
  Check(tracker.InterpolatedX(1.0)==90&&tracker.InterpolatedX(1.1)==90,"a stopped finger keeps its last reported position");
  var flow=new CarouselInertia();flow.Begin(4,5000/2000.0*2);while(flow.Active)flow.Step(.008,8);Check(flow.Value>=6,"realistic 5000 px/s flick crosses at least two cards");
  Console.WriteLine(checks+" measured velocity and flick checks PASS");
 }
}
