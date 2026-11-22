using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class HomeNativeNavigation {
 [DllImport("user32.dll")]internal static extern IntPtr GetForegroundWindow();
 [StructLayout(LayoutKind.Sequential)]struct Key {public ushort Virtual,Scan;public uint Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Explicit)]struct InputValue {
  [FieldOffset(0)]public Key Keyboard;
  [FieldOffset(24)]public long PointerSize;
 }
 [StructLayout(LayoutKind.Sequential)]struct Input {public uint Type;public InputValue Value;}
 [DllImport("user32.dll")]static extern uint SendInput(uint count,Input[] inputs,int size);
 internal static bool DismissStart(){
  IntPtr current=GetForegroundWindow();
  try{
   using(var process=Process.GetProcessById((int)DesktopMotionNative.ProcessOf(current)))
    if(!process.ProcessName.Equals("StartMenuExperienceHost",StringComparison.OrdinalIgnoreCase))return false;
  }catch(ArgumentException){return false;}
  catch(InvalidOperationException){return false;}
  if(GetForegroundWindow()!=current)return false;
  var keys=new[]{
   new Input{Type=1,Value=new InputValue{Keyboard=new Key{Virtual=27}}},
   new Input{Type=1,Value=new InputValue{Keyboard=new Key{Virtual=27,Flags=2}}}
  };
  return SendInput(2,keys,Marshal.SizeOf(typeof(Input)))==2;
 }
}
internal static class DisplayOrientation {
 const uint ActivePaths=2, OrientationAndDimensions=0x00180080, PreserveMode=0x00640020, TestMode=2;
 [StructLayout(LayoutKind.Explicit,Size=220)]struct DisplayMode {
  [FieldOffset(68)]public ushort Size;
  [FieldOffset(72)]public uint Fields;
  [FieldOffset(84)]public int Rotation;
  [FieldOffset(172)]public int Width;
  [FieldOffset(176)]public int Height;
 }
 [StructLayout(LayoutKind.Explicit,Size=72)]struct DisplayPath {
  [FieldOffset(36)]public uint Technology;
 }
 [StructLayout(LayoutKind.Sequential,Size=64)]struct PathMode {
  public uint Type;
 }
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool EnumDisplaySettings(string device,int mode,ref DisplayMode settings);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int ChangeDisplaySettingsEx(string device,ref DisplayMode settings,IntPtr window,uint flags,IntPtr data);
 [DllImport("user32.dll")]static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
 [DllImport("user32.dll")]static extern int QueryDisplayConfig(uint flags,ref uint paths,[Out]DisplayPath[] pathArray,
  ref uint modes,[Out]PathMode[] modeArray,IntPtr topology);
 internal static int Read(string device){
  var mode=new DisplayMode{Size=(ushort)Marshal.SizeOf(typeof(DisplayMode))};
  return EnumDisplaySettings(device,-1,ref mode)&&mode.Rotation>=0&&mode.Rotation<=3?mode.Rotation:-1;
 }
 // SimpleOrientationSensor and DEVMODEW both count counterclockwise quarter turns.
 internal static void ApplySensor(int rotation){
  if(rotation<0||rotation>3||!RotationLock.AllowsSensorCorrection()||!SingleInternalDisplay())return;
  string device=Screen.PrimaryScreen.DeviceName;
  var mode=new DisplayMode{Size=(ushort)Marshal.SizeOf(typeof(DisplayMode))};
  if(!EnumDisplaySettings(device,-1,ref mode)||mode.Rotation==rotation||mode.Rotation<0||mode.Rotation>3)return;
  if(((mode.Rotation^rotation)&1)!=0){int width=mode.Width;mode.Width=mode.Height;mode.Height=width;}
  mode.Fields=(mode.Fields&PreserveMode)|OrientationAndDimensions;
  mode.Rotation=rotation;
  if(ChangeDisplaySettingsEx(device,ref mode,IntPtr.Zero,TestMode,IntPtr.Zero)==0)
   ChangeDisplaySettingsEx(device,ref mode,IntPtr.Zero,0,IntPtr.Zero);
 }
 static bool SingleInternalDisplay(){
  uint paths,modes;
  if(GetDisplayConfigBufferSizes(ActivePaths,out paths,out modes)!=0||paths!=1||modes==0)return false;
  var active=new DisplayPath[1];
  if(QueryDisplayConfig(ActivePaths,ref paths,active,ref modes,new PathMode[modes],IntPtr.Zero)!=0||paths!=1)return false;
  uint technology=active[0].Technology;
  return technology==0x80000000||technology==6||technology==11||technology==13;
 }
}
internal sealed class PillGesture {
 internal enum Intent { Native, Horizontal }
 const double MinimumHorizontal=24,DirectionRatio=1.35;
 internal double StartX {get;private set;}
 internal double StartY {get;private set;}
 internal int Finger {get;private set;}
 internal Intent Direction {get;private set;}

 internal void Begin(double x,double y,int finger){StartX=x;StartY=y;Finger=finger;Direction=Intent.Native;}
 internal Intent Move(double x,double y,double scale){
  if(Direction!=Intent.Native)return Direction;
  double dx=Math.Abs(x-StartX)/scale,dy=(StartY-y)/scale;
  if(dx>=MinimumHorizontal&&dx>Math.Abs(dy)*DirectionRatio)Direction=Intent.Horizontal;
  return Direction;
 }
}
internal sealed class HomeIndicator : NativeWindow,IDisposable {
 [DllImport("user32.dll")]static extern uint GetDpiForSystem();
 readonly string folder;readonly Control dispatcher;readonly AppCarousel carousel=new AppCarousel();readonly PillGesture pillGesture=new PillGesture();readonly JavaScriptSerializer json=new JavaScriptSerializer();
 readonly BottomPairGesture twoFingerOverview=new BottomPairGesture();
 readonly Timer posture=new Timer{Interval=250},statusTimer=new Timer{Interval=300};RawTouchpad screen;TabletEdgeControls edges;GestureMouseGate tabletMouse;
 readonly Timer startGuard=new Timer{Interval=20};
 readonly bool isolatedUiTest;readonly TouchCaptionMinimize touchCaption;bool ending,touching;enum Route { None, Carousel, Edge, NativePill, Quick }
 bool observingPanelTouch,panelContactDown;
 Route route;int orientation,gestures;double scale;Rectangle bounds;string lastAction="";long reports;
 bool padSlideStarted,padSlideInOverview;
 double padSlideX;
 enum OverviewContact {None,Waiting,Horizontal,Down,Rejected}
 OverviewContact overviewContact;
 bool pairOpening;
 long tabletGestureUntil;
 long checkStartUntil;
 bool dismissedStart;
 double overviewStartX,overviewStartY;
 internal bool Enabled {get;private set;}internal bool EdgesEnabled {get;private set;}internal bool Active {get;private set;}internal string Error {get;private set;}internal event Action Changed;
 internal bool AnimationPlus {get{return carousel.AnimationPlus;}set{carousel.AnimationPlus=value;}}
 internal bool CarouselVisible {get{return carousel.Busy;}}
 internal event Action<Point> ScreenContactStarted;
 internal HomeIndicator(string directory,Control owner,bool isolated=false){isolatedUiTest=isolated;folder=directory;dispatcher=owner;Error="";CreateHandle(new CreateParams{Caption="ModeTapper posture events",Style=unchecked((int)0x80000000),ExStyle=0x80});posture.Tick+=(s,e)=>{posture.Stop();Refresh();};statusTimer.Tick+=(s,e)=>{statusTimer.Stop();WriteStatus();};startGuard.Tick+=(s,e)=>CheckStart();carousel.Completed+=action=>{gestures++;lastAction=action;Error="";WriteStatus();};carousel.Failed+=message=>{Error=message;WriteStatus();if(Changed!=null)Changed();};Enabled=ReadFlag("home-indicator-enabled.txt",true);EdgesEnabled=!isolatedUiTest&&ReadFlag("screen-edges-enabled.txt",true);if(!isolated){touchCaption=new TouchCaptionMinimize();touchCaption.Handled+=WriteStatus;}Refresh();}
 bool ReadFlag(string file,bool initial){try{return File.Exists(Path.Combine(folder,file))?File.ReadAllText(Path.Combine(folder,file)).Trim()=="1":initial;}catch{return initial;}}
 internal void SetEnabled(bool value){Enabled=value;try{File.WriteAllText(Path.Combine(folder,"home-indicator-enabled.txt"),value?"1":"0");}catch{}if(!value)carousel.CancelImmediate();Refresh();}
 internal void SetEdges(bool value){if(isolatedUiTest)return;EdgesEnabled=value;try{File.WriteAllText(Path.Combine(folder,"screen-edges-enabled.txt"),value?"1":"0");}catch{}Refresh();}
 internal void OpenOverview(bool toggle){
  if(ending)return;
  try{
   if(carousel.Busy){if(toggle)carousel.CancelImmediate();return;}
   if(carousel.OpenOverview())WriteStatus();
  }catch(Exception ex){Error=ex.Message;WriteStatus();}
 }
 internal void SlideFromPad(PadSlidePhase phase,double dx,double padWidth){
  try{
   if(phase==PadSlidePhase.Move){
    if(!padSlideStarted&&!padSlideInOverview){
     padSlideInOverview=carousel.Overview;
     if(!padSlideInOverview){
      carousel.PreferredWindow=HomeNativeNavigation.GetForegroundWindow();
      padSlideStarted=carousel.BeginQuick(0);
     }
    }
    padSlideX=dx;
    if(padSlideStarted)carousel.MoveFinger(dx*bounds.Width*.62/padWidth,0);
    return;
   }
   if(padSlideStarted){if(phase==PadSlidePhase.Release)carousel.EndFinger();else carousel.CancelQuick();}
   else if(padSlideInOverview&&phase==PadSlidePhase.Release)carousel.StepOverview(padSlideX>0?-1:1);
  }catch(Exception ex){Error=ex.Message;carousel.CancelQuick();WriteStatus();}
  finally{if(phase!=PadSlidePhase.Move)padSlideStarted=padSlideInOverview=false;}
 }
 internal void SetRawPadScroll(bool enabled){
  carousel.UseRawPadScroll=enabled;
  if(!enabled&&!carousel.RawScreenTouch)FinishOverviewContact(true);
 }
 internal void DragOverviewFromPad(PadSlidePhase phase,double up,double padHeight){
  if(phase!=PadSlidePhase.Move){
   if(pairOpening)carousel.EndOverviewDrag(phase==PadSlidePhase.Cancel);
   pairOpening=false;
   return;
  }
  if(padHeight<=0)return;
  double progress=up/(padHeight*.40);
  if(!pairOpening)pairOpening=carousel.BeginOverviewDrag(true,progress);
  if(pairOpening)carousel.MoveOverviewDrag(progress);
 }
 internal bool ScrollFromPad(TouchFrame frame){
  if(touching||!carousel.Overview&&overviewContact==OverviewContact.None)return false;
  double x=frame.Width>0?frame.CenterX*bounds.Width*.62/frame.Width:0;
  double y=frame.Height>0?frame.CenterY*OverviewTravel/(frame.Height*.40):0;
  return TrackOverviewContact(frame,x,y);
 }
 double OverviewTravel {get{return Math.Max(145*scale,bounds.Height*.18);}}
 bool TrackOverviewContact(TouchFrame frame,double x,double y){
  if(!frame.Pressed||frame.Count<2){
   bool consumed=overviewContact!=OverviewContact.None;
   FinishOverviewContact(frame.Width<=0);
   if(consumed&&frame.Pressed)overviewContact=OverviewContact.Rejected;
   return consumed;
  }
  if(frame.Count!=2||!frame.Confident||frame.ButtonDown){
   FinishOverviewContact(true);
   overviewContact=OverviewContact.Rejected;
   return true;
  }
  if(frame.SampledContacts!=2||overviewContact==OverviewContact.Rejected)return true;
  if(overviewContact==OverviewContact.None){
   overviewContact=OverviewContact.Waiting;
   overviewStartX=x;overviewStartY=y;
   return true;
  }
  double dx=x-overviewStartX,dy=y-overviewStartY;
  if(overviewContact==OverviewContact.Waiting){
   double threshold=12*scale;
   if(Math.Abs(dy)>=threshold&&Math.Abs(dy)>Math.Abs(dx)*1.35)
    overviewContact=dy>0&&carousel.BeginOverviewDrag(false,dy/OverviewTravel)?OverviewContact.Down:OverviewContact.Rejected;
   else if(Math.Abs(dx)>=threshold&&Math.Abs(dx)>Math.Abs(dy)*1.35)
    overviewContact=carousel.BeginPadScroll()?OverviewContact.Horizontal:OverviewContact.Rejected;
  }
  if(overviewContact==OverviewContact.Down)carousel.MoveOverviewDrag(dy/OverviewTravel);
  else if(overviewContact==OverviewContact.Horizontal)carousel.MovePadScroll(dx);
  return true;
 }
 void FinishOverviewContact(bool cancelled){
  if(overviewContact==OverviewContact.Down)carousel.EndOverviewDrag(cancelled);
  else if(overviewContact==OverviewContact.Horizontal)carousel.EndPadScroll();
  overviewContact=OverviewContact.None;
 }
 void ArmTabletTapGuard(){System.Threading.Interlocked.Exchange(ref tabletGestureUntil,Stopwatch.GetTimestamp()+Stopwatch.Frequency/7);}
 void GuardOverviewStart(bool newGesture=false){
  if(isolatedUiTest)return;
  if(newGesture)dismissedStart=false;
  ArmTabletTapGuard();
  if(dismissedStart)return;
  checkStartUntil=Stopwatch.GetTimestamp()+Stopwatch.Frequency/3;
  startGuard.Start();
  CheckStart();
 }
 void CheckStart(){
  if(!Active||!Enabled||ending||dismissedStart||Stopwatch.GetTimestamp()>=checkStartUntil){
   startGuard.Stop();return;
  }
  if(HomeNativeNavigation.DismissStart()){dismissedStart=true;startGuard.Stop();}
 }
 bool TabletSwipeActive(){return Stopwatch.GetTimestamp()<System.Threading.Interlocked.Read(ref tabletGestureUntil);}
 internal void ObserveCaptionTouch(Point point,bool down){
  if(touchCaption!=null&&!CarouselVisible&&!observingPanelTouch)touchCaption.Promoted(point,down,scale);
 }
 void RefreshTabletTapGuard(){
  if(Active&&Enabled&&!isolatedUiTest){
   if(tabletMouse==null)tabletMouse=new GestureMouseGate(null,TabletSwipeActive);
  }else if(tabletMouse!=null){
   System.Threading.Interlocked.Exchange(ref tabletGestureUntil,0);
   tabletMouse.Dispose();tabletMouse=null;
  }
 }
 internal void ObservePanelTouch(bool enabled){
  if(ending||observingPanelTouch==enabled)return;
  observingPanelTouch=enabled;
  UpdateScreenReader(Active);
 }
 void UpdateScreenReader(bool forGestures){
  if(!ModeTapperDeviceSupport.HasTouchScreen()||!forGestures&&!observingPanelTouch&&touchCaption==null){
   if(screen!=null){screen.Dispose();screen=null;}
   panelContactDown=false;
   return;
  }
  if(screen!=null)return;
  try{
   screen=new RawTouchpad(4,true);
   screen.Frame+=Frame;
   screen.Failed+=message=>Post(()=>{Error=message;carousel.CancelImmediate();WriteStatus();});
  }catch(Exception ex){Error=ex.Message;}
 }
 internal void RefreshCapabilities(){Refresh();}
 void Refresh(){
  if(ending)return;
  bool should=(Enabled||EdgesEnabled)&&ModeTapperDeviceSupport.HasTouchScreen();
  var display=should?ModeTapperDeviceSupport.TouchDisplay():Screen.PrimaryScreen;
  carousel.TargetDisplay=display;
  Rectangle next=display.Bounds;
  bool geometry=next!=bounds;
  bounds=next;scale=GetDpiForSystem()/96.0;
  int rotation=DisplayOrientation.Read(display.DeviceName);
  if(rotation>=0){geometry|=rotation!=orientation;orientation=rotation;}
  if(geometry||Active&&!should){startGuard.Stop();carousel.CancelImmediate();twoFingerOverview.Reset();overviewContact=OverviewContact.None;pairOpening=false;route=Route.None;touching=false;if(edges!=null)edges.Reset();if(touchCaption!=null)touchCaption.Cancel();}
  if(geometry)carousel.PrepareBackdrop();
  Error="";UpdateScreenReader(should);
  if(edges!=null&&(!should||geometry)){edges.Dispose();edges=null;}
  if(should){
   if(EdgesEnabled&&edges==null){edges=new TabletEdgeControls(folder,dispatcher,display);edges.Changed+=()=>{if(edges==null)return;Error=edges.Error;statusTimer.Stop();statusTimer.Start();};}else if(!EdgesEnabled&&edges!=null){edges.Dispose();edges=null;}
  }
  Active=should&&screen!=null&&screen.DeviceCount>0;
  carousel.RawScreenTouch=Active;
  try{RefreshTabletTapGuard();}catch(Exception ex){Error=ex.Message;}
  WriteStatus();if(Changed!=null)Changed();
 }
 void Frame(TouchFrame frame){
  if(!Active&&!observingPanelTouch&&touchCaption==null)return;
  reports++;
  bool pressed=frame.Pressed&&frame.Count>0;
  bool pair=frame.Count==2&&frame.SampledContacts==2;
  double nx=frame.Width>0?(pair?frame.CenterX:frame.X)/frame.Width:0,ny=frame.Height>0?(pair?frame.CenterY:frame.Y)/frame.Height:0;
  double x=nx*bounds.Width,y=ny*bounds.Height;
  if(orientation==1){x=(1-ny)*bounds.Width;y=nx*bounds.Height;}
  else if(orientation==2){x=(1-nx)*bounds.Width;y=(1-ny)*bounds.Height;}
  else if(orientation==3){x=ny*bounds.Width;y=(1-nx)*bounds.Height;}
  bool firstContact=pressed&&!panelContactDown;
  panelContactDown=pressed;
  var point=new Point(bounds.Left+(int)Math.Round(x),bounds.Top+(int)Math.Round(y));
  if(touchCaption!=null){
   if(carousel.Busy||observingPanelTouch)touchCaption.Cancel();
   else touchCaption.Update(frame,point,firstContact,scale);
  }
  if(firstContact&&observingPanelTouch&&frame.Width>0&&frame.Height>0&&ScreenContactStarted!=null)
   ScreenContactStarted(point);
  if(!Active)return;
  try{
   if(twoFingerOverview.InProgress||Enabled&&!carousel.Busy&&pressed&&frame.Count>1){
    if(pairOpening)GuardOverviewStart();
    bool inPillZone=y>=bounds.Height-34*scale&&Math.Abs(x-bounds.Width/2)<=180*scale;
    twoFingerOverview.Update(frame,x,y,inPillZone,62*scale);
    if(!pressed||frame.Count!=2||!frame.Confident||frame.ButtonDown){
     if(pairOpening)carousel.EndOverviewDrag(frame.Width<=0||frame.Count>2||!frame.Confident||frame.ButtonDown);
     pairOpening=false;
    }else if(frame.SampledContacts==2&&twoFingerOverview.Intent==BottomPairGesture.Direction.Up){
     if(!pairOpening){
      carousel.PreferredWindow=HomeNativeNavigation.GetForegroundWindow();
      pairOpening=carousel.BeginOverviewDrag(true,-twoFingerOverview.OffsetY/OverviewTravel,activateOnOpen:true);
      if(pairOpening)GuardOverviewStart(true);
     }
     if(pairOpening){ArmTabletTapGuard();carousel.MoveOverviewDrag(-twoFingerOverview.OffsetY/OverviewTravel);}
    }
    if(edges!=null&&route==Route.Edge)edges.Reset();
    route=Route.None;touching=pressed;
    return;
   }
   if(overviewContact!=OverviewContact.None||carousel.Overview&&pressed&&frame.Count>1){
    if(overviewContact==OverviewContact.Down)ArmTabletTapGuard();
    if(route==Route.Carousel)carousel.AbortOverviewTouch();
    TrackOverviewContact(frame,x,y);
    if(overviewContact==OverviewContact.Down)ArmTabletTapGuard();
    route=Route.None;touching=pressed;
    return;
   }
   if(pressed&&!touching){
    route=Route.None;
    if(carousel.Overview){if(carousel.Begin(x,y))route=Route.Carousel;}
    else if(carousel.CanStartQuick&&Enabled&&frame.Count==1&&y>=bounds.Height-28*scale&&Math.Abs(x-bounds.Width/2)<=180*scale){
     // Observe the physical contact. Windows receives its original input on the taskbar.
     pillGesture.Begin(x,y,frame.Id);carousel.PreferredWindow=HomeNativeNavigation.GetForegroundWindow();route=Route.NativePill;
    }else if(!carousel.Busy&&EdgesEnabled&&edges!=null)route=Route.Edge;
   }
   if(pressed){
    if(route==Route.Quick)ArmTabletTapGuard();
    if(frame.Count!=1||((route==Route.NativePill||route==Route.Quick)&&frame.Id!=pillGesture.Finger)){
     if(route==Route.Carousel)carousel.CancelImmediate();
     if(route==Route.Quick)carousel.CancelQuick();
     if(edges!=null)edges.Reset();route=Route.None;
    }else if(route==Route.NativePill){
     var intent=pillGesture.Move(x,y,scale);
     if(intent==PillGesture.Intent.Horizontal){
      route=carousel.BeginQuick(pillGesture.StartX)?Route.Quick:Route.None;
      if(route==Route.Quick){ArmTabletTapGuard();carousel.MoveFinger(x,0);}
     }
    }else if(route==Route.Quick)carousel.MoveFinger(x,0);
    else if(route==Route.Carousel)carousel.MoveFinger(x,y);
    else if(route==Route.Edge&&edges!=null)edges.Update(true,frame.Count,frame.Id,x,y,bounds.Width,bounds.Height,scale);
   }else if(touching){
    if(route==Route.Quick){ArmTabletTapGuard();if(frame.Width>0)carousel.EndFinger();else carousel.CancelQuick();}
    else if(route==Route.Carousel)carousel.EndFinger();
    else if(route==Route.Edge&&edges!=null)edges.Update(false,0,frame.Id,x,y,bounds.Width,bounds.Height,scale);
    route=Route.None;WriteStatus();
   }
   touching=pressed;
  }catch(Exception ex){Error=ex.Message;route=Route.None;touching=pressed;carousel.CancelImmediate();if(edges!=null)edges.Reset();WriteStatus();if(Changed!=null)Changed();}
 }
 void Post(Action action){if(!ending&&!dispatcher.IsDisposed)try{dispatcher.BeginInvoke(action);}catch(InvalidOperationException){}}
 protected override void WndProc(ref Message m){bool update=m.Msg==0x219||m.Msg==0x7E;if(m.Msg==0x1A&&m.LParam!=IntPtr.Zero){string name=Marshal.PtrToStringUni(m.LParam);update=name=="ConvertibleSlateMode"||name=="SystemDockMode"||name=="UserInteractionMode";}if(update){posture.Stop();posture.Start();}base.WndProc(ref m);}
 void WriteStatus(){try{File.WriteAllText(Path.Combine(folder,"home-indicator-status.json"),json.Serialize(new{enabled=Enabled,screenEdges=EdgesEnabled,active=Active,error=Error,gestures,lastAction,reports,carouselVisible=carousel.Busy,apps=carousel.AppCount,livePreviews=carousel.LastLivePreviews,activationSucceeded=carousel.LastActivationSucceeded,lastInputMode=carousel.LastInputMode,flickPixelsPerSecond=carousel.LastFlickPixels,flickPagesPerSecond=carousel.LastFlickPages,flickStart=carousel.LastFlickStart,flickApps=carousel.LastFlickCount,flickTarget=carousel.LastFlickTarget,taskbarInput="native-pass-through",orientation,edgeControl=edges==null?"":edges.LastControl,edgeValue=edges==null?-1:edges.LastValue,touchCaptionFallbacks=touchCaption==null?0:touchCaption.Fallbacks,pid=Process.GetCurrentProcess().Id,updatedUtc=DateTime.UtcNow.ToString("o")}));}catch{}}
 public void Dispose(){if(ending)return;ending=true;Active=false;RefreshTabletTapGuard();posture.Dispose();statusTimer.Dispose();startGuard.Dispose();carousel.Dispose();if(screen!=null)screen.Dispose();if(edges!=null)edges.Dispose();if(touchCaption!=null)touchCaption.Dispose();WriteStatus();DestroyHandle();}
}
