using System;
using System.Diagnostics;
class TabletInteractionTests {
 static int checks;static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static void Main(){
  Check(CarouselMotion.QuickTarget(2,250,0,2,5)==1,"right drag reveals older app on left");
  Check(CarouselMotion.QuickTarget(2,-250,0,2,5)==3,"left drag reveals newer app on right");
  Check(CarouselMotion.QuickTarget(0,250,0,2,5)==0,"left end does not wrap");
  Check(CarouselMotion.QuickTarget(4,-250,0,2,5)==4,"right end does not wrap");
  Check(CarouselMotion.QuickTarget(2,10,0,2,5)==2,"tap does not switch");
  Check(CarouselMotion.QuickTarget(2,60,1400,2,5)==1,"short fast flick switches older");
  Check(CarouselMotion.Rubber(-2,5)<0&&CarouselMotion.Rubber(-2,5)>-.18,"elastic left boundary limited");
  Check(CarouselMotion.Rubber(6,5)>4&&CarouselMotion.Rubber(6,5)<4.18,"elastic right boundary limited");
  var spring=new CarouselSpring();spring.Set(0);spring.Target=1;for(int i=0;i<40;i++)spring.Step(.015);Check(Math.Abs(spring.Value-1)<.001,"spring settles within 600 ms");
  var gesture=new TabletEdgeGesture();PadEdge edge=PadEdge.None;double delta=0;int hits=0;gesture.Motion+=(id,e,d)=>{hits++;edge=e;delta=d;};long t=Stopwatch.GetTimestamp();
  gesture.Update(true,1,1,5,700,2560,1600,2,t);gesture.Update(true,1,1,5,640,2560,1600,2,t+Stopwatch.Frequency/10);gesture.Update(true,1,1,5,540,2560,1600,2,t+Stopwatch.Frequency/5);Check(edge==PadEdge.Screen&&hits==2&&delta>0,"left screen edge increases brightness upward");
  gesture.Reset();hits=0;gesture.Update(true,1,1,2555,700,2560,1600,2,t);gesture.Update(true,1,1,2555,640,2560,1600,2,t+Stopwatch.Frequency/10);gesture.Update(true,1,1,2555,540,2560,1600,2,t+Stopwatch.Frequency/5);Check(edge==PadEdge.Volume&&hits==2&&delta>0,"right screen edge increases volume upward");
  gesture.Reset();hits=0;gesture.Update(true,1,1,1595,1000,1600,2560,2,t);gesture.Update(true,1,1,1595,940,1600,2560,2,t+Stopwatch.Frequency/10);Check(edge==PadEdge.Volume&&hits==1,"portrait right edge still volume");
  gesture.Reset();hits=0;gesture.Update(true,1,1,800,1000,1600,2560,2,t);gesture.Update(true,1,1,1595,940,1600,2560,2,t+Stopwatch.Frequency/10);Check(hits==0,"center touch entering edge ignored");
  gesture.Reset();hits=0;gesture.Update(true,1,1,1595,1000,1600,2560,2,t);gesture.Update(true,2,1,1595,940,1600,2560,2,t+Stopwatch.Frequency/10);Check(hits==0,"two fingers cancel screen edge");
  gesture.Reset();hits=0;gesture.Update(true,1,1,1555,1000,1600,2560,2,t);gesture.Update(true,1,1,1539,940,1600,2560,2,t+Stopwatch.Frequency/10);gesture.Update(true,1,1,1470,860,1600,2560,2,t+Stopwatch.Frequency/5);
  Check(hits==2&&edge==PadEdge.Volume&&delta>0,"screen volume stays locked after a deliberate edge start");
  Console.WriteLine(checks+" carousel and tablet edge checks PASS");
 }
}
