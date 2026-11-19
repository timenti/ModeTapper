using System;
using System.Drawing;
class CarouselResponseTests {
 static int checks;
 static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
 static double Finish(double speed){var flow=new CarouselInertia();flow.Begin(5,speed);double previous=5;int frames=0;while(flow.Active&&frames++<300){flow.Step(.008,20);Check(speed>0?flow.Value>=previous-.00001:flow.Value<=previous+.00001,"flick must not reverse before settling");previous=flow.Value;}Check(frames<250,"flick finishes continuously with no extra settling phase");Check(Math.Abs(flow.Value-Math.Round(flow.Value))<.00001,"flick finishes exactly on a card without a second snap");Check(flow.Velocity==0,"flick ends at rest");return flow.Value;}
 static void Main(){
  var touch=new PillGesture();touch.Begin(1280,1585,1);
  Check(touch.Move(1286,1573,2)==PillGesture.Intent.Native,"tap remains with Windows");
  Check(touch.Move(1267,1480,2)==PillGesture.Intent.Native,"short upward swipe remains with Windows");
  Check(touch.Move(1280,1415,2)==PillGesture.Intent.Native,"long single-finger upward swipe does not open overview");
  var pair=new BottomPairGesture();
  var fingers=new TouchFrame{Count=2,SampledContacts=2,Pressed=true,Confident=true};
  var finger=new TouchFrame{Count=1,SampledContacts=1,Pressed=true,Confident=true};
  pair.Update(finger,1280,1585,true,140);
  pair.Update(finger,1280,1415,true,140);
  Check(!pair.InProgress&&pair.Intent==BottomPairGesture.Direction.None,"a lone finger never starts the paired overview");
  pair.Update(fingers,1280,1345,false,140);
  pair.Update(fingers,1280,1140,false,140);
  Check(pair.Intent==BottomPairGesture.Direction.None,"a pair must start at the bottom edge");
  pair.Reset();
  pair.Update(finger,1240,1585,true,140);
  pair.Update(fingers,1280,1585,true,140);
  pair.Update(fingers,1280,1480,true,140);
  Check(pair.Intent==BottomPairGesture.Direction.None,"short two-finger upward swipe stays below activation threshold");
  pair.Update(fingers,1280,1415,true,140);
  Check(pair.Intent==BottomPairGesture.Direction.Up,"long two-finger upward swipe opens overview");
  pair.Update(fingers,1430,1415,true,140);
  Check(pair.Intent==BottomPairGesture.Direction.Up,"overview does not also switch apps");
  pair.Update(fingers,1290,1577,true,140);
  Check(pair.Intent==BottomPairGesture.Direction.Up&&pair.OffsetY==-8,"the same gesture tracks movement back to its starting edge");
  pair.Update(finger,1290,1577,true,140);
  pair.Update(fingers,1280,1410,true,140);
  Check(pair.Intent==BottomPairGesture.Direction.None,"returning a second contact before lifting both does not restart an ended gesture");
  touch.Begin(1280,1585,2);
  Check(touch.Move(1300,1582,2)==PillGesture.Intent.Native,"small sideways drift stays native");
  Check(touch.Move(1330,1583,2)==PillGesture.Intent.Horizontal,"sideways swipe commits");
  Check(CarouselMotion.QuickTarget(2,1380-touch.StartX,0,2,5)==1,"right goes to older app");
  Check(CarouselMotion.QuickTarget(2,1282-touch.StartX,0,2,5)==2,"drag returned to origin does not switch");
  touch.Begin(800,2500,3);
  Check(touch.Move(750,2498,2)==PillGesture.Intent.Horizontal&&CarouselMotion.QuickTarget(2,700-touch.StartX,0,2,5)==3,"left goes to newer app in portrait");
  touch.Begin(800,2500,4);
  Check(touch.Move(803,2490,2)==PillGesture.Intent.Native,"portrait tap has same tolerance");
  pair.Reset();
  pair.Update(fingers,800,2500,true,140);
  pair.Update(fingers,800,2330,true,140);
  Check(pair.Intent==BottomPairGesture.Direction.Up,"portrait long two-finger upward swipe opens overview");
  double slow=Finish(.6),fast=Finish(6),back=Finish(-6);Check(fast>slow+1,"strong flick skips multiple cards");Check(back<4,"opposite flick travels backward through multiple cards");
  var boundary=new CarouselInertia();boundary.Begin(1,-12);while(boundary.Active)boundary.Step(.008,8);Check(boundary.Value==0,"bounded flick stops at first card");
  Check(CarouselInertia.Destination(0,30,200)>CarouselInertia.Destination(0,20,200)+40,"faster throws retain their strength above previous speed limit");
  var whole=new CarouselInertia();whole.Begin(0,30);int steps=0;while(whole.Active&&steps++<2000)whole.Step(.008,100);Check(whole.Value==99&&!whole.Active,"one strong throw can traverse the complete application list");
  var live=new Rectangle(3,5,2554,1498);
  var thumbnail=new Rectangle(282,176,1996,1200);
  Check(WindowMorph.OverviewFrame(live,thumbnail,0,true)==live&&WindowMorph.OverviewFrame(live,thumbnail,1,true)==thumbnail,"Animation Plus preserves exact live and overview geometry");
  var standard=WindowMorph.OverviewFrame(live,thumbnail,.5,false);
  var animated=WindowMorph.OverviewFrame(live,thumbnail,.5,true);
  Check(animated.Width<standard.Width&&animated.Height>standard.Height,"Animation Plus varies both axes while keeping the handoff endpoints");
  Console.WriteLine(checks+" tap discrimination and continuous flick checks PASS");
 }
}
