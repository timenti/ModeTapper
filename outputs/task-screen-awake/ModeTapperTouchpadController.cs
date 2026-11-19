using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class EdgeToast : Form {
 [DllImport("user32.dll")]static extern uint GetDpiForSystem();
 readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();readonly Font font;
 readonly Stopwatch time=new Stopwatch();
 readonly float scale;int level;
 int restTop;
 PadEdge control;
 internal EdgeToast(){
  scale=GetDpiForSystem()/96f;
  AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
  StartPosition=FormStartPosition.Manual;
  BackColor=Color.FromArgb(28,28,28);DoubleBuffered=true;
  font=new Font("Segoe UI",13*scale,FontStyle.Regular,GraphicsUnit.Pixel);
  ClientSize=new Size(S(214),S(37));
  using(var shape=TrayApp.RoundRect(new RectangleF(0,0,Width,Height),9*scale))Region=new Region(shape);
  timer.Interval=16;timer.Tick+=(s,e)=>UpdatePresentation();
 }
 int S(float value){return (int)Math.Round(value*scale);}
 protected override bool ShowWithoutActivation {get{return true;}}
 protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x080800a0;return p;}}
 double entrance;
 internal void Display(PadEdge edge,int value,Screen screen=null){
  control=edge;level=Math.Max(0,Math.Min(100,value));
  AccessibleName=edge==PadEdge.Keyboard?"Подсветка клавиатуры":edge==PadEdge.Screen?"Яркость экрана":"Громкость";
  AccessibleDescription=level+"%";
  var area=(screen??Screen.FromPoint(Cursor.Position)).Bounds;
  Left=area.Left+(area.Width-Width)/2;restTop=area.Top+S(10);
  entrance=Visible?Opacity/.84:0;
  time.Restart();UpdatePresentation();
  Invalidate();if(!Visible)Show();timer.Start();
 }
 void UpdatePresentation(){
  const double duration=1.15;
  double elapsed=time.Elapsed.TotalSeconds;
  if(elapsed>=duration){timer.Stop();Hide();return;}
  double amount=LumaFlyout.AnimationsEnabled()?Math.Min(1,Math.Min(entrance+elapsed/.14,(duration-elapsed)/.20)):1;
  Top=restTop-(int)(S(8)*(1-amount)*(1-amount));
  Opacity=.84*amount;
 }
 protected override void OnPaint(PaintEventArgs e){
  base.OnPaint(e);
  var g=e.Graphics;
  g.SmoothingMode=SmoothingMode.AntiAlias;
  var glyph=new Rectangle(S(14),(Height-S(18))/2,S(18),S(18));
  DrawLevelIcon(g,glyph,control,level);
  int start=S(44),end=Width-S(44),y=Height/2;
  int position=start+(int)Math.Round((end-start)*level/100.0);
  using(var track=new Pen(Color.FromArgb(100,100,103),S(3)){StartCap=LineCap.Round,EndCap=LineCap.Round})
  using(var fill=new Pen(Color.White,S(3)){StartCap=LineCap.Round,EndCap=LineCap.Round}){
   g.DrawLine(track,start,y,end,y);
   if(position>start)g.DrawLine(fill,start,y,position,y);
  }
  TextRenderer.DrawText(g,level.ToString(),font,new Rectangle(Width-S(38),0,S(24),Height),
   Color.FromArgb(246,246,248),TextFormatFlags.NoPadding|TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
 }
 static void DrawLevelIcon(Graphics graphics,Rectangle area,PadEdge edge,int value){
  var saved=graphics.Save();
  graphics.TranslateTransform(area.Left,area.Top);
  graphics.ScaleTransform(area.Width/24f,area.Height/24f);
  using(var pen=new Pen(Color.FromArgb(232,231,237),1.65f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}){
   if(edge==PadEdge.Screen){
    graphics.DrawRectangle(pen,2.5f,4.5f,19,12.5f);
    graphics.DrawLine(pen,12,17,12,21);
    graphics.DrawLine(pen,8,21,16,21);
   }else if(edge==PadEdge.Keyboard){
    graphics.DrawRectangle(pen,2,6,20,12);
    for(int i=0;i<5;i++){
     graphics.DrawLine(pen,5+i*3,10,6+i*3,10);
     graphics.DrawLine(pen,5+i*3,13,6+i*3,13);
    }
    graphics.DrawLine(pen,8,15.5f,16,15.5f);
   }else{
    var speaker=new[]{new PointF(2,9),new PointF(5,9),new PointF(10,5),new PointF(10,19),new PointF(5,15),new PointF(2,15)};
    graphics.DrawPolygon(pen,speaker);
    if(value==0){graphics.DrawLine(pen,14,9,20,15);graphics.DrawLine(pen,20,9,14,15);}
    else{
     int waves=1+(value>33?1:0)+(value>66?1:0);
     for(int i=0;i<waves;i++){
      float radius=5+i*3.2f;
      graphics.DrawArc(pen,10-radius,12-radius,radius*2,radius*2,-48,96);
     }
    }
   }
  }
  graphics.Restore(saved);
 }
 protected override void Dispose(bool disposing){if(disposing){timer.Dispose();font.Dispose();}base.Dispose(disposing);}
}
internal sealed class EdgeActuator : IDisposable {
 sealed class Request {internal int Id;internal PadEdge Edge;internal double Delta;}
 readonly object sync=new object();readonly AutoResetEvent signal=new AutoResetEvent(false);readonly Thread worker;readonly KeyboardLight keyboard;
 Request pending;volatile bool ending;internal event Action<PadEdge,int> Updated;internal event Action<int,string> Failed;
 internal EdgeActuator(string folder){keyboard=new KeyboardLight(folder);worker=new Thread(Run){IsBackground=true,Name="ModeTapper device controls"};worker.Start();}
 internal void Schedule(int id,PadEdge edge,double delta){lock(sync){if(ending)return;pending=new Request{Id=id,Edge=edge,Delta=delta};}try{signal.Set();}catch(ObjectDisposedException){}}
 void Run(){DisplayLight display=null;SoundLevel sound=null;int currentId=-1,failedId=-1,baseline=0,lastValue=-1;long lastSent=0;
  try{while(!ending){signal.WaitOne();if(ending)break;Request request;lock(sync){request=pending;pending=null;}if(request==null)continue;
   int wait=(int)(35-(Stopwatch.GetTimestamp()-lastSent)*1000.0/Stopwatch.Frequency);if(wait>0){Thread.Sleep(wait);lock(sync){if(pending!=null){request=pending;pending=null;}}}if(ending)break;
   if(request.Id==failedId)continue;
   try{
    if(request.Id!=currentId){
     if(display!=null){display.Dispose();display=null;}
     if(sound!=null){sound.Dispose();sound=null;}
     currentId=request.Id;lastValue=-1;
     if(request.Edge==PadEdge.Screen){display=new DisplayLight();baseline=DisplayLight.Get();}
     else if(request.Edge==PadEdge.Volume){sound=new SoundLevel();baseline=sound.Get();}
     else baseline=keyboard.BeginGesture();
    }
    if(ending)break;
    int value=Math.Max(0,Math.Min(100,(int)Math.Round(baseline+request.Delta*(request.Edge==PadEdge.Volume?1.25:2))));
    if(value==lastValue)continue;
    if(value!=baseline||lastValue>=0){
     if(request.Edge==PadEdge.Keyboard)keyboard.Set(value);
     else if(request.Edge==PadEdge.Screen)display.Set(value);
     else sound.Set(value,value>lastValue&&value>baseline);
    }
    lastValue=value;
    if(!ending&&Updated!=null)Updated(request.Edge,value);
   }catch(Exception ex){
    failedId=request.Id;
    if(!ending&&Failed!=null)Failed(request.Id,ex.Message);
   }finally{lastSent=Stopwatch.GetTimestamp();}
  }}finally{if(display!=null)display.Dispose();if(sound!=null)sound.Dispose();signal.Dispose();}
 }
 public void Dispose(){lock(sync){if(ending)return;ending=true;pending=null;try{signal.Set();}catch(ObjectDisposedException){}}if(worker.IsAlive)worker.Join(500);}
}
internal sealed class SmartTouchpad : IDisposable {
 readonly System.Windows.Forms.Timer statusTimer=new System.Windows.Forms.Timer{Interval=250};
 readonly System.Windows.Forms.Timer photoWheelTimer=new System.Windows.Forms.Timer{Interval=15};
 readonly System.Windows.Forms.Timer photoLimitTimer=new System.Windows.Forms.Timer{Interval=170};
 readonly string folder;readonly Control dispatcher;readonly HomeIndicator home;readonly EdgeToast toast=new EdgeToast();readonly EdgeGesture engine=new EdgeGesture();readonly JavaScriptSerializer json=new JavaScriptSerializer();
 readonly TouchpadPinch pinch=new TouchpadPinch();readonly ScreenLens lens=new ScreenLens();
 readonly ScreenLens enlargedPhoto=new ScreenLens(true);
 readonly PhotoZoomSteps photoSteps=new PhotoZoomSteps();
 readonly PhotoZoomLimit photoLimit=new PhotoZoomLimit();
 readonly TelegramPhotoZoom photo=new TelegramPhotoZoom();
 readonly Action<double> drawZoom;
 readonly Action<double,double> movePhoto;
 readonly Func<int,bool> stepPhoto;
 bool zoomPair,zoomFallback,zoomReported,zoomOwned,bottomZoomPair;
 bool photoContact,photoPairSeen,photoWheelPath;
 long photoContactAt,queuedWheelUntil;
 double firstPhotoX,firstPhotoY;
 int queuedWheelDirection;
 int firstZoomFinger,secondZoomFinger;
 IntPtr photoViewer,coveredPhoto,queuedWheelWindow,photoLimitWindow;
 RawTouchpad input;GestureMouseGate gate;EdgeActuator actuator;bool ending;int gestureId,gestures;string lastControl="";int lastValue=-1;
 internal bool Enabled {get;private set;}internal string Error {get;private set;}
 internal bool ZoomEnabled {get;private set;}
 string ZoomPreference {get{return Path.Combine(folder,"touchpad-zoom-enabled.txt");}}
 internal event Action Changed;
 internal SmartTouchpad(string directory,Control owner,HomeIndicator indicator,Action hidePanel){
  folder=directory;dispatcher=owner;home=indicator;Error="";
  drawZoom=DrawZoom;
  movePhoto=MovePhoto;
  stepPhoto=StepPhoto;
  statusTimer.Tick+=(s,e)=>{statusTimer.Stop();WriteStatus();};
  photoWheelTimer.Tick+=(s,e)=>FinishPhotoWheel();
  photoLimitTimer.Tick+=(s,e)=>CheckPhotoLimit();
  engine.Motion+=(id,edge,delta)=>{if(id!=gestureId){gestureId=id;gestures++;}if(actuator!=null)actuator.Schedule(id,edge,delta);};
  engine.Slide+=(phase,dx,width)=>{
   if(phase==PadSlidePhase.Cancel){home.SlideFromPad(phase,dx,width);return;}
   if(!Enabled||ending||Safety.Safe)return;
   home.SlideFromPad(phase,dx,width);
   if(phase==PadSlidePhase.Release){lastControl="SwitchApp";lastValue=-1;gestures++;WriteStatus();}
  };
  engine.OverviewDrag+=(phase,up,height)=>{
   if(phase==PadSlidePhase.Cancel){home.DragOverviewFromPad(phase,up,height);return;}
   if(!Enabled||ending||Safety.Safe)return;
   hidePanel();
   home.DragOverviewFromPad(phase,up,height);
   if(phase==PadSlidePhase.Release){lastControl="Overview";lastValue=-1;gestures++;WriteStatus();}
  };
  bool on=!File.Exists(Path.Combine(folder,"touchpad-enabled.txt"))||File.ReadAllText(Path.Combine(folder,"touchpad-enabled.txt")).Trim()=="1";SetEnabled(on);
  try{if(File.Exists(ZoomPreference)&&File.ReadAllText(ZoomPreference).Trim()=="1")SetZoom(true);}
  catch(Exception ex){Error=ex.Message;WriteStatus();}
 }
 void Post(Action action){if(!ending&&!dispatcher.IsDisposed)try{dispatcher.BeginInvoke(action);}catch(InvalidOperationException){}}
 void OpenReader(){
  if(input!=null)return;
  input=new RawTouchpad(5,true);
  input.Failed+=ReportGestureFailure;
  input.Frame+=ProcessFrame;
 }
 void CloseUnusedReader(){
  if(Enabled||ZoomEnabled)return;
  if(gate!=null){gate.Dispose();gate=null;}
  if(input!=null){input.Dispose();input=null;}
 }
 void EnsureGate(){
  if(gate==null)gate=new GestureMouseGate(engine,observeLeftButton:ObservePhotoPress,suppressWheel:photo.BlocksWheel,
   reserveExtraTarget:photo.TargetRequested,cancelExtraTarget:photo.ReleaseTarget);
 }
 void ObservePhotoPress(Point point,bool down,bool touch){
  IntPtr window=photo.ZoomWindow;
  if(!down||window==IntPtr.Zero||DesktopMotionNative.Foreground()!=window)return;
  photo.ReleaseTarget();
  Post(()=>{
   photo.EndPan();ResetZoomGesture();photoContact=true;ClearPhotoWheel();
   photoLimitTimer.Stop();photoLimit.Reset();
   if(!TelegramPhotoZoom.IsMediaPoint(window,point))enlargedPhoto.Hide();
   if(gate!=null)gate.SyncPadTarget();
  });
 }
 internal void SetEnabled(bool value){
  if(ending)return;StopEdges();Error="";
  if(value)try{
   OpenReader();engine.Reset();actuator=new EdgeActuator(folder);
   var currentActuator=actuator;
   actuator.Updated+=(edge,level)=>Post(()=>{if(!Enabled||actuator!=currentActuator)return;Error="";lastControl=edge.ToString();lastValue=level;toast.Display(edge,level);statusTimer.Stop();statusTimer.Start();});
   actuator.Failed+=(id,message)=>Post(()=>{if(Enabled&&actuator==currentActuator&&gestureId==id)ReportGestureFailure(message);});
   EnsureGate();Enabled=true;
   home.SetRawPadScroll(input.DeviceCount>0);
  }catch(Exception ex){StopEdges();Error=ex.Message;}
  CloseUnusedReader();
  try{File.WriteAllText(Path.Combine(folder,"touchpad-enabled.txt"),Enabled?"1":"0");}catch{}WriteStatus();if(Changed!=null)Changed();
 }
 internal void SetZoom(bool value){
  if(ending||value==ZoomEnabled)return;
  try{
   if(value){
    OpenReader();EnsureGate();Safety.Atomic(ZoomPreference,"1");
    ZoomEnabled=true;
   }else{
    Safety.Atomic(ZoomPreference,"0");StopZoom();
   }
   Error="";
  }finally{CloseUnusedReader();WriteStatus();if(Changed!=null)Changed();}
 }
 void StopZoom(){
  ZoomEnabled=false;ResetZoomGesture();ClearPhotoWheel();photoLimitTimer.Stop();photoLimit.Reset();
  photo.Reset();lens.Hide();enlargedPhoto.Hide();
  if(gate!=null)gate.SyncPadTarget();
 }
 void ResetZoomGesture(){
  EndZoomPair();zoomOwned=photoContact=photoPairSeen=photoWheelPath=false;
  coveredPhoto=IntPtr.Zero;photo.ReleaseTarget();
 }
 void EndZoomPair(){
  zoomPair=zoomFallback=zoomReported=photoWheelPath=bottomZoomPair=false;photoViewer=IntPtr.Zero;
  pinch.Reset();photoSteps.Reset();photo.EndPan();lens.End();enlargedPhoto.End();
 }
 bool UpdateZoom(TouchFrame frame){
  if(!frame.Pressed){ResetZoomGesture();return false;}
  UpdatePhotoTarget(frame);
  if(frame.Count==1&&frame.SampledContacts==1){
   if(zoomPair)EndZoomPair();
   return zoomOwned;
  }
  if(frame.Count==2&&frame.SampledContacts==2&&frame.Confident){
   int first=Math.Min(frame.FirstContactId,frame.SecondContactId);
   int second=Math.Max(frame.FirstContactId,frame.SecondContactId);
   if(!zoomPair||first!=firstZoomFinger||second!=secondZoomFinger){
    EndZoomPair();zoomPair=true;
    firstZoomFinger=first;secondZoomFinger=second;
    bottomZoomPair=Enabled&&EdgeGesture.PairStartsAtBottom(frame);
    Point point=Cursor.Position;
    photoViewer=coveredPhoto!=IntPtr.Zero?coveredPhoto:TelegramPhotoZoom.FindViewer(point);
    zoomFallback=photoViewer==IntPtr.Zero&&!ScreenLens.SystemMagnifierActive&&!ScreenLens.UsesNativeZoom(point);
    if(zoomFallback)lens.Begin(point);else lens.Hide();
    if(enlargedPhoto.IsPhoto(photoViewer))enlargedPhoto.BeginPhoto(photoViewer,point);
   }
  }
  bool panAllowed=photo.CanPan(photoViewer);
  if(panAllowed)photo.GuardWheel(photoViewer);
  if(frame.ButtonDown||frame.Count>2)photo.EndPan();
  if(bottomZoomPair&&engine.WindowGestureClaimed)return false;
  if((zoomFallback||photoViewer!=IntPtr.Zero)&&pinch.Update(frame,drawZoom,panAllowed&&!bottomZoomPair?movePhoto:null))zoomOwned=true;
  return zoomOwned;
 }
 void UpdatePhotoTarget(TouchFrame frame){
  long now=Stopwatch.GetTimestamp();
  if(!photoContact){
   photoContact=true;photoContactAt=now;firstPhotoX=frame.X;firstPhotoY=frame.Y;
   IntPtr window=TelegramPhotoZoom.FindViewer(Cursor.Position);
   if(photo.CanPan(window))coveredPhoto=window;
  }
  if(coveredPhoto==IntPtr.Zero||photoWheelPath)return;
  if(frame.ButtonDown||frame.Count>2||DesktopMotionNative.Foreground()!=coveredPhoto||
     !TelegramPhotoZoom.IsMediaPoint(coveredPhoto,Cursor.Position)||
     (GestureMouseGate.GetAsyncKeyState(27)&0x8000)!=0){
   coveredPhoto=IntPtr.Zero;photo.ReleaseTarget();return;
  }
  if(frame.Count==2){
   photoPairSeen=true;
   photo.GuardWheel(coveredPhoto);
   photo.ReserveTarget(coveredPhoto,now+Stopwatch.Frequency*150/1000);
  }else if(!photoPairSeen&&Math.Abs(frame.X-firstPhotoX)<=2.5&&Math.Abs(frame.Y-firstPhotoY)<=2.5){
   // Windows can choose the destination while only the first finger is down.
   photo.ReserveTarget(coveredPhoto,photoContactAt+Stopwatch.Frequency/4);
  }else photo.ReleaseTarget();
 }
 void DrawZoom(double ratio){
  if(photoViewer!=IntPtr.Zero){
   if(enlargedPhoto.IsPhoto(photoViewer)){
    enlargedPhoto.Zoom(ratio);
    if(!enlargedPhoto.Visible){enlargedPhoto.Hide();pinch.Reset();photoSteps.Reset();}
    return;
   }
   if(!photoWheelPath){photoWheelPath=true;photo.ReleaseTarget();engine.Reset();gate.SyncPadTarget();}
   photoSteps.Update(ratio,Stopwatch.GetTimestamp(),stepPhoto);
   return;
  }
  lens.Zoom(ratio);
  if(!zoomReported&&lens.Visible){
   zoomReported=true;lastControl="LensZoom";lastValue=-1;gestures++;statusTimer.Start();
  }
 }
 bool StepPhoto(int direction){
  photo.GuardWheel(photoViewer);
  if(gate.PadTargetVisible){
   queuedWheelWindow=photoViewer;queuedWheelDirection=direction;
   queuedWheelUntil=Stopwatch.GetTimestamp()+Stopwatch.Frequency/8;
   photoWheelTimer.Start();return true;
  }
  return SendPhotoWheel(photoViewer,direction);
 }
 void FinishPhotoWheel(){
  if(Stopwatch.GetTimestamp()>=queuedWheelUntil||DesktopMotionNative.Foreground()!=queuedWheelWindow||
     (GestureMouseGate.GetAsyncKeyState(27)&0x8000)!=0){ClearPhotoWheel();return;}
  if(gate.PadTargetVisible){gate.SyncPadTarget();return;}
  IntPtr window=queuedWheelWindow;int direction=queuedWheelDirection;
  ClearPhotoWheel();SendPhotoWheel(window,direction);
 }
 void ClearPhotoWheel(){photoWheelTimer.Stop();queuedWheelWindow=IntPtr.Zero;}
 bool SendPhotoWheel(IntPtr window,int direction){
  var before=direction>0?TelegramPhotoZoom.ReadPhotoScene(window):null;
  if(direction>0&&photoLimit.Confirmed(window,before)&&StartExtraZoom(window))return true;
  if(!photo.Step(window,direction))return false;
  photoLimitTimer.Stop();
  if(direction<0)photoLimit.Reset();
  else if(before.HasValue){photoLimit.Outward(window,before);photoLimitWindow=window;photoLimitTimer.Start();}
  if(!zoomReported){zoomReported=true;gestures++;}
  lastControl="PhotoZoom";lastValue=direction;statusTimer.Start();
  return true;
 }
 void CheckPhotoLimit(){
  photoLimitTimer.Stop();
  var sample=TelegramPhotoZoom.ReadPhotoScene(photoLimitWindow);
  if(!sample.HasValue){photoLimit.Reset();return;}
  if(photoLimit.Finish(photoLimitWindow,sample))StartExtraZoom(photoLimitWindow);
 }
 bool StartExtraZoom(IntPtr window){
  if(Safety.Safe||DesktopMotionNative.Foreground()!=window||
     (GestureMouseGate.GetAsyncKeyState(27)&0x8000)!=0||
     !enlargedPhoto.BeginPhoto(window,Cursor.Position))return false;
  enlargedPhoto.Zoom(1.25);
  if(!enlargedPhoto.Visible)return false;
  if(photoViewer!=window)enlargedPhoto.End();
  else{enlargedPhoto.BeginPhoto(window,Cursor.Position);pinch.Reset();}
  lastControl="PhotoExtraZoom";lastValue=125;statusTimer.Start();
  return true;
 }
 void MovePhoto(double x,double y){
  photoLimitTimer.Stop();
  if(enlargedPhoto.IsPhoto(photoViewer)){enlargedPhoto.MovePhoto(x,y);return;}
  photoLimit.Reset();
  if(photo.Pan(photoViewer,x,y)&&!zoomReported){
   zoomReported=true;gestures++;lastControl="PhotoPan";lastValue=-1;statusTimer.Start();
  }
 }
 void ProcessFrame(TouchFrame frame){
  try{
   if(Safety.Safe){if(ZoomEnabled)StopZoom();return;}
   if(ZoomEnabled){
    if(home.CarouselVisible){ResetZoomGesture();ClearPhotoWheel();photoLimitTimer.Stop();lens.Hide();enlargedPhoto.Hide();}
    else if(UpdateZoom(frame)){if(Enabled)engine.Reset();return;}
   }
   if(!Enabled)return;
   home.SetRawPadScroll(input.DeviceCount>0);
   if(!engine.FollowingOverview&&home.ScrollFromPad(frame)){engine.Reset();return;}
   if((GestureMouseGate.GetAsyncKeyState(27)&0x8000)!=0)engine.EmergencyCancel();
   engine.Update(frame,Stopwatch.GetTimestamp());
  }finally{
   if(gate!=null)gate.SyncPadTarget();
  }
 }
 void ReportGestureFailure(string message){
  if(ending||!Enabled&&!ZoomEnabled)return;
  ResetZoomGesture();ClearPhotoWheel();photoLimitTimer.Stop();engine.EmergencyCancel();toast.Hide();Error=message;WriteStatus();
  if(Changed!=null)Changed();
 }
 void StopEdges(){statusTimer.Stop();Enabled=false;engine.EmergencyCancel();if(actuator!=null){actuator.Dispose();actuator=null;}toast.Hide();engine.Reset();if(gate!=null)gate.SyncPadTarget();home.SetRawPadScroll(false);}
 void WriteStatus(){try{File.WriteAllText(Path.Combine(folder,"touchpad-status.json"),json.Serialize(new{enabled=Enabled,zoomEnabled=ZoomEnabled,error=Error,gestures,lastControl,lastValue,padPointerMessages=gate==null?0:gate.PadPointerMessages,horizontalWheelBlocks=gate==null?0:gate.HorizontalWheelBlocks,pid=Process.GetCurrentProcess().Id,updatedUtc=DateTime.UtcNow.ToString("o")}));}catch{}}
 public void Dispose(){
  if(ending)return;ending=true;
  try{StopZoom();}
  finally{ZoomEnabled=false;StopEdges();CloseUnusedReader();WriteStatus();statusTimer.Dispose();photoWheelTimer.Dispose();photoLimitTimer.Dispose();toast.Dispose();lens.Dispose();enlargedPhoto.Dispose();photo.Close();}
 }
}
