using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class CarouselMotion {
 internal static double Ease(double progress){double t=Math.Max(0,Math.Min(1,progress));return 1-Math.Pow(1-t,3);}
 internal static double Rubber(double value,int count){double end=Math.Max(0,count-1);if(value<0)return -.18*(1-Math.Exp(value/.35));if(value>end)return end+.18*(1-Math.Exp((end-value)/.35));return value;}
 internal static int Clamp(int value,int count){return Math.Max(0,Math.Min(Math.Max(0,count-1),value));}
 internal static bool CloseSwipe(double up,double velocity,double height,double scale){return up>height*.13||(up>25*scale&&velocity<-650*scale);}
 internal static int QuickTarget(int index,double distance,double velocity,double scale,int count){
  double travel=Math.Abs(distance)/scale;
  bool flick=Math.Sign(distance)==Math.Sign(velocity)&&Math.Abs(velocity)>=330*scale;
  bool commit=travel>=46||travel>=23&&flick;
  return Clamp(index+(commit?(distance>0?-1:1):0),count);
 }
}
// Timestamp a short position history instead of differentiating adjacent HID reports.
// Windows can deliver many reports together, with effectively identical arrival times.
internal sealed class CarouselVelocity {
 readonly double[] times=new double[64],xs=new double[64],ys=new double[64];int first,count;
 double sampleInterval;
 internal void Reset(double time,double x,double y){first=0;count=1;times[0]=time;xs[0]=x;ys[0]=y;sampleInterval=.010;}
 internal void Add(double time,double x,double y){
  int last=(first+count-1)%64;
  if(count>0){
   double interval=time-times[last];
   if(interval<.003){xs[last]=x;ys[last]=y;return;}
   sampleInterval+=(Math.Min(.040,interval)-sampleInterval)*.18;
  }
  int slot=(first+count)%64;
  if(count==64){first=(first+1)%64;slot=(first+count-1)%64;}else count++;
  times[slot]=time;xs[slot]=x;ys[slot]=y;
 }
 internal double InterpolatedX(double now){
  if(count<=1)return xs[first];
  double sample=now-Math.Max(.010,Math.Min(.038,sampleInterval*1.24));
  for(int n=count-1;n>0;n--){
   int upper=(first+n)%64,lower=(first+n-1)%64;
   if(sample>=times[upper])return xs[upper];
   if(sample<times[lower])continue;
   double portion=(sample-times[lower])/(times[upper]-times[lower]);
   return xs[lower]+(xs[upper]-xs[lower])*portion;
  }
  return xs[first];
 }
 internal void Read(double now,out double x,out double y){x=y=0;if(count<2)return;int last=(first+count-1)%64;double age=now-times[last];if(age>.16)return;int begin=first;for(int n=0;n<count-1;n++){int next=(first+n+1)%64;if(times[next]>times[last]-.10)break;begin=next;}double elapsed=times[last]-times[begin];if(elapsed<.012)return;double decay=age>.08?Math.Exp(-(age-.08)/.05):1;x=(xs[last]-xs[begin])/elapsed*decay;y=(ys[last]-ys[begin])/elapsed*decay;}
}
internal sealed class CarouselInertia {
 internal double Value,Velocity;internal bool Active;
 double start,initialVelocity,target,duration,elapsed;bool prepare;
 internal static int Destination(double value,double velocity,int count){double projected=value+velocity*Math.Abs(velocity)/8.0;return (int)Math.Round(Math.Max(0,Math.Min(Math.Max(0,count-1),projected)));}
 internal void Begin(double value,double velocity){Value=start=value;Velocity=initialVelocity=velocity;elapsed=0;prepare=true;Active=Math.Abs(Velocity)>.10;}
 internal bool Step(double seconds,int count){
  if(!Active)return false;
  if(prepare){prepare=false;target=Destination(start,initialVelocity,count);double distance=target-start;if(Math.Abs(distance)<.001){Value=target;Velocity=0;return Active=false;}if(distance*initialVelocity<0)initialVelocity=0;duration=Math.Max(.16,Math.Abs(distance)/Math.Max(.5,Math.Abs(initialVelocity))*2.0);initialVelocity=Math.Sign(distance)*Math.Min(Math.Abs(initialVelocity),Math.Abs(distance)*2.6/duration);}
  elapsed+=Math.Max(0,seconds);double t=Math.Min(1,elapsed/duration),t2=t*t,t3=t2*t,delta=target-start;
  Value=start+delta*(3*t2-2*t3)+initialVelocity*duration*(t3-2*t2+t);
  Velocity=(delta*(6*t-6*t2)+initialVelocity*duration*(3*t2-4*t+1))/duration;
  if(t>=1){Value=target;Velocity=0;Active=false;}return Active;
 }
}
internal sealed class CarouselSpring {
 internal double Value,Target,Velocity;
 internal void Set(double value){Value=Target=value;Velocity=0;}
 internal bool Step(double elapsed,double rate=22){double dt=Math.Min(.04,Math.Max(.001,elapsed)),offset=Value-Target,change=(Velocity+rate*offset)*dt,decay=Math.Exp(-rate*dt);Value=Target+(offset+change)*decay;Velocity=(Velocity-rate*change)*decay;if(Math.Abs(Target-Value)<.001&&Math.Abs(Velocity)<.01){Value=Target;Velocity=0;return false;}return true;}
}
internal sealed class CarouselApp : IDisposable {
 internal IntPtr Window;internal string Title;
 readonly object iconSync=new object();
 Bitmap icon;
 bool iconRequested,disposed;
 internal Bitmap Image {get{lock(iconSync)return icon;}}
 internal Rectangle LastVisibleFrame,LastNormalPlacement;
 internal bool LastVisibleMaximized;
 internal bool MinimizedByCarousel,RestoreRequested,RestoreMaximized,ForegroundRequested;
 internal void PrepareIcon(Control owner,Action redraw){
  if(iconRequested)return;
  iconRequested=true;
  System.Threading.ThreadPool.QueueUserWorkItem(_=>{
   Bitmap result;
   try{result=CarouselAppImages.Load(Window,Title);}
   catch(System.Runtime.InteropServices.ExternalException){return;}
   catch(ArgumentException){return;}
   if(result==null)return;
   lock(iconSync){
    if(disposed){result.Dispose();return;}
    icon=result;
   }
   try{owner.BeginInvoke(redraw);}
   catch(InvalidOperationException){}
  });
 }
 public void Dispose(){
  lock(iconSync){disposed=true;if(icon!=null){icon.Dispose();icon=null;}}
 }
}
internal sealed class CarouselCatalog : IDisposable {
 const int Normal=9,Maximized=3,ShowInCurrentState=5,MinimizedWithoutActivation=7,RestoreToMaximized=2;
 [StructLayout(LayoutKind.Sequential)]struct WindowRect {public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)]struct WindowPlacement {public int Length,Flags,Show;public int MinX,MinY,MaxX,MaxY;public WindowRect Normal;}
 [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out WindowRect rect);
 [DllImport("user32.dll")]static extern bool GetWindowPlacement(IntPtr window,ref WindowPlacement placement);
 delegate bool EnumProc(IntPtr window,IntPtr state);
 [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc callback,IntPtr state);
 [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
 [DllImport("user32.dll")]static extern bool IsWindow(IntPtr window);
 [DllImport("user32.dll")]static extern bool IsIconic(IntPtr window);
 [DllImport("user32.dll")]static extern bool IsZoomed(IntPtr window);
 [DllImport("user32.dll")]static extern bool IsHungAppWindow(IntPtr window);
 [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
 [DllImport("user32.dll")]static extern bool ShowWindowAsync(IntPtr window,int command);
 [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr window);
 [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr window,uint flags);
 
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder text,int count);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
 [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr window,uint command);
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);

 [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
 [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")]static extern int FrameBounds(IntPtr window,int attribute,out WindowRect value,int size);
 internal IntPtr PreferredWindow;
 internal readonly List<CarouselApp> Apps=new List<CarouselApp>();internal IntPtr LastActivated;internal int Current;internal bool LastActivationSucceeded {get;private set;}
 internal static bool IsShellSurface(string process,string windowClass){
  switch(process.ToLowerInvariant()){
   case "startmenuexperiencehost":case "searchhost":case "shellhost":case "shellexperiencehost":case "sihost":return true;
   case "explorer":return windowClass!="CabinetWClass"&&windowClass!="ExploreWClass";
   default:return false;
  }
 }
 bool Eligible(IntPtr window){if(!IsWindowVisible(window))return false;uint pid;GetWindowThreadProcessId(window,out pid);if(pid==Process.GetCurrentProcess().Id)return false;long style=GetWindowLongPtr(window,-20).ToInt64();if((style&0x08000080)!=0||GetWindow(window,4)!=IntPtr.Zero)return false;var cls=new StringBuilder(256);GetClassName(window,cls,256);int hidden;if(DwmGetWindowAttribute(window,14,out hidden,4)==0&&hidden!=0&&!IsIconic(window))return false;try{using(var p=Process.GetProcessById((int)pid))if(IsShellSurface(p.ProcessName,cls.ToString()))return false;}catch{return false;}var title=new StringBuilder(512);return GetWindowText(window,title,512)>0;}
 CarouselApp Create(IntPtr window){var text=new StringBuilder(512);GetWindowText(window,text,512);string title=text.ToString();uint pid;GetWindowThreadProcessId(window,out pid);try{using(var process=Process.GetProcessById((int)pid)){string name=process.ProcessName.ToLowerInvariant();if(name=="chatgpt"||name=="codex")title="ChatGPT";else if(name=="happ")title="Happ";else if(name=="steamwebhelper"||name=="steam")title="Steam";else if(name=="systemsettings")title="Настройки";else if(name=="msedge")title="Microsoft Edge";else if(name=="browser"&&title.Contains("Яндекс"))title="Яндекс";}}catch{}return new CarouselApp{Window=window,Title=title};}
 internal bool Refresh(){var windows=new List<IntPtr>();EnumWindows((window,state)=>{if(Eligible(window))windows.Add(window);return true;},IntPtr.Zero);for(int i=Apps.Count-1;i>=0;i--)if(!windows.Contains(Apps[i].Window)){Apps[i].Dispose();Apps.RemoveAt(i);}windows.Reverse();foreach(IntPtr window in windows)if(!Apps.Exists(app=>app.Window==window))Apps.Add(Create(window));if(Apps.Count==0)return false;IntPtr foreground=GetForegroundWindow();int index=Apps.FindIndex(app=>app.Window==foreground);if(index<0&&PreferredWindow!=IntPtr.Zero){foreground=PreferredWindow;index=Apps.FindIndex(app=>app.Window==foreground);}if(index>=0&&foreground!=LastActivated){var app=Apps[index];Apps.RemoveAt(index);Apps.Add(app);index=Apps.Count-1;}if(index<0)index=Apps.FindIndex(app=>app.Window==LastActivated);Current=index>=0?index:Apps.Count-1;return true;}
 internal Rectangle BaseBounds(int index,Rectangle monitor){
  CarouselApp app=Apps[index];
  IntPtr window=app.Window;
  WindowRect rect;
  var placement=new WindowPlacement{Length=Marshal.SizeOf(typeof(WindowPlacement))};
  if(IsIconic(window)){
   if(!GetWindowPlacement(window,ref placement))return new Rectangle(0,0,monitor.Width,monitor.Height);
   var normal=Rectangle.FromLTRB(placement.Normal.Left,placement.Normal.Top,placement.Normal.Right,placement.Normal.Bottom);
   bool restoreMaximized=ShouldRestoreMaximized(app,placement);
   if(!app.LastVisibleFrame.IsEmpty&&app.LastNormalPlacement==normal&&app.LastVisibleMaximized==restoreMaximized){
    Rectangle previous=app.LastVisibleFrame;
    previous.Offset(-monitor.X,-monitor.Y);
    return previous;
   }
   if(restoreMaximized){
    var maximized=Screen.FromHandle(window).WorkingArea;
    maximized.Offset(-monitor.X,-monitor.Y);
    return maximized;
   }
   rect=placement.Normal;
  }else{
   if(FrameBounds(window,9,out rect,16)!=0&&!GetWindowRect(window,out rect))
    return new Rectangle(0,0,monitor.Width,monitor.Height);
   if(GetWindowPlacement(window,ref placement)){
    app.LastNormalPlacement=Rectangle.FromLTRB(placement.Normal.Left,placement.Normal.Top,placement.Normal.Right,placement.Normal.Bottom);
    app.LastVisibleFrame=Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
    app.LastVisibleMaximized=IsZoomed(window);
   }
  }
  return new Rectangle(rect.Left-monitor.Left,rect.Top-monitor.Top,Math.Max(1,rect.Right-rect.Left),Math.Max(1,rect.Bottom-rect.Top));
 }
 internal bool TryVisibleFrame(int index,out Rectangle frame){
  frame=Rectangle.Empty;
  if(index<0||index>=Apps.Count)return false;
  IntPtr window=Apps[index].Window;
  WindowRect rect;
  if(IsIconic(window)||FrameBounds(window,9,out rect,16)!=0)return false;
  frame=Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
  return frame.Width>0&&frame.Height>0;
 }
 internal void RequestClose(int index){if(index>=0&&index<Apps.Count)PostMessage(Apps[index].Window,0x10,IntPtr.Zero,IntPtr.Zero);}
 internal bool WindowOpen(int index){return index>=0&&index<Apps.Count&&IsWindow(Apps[index].Window)&&IsWindowVisible(Apps[index].Window);}
 internal bool IsMinimized(int index){return index>=0&&index<Apps.Count&&IsIconic(Apps[index].Window);}
 internal void RemoveAt(int index){if(index<0||index>=Apps.Count)return;Apps[index].Dispose();Apps.RemoveAt(index);Current=CarouselMotion.Clamp(Current,Apps.Count);}
 internal void Activate(int index,bool allowAsyncForeground=true){
  LastActivationSucceeded=false;
  if(Apps.Count==0)return;
  index=CarouselMotion.Clamp(index,Apps.Count);
  CarouselApp app=Apps[index];
  IntPtr window=app.Window;
  if(!IsWindow(window))return;
  if(IsIconic(window)){
   if(!app.RestoreRequested){
    var placement=new WindowPlacement{Length=Marshal.SizeOf(typeof(WindowPlacement))};
    GetWindowPlacement(window,ref placement);
    app.RestoreMaximized=ShouldRestoreMaximized(app,placement);
    app.RestoreRequested=ShowWindowAsync(window,app.RestoreMaximized?Maximized:Normal);
   }
   return;
  }
  if(app.RestoreRequested&&app.RestoreMaximized&&!IsZoomed(window))return;
  app.RestoreRequested=app.MinimizedByCarousel=false;
  if(!IsActive(index)&&!SetForegroundWindow(window)&&allowAsyncForeground&&!app.ForegroundRequested)
   app.ForegroundRequested=ShowWindowAsync(window,ShowInCurrentState);
  LastActivationSucceeded=IsActive(index);
  if(!LastActivationSucceeded)return;
  app.ForegroundRequested=false;
  LastActivated=window;Current=index;
 }
 internal bool IsActive(int index){
  if(index<0||index>=Apps.Count)return false;
  IntPtr window=Apps[index].Window,foreground=GetForegroundWindow();
  return IsWindowVisible(window)&&!IsIconic(window)&&!IsHungAppWindow(window)&&
      (foreground==window||GetAncestor(foreground,3)==window);
 }
 internal bool RefreshRevealedWindow(IntPtr window){
  int index=Apps.FindIndex(app=>app.Window==window);
  if(index<0||!IsActive(index))return false;
  if(IsFullScreen(window))return true;
  // Some compositors still see a restored window as hidden. Refresh its frame
  // after the preview is gone, without resizing, raising or activating it.
  const uint asynchronousFrameRefresh=0x4000|0x20|0x10|0x04|0x02|0x01;
  return SetWindowPos(window,IntPtr.Zero,0,0,0,0,asynchronousFrameRefresh);
 }
 internal bool ConfirmQuickReveal(IntPtr target,IntPtr previous){
  int index=Apps.FindIndex(app=>app.Window==target);
  if(index<0)return false;
  if(IsActive(index))return true;
  IntPtr foreground=GetForegroundWindow();
  if(foreground!=previous&&foreground!=IntPtr.Zero)return false;
  Activate(index,false);
  return IsActive(index);
 }
 static bool ShouldRestoreMaximized(CarouselApp app,WindowPlacement placement){
  return app.MinimizedByCarousel?app.LastVisibleMaximized:(placement.Flags&RestoreToMaximized)!=0;
 }
 static bool IsFullScreen(IntPtr window){
  WindowRect frame;
  if(FrameBounds(window,9,out frame,16)!=0&&!GetWindowRect(window,out frame))return false;
  Rectangle screen=Screen.FromHandle(window).Bounds;
  const long caption=0x00c00000;
  // A borderless fullscreen surface can leave its own fullscreen mode on minimize.
  return (!IsZoomed(window)||(GetWindowLongPtr(window,-16).ToInt64()&caption)!=caption)&&
      frame.Left<=screen.Left+2&&frame.Top<=screen.Top+2&&frame.Right>=screen.Right-2&&frame.Bottom>=screen.Bottom-2;
 }
 internal bool MinimizeOthers(int index){
  bool requested=false;
  for(int i=0;i<Apps.Count;i++){
   var app=Apps[i];
   if(i==index||!CanMinimize(app))continue;
   BaseBounds(i,Screen.FromHandle(app.Window).Bounds);
   app.LastVisibleMaximized=IsZoomed(app.Window);
   app.RestoreRequested=false;
   app.ForegroundRequested=false;
   app.MinimizedByCarousel=ShowWindowAsync(app.Window,MinimizedWithoutActivation);
   requested|=app.MinimizedByCarousel;
  }
  return requested;
 }
 internal bool OthersMinimized(int index){
  return !Apps.Exists(app=>app.Window!=Apps[index].Window&&CanMinimize(app));
 }
 static bool CanMinimize(CarouselApp app){return IsWindowVisible(app.Window)&&!IsIconic(app.Window)&&!IsFullScreen(app.Window);}
 public void Dispose(){foreach(var app in Apps)app.Dispose();Apps.Clear();}
}
internal sealed class CarouselCard : IDisposable {
 [StructLayout(LayoutKind.Sequential)]struct Rect {public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)]struct Size2 {public int Width,Height;}
 [StructLayout(LayoutKind.Sequential)]struct ThumbnailProps {public uint Flags;public Rect Destination,Source;public byte Opacity;[MarshalAs(UnmanagedType.Bool)]public bool Visible;[MarshalAs(UnmanagedType.Bool)]public bool ClientOnly;}
 [DllImport("dwmapi.dll")]static extern int DwmRegisterThumbnail(IntPtr destination,IntPtr source,out IntPtr thumbnail);
 [DllImport("dwmapi.dll")]static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail,ref ThumbnailProps props);
 [DllImport("dwmapi.dll")]static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail,out Size2 size);
 [DllImport("dwmapi.dll")]static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
 internal Rectangle NativeBounds {get;set;}
 readonly CarouselApp app;
 readonly IntPtr host;
 CarouselTile tile;
 IntPtr[] strips;
 bool stripsVisible,stripRegistrationFailed;
 IntPtr thumbnail;Size2 source;Rectangle last;bool lastVisible,lastFill;byte lastOpacity=255;
 internal bool Live {get{return thumbnail!=IntPtr.Zero;}}internal bool Visible {get;private set;}
 internal Rectangle PreviewRect {get;private set;}
 internal bool AttachedCaption {get{return tile!=null&&tile.Visible;}}
 internal CarouselCard(IntPtr destination,CarouselApp app){
  this.app=app;host=destination;
  if(DwmRegisterThumbnail(destination,app.Window,out thumbnail)>=0)DwmQueryThumbnailSourceSize(thumbnail,out source);else thumbnail=IntPtr.Zero;
 }
 Rectangle Fit(Rectangle rect,bool fillBounds){
  if(source.Width<=0||source.Height<=0||fillBounds)return rect;
  double factor=Math.Min(rect.Width/(double)source.Width,rect.Height/(double)source.Height);
  int width=(int)Math.Round(source.Width*factor),height=(int)Math.Round(source.Height*factor);
  return new Rectangle(rect.X+(rect.Width-width)/2,rect.Y+(rect.Height-height)/2,width,height);
 }
 internal bool PlaceWithCaption(Form owner,Rectangle rect,byte opacity,int header,Font font,double scale,bool clipToViewport){
  if(!Live||opacity==0)return false;
  var picture=Fit(rect,false);
  if(picture.Width<1||picture.Height<1)return false;
  if(tile==null)tile=new CarouselTile(owner,app,header,font,scale,clipToViewport);
  if(!tile.PlaceTile(picture,opacity,owner.Bounds))return false;
  HideThumbnail();
  PreviewRect=picture;
  Visible=true;
  return true;
 }
 internal void Place(Form owner,Rectangle rect,bool visible,byte opacity=255,bool fillBounds=false){
  PlaceThumbnail(owner,rect,visible,opacity,fillBounds);
  HideStrips();
  if(tile!=null&&tile.Visible)tile.Hide();
 }
 internal void PlaceTapered(Form owner,Rectangle bounds,Point anchor,double bend,byte opacity){
  if(bend<.018||bounds.Width<40||bounds.Height<40||!PrepareStrips()){
   Place(owner,bounds,true,opacity,true);
   return;
  }
  var local=bounds;local.Offset(-owner.Left,-owner.Top);
  bool visible=opacity>0&&local.IntersectsWith(owner.ClientRectangle);
  double tilt=Math.Max(-1,Math.Min(1,(anchor.X-bounds.Left-bounds.Width*.5)/bounds.Width));
  bool towardBottom=anchor.Y>=bounds.Top+bounds.Height/2;
  for(int i=0;i<strips.Length;i++){
   double row=(i+.5)/strips.Length;
   if(!towardBottom)row=1-row;
   double amount=bend*row*row;
   int inset=(int)Math.Round(local.Width*amount*.5);
   int shift=(int)Math.Round(local.Width*amount*tilt*.4);
   var props=new ThumbnailProps{Flags=1|2|4|8|16,Opacity=opacity,Visible=visible,
    Destination=new Rect{Left=local.Left+inset+shift,Right=local.Right-inset+shift,
     Top=local.Top+local.Height*i/strips.Length,Bottom=local.Top+local.Height*(i+1)/strips.Length},
    Source=new Rect{Left=0,Right=source.Width,Top=source.Height*i/strips.Length,Bottom=source.Height*(i+1)/strips.Length}};
   DwmUpdateThumbnailProperties(strips[i],ref props);
  }
  HideThumbnail();
  stripsVisible=visible;
  PreviewRect=bounds;
  Visible=visible;
 }
 bool PrepareStrips(){
  if(strips!=null)return true;
  if(!Live||stripRegistrationFailed||source.Width<12||source.Height<12)return false;
  var group=new IntPtr[12];
  for(int i=0;i<group.Length;i++){
   if(DwmRegisterThumbnail(host,app.Window,out group[i])>=0)continue;
   for(int j=0;j<i;j++)DwmUnregisterThumbnail(group[j]);
   stripRegistrationFailed=true;
   return false;
  }
  strips=group;
  return true;
 }
 void HideStrips(){
  if(!stripsVisible)return;
  stripsVisible=false;
  var hidden=new ThumbnailProps{Flags=8,Visible=false};
  foreach(var strip in strips)DwmUpdateThumbnailProperties(strip,ref hidden);
 }
 void PlaceThumbnail(Form owner,Rectangle rect,bool visible,byte opacity,bool fillBounds){
  if(!visible&&!lastVisible){Visible=false;return;}
  if(last==rect&&lastVisible==visible&&lastOpacity==opacity&&lastFill==fillBounds)return;
  last=rect;lastVisible=visible;lastOpacity=opacity;lastFill=fillBounds;
  if(thumbnail==IntPtr.Zero)return;
  if(!visible||opacity==0){
   Visible=false;
   var hidden=new ThumbnailProps{Flags=8,Visible=false};
   DwmUpdateThumbnailProperties(thumbnail,ref hidden);
   return;
  }
  Rectangle full=Fit(rect,fillBounds);
  PreviewRect=full;
  full.Offset(-owner.Left,-owner.Top);
  bool onScreen=full.IntersectsWith(owner.ClientRectangle);
  // Let DWM use and clip its default visible frame. An explicit source crop
  // uses window-relative coordinates that can include invisible resize borders.
  var props=new ThumbnailProps{
   Flags=1|4|8|16,Destination=new Rect{Left=full.Left,Top=full.Top,Right=full.Right,Bottom=full.Bottom},
   Opacity=opacity,Visible=onScreen,ClientOnly=false
  };
  Visible=DwmUpdateThumbnailProperties(thumbnail,ref props)>=0&&onScreen;
 }
 void HideThumbnail(){if(!lastVisible)return;lastVisible=false;if(thumbnail!=IntPtr.Zero){var props=new ThumbnailProps{Flags=8,Visible=false};DwmUpdateThumbnailProperties(thumbnail,ref props);}}
 internal void RefreshCaption(){if(tile!=null)tile.RefreshCaption();}
 internal void Hide(){if(tile!=null&&tile.Visible)tile.Hide();Visible=false;HideThumbnail();HideStrips();}
 public void Dispose(){
  if(tile!=null){tile.Dispose();tile=null;}
  if(strips!=null){foreach(var strip in strips)DwmUnregisterThumbnail(strip);strips=null;stripsVisible=false;}
  if(thumbnail!=IntPtr.Zero){DwmUnregisterThumbnail(thumbnail);thumbnail=IntPtr.Zero;}
 }
}
internal sealed class CarouselTile : Form {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool SetProp(IntPtr window,string name,IntPtr data);
 [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
 readonly int header;
 readonly Font font;
 readonly double scale;
 readonly CarouselApp app;
 readonly CarouselCard image;
 readonly bool clipToViewport;
 bool clipped;
 Rectangle clip;
 internal CarouselTile(Form owner,CarouselApp app,int header,Font font,double scale,bool clipToViewport){
  this.app=app;this.header=header;this.font=font;this.scale=scale;this.clipToViewport=clipToViewport;
  Owner=owner;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;
  StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;
  TopMost=true;BackColor=TransparencyKey=Color.FromArgb(1,2,3);
  image=new CarouselCard(Handle,app);
 }
 protected override bool ShowWithoutActivation{get{return true;}}
 protected override CreateParams CreateParams{
  get{var p=base.CreateParams;p.ExStyle|=0x080800a0;return p;}
 }
 protected override void OnHandleCreated(EventArgs e){
  base.OnHandleCreated(e);
  SetProp(Handle,"NonRudeHWND",new IntPtr(1));
  int square=1;DwmSetWindowAttribute(Handle,33,ref square,4);
 }
 protected override void WndProc(ref Message message){
  if(message.Msg==0x84){message.Result=new IntPtr(-1);return;}
  base.WndProc(ref message);
 }
 internal bool PlaceTile(Rectangle picture,byte opacity,Rectangle viewport){
  if(!image.Live)return false;
  Rectangle tile=Rectangle.FromLTRB(picture.Left,picture.Top-header,picture.Right,picture.Bottom);
  Rectangle visible=Rectangle.Intersect(viewport,tile);
  if(visible.Width<=0||visible.Height<=0)return false;
  bool needsClip=clipToViewport&&visible!=tile;
  if(needsClip){
   visible.Offset(-tile.Left,-tile.Top);
   if(!clipped||clip!=visible){SetClip(new Region(visible));clip=visible;clipped=true;}
  }else if(clipped){SetClip(null);clipped=false;}
  bool rebuild=!Visible||Size!=tile.Size;
  if(Bounds!=tile)Bounds=tile;
  double alpha=opacity/255.0;
  if(Math.Abs(Opacity-alpha)>.0001)Opacity=alpha;
  if(rebuild){
   image.Place(this,picture,true,255,true);
   if(!image.Visible)return false;
   Invalidate();
   if(!Visible)Show();
   Update();
  }
  return true;
 }
 void SetClip(Region region){
  var previous=Region;
  Region=region;
  if(previous!=null)previous.Dispose();
 }
 internal void RefreshCaption(){if(Visible)Invalidate(new Rectangle(0,0,Width,header));}
 protected override void OnPaint(PaintEventArgs e){
  base.OnPaint(e);
  int inset=(int)Math.Round(8*scale),size=(int)Math.Round(28*scale);
  DrawCaption(e.Graphics,app,new Rectangle(inset,0,Width-inset,size),font,scale);
 }
 internal static void DrawCaption(Graphics graphics,CarouselApp app,Rectangle label,Font font,double scale){
  var iconBounds=new Rectangle(label.X,label.Y,label.Height,label.Height);
  Bitmap icon=app.Image;
  if(icon==null)CarouselAppImages.DrawFallback(graphics,iconBounds,app.Title);
  else{
   var interpolation=graphics.InterpolationMode;
   graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
   graphics.DrawImage(icon,iconBounds);
   graphics.InterpolationMode=interpolation;
  }
  int gap=(int)Math.Round(10*scale);
  var title=new Rectangle(iconBounds.Right+gap,label.Y,Math.Max(1,label.Width-label.Height-2*gap),label.Height);
  TextRenderer.DrawText(graphics,app.Title,font,title,Color.FromArgb(245,245,247),
   TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|
   TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.PreserveGraphicsClipping);
 }
 protected override void Dispose(bool disposing){if(disposing){image.Dispose();SetClip(null);}base.Dispose(disposing);}
}
internal sealed class AppCarousel : Form {
 const double HorizontalWheelPagesPerDetent=.048;
 const double QuickSettleRate=19;
 const double QuickBackdropStrength=.21;
 const double OverviewBackdropStrength=.43;
 const string SuppressFullscreenDetection="NonRudeHWND";
 [StructLayout(LayoutKind.Sequential)]struct PropertyKey {public Guid Format;public uint Id;}
 [StructLayout(LayoutKind.Explicit,Size=24)]struct Variant {[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public short Boolean;}
 [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface Store {[PreserveSig]int Count(out uint count);[PreserveSig]int At(uint index,out PropertyKey key);[PreserveSig]int Get(ref PropertyKey key,out Variant value);[PreserveSig]int Set(ref PropertyKey key,ref Variant value);[PreserveSig]int Commit();}
 [DllImport("shell32.dll")]static extern int SHGetPropertyStoreForWindow(IntPtr window,ref Guid id,[MarshalAs(UnmanagedType.Interface)]out Store store);
 [DllImport("user32.dll")]static extern uint GetDpiForSystem();
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool SetProp(IntPtr window,string name,IntPtr data);
 [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern IntPtr GetWindowStyle(IntPtr window,int index);
 [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]static extern IntPtr SetWindowStyle(IntPtr window,int index,IntPtr style);
 [DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
 [StructLayout(LayoutKind.Sequential)]struct LastInput {public uint Size,Ticks;}
 [DllImport("user32.dll")]static extern bool GetLastInputInfo(ref LastInput data);
 [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr window);
 [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr window,IntPtr dc);
 [DllImport("user32.dll")]static extern int SetWindowRgn(IntPtr window,IntPtr region,bool redraw);
 [DllImport("gdi32.dll")]static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
 [DllImport("gdi32.dll")]static extern bool BitBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sx,int sy,uint operation);
 [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleDC(IntPtr device);
 [DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr device);
 [DllImport("gdi32.dll")]static extern IntPtr SelectObject(IntPtr device,IntPtr item);
 [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr item);
 [DllImport("msimg32.dll")]static extern bool AlphaBlend(IntPtr target,int x,int y,int width,int height,IntPtr source,int sx,int sy,int sw,int sh,BlendFunction blend);
 [StructLayout(LayoutKind.Sequential)]struct BlendFunction{public byte Operation,Flags,Opacity,PixelAlpha;}
 [DllImport("dwmapi.dll")]static extern int DwmFlush();
 readonly CarouselCatalog catalog=new CarouselCatalog();readonly List<CarouselCard> cards=new List<CarouselCard>();readonly CarouselSpring rail=new CarouselSpring(),openness=new CarouselSpring(),lift=new CarouselSpring();readonly CarouselInertia inertia=new CarouselInertia();readonly CarouselVelocity fingerVelocity=new CarouselVelocity();
 readonly CarouselFrameClock animation;readonly Timer idle=new Timer{Interval=45000},closeWait=new Timer{Interval=60},wheelQuiet=new Timer{Interval=80};readonly Stopwatch clock=Stopwatch.StartNew();Font labelFont;
 readonly CarouselSpring quickBackdrop=new CarouselSpring();
 readonly CarouselVelocity verticalMotion=new CarouselVelocity();
 double verticalAnchor,verticalStart,verticalIntent,verticalTravel,verticalMaximum;
 bool verticalRendering,drawnWithCaptions;
 IntPtr desktopDc,desktopBitmap,matteDc,matteBitmap;
 Rectangle backdropBounds;
 int taskbarTop;
 bool taskbarRevealed;
 int backdropAlpha;
 double lastBackdropPaint;
 readonly Timer revealRefresh=new Timer{Interval=60};
 readonly Timer backdropWarmup=new Timer{Interval=300};
 IntPtr refreshWindow;
 IntPtr refreshQuickSource;
 uint revealInput;
 int revealChecks;
 bool refreshedFrame;
 double wheelPosition;
 bool wheelScrolling;
 bool quickAlreadyCommitted;
 bool fadingSelection;
 Bitmap selectionFrame;
 Rectangle selectionFrameBounds,selectionCardBounds,alignmentFrom,alignmentTarget;
 double alignmentStarted;
 bool openFromIcon,clipTiles,elasticOverview;
 Point transitionIcon;
 double iconStartOpen;
 double fadeStarted,fadeDuration,fadeFrom,fadeTo;
 double closeStart,closeFrom,closeProgress,gapStart,gapProgress,renderGap=Double.NaN,activationStarted,nextActivate,isolatingSince,lastMinimizeRequested=-1;
 bool foldClosing;
 bool closeGone,targetActivated,overlayActivationRequested,isolationStarted;
 double scale,lastTime,startX,startY,lastX,lastY,basePosition,velocity,velocityY,lastMoveTime;int mode,original,finishIndex,closeIndex=-1,closeChecks;bool touching,dragging,activateAfter,disposed,highResolution,isolateAfter,closing,closePending;Rectangle labelBounds=Rectangle.Empty;double renderRail=Double.NaN,renderOpen=Double.NaN,renderLift=Double.NaN;
 internal int LastInputMode {get;private set;}internal double LastFlickPixels {get;private set;}internal double LastFlickPages {get;private set;}internal double LastFlickStart {get;private set;}internal int LastFlickCount {get;private set;}internal int LastFlickTarget {get;private set;}
 internal IntPtr PreferredWindow {set{catalog.PreferredWindow=value;}}
 internal bool MouseInputEnabled {get;set;}
 internal bool UseRawPadScroll {get;set;}
 internal bool AnimationPlus {get;set;}
 bool mouseContact,overviewEnterPending;
 internal bool Overview {get;private set;}internal bool Busy {get{return Visible;}}internal int AppCount {get{return catalog.Apps.Count;}}internal int LastLivePreviews {get;private set;}internal bool LastActivationSucceeded {get{return catalog.LastActivationSucceeded;}}internal event Action<string> Completed;internal event Action<string> Failed;
 [DllImport("winmm.dll")]static extern uint timeBeginPeriod(uint period);
 [DllImport("winmm.dll")]static extern uint timeEndPeriod(uint period);
 internal AppCarousel(){animation=new CarouselFrameClock(this);Text="ModeTapper · Карусель";AutoScaleMode=AutoScaleMode.None;StartPosition=FormStartPosition.Manual;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;BackColor=Color.FromArgb(21,21,23);DoubleBuffered=true;SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);KeyPreview=true;scale=GetDpiForSystem()/96.0;labelFont=new Font("Segoe UI Semibold",15*(float)scale,FontStyle.Regular,GraphicsUnit.Pixel);Bounds=Screen.PrimaryScreen.Bounds;IntPtr window=Handle;DisableNativeEdgeGesture(window);animation.Tick+=(s,e)=>Animate();idle.Tick+=(s,e)=>CancelImmediate();closeWait.Tick+=(s,e)=>CheckClose();wheelQuiet.Tick+=(s,e)=>EndHorizontalScroll();revealRefresh.Tick+=(s,e)=>RefreshAfterReveal();backdropWarmup.Tick+=(s,e)=>WarmBackdrop();}
 internal static void DisableNativeEdgeGesture(IntPtr window){Guid id=typeof(Store).GUID;Store store;if(SHGetPropertyStoreForWindow(window,ref id,out store)==0){var key=new PropertyKey{Format=new Guid("32CE38B2-2C9A-41B1-9BC5-B3784394AA44"),Id=2};var value=new Variant{Type=11,Boolean=-1};store.Set(ref key,ref value);store.Commit();Marshal.ReleaseComObject(store);}}
 int S(double value){return (int)Math.Round(value*scale);}
 protected override bool ShowWithoutActivation {get{return mode==1||mode==2;}}
 internal bool CanStartQuick {get{return !disposed&&(!Visible||mode==1&&!touching);}}
 internal bool BeginQuick(double x){
  if(!CanStartQuick)return false;
  if(!Visible)return Open(1,x,0);
  // The next swipe can take over while the previous card is still settling.
  ReleaseSelectionFrame();
  quickAlreadyCommitted=isolateAfter;
  original=finishIndex;basePosition=rail.Value;rail.Set(basePosition);
  ResetActivation();activateAfter=false;touching=true;startX=lastX=x;startY=lastY=0;
  velocity=velocityY=0;lastMoveTime=clock.Elapsed.TotalSeconds;fingerVelocity.Reset(lastMoveTime,x,0);
  if(fadingSelection){fadingSelection=false;FadePreviewTo(1,.075);}
  StartAnimation();return true;
 }
 internal void CancelQuick(){
  if(!Visible||mode!=1||!touching)return;
  BeginQuickSettle(original,0,quickAlreadyCommitted);
 }
 void BeginQuickSettle(int target,double releaseVelocity,bool isolate){
  touching=false;finishIndex=target;activateAfter=true;isolateAfter=isolate;ResetActivation();
  double remaining=target-rail.Value;
  rail.Target=target;
  rail.Velocity=Math.Sign(remaining)*Math.Max(0,Math.Min(Math.Abs(remaining)*QuickSettleRate,Math.Sign(remaining)*releaseVelocity));
  StartAnimation();
 }
 internal bool Begin(double x,double y){
  if(disposed||closePending||closing||!Overview||touching||activateAfter)return false;
  BeginOverviewTouch(x,y);return true;
 }
 internal void AbortOverviewTouch(){
  if(!touching||(mode!=3&&mode!=4))return;
  touching=false;mode=0;closeIndex=-1;
  rail.Target=CarouselMotion.Clamp((int)Math.Round(rail.Value),cards.Count);
  lift.Target=0;openness.Target=1;
  StartAnimation();
 }
 internal bool OpenOverview(){return !disposed&&!Visible&&Open(0,0,0);}
 internal bool BeginOverviewDrag(bool opening,double recognition=0,bool activateOnOpen=false){
  if(opening){
   if(disposed||Visible||!Open(2,0,0))return false;
   if(activateOnOpen)Activate();
   StartVerticalMotion(recognition);
   return true;
  }
  if(disposed||!Overview||touching||activateAfter||closing||closePending)return false;
  CancelHorizontalScroll();
  inertia.Active=false;
  finishIndex=CarouselMotion.Clamp((int)Math.Round(rail.Value),cards.Count);
  rail.Target=finishIndex;
  openness.Velocity=0;
  mode=6;touching=true;closeIndex=-1;
  StartVerticalMotion(recognition);
  idle.Stop();idle.Start();StartAnimation();
  return true;
 }
 void StartVerticalMotion(double recognition){
  lastMoveTime=clock.Elapsed.TotalSeconds;
  verticalRendering=true;
  verticalAnchor=Math.Max(0,Math.Min(.60,recognition));
  verticalStart=openness.Value;
  verticalIntent=mode==2?0:1;
  verticalTravel=verticalMaximum=0;
  verticalMotion.Reset(lastMoveTime,openness.Value,0);
 }
 internal void MoveOverviewDrag(double progress){
  if(!Visible||!touching||(mode!=2&&mode!=6))return;
  double raw=Math.Max(0,Math.Min(1,progress));
  double distance=Math.Max(0,(raw-verticalAnchor)/(1-verticalAnchor));
  verticalTravel=distance;
  verticalMaximum=Math.Max(verticalMaximum,distance);
  verticalIntent=mode==2?raw:1-raw;
  lastMoveTime=clock.Elapsed.TotalSeconds;
  verticalMotion.Add(lastMoveTime,mode==2?distance:verticalStart*(1-distance),0);
  StartAnimation();
 }
 internal void EndOverviewDrag(bool cancelled){
  if(!Visible||!touching||(mode!=2&&mode!=6))return;
  bool opening=mode==2;
  bool returnedToStart=verticalMaximum>=.12&&verticalTravel<=.04;
  touching=false;
  LastInputMode=mode;
  if(opening?(cancelled||returnedToStart||verticalIntent<.35):(!cancelled&&verticalIntent<=.65)){
   int target=opening?original:finishIndex;
   SelectOverview(target,target!=original);
   return;
  }
  Overview=true;mode=0;openness.Target=1;
  PreserveVerticalVelocity();
  if(opening){FadePreviewTo(1,.075);Activate();}
  StartAnimation();
 }
 void SelectOverview(int index,bool isolate,bool click=false){
  CancelHorizontalScroll();
  inertia.Active=false;
  finishIndex=index;rail.Target=index;openness.Target=0;
  activateAfter=true;isolateAfter=isolate;fadingSelection=false;
  if(verticalRendering)PreserveVerticalVelocity();
  openFromIcon=click&&AnimationPlus&&catalog.IsMinimized(index)&&LumaFlyout.AnimationsEnabled();
  if(openFromIcon){
   var caption=CaptionBounds(cards[index].PreviewRect);
   transitionIcon=new Point(Left+caption.Left+S(14),Top+caption.Top+S(14));
   iconStartOpen=openness.Value;
   renderOpen=Double.NaN;
   RenderScene();
  }
  ResetActivation();
  StartAnimation();
 }
 void PreserveVerticalVelocity(){
  double distance=openness.Target-openness.Value;
  double direction=Math.Sign(distance);
  openness.Velocity=direction*Math.Max(0,Math.Min(Math.Abs(distance)*22,direction*openness.Velocity));
 }
 void SetSceneBounds(bool quick){
  var screen=Screen.PrimaryScreen;
  Rectangle area=screen.WorkingArea;
  clipTiles=Screen.AllScreens.Length>1;
  taskbarTop=area.Bottom;
  scale=GetDpiForSystem()/96.0;
  AllowTransparency=true;
  Opacity=quick?0:1;
  const int toolWindow=0x80,transparent=0x20;
  long style=GetWindowStyle(Handle,-20).ToInt64();
  // A preview host is not an application occluding the live Chromium window.
  long nextStyle=quick?style|toolWindow|transparent:(style|toolWindow)&~transparent;
  if(style!=nextStyle)SetWindowStyle(Handle,-20,new IntPtr(nextStyle));
  SetProp(Handle,SuppressFullscreenDetection,new IntPtr(1));
  if(quick){
   // Keep the shell bar outside the animation and preserve its existing z-order.
   if(area==screen.Bounds)area.Height=Math.Max(1,area.Height-S(12));
   Bounds=area;
  }else{
   Bounds=screen.Bounds;
  }
 }
 bool Open(int inputMode,double x,double y){
  if(!catalog.Refresh()||inputMode==1&&catalog.Apps.Count<2)return false;
  original=catalog.Current;
  SetSceneBounds(inputMode==1);
  if(inputMode==2)Opacity=0;
  foreach(var card in cards)card.Dispose();cards.Clear();rail.Set(original);openness.Set(0);lift.Set(0);inertia.Active=false;closeIndex=-1;closing=closePending=false;gapProgress=0;renderGap=Double.NaN;closeGone=false;activationStarted=0;renderRail=renderOpen=renderLift=Double.NaN;labelBounds=Rectangle.Empty;mode=inputMode;touching=inputMode!=0;dragging=false;startX=lastX=x;startY=lastY=y;basePosition=original;velocity=velocityY=0;lastMoveTime=clock.Elapsed.TotalSeconds;fingerVelocity.Reset(lastMoveTime,x,y);
  Overview=inputMode==0;activateAfter=isolateAfter=quickAlreadyCommitted=fadingSelection=openFromIcon=false;fadeDuration=0;finishIndex=original;
  verticalRendering=inputMode==2;
  drawnWithCaptions=false;
  elasticOverview=inputMode!=1&&AnimationPlus&&LumaFlyout.AnimationsEnabled();
  quickBackdrop.Set(0);
  backdropAlpha=inputMode==1?0:(int)Math.Round(OverviewBackdropStrength*255);
  lastBackdropPaint=0;
  if(Overview)openness.Target=1;
  try{
   if(!highResolution){highResolution=timeBeginPeriod(1)==0;}
   for(int i=0;i<catalog.Apps.Count;i++){
    var card=new CarouselCard(Handle,catalog.Apps[i]){NativeBounds=catalog.BaseBounds(i,Bounds)};
    cards.Add(card);
    if(inputMode!=1)catalog.Apps[i].PrepareIcon(this,card.RefreshCaption);
   }
   LastLivePreviews=cards.FindAll(card=>card.Live).Count;
   if(inputMode==1&&!cards[original].Live){Dismiss();return false;}
   ResetActivation();EnsureBackground();RenderScene();
   Show();SetWindowPos(Handle,new IntPtr(-1),Bounds.X,Bounds.Y,Bounds.Width,Bounds.Height,0x50);
   if(inputMode==1)FadePreviewTo(1,.075);
   else if(inputMode==0)Activate();
   StartAnimation();idle.Stop();idle.Start();return true;
  }catch{Dismiss();throw;}
 }
 void BeginOverviewTouch(double x,double y){CancelHorizontalScroll();animation.Stop();inertia.Active=false;rail.Set(rail.Value);openness.Velocity=0;touching=true;dragging=false;mode=3;activateAfter=false;startX=lastX=x;startY=lastY=y;basePosition=rail.Value;velocity=velocityY=0;closeIndex=HitCard(x,y);lift.Set(0);lastMoveTime=clock.Elapsed.TotalSeconds;fingerVelocity.Reset(lastMoveTime,x,y);idle.Stop();idle.Start();StartAnimation();}
 internal void MoveFinger(double x,double y){if(!Visible||!touching)return;double now=clock.Elapsed.TotalSeconds;fingerVelocity.Add(now,x,y);fingerVelocity.Read(now,out velocity,out velocityY);lastMoveTime=now;lastX=x;lastY=y;double dx=x-startX,up=startY-y;
  if(mode==3&&closeIndex>=0&&up>S(10)&&up>Math.Abs(dx)*1.4){mode=4;rail.Set(basePosition);}
  if(Math.Abs(dx)>S(8)||Math.Abs(up)>S(8))dragging=true;
  if(mode==1)rail.Set(CarouselMotion.Rubber(Math.Max(original-1,Math.Min(original+1,basePosition-dx/QuickTravel())),cards.Count));
  else if(mode==4){lift.Value=lift.Target=Math.Min(0,y-startY);lift.Velocity=0;}
  StartAnimation();
 }
 internal void EndFinger(){if(!Visible||!touching)return;touching=false;LastInputMode=mode;fingerVelocity.Read(clock.Elapsed.TotalSeconds,out velocity,out velocityY);double dx=lastX-startX,up=startY-lastY;activateAfter=isolateAfter=false;
  if(mode==1){
   int target=CarouselMotion.QuickTarget(original,dx,velocity,scale,cards.Count);
   BeginQuickSettle(target,-velocity/QuickTravel(),target!=original||quickAlreadyCommitted);
   return;
  }
  if(mode==5){StartInertia();return;}
  if(mode==4){if(CarouselMotion.CloseSwipe(up,velocityY,Height,scale)){
    closing=true;closeStart=clock.Elapsed.TotalSeconds;closeFrom=lift.Value;closeProgress=0;lift.Velocity=0;
    foldClosing=AnimationPlus&&LumaFlyout.AnimationsEnabled();
   }else{lift.Target=0;lift.Velocity=velocityY*.2;}openness.Target=1;}
  else if(mode==3){
   if(!dragging)SelectOverview(CenteredCard(),true,true);
   else{StartInertia();openness.Target=1;}
  }
  StartAnimation();
 }
 double QuickTravel(){return Math.Max(S(180),Width*.53);}
 internal bool BeginPadScroll(){
  if(!Overview||activateAfter||touching||closing||closePending)return false;
  BeginOverviewTouch(0,0);mode=5;return true;
 }
 internal void MovePadScroll(double displacement){if(mode==5&&touching)MoveFinger(displacement,0);}
 internal void EndPadScroll(){if(mode==5&&touching)EndFinger();}
 void StartInertia(){fingerVelocity.Read(clock.Elapsed.TotalSeconds,out velocity,out velocityY);LastFlickPixels=velocity;BeginInertia(-velocity/PageStep()*2.0);}
 void BeginInertia(double pagesPerSecond){
  LastFlickPages=pagesPerSecond;LastFlickStart=rail.Value;LastFlickCount=cards.Count;
  LastFlickTarget=CarouselInertia.Destination(rail.Value,pagesPerSecond,cards.Count);
  inertia.Begin(rail.Value,pagesPerSecond);
  if(!inertia.Active)rail.Target=CarouselMotion.Clamp((int)Math.Round(rail.Value),cards.Count);
 }
 void CheckClose(){if(!closePending)return;if(!catalog.WindowOpen(closeIndex)){closeGone=true;closeWait.Stop();StartAnimation();}else if(++closeChecks>=10){closeWait.Stop();int target=closeIndex;closePending=false;Dismiss();catalog.Activate(target);if(Completed!=null)Completed("CloseNeedsAttention");}}
 void FinishClose(){closePending=false;cards[closeIndex].Dispose();cards.RemoveAt(closeIndex);catalog.RemoveAt(closeIndex);if(original>closeIndex)original--;original=CarouselMotion.Clamp(original,cards.Count);double position=rail.Value;if(closeIndex<position||closeIndex==cards.Count)position--;rail.Set(Math.Max(0,Math.Min(Math.Max(0,cards.Count-1),position)));rail.Target=CarouselMotion.Clamp((int)Math.Round(rail.Value),cards.Count);closeIndex=-1;lift.Set(0);gapProgress=0;renderGap=Double.NaN;renderRail=renderOpen=renderLift=Double.NaN;Overview=true;if(cards.Count==0){Dismiss();}if(Completed!=null)Completed("Closed");}
 double PageStep(){return Width*(1-openness.Value)+Width*.78*openness.Value+S(22);}
 int CenteredCard(){return CarouselMotion.Clamp((int)Math.Round(rail.Value),cards.Count);}
 int HitCard(double x,double y){for(int i=cards.Count-1;i>=0;i--)if(cards[i].Visible&&cards[i].PreviewRect.Contains((int)x+Left,(int)y+Top))return i;return -1;}
 void StartAnimation(){if(!highResolution)highResolution=timeBeginPeriod(1)==0;if(!animation.Enabled){lastTime=clock.Elapsed.TotalSeconds;animation.Start();}}
 void FadePreviewTo(double opacity,double seconds){
  fadeFrom=Opacity;fadeTo=opacity;
  fadeStarted=clock.Elapsed.TotalSeconds;fadeDuration=seconds;
 }
 void AdvancePreviewFade(double now){
  if(fadeDuration==0)return;
  double t=Math.Min(1,(now-fadeStarted)/fadeDuration);
  double eased=t*t*(3-2*t);
  Opacity=fadeFrom+(fadeTo-fadeFrom)*eased;
  if(t>=1)fadeDuration=0;
 }
 void Animate(){try{AnimateCore();}catch(Exception ex){Dismiss();if(Failed!=null)Failed(ex.Message);}}
 void AnimateCore(){
  if(!Visible){animation.Stop();return;}
  if((GetAsyncKeyState(27)&0x8000)!=0){CancelImmediate();return;}
  double now=clock.Elapsed.TotalSeconds,dt=now-lastTime;lastTime=now;
  AdvancePreviewFade(now);
  if(touching&&mode==1&&now-lastMoveTime>.42)EndFinger();
  if(touching&&(mode==2||mode==6)&&now-lastMoveTime>3)EndOverviewDrag(true);
  bool moving=false;
  if(closing){
   double progress=(now-closeStart)/(foldClosing?.32:.24);
   closeProgress=CarouselMotion.Ease(progress);
   double destination=foldClosing?closeFrom-S(38):-Height*1.1;
   lift.Value=closeFrom+(destination-closeFrom)*closeProgress;
   lift.Target=lift.Value;moving=progress<1;
  }
  if(closePending){
   gapProgress=CarouselMotion.Ease((now-gapStart)/.24);
   moving=gapProgress<1||!closeGone;
   if(gapProgress>=1&&closeGone){FinishClose();if(!Visible)return;moving=true;}
  }
  if(!touching){
   moving=AdvanceRail(dt)|moving;
   moving=openness.Step(dt)|(!closing&&lift.Step(dt))|moving;
  }else if(mode==2||mode==6){
   moving=rail.Step(dt);
   double next=verticalMotion.InterpolatedX(now);
   openness.Velocity=(next-openness.Value)/Math.Max(.004,dt);
   openness.Value=next;
   if(mode==2){
    double alpha=Math.Min(1,next*7);
    if(Math.Abs(Opacity-alpha)>=1/255.0)Opacity=alpha;
   }
  }else if(mode==3||mode==5){
   double x=fingerVelocity.InterpolatedX(now);
   rail.Set(CarouselMotion.Rubber(basePosition-(x-startX)/PageStep(),cards.Count));
  }
  if(verticalRendering&&!touching&&!activateAfter&&openness.Value==1)verticalRendering=false;
  UpdateBackdrop(now,dt);
  RenderScene();
  UpdateTaskbarReveal();
  if(activateAfter){
   bool quick=mode==1;
   if(activationStarted==0){
    bool preserveFrame=(mode!=2&&mode!=6)||catalog.IsMinimized(finishIndex);
    if(moving||fadeDuration!=0||now-lastMinimizeRequested<.22)return;
    if(preserveFrame&&selectionFrame==null)HoldSelectionFrame(finishIndex);
   }
   if(!AdvanceActivation(now)||moving){
    if(fadingSelection){fadingSelection=false;FadePreviewTo(1,.075);}
    return;
   }
   if(!fadingSelection){
    if(!AlignSelectionFrame(finishIndex,now))return;
    fadingSelection=true;
    FadePreviewTo(0,.22);
    return;
   }
   if(fadeDuration!=0)return;
   LastInputMode=mode;
   bool sameWindowReturn=(mode==2||mode==6)&&finishIndex==original&&selectionFrame==null;
   refreshWindow=sameWindowReturn?IntPtr.Zero:catalog.Apps[finishIndex].Window;
   refreshQuickSource=quick&&finishIndex!=original?catalog.Apps[original].Window:IntPtr.Zero;
   revealInput=InputTimestamp();
   revealChecks=0;
   refreshedFrame=false;
   Dismiss();
   revealRefresh.Stop();
   if(refreshWindow!=IntPtr.Zero)revealRefresh.Start();
   if(Completed!=null)Completed(quick?"QuickApp":"App");
   return;
  }
  if(touching||moving||fadeDuration!=0)return;
  animation.Stop();
  if(wheelScrolling)return;
  if(closing){
   closing=false;closePending=true;closeGone=false;closeChecks=0;gapStart=now;gapProgress=0;
   catalog.RequestClose(closeIndex);closeWait.Start();animation.Start();return;
  }
  Overview=true;
  if(highResolution){timeEndPeriod(1);highResolution=false;}
  if(Completed!=null)Completed("Overview");
 }
 void UpdateTaskbarReveal(){
  if(taskbarTop>=Bottom||taskbarTop<=Top)return;
  bool reveal=false;
  if(mode!=1&&(activateAfter||mode==6&&touching)&&finishIndex>=0&&finishIndex<cards.Count){
   var card=cards[finishIndex];
   Rectangle preview=selectionFrame!=null?selectionFrameBounds:card.PreviewRect;
   bool atTaskbar=(selectionFrame!=null||card.Visible)&&preview.Bottom>=taskbarTop-S(3);
   double revealThreshold=activateAfter?.14:.065;
   bool heldNearEdge=taskbarRevealed&&openness.Value<.24&&preview.Bottom>=taskbarTop-S(18);
   reveal=(taskbarRevealed&&activateAfter)||heldNearEdge||openness.Value<=revealThreshold||(openness.Value<.23&&atTaskbar);
  }
  SetTaskbarReveal(reveal);
 }
 void SetTaskbarReveal(bool reveal){
  if(reveal==taskbarRevealed)return;
  // Clip the preview host at the work area's edge without changing card geometry.
  IntPtr region=reveal?CreateRectRgn(0,0,Width,taskbarTop-Top):IntPtr.Zero;
  if(reveal&&region==IntPtr.Zero)return;
  if(SetWindowRgn(Handle,region,true)!=0)taskbarRevealed=reveal;
  else if(region!=IntPtr.Zero)DeleteObject(region);
 }
 void RefreshAfterReveal(){
  if(!Visible){
   bool sameInput=revealInput!=0&&revealInput==InputTimestamp();
   if(refreshQuickSource!=IntPtr.Zero&&sameInput)
    catalog.ConfirmQuickReveal(refreshWindow,refreshQuickSource);
   if(!refreshedFrame)refreshedFrame=catalog.RefreshRevealedWindow(refreshWindow);
   if(refreshQuickSource!=IntPtr.Zero&&sameInput&&++revealChecks<3)return;
  }
  revealRefresh.Stop();
  refreshQuickSource=refreshWindow=IntPtr.Zero;
 }
 static uint InputTimestamp(){var info=new LastInput{Size=8};return GetLastInputInfo(ref info)?info.Ticks:0;}
 void ResetActivation(){
  activationStarted=nextActivate=isolatingSince=0;
  targetActivated=overlayActivationRequested=isolationStarted=false;
 }
 bool AdvanceActivation(double now){
  if(activationStarted==0)activationStarted=now;
  if(targetActivated&&!catalog.IsActive(finishIndex))targetActivated=false;
  if(!targetActivated&&now>=nextActivate){
   nextActivate=now+.050;
   bool allowAsyncForeground=mode!=1||overlayActivationRequested&&now-activationStarted>.30;
   catalog.Activate(finishIndex,allowAsyncForeground);
   targetActivated=catalog.LastActivationSucceeded;
   if(!targetActivated&&!overlayActivationRequested&&now-activationStarted>=.12){
    overlayActivationRequested=true;
    Activate();
   }
   if(targetActivated&&isolateAfter&&!isolationStarted){
    isolationStarted=true;
    if(catalog.MinimizeOthers(finishIndex))isolatingSince=lastMinimizeRequested=now;
   }
  }
  if(!targetActivated){
   if(now-activationStarted<.85)return false;
   throw new InvalidOperationException("Windows не переключила выбранное приложение");
  }
  if(isolatingSince==0)return true;
  // Keep the opaque carousel above the desktop until Windows has finished changing window state.
  return now-isolatingSince>=.26&&(catalog.OthersMinimized(finishIndex)||now-isolatingSince>=.65);
 }
 bool AdvanceRail(double dt){
  if(inertia.Active){
   bool flowing=inertia.Step(dt,cards.Count);
   rail.Value=inertia.Value;rail.Velocity=inertia.Velocity;
   if(flowing)return true;
   rail.Target=CarouselMotion.Clamp((int)Math.Round(rail.Value),cards.Count);
  }
  return mode==1?rail.Step(dt,QuickSettleRate):rail.Step(dt);
 }
 void HoldSelectionFrame(int index){
  if(Opacity<.995||index<0||index>=cards.Count||!cards[index].Live)return;
  Rectangle previewBounds=cards[index].PreviewRect;
  Rectangle area=Rectangle.Intersect(previewBounds,Bounds);
  if(area.Width<24||area.Height<24)return;
  // Preserve the visible preview while the source surface is recreated during restore.
  // This copy stays in memory until the transition clears.
  DwmFlush();
  IntPtr screen=GetDC(IntPtr.Zero);
  if(screen==IntPtr.Zero)return;
  Bitmap frame=null;
  try{
   frame=new Bitmap(area.Width,area.Height,System.Drawing.Imaging.PixelFormat.Format32bppRgb);
   using(var graphics=Graphics.FromImage(frame)){
    IntPtr image=graphics.GetHdc();
    try{
     if(!BitBlt(image,0,0,area.Width,area.Height,screen,area.Left,area.Top,0x40cc0020))return;
    }finally{graphics.ReleaseHdc(image);}
   }
   selectionCardBounds=previewBounds;
   selectionFrameBounds=area;
   selectionFrame=frame;
   frame=null;
   Invalidate();
   Update();
   foreach(var card in cards)card.Hide();
  }finally{
   ReleaseDC(IntPtr.Zero,screen);
   if(frame!=null)frame.Dispose();
  }
 }
 bool AlignSelectionFrame(int index,double now){
  if(selectionFrame==null)return true;
  if(alignmentStarted==0){
   Rectangle native;
   if(!catalog.TryVisibleFrame(index,out native))return true;
   if(native==selectionCardBounds)return true;
   alignmentFrom=selectionFrameBounds;
   // Preserve the crop when the preview was clipped by the display or taskbar.
   double scaleX=native.Width/(double)selectionCardBounds.Width;
   double scaleY=native.Height/(double)selectionCardBounds.Height;
   alignmentTarget=Rectangle.FromLTRB(
    native.Left+(int)Math.Round((alignmentFrom.Left-selectionCardBounds.Left)*scaleX),
    native.Top+(int)Math.Round((alignmentFrom.Top-selectionCardBounds.Top)*scaleY),
    native.Left+(int)Math.Round((alignmentFrom.Right-selectionCardBounds.Left)*scaleX),
    native.Top+(int)Math.Round((alignmentFrom.Bottom-selectionCardBounds.Top)*scaleY));
   if(alignmentTarget==alignmentFrom||alignmentTarget.Width<=0||alignmentTarget.Height<=0)return true;
   alignmentStarted=now;
  }
  double t=Math.Min(1,(now-alignmentStarted)/.07);
  double progress=t*t*(3-2*t);
  Rectangle previous=selectionFrameBounds;
  selectionFrameBounds=Rectangle.FromLTRB(
   Blend(alignmentFrom.Left,alignmentTarget.Left,progress),
   Blend(alignmentFrom.Top,alignmentTarget.Top,progress),
   Blend(alignmentFrom.Right,alignmentTarget.Right,progress),
   Blend(alignmentFrom.Bottom,alignmentTarget.Bottom,progress));
  if(previous!=selectionFrameBounds){
   Rectangle dirty=Rectangle.Union(previous,selectionFrameBounds);
   dirty.Offset(-Left,-Top);
   Invalidate(dirty);
  }
  if(t>=1)Update();
  return t>=1;
 }
 static int Blend(int start,int end,double progress){return start+(int)Math.Round((end-start)*progress);}
 void ReleaseSelectionFrame(){
  if(selectionFrame==null)return;
  selectionFrame.Dispose();
  selectionFrame=null;
  alignmentStarted=0;
  renderRail=Double.NaN;
  Invalidate();
 }
 void RenderScene(){
  if(selectionFrame!=null)return;
  double open=Math.Max(0,Math.Min(1,openness.Value));
  bool withCaptions=Overview&&!verticalRendering&&open>=.995&&!activateAfter&&!taskbarRevealed&&Opacity>=.995;
  if(Math.Abs(renderRail-rail.Value)<.00002&&Math.Abs(renderOpen-openness.Value)<.00002&&
     Math.Abs(renderLift-lift.Value)<.01&&Math.Abs(renderGap-gapProgress)<.00002&&drawnWithCaptions==withCaptions)return;
  drawnWithCaptions=withCaptions;
  double oldOpen=renderOpen;
  renderRail=rail.Value;renderOpen=openness.Value;renderLift=lift.Value;renderGap=gapProgress;
  Rectangle oldLabels=labelBounds,nextLabels=Rectangle.Empty;
  double step=PageStep();
  double position=rail.Value;
  if(closePending&&(closeIndex<position||closeIndex==cards.Count-1))position-=gapProgress;
  for(int i=0;i<cards.Count;i++){
   var card=cards[i];
   if(openFromIcon){
    if(i!=finishIndex){card.Hide();continue;}
    Rectangle target=card.NativeBounds;
    target.Offset(Left,Top);
    double progress=1-open/Math.Max(.01,iconStartOpen);
    Rectangle opening=WindowMorph.Unfurl(target,transitionIcon,S(24),progress);
    card.Place(this,opening,true,255,true);
    continue;
   }
   double relative=i-position-(closePending&&i>closeIndex?gapProgress:0);
   if(Math.Abs(relative)>=2.2){card.Hide();continue;}
   var native=card.NativeBounds;
   int overviewWidth=(int)Math.Round(Width*.78),overviewHeight=(int)Math.Round(Height*.75);
   var overviewCard=new Rectangle((Width-overviewWidth)/2,(Height-overviewHeight)/2-S(12),overviewWidth,overviewHeight);
   var rect=WindowMorph.OverviewFrame(native,overviewCard,open,elasticOverview);
   rect.Offset(Left+(int)Math.Round(relative*step),Top);
   if(i==closeIndex)rect.Offset(0,(int)Math.Round(lift.Value));
   if(i==closeIndex&&closing&&foldClosing)
    rect=WindowMorph.Collapse(rect,closeProgress,S(24));
   bool visible=rect.Right>Left&&rect.Left<Right;
   byte opacity=(byte)(i!=closeIndex?255:closing&&foldClosing?
    255*Math.Min(1,(1-closeProgress)*3):255*Math.Max(0,1-Math.Max(0,Math.Abs(lift.Value)/Height-.45)/.8));
   if(withCaptions&&visible&&card.PlaceWithCaption(this,rect,opacity,S(38),labelFont,scale,clipTiles))continue;
   card.Place(this,rect,visible,opacity,mode==1);
   if(card.Visible&&withCaptions){
    Rectangle label=Rectangle.Intersect(CaptionBounds(card.PreviewRect),ClientRectangle);
    if(label.Width>0&&label.Height>0)nextLabels=nextLabels.IsEmpty?label:Rectangle.Union(nextLabels,label);
   }
  }
  labelBounds=nextLabels;
  Rectangle dirty=oldLabels.IsEmpty?nextLabels:nextLabels.IsEmpty?oldLabels:Rectangle.Union(oldLabels,nextLabels);
  if(!dirty.IsEmpty)Invalidate(dirty);
  else if(Double.IsNaN(oldOpen))Invalidate();
 }
 void UpdateBackdrop(double now,double dt){
  // The overview uses a static backdrop while its native thumbnails move.
  if(mode!=1)return;
  quickBackdrop.Target=fadingSelection?0:QuickBackdropStrength;
  quickBackdrop.Step(dt,21);
  int alpha=(int)Math.Round(Math.Max(0,Math.Min(1,quickBackdrop.Value))*255);
  if(alpha==backdropAlpha||alpha!=0&&now-lastBackdropPaint<.013)return;
  backdropAlpha=alpha;
  lastBackdropPaint=now;
  Invalidate();
 }
 internal void PrepareBackdrop(){
  if(Visible||disposed)return;
  backdropWarmup.Stop();backdropWarmup.Start();
 }
 void WarmBackdrop(){
  backdropWarmup.Stop();
  if(Visible||disposed)return;
  Bounds=Screen.PrimaryScreen.Bounds;
  scale=GetDpiForSystem()/96.0;
  try{EnsureBackground();}
  catch(ExternalException){backdropBounds=Rectangle.Empty;}
 }
 void EnsureBackground(){
  if(!backdropBounds.IsEmpty&&backdropBounds.Contains(Bounds))return;
  ReleaseSurface(ref desktopDc,ref desktopBitmap);
  ReleaseSurface(ref matteDc,ref matteBitmap);
  backdropBounds=Screen.FromRectangle(Bounds).Bounds;
  using(var desktop=new Bitmap(backdropBounds.Width,backdropBounds.Height,PixelFormat.Format32bppRgb)){
   using(var graphics=Graphics.FromImage(desktop))DrawDesktop(graphics,backdropBounds);
   CacheSurface(desktop,out desktopDc,out desktopBitmap);
   using(var matte=CarouselBackdropImage.Create(desktop,S(7)))CacheSurface(matte,out matteDc,out matteBitmap);
  }
 }
 static void CacheSurface(Bitmap image,out IntPtr device,out IntPtr bitmap){
  device=CreateCompatibleDC(IntPtr.Zero);
  bitmap=IntPtr.Zero;
  if(device==IntPtr.Zero)return;
  try{
   bitmap=image.GetHbitmap();
   if(SelectObject(device,bitmap)==IntPtr.Zero)ReleaseSurface(ref device,ref bitmap);
  }catch{ReleaseSurface(ref device,ref bitmap);throw;}
 }
 static void ReleaseSurface(ref IntPtr device,ref IntPtr bitmap){
  if(device!=IntPtr.Zero){DeleteDC(device);device=IntPtr.Zero;}
  if(bitmap!=IntPtr.Zero){DeleteObject(bitmap);bitmap=IntPtr.Zero;}
 }
 void DrawBackground(Graphics graphics,Rectangle dirty){
  EnsureBackground();
  if(desktopDc==IntPtr.Zero){graphics.Clear(BackColor);return;}
  Rectangle area=Rectangle.Intersect(dirty,ClientRectangle);
  if(area.Width<=0||area.Height<=0)return;
  IntPtr output=graphics.GetHdc();
  try{BlitBackdrop(output,area);}
  finally{graphics.ReleaseHdc(output);}
 }
 void BlitBackdrop(IntPtr output,Rectangle area){
  int x=Left-backdropBounds.Left,y=Top-backdropBounds.Top;
  BitBlt(output,area.X,area.Y,area.Width,area.Height,desktopDc,area.X+x,area.Y+y,0x00cc0020);
  if(backdropAlpha!=0&&matteDc!=IntPtr.Zero){
   var blend=new BlendFunction{Opacity=(byte)backdropAlpha};
   AlphaBlend(output,area.X,area.Y,area.Width,area.Height,matteDc,area.X+x,area.Y+y,area.Width,area.Height,blend);
  }
 }
 void DrawDesktop(Graphics graphics,Rectangle display){
  graphics.Clear(SystemColors.Desktop);
  try{
   const string settings=@"HKEY_CURRENT_USER\Control Panel\Desktop";
   string path=Convert.ToString(Microsoft.Win32.Registry.GetValue(settings,"WallPaper",""));
   if(!File.Exists(path))return;
   int style;
   Int32.TryParse(Convert.ToString(Microsoft.Win32.Registry.GetValue(settings,"WallpaperStyle","")),out style);
   bool tile=Convert.ToString(Microsoft.Win32.Registry.GetValue(settings,"TileWallpaper",""))=="1";
   using(var source=Image.FromFile(path)){
    Rectangle screen=style==22?SystemInformation.VirtualScreen:display;
    if(tile){
     using(var brush=new TextureBrush(source)){brush.TranslateTransform(screen.Left-display.Left,screen.Top-display.Top);graphics.FillRectangle(brush,new Rectangle(Point.Empty,display.Size));}
     return;
    }
    double horizontal=screen.Width/(double)source.Width,vertical=screen.Height/(double)source.Height;
    double fit=style==6?Math.Min(horizontal,vertical):Math.Max(horizontal,vertical);
    int width=style==2?screen.Width:style==0?source.Width:(int)Math.Round(source.Width*fit);
    int height=style==2?screen.Height:style==0?source.Height:(int)Math.Round(source.Height*fit);
    var placement=new Rectangle(screen.X-display.Left+(screen.Width-width)/2,screen.Y-display.Top+(screen.Height-height)/2,width,height);
    graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;
    graphics.DrawImage(source,placement);
   }
  }catch{}
 }
 protected override void OnPaintBackground(PaintEventArgs e){}
 protected override void OnPaint(PaintEventArgs e){
  base.OnPaint(e);
  var graphics=e.Graphics;
  graphics.SmoothingMode=SmoothingMode.AntiAlias;
  DrawBackground(graphics,e.ClipRectangle);
  if(selectionFrame!=null){
   Rectangle target=selectionFrameBounds;
   target.Offset(-Left,-Top);
   if(selectionFrame.Size==target.Size)graphics.DrawImageUnscaled(selectionFrame,target.Location);
   else graphics.DrawImage(selectionFrame,target,new Rectangle(Point.Empty,selectionFrame.Size),GraphicsUnit.Pixel);
   return;
  }
  if(!drawnWithCaptions)return;
  for(int i=0;i<cards.Count;i++){
   if(!cards[i].Visible||cards[i].AttachedCaption)continue;
   CarouselTile.DrawCaption(graphics,catalog.Apps[i],CaptionBounds(cards[i].PreviewRect),labelFont,scale);
  }
 }
 Rectangle CaptionBounds(Rectangle preview){
  return Rectangle.FromLTRB(preview.Left-Left+S(8),preview.Top-Top-S(38),preview.Right-Left,preview.Top-Top-S(10));
 }
 internal void CancelImmediate(){if(!Visible)return;int restore=original;Dismiss();catalog.Activate(restore);if(Completed!=null)Completed("Cancelled");}
 void Dismiss(){if(highResolution){timeEndPeriod(1);highResolution=false;}animation.Stop();idle.Stop();closeWait.Stop();CancelHorizontalScroll();closing=closePending=overviewEnterPending=false;inertia.Active=false;touching=Overview=false;if(mouseContact){mouseContact=false;Capture=false;}Hide();SetTaskbarReveal(false);ReleaseSelectionFrame();foreach(var card in cards)card.Dispose();cards.Clear();}
 internal void StepOverview(int step){
  if(!Overview||activateAfter||touching||closing||closePending)return;
  CancelHorizontalScroll();
  inertia.Active=false;rail.Target=CarouselMotion.Clamp((int)Math.Round(rail.Target)+step,cards.Count);
  idle.Stop();idle.Start();StartAnimation();
 }
 bool ScrollHorizontally(int delta){
  if(delta==0||!MouseInputEnabled||!Overview||activateAfter||touching||closing||closePending)return false;
  if(!wheelScrolling){
   wheelScrolling=true;inertia.Active=false;wheelPosition=rail.Value;
  }
  wheelPosition=Math.Max(-.65,Math.Min(cards.Count-1+.65,wheelPosition+delta/120.0*HorizontalWheelPagesPerDetent));
  rail.Set(CarouselMotion.Rubber(wheelPosition,cards.Count));
  wheelQuiet.Stop();wheelQuiet.Start();idle.Stop();idle.Start();StartAnimation();
  return true;
 }
 void CancelHorizontalScroll(){wheelQuiet.Stop();wheelScrolling=false;}
 void EndHorizontalScroll(){
  bool wasScrolling=wheelScrolling;
  CancelHorizontalScroll();
  if(!wasScrolling||!Visible||!Overview)return;
  rail.Target=CarouselMotion.Clamp((int)Math.Round(rail.Value),cards.Count);
  StartAnimation();
 }
 protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(MouseInputEnabled&&e.Button==MouseButtons.Left&&Overview&&Begin(e.X,e.Y)){mouseContact=true;Capture=true;}}
 protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(mouseContact)MoveFinger(e.X,e.Y);}
 protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(!mouseContact||e.Button!=MouseButtons.Left)return;mouseContact=false;Capture=false;MoveFinger(e.X,e.Y);EndFinger();}
 protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(mouseContact&&!Capture){mouseContact=false;CancelImmediate();}}
 protected override void WndProc(ref Message message){
  const int VerticalWheel=0x020a,HorizontalWheel=0x020e;
  if(message.Msg==VerticalWheel&&Overview){message.Result=IntPtr.Zero;return;}
  if(message.Msg==HorizontalWheel&&Overview&&(UseRawPadScroll||ScrollHorizontally((short)(message.WParam.ToInt64()>>16)))){message.Result=IntPtr.Zero;return;}
  base.WndProc(ref message);
 }
 protected override void OnKeyDown(KeyEventArgs e){
  base.OnKeyDown(e);
  if(e.KeyCode==Keys.Escape){CancelImmediate();e.Handled=true;}
  else if(Overview&&(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right)){StepOverview(e.KeyCode==Keys.Left?-1:1);e.Handled=true;}
  else if(Overview&&e.KeyCode==Keys.Enter){
   if(!touching&&!closing&&!closePending&&!activateAfter)overviewEnterPending=true;
   e.Handled=true;
  }
 }
 protected override void OnKeyUp(KeyEventArgs e){
  base.OnKeyUp(e);
  if(e.KeyCode!=Keys.Enter||!overviewEnterPending)return;
  overviewEnterPending=false;
  if(Overview&&!touching&&!closing&&!closePending&&!activateAfter)
   SelectOverview(CenteredCard(),true,true);
  e.Handled=true;
 }
 protected override void Dispose(bool disposing){if(disposing&&!disposed){disposed=true;Dismiss();animation.Dispose();idle.Dispose();closeWait.Dispose();wheelQuiet.Dispose();revealRefresh.Dispose();backdropWarmup.Dispose();catalog.Dispose();labelFont.Dispose();ReleaseSurface(ref desktopDc,ref desktopBitmap);ReleaseSurface(ref matteDc,ref matteBitmap);}base.Dispose(disposing);}
}
