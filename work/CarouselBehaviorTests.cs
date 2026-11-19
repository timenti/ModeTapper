using System;
using System.Drawing;
using System.Windows.Forms;
class CloseTestForm : Form {internal bool CancelClose;internal int Attempts;protected override void OnFormClosing(FormClosingEventArgs e){Attempts++;e.Cancel=CancelClose;base.OnFormClosing(e);}}
class CarouselBehaviorTests {
 static int checks;static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
 static double Travel(double speed){var inertia=new CarouselInertia();inertia.Begin(5,speed);for(int i=0;i<1000&&inertia.Active;i++)inertia.Step(.008,20);return inertia.Value-5;}
 [STAThread]static void Main(){
  double slow=Travel(.6),fast=Travel(6);Check(fast>2,"strong flick travels across multiple cards");Check(fast>slow*5,"flick strength changes travel distance");
  var edge=new CarouselInertia();edge.Begin(3,8);for(int i=0;i<100&&edge.Active;i++)edge.Step(.008,4);Check(!edge.Active&&edge.Value>=3&&edge.Value<3.18,"inertia respects far edge without wrapping");
  Check(CarouselMotion.CloseSwipe(300,0,1600,2),"deliberate long upward drag closes");
  Check(CarouselMotion.CloseSwipe(80,-2200,1600,2),"short fast upward flick closes");
  Check(!CarouselMotion.CloseSwipe(20,-3000,1600,2),"small jitter cannot close");
  Check(!CarouselMotion.CloseSwipe(-100,2200,1600,2),"downward drag does not close");
  using(var catalog=new CarouselCatalog())using(var cancel=new CloseTestForm{CancelClose=true}){IntPtr handle=cancel.Handle;catalog.Apps.Add(new CarouselApp{Window=handle,Title="Luma test only"});catalog.RequestClose(0);Application.DoEvents();Check(cancel.Attempts==1&&!cancel.IsDisposed,"normal close respects application cancellation");cancel.CancelClose=false;catalog.RequestClose(0);Application.DoEvents();Check(cancel.IsDisposed,"normal close finishes when application permits");}
  Console.WriteLine(checks+" inertia and native close checks PASS");
 }
}
