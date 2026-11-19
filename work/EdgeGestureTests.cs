using System;
using System.Diagnostics;
class EdgeGestureTests {
 static int checks;static long time;static int hits;static PadEdge edge;static double movement;
 static TouchFrame Frame(double x,double y,int count=1,bool confident=true,bool click=false){return new TouchFrame{X=x,Y=y,Width=125.2,Height=69.4,Count=count,Pressed=count>0,Id=1,Confident=confident,ButtonDown=click};}
 static TouchFrame Pair(double x,double y,bool sideBySide=true){var f=Frame(x,y,2);f.SampledContacts=2;f.CenterX=x;f.CenterY=y;f.SpanX=sideBySide?25:2;f.SpanY=sideBySide?2:20;return f;}
 static void Send(EdgeGesture g,TouchFrame f,int ms){time+=Stopwatch.Frequency*ms/1000;g.Update(f,time);}
 static void Clear(EdgeGesture g){g.Reset();hits=0;time=Stopwatch.GetTimestamp();}
 static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
 static void Main(){var g=new EdgeGesture();g.Motion+=(id,e,delta)=>{hits++;edge=e;movement=delta;};
  Clear(g);Send(g,Frame(60,40),0);Send(g,Frame(124,35),90);Send(g,Frame(124,20),90);Check(hits==0&&!g.Suppressing,"center-to-edge must remain normal");
  Clear(g);Send(g,Frame(124,45),0);Send(g,Frame(124,38),70);Send(g,Frame(124,28),70);Check(hits==2&&edge==PadEdge.Screen&&movement==10,"right edge screen");Send(g,Frame(0,0,0),1);Check(!g.Suppressing,"finger lift releases cursor");
  Clear(g);Send(g,Frame(2,45),0);Send(g,Frame(2,38),70);Send(g,Frame(2,28),70);Check(hits==2&&edge==PadEdge.Keyboard,"left edge keyboard");
  Clear(g);Send(g,Frame(45,2),0);Send(g,Frame(52,2),70);Send(g,Frame(62,2),70);Check(hits==2&&edge==PadEdge.Volume&&movement==10,"top edge volume");
  Clear(g);Send(g,Frame(124,45),0);Send(g,Frame(124,38,2),70);Send(g,Frame(124,28),70);Check(hits==0&&!g.Suppressing,"second finger cancels until lift");
  Clear(g);Send(g,Frame(124,45),0);Send(g,Frame(118,38),70);Send(g,Frame(124,28),70);Check(hits==0&&!g.Suppressing,"sideways exit cancels");
  Clear(g);Send(g,Frame(124,45,1,false),0);Send(g,Frame(124,28),100);Check(hits==0,"palm rejected");
  Clear(g);Send(g,Frame(124,45,1,true,true),0);Send(g,Frame(124,28),100);Check(hits==0,"physical click rejected");
  Clear(g);Send(g,Frame(124,45),0);Send(g,Frame(124,43),1300);Send(g,Frame(124,28),100);Check(hits==0,"resting finger does not arm later");
  Clear(g);Send(g,Frame(125.2,0),0);Send(g,Frame(125.2,7),70);Send(g,Frame(125.2,17),70);Check(hits==2&&edge==PadEdge.Screen&&movement==-10,"exact top-right down selects screen");
  Clear(g);Send(g,Frame(125.2,0),0);Send(g,Frame(118.2,0),70);Send(g,Frame(108.2,0),70);Check(hits==2&&edge==PadEdge.Volume&&Math.Abs(movement+10)<.001,"exact top-right left selects volume");
  Clear(g);Send(g,Frame(0,0),0);Send(g,Frame(0,7),70);Send(g,Frame(0,17),70);Check(hits==2&&edge==PadEdge.Keyboard&&movement==-10,"exact top-left down selects keyboard");
  Clear(g);Send(g,Frame(0,0),0);Send(g,Frame(7,0),70);Send(g,Frame(17,0),70);Check(hits==2&&edge==PadEdge.Volume&&movement==10,"exact top-left right selects volume");
  Clear(g);Send(g,Frame(0,69.4),0);Send(g,Frame(0,62.4),70);Send(g,Frame(0,52.4),70);Check(hits==2&&edge==PadEdge.Keyboard&&Math.Abs(movement-10)<.001,"exact bottom-left up selects keyboard");
  Clear(g);Send(g,Frame(125.2,69.4),0);Send(g,Frame(125.2,62.4),70);Send(g,Frame(125.2,52.4),70);Check(hits==2&&edge==PadEdge.Screen&&Math.Abs(movement-10)<.001,"exact bottom-right up selects screen");
  Clear(g);Send(g,Frame(0,0),0);Send(g,Frame(4,4),70);Send(g,Frame(8,8),70);Check(hits==0&&!g.Suppressing,"ambiguous diagonal corner swipe rejected");
  Clear(g);Send(g,Frame(125.2,0),0);Send(g,Frame(125.2,7),70);Send(g,Frame(125.2,17),70);Send(g,Frame(120,0),70);Check(edge==PadEdge.Screen,"gesture direction remains locked until lift");
  Clear(g);Send(g,Frame(124,45),0);g.EmergencyCancel();Send(g,Frame(124,28),100);Check(hits==0&&!g.Suppressing,"escape releases cursor");
  Clear(g);Send(g,Frame(35,7),0);Send(g,Frame(42,8),70);Send(g,Frame(52,13,1,false),70);Send(g,Frame(59,15),70);
  Check(hits==2&&edge==PadEdge.Volume&&movement==17,"top edge stays tracked after drift and an uncertain sample");
  Clear(g);Send(g,Frame(3,45),0);Send(g,Frame(4,38),70);Send(g,Frame(13,28),70);
  Check(hits==2&&edge==PadEdge.Keyboard&&movement==10,"engaged keyboard backlight continues past the start zone");
  Clear(g);Send(g,Frame(123,45),0);Send(g,Frame(121,38),70);Send(g,Frame(113,28),70);
  Check(hits==2&&edge==PadEdge.Screen&&movement==10,"engaged screen brightness continues past the start zone");
  int switches=0;g.Slide+=(phase,x,width)=>{if(phase==PadSlidePhase.Release)switches++;};
  Clear(g);Send(g,Frame(45,66),0);
  Check(g.ReservePadTarget&&!g.BlocksHorizontalWheel&&!g.Suppressing,"first bottom contact protects native routing without swallowing cursor or wheel input");
  var incomplete=Pair(45,66);incomplete.SampledContacts=1;Send(g,incomplete,16);
  Check(g.ReservePadTarget&&!g.BlocksHorizontalWheel,"partial second contact keeps the early reservation");
  Send(g,Pair(45,66),16);Send(g,Pair(57,66),32);Send(g,Frame(0,0,0),16);
  Check(switches==1&&!g.ReservePadTarget&&g.BlocksHorizontalWheel,"staggered pair commits and releases its native route");
  Clear(g);Send(g,Frame(45,66),0);Send(g,Frame(49,66),30);
  Check(!g.ReservePadTarget&&!g.BlocksHorizontalWheel,"one-finger motion at the bottom releases the tentative reservation");
  Clear(g);Send(g,Frame(45,38),0);
  Check(!g.ReservePadTarget&&!g.BlocksHorizontalWheel,"first contact away from the bottom is never reserved");
  switches=0;
  Clear(g);Send(g,Pair(45,66),0);
  Check(g.ReservePadTarget&&g.BlocksHorizontalWheel,"bottom pair reserves horizontal input before Windows starts scrolling");
  Send(g,Pair(57,66),60);Send(g,Frame(0,0,0),16);
  Check(switches==1&&!g.ReservePadTarget&&g.BlocksHorizontalWheel,"committed swipe keeps the wheel guard during inertia and releases the pointer target");
  Send(g,Pair(45,38),50);Send(g,Pair(57,38),60);
  Check(!g.ReservePadTarget&&!g.BlocksHorizontalWheel,"new center scroll goes straight to the active application");
  Clear(g);Send(g,Pair(45,66),0);Send(g,Pair(45,58),55);
  Check(!g.ReservePadTarget&&!g.BlocksHorizontalWheel,"upward gesture clears horizontal reservation before it opens the overview");
  Clear(g);Send(g,Pair(45,66,false),0);Send(g,Pair(57,66,false),55);
  Check(!g.ReservePadTarget&&!g.BlocksHorizontalWheel&&switches==1,"stacked fingers cannot claim a horizontal edge swipe");
  Clear(g);Send(g,Pair(45,66),0);g.EmergencyCancel();
  Check(!g.ReservePadTarget&&!g.BlocksHorizontalWheel,"emergency cancellation releases horizontal input immediately");
  Console.WriteLine(checks+" gesture and accidental activation checks PASS");

 }
}
