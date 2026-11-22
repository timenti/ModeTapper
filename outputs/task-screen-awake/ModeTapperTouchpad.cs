using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed class TouchFrame {
 internal int Count,Id;internal double X,Y,Width,Height;internal bool Confident,Pressed,ButtonDown;
 internal int SampledContacts;
 internal int FirstContactId,SecondContactId;
 internal double CenterX,CenterY,SpanX,SpanY;
}
internal sealed class RawTouchpad : NativeWindow,IDisposable {
 [StructLayout(LayoutKind.Sequential)]struct Device {public IntPtr Handle;public uint Type;}
 [StructLayout(LayoutKind.Sequential)]struct Registration {public ushort Page,Usage;public uint Flags;public IntPtr Window;}
 sealed class Pad {internal IntPtr Data;internal int Xmin,Xmax,Ymin,Ymax;internal double Width,Height;internal ushort[] Links;internal byte Report;internal HashSet<ushort> ConfidenceButtons;}
 [DllImport("user32.dll",SetLastError=true)]static extern uint GetRawInputDeviceList([Out]Device[] list,ref uint count,uint size);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetRawInputDeviceInfo(IntPtr device,uint command,IntPtr data,ref uint size);
 [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterRawInputDevices(Registration[] devices,uint count,uint size);
 [DllImport("user32.dll")]static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint bytes,uint headerSize);
 [DllImport("hid.dll")]static extern int HidP_GetCaps(IntPtr data,[Out]byte[] caps);
 [DllImport("hid.dll")]static extern int HidP_GetValueCaps(int type,IntPtr caps,ref ushort count,IntPtr data);
 [DllImport("hid.dll")]static extern int HidP_GetSpecificButtonCaps(int type,ushort page,ushort link,ushort usage,IntPtr caps,ref ushort count,IntPtr data);
 [DllImport("hid.dll")]static extern int HidP_GetUsageValue(int type,ushort page,ushort link,ushort usage,out uint value,IntPtr data,IntPtr report,uint bytes);
 [DllImport("hid.dll")]static extern int HidP_GetUsages(int type,ushort page,ushort link,[Out]ushort[] usages,ref uint count,IntPtr data,IntPtr report,uint bytes);
 const int HidUsageNotFound=unchecked((int)0xc0110004);
 readonly Dictionary<IntPtr,Pad> pads=new Dictionary<IntPtr,Pad>();readonly ushort[] usages=new ushort[32];
 readonly ushort readerUsage;IntPtr buffer=Marshal.AllocHGlobal(4096);uint capacity=4096;bool disposed;
 internal event Action<TouchFrame> Frame;internal event Action<string> Failed;
 internal int DeviceCount {get{return pads.Count;}}internal long Reports {get;private set;}
 internal static bool HasUsage(ushort usage){
  uint count=0,size=(uint)Marshal.SizeOf(typeof(Device));
  if(GetRawInputDeviceList(null,ref count,size)==UInt32.MaxValue||count==0)return false;
  var list=new Device[count];
  uint found=GetRawInputDeviceList(list,ref count,size);
  if(found==UInt32.MaxValue)return false;
  const uint infoSize=32,deviceInfo=0x2000000b;
  IntPtr info=Marshal.AllocHGlobal((int)infoSize);
  try{
   for(int i=0;i<found;i++){
    var device=list[i];if(device.Type!=2)continue;
    uint bytes=infoSize;
    Marshal.WriteInt32(info,(int)infoSize);
    if(GetRawInputDeviceInfo(device.Handle,deviceInfo,info,ref bytes)!=UInt32.MaxValue&&
       Marshal.ReadInt16(info,20)==13&&Marshal.ReadInt16(info,22)==usage)return true;
   }
   return false;
  }finally{Marshal.FreeHGlobal(info);}
 }
 internal RawTouchpad(ushort usage=5,bool allowMissing=false){readerUsage=usage;CreateHandle(new CreateParams{Parent=new IntPtr(-3)});try{Refresh();if(pads.Count==0&&!allowMissing)throw new InvalidOperationException("Precision Touchpad не найден");var registration=new[]{new Registration{Page=13,Usage=readerUsage,Flags=0x2100,Window=Handle}};if(!RegisterRawInputDevices(registration,1,(uint)Marshal.SizeOf(typeof(Registration))))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}catch{Dispose();throw;}}
 void Refresh(){
  foreach(var pad in pads.Values)Marshal.FreeHGlobal(pad.Data);pads.Clear();uint count=0,size=(uint)Marshal.SizeOf(typeof(Device));GetRawInputDeviceList(null,ref count,size);var list=new Device[count];if(GetRawInputDeviceList(list,ref count,size)==UInt32.MaxValue)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  foreach(var device in list){if(device.Type!=2)continue;uint bytes=0;GetRawInputDeviceInfo(device.Handle,0x20000005,IntPtr.Zero,ref bytes);if(bytes==0)continue;IntPtr data=Marshal.AllocHGlobal((int)bytes);bool keep=false;try{
   if(GetRawInputDeviceInfo(device.Handle,0x20000005,data,ref bytes)==UInt32.MaxValue)continue;var caps=new byte[64];if(HidP_GetCaps(data,caps)<0||BitConverter.ToUInt16(caps,0)!=readerUsage||BitConverter.ToUInt16(caps,2)!=13)continue;
   ushort length=BitConverter.ToUInt16(caps,48);IntPtr values=Marshal.AllocHGlobal(length*72);var pad=new Pad{Data=data};var links=new HashSet<ushort>();try{if(HidP_GetValueCaps(0,values,ref length,data)<0)continue;for(int i=0;i<length;i++){IntPtr cap=IntPtr.Add(values,i*72);ushort page=(ushort)Marshal.ReadInt16(cap,0),usage=(ushort)Marshal.ReadInt16(cap,56),link=(ushort)Marshal.ReadInt16(cap,6);if(link==0||page!=1)continue;if(usage==48){links.Add(link);pad.Xmin=Marshal.ReadInt32(cap,40);pad.Xmax=Marshal.ReadInt32(cap,44);pad.Width=(Marshal.ReadInt32(cap,52)-Marshal.ReadInt32(cap,48))/100.0;pad.Report=Marshal.ReadByte(cap,2);}else if(usage==49){pad.Ymin=Marshal.ReadInt32(cap,40);pad.Ymax=Marshal.ReadInt32(cap,44);pad.Height=(Marshal.ReadInt32(cap,52)-Marshal.ReadInt32(cap,48))/100.0;}}}finally{Marshal.FreeHGlobal(values);}
   if(pad.Xmax<=pad.Xmin||pad.Ymax<=pad.Ymin||links.Count==0)continue;if(pad.Width<20||pad.Width>300)pad.Width=125;if(pad.Height<20||pad.Height>200)pad.Height=70;pad.Links=new ushort[links.Count];links.CopyTo(pad.Links);
   if(readerUsage==4)pad.ConfidenceButtons=ReadConfidenceButtons(pad,BitConverter.ToUInt16(caps,46));
   pads.Add(device.Handle,pad);keep=true;
  }finally{if(!keep)Marshal.FreeHGlobal(data);}}
 }
 static HashSet<ushort> ReadConfidenceButtons(Pad pad,ushort count){
  var links=new HashSet<ushort>();
  if(count==0)return links;
  const int buttonCapsSize=72;
  IntPtr caps=Marshal.AllocHGlobal(count*buttonCapsSize);
  try{
   int status=HidP_GetSpecificButtonCaps(0,13,0,71,caps,ref count,pad.Data);
   if(status==HidUsageNotFound)return links;
   if(status<0)return null;
   for(int i=0;i<count;i++){
    IntPtr cap=IntPtr.Add(caps,i*buttonCapsSize);
    if(Marshal.ReadByte(cap,2)==pad.Report)links.Add((ushort)Marshal.ReadInt16(cap,6));
   }
   return links;
  }finally{Marshal.FreeHGlobal(caps);}
 }
 protected override void WndProc(ref Message m){
  try{if(m.Msg==0x00FE){if(Frame!=null)Frame(new TouchFrame());Refresh();}else if(m.Msg==0x00FF)Read(m.LParam);}catch(Exception ex){if(Failed!=null)Failed(ex.Message);}
  base.WndProc(ref m);
 }
 void Read(IntPtr input){
  uint bytes=capacity,header=(uint)(IntPtr.Size==8?24:16);uint got=GetRawInputData(input,0x10000003,buffer,ref bytes,header);if(got==UInt32.MaxValue){if(bytes>capacity){Marshal.FreeHGlobal(buffer);capacity=bytes;buffer=Marshal.AllocHGlobal((int)capacity);got=GetRawInputData(input,0x10000003,buffer,ref bytes,header);}if(got==UInt32.MaxValue)return;}
  if(got<header+8||Marshal.ReadInt32(buffer,0)!=2)return;Pad pad;if(!pads.TryGetValue(Marshal.ReadIntPtr(buffer,8),out pad))return;int size=Marshal.ReadInt32(buffer,(int)header),count=Marshal.ReadInt32(buffer,(int)header+4);if(size<=0||count<0||(long)size*count+header+8>got)return;
  for(int index=0;index<count;index++){IntPtr report=IntPtr.Add(buffer,(int)header+8+index*size);if(Marshal.ReadByte(report)!=pad.Report)continue;Reports++;uint contactCount;HidP_GetUsageValue(0,13,0,84,out contactCount,pad.Data,report,(uint)size);var frame=new TouchFrame{Count=(int)contactCount,Width=pad.Width,Height=pad.Height,Confident=true};int tips=0;
   foreach(ushort link in pad.Links){uint length=32;if(HidP_GetUsages(0,13,link,usages,ref length,pad.Data,report,(uint)size)<0)continue;bool tip=false,confidence=false;for(int u=0;u<length;u++){if(usages[u]==66)tip=true;if(usages[u]==71)confidence=true;}if(!tip)continue;tips++;uint x,y,id;if(HidP_GetUsageValue(0,1,link,48,out x,pad.Data,report,(uint)size)<0||HidP_GetUsageValue(0,1,link,49,out y,pad.Data,report,(uint)size)<0)continue;HidP_GetUsageValue(0,13,link,81,out id,pad.Data,report,(uint)size);frame.X=(x-pad.Xmin)*pad.Width/(pad.Xmax-pad.Xmin);frame.Y=(y-pad.Ymin)*pad.Height/(pad.Ymax-pad.Ymin);frame.Id=(int)id;
    // The Confidence usage is optional on a touchscreen. A reported false value still rejects the contact.
    if(!confidence&&pad.ConfidenceButtons!=null&&!pad.ConfidenceButtons.Contains(link)){
     uint value;
     int status=HidP_GetUsageValue(0,13,link,71,out value,pad.Data,report,(uint)size);
     confidence=status==HidUsageNotFound||(status>=0&&value!=0);
    }
    frame.Confident&=confidence;
    if(frame.SampledContacts==0)frame.FirstContactId=frame.Id;
    else if(frame.SampledContacts==1){
     frame.SecondContactId=frame.Id;frame.SpanX=Math.Abs(frame.X-frame.CenterX);frame.SpanY=Math.Abs(frame.Y-frame.CenterY);
    }
    frame.SampledContacts++;frame.CenterX+=frame.X;frame.CenterY+=frame.Y;
   }
   if(frame.SampledContacts>0){frame.CenterX/=frame.SampledContacts;frame.CenterY/=frame.SampledContacts;}
   uint buttons=32;if(HidP_GetUsages(0,9,0,usages,ref buttons,pad.Data,report,(uint)size)>=0)frame.ButtonDown=buttons>0;
   frame.Count=Math.Max(tips,frame.Count);frame.Pressed=tips>0;
   if(tips>0&&frame.SampledContacts==0&&!frame.ButtonDown)continue;
   if(Frame!=null)Frame(frame);
  }
 }
 public void Dispose(){if(disposed)return;disposed=true;if(Handle!=IntPtr.Zero){RegisterRawInputDevices(new[]{new Registration{Page=13,Usage=readerUsage,Flags=1}},1,(uint)Marshal.SizeOf(typeof(Registration)));DestroyHandle();}foreach(var p in pads.Values)Marshal.FreeHGlobal(p.Data);pads.Clear();if(buffer!=IntPtr.Zero){Marshal.FreeHGlobal(buffer);buffer=IntPtr.Zero;}}
}
internal enum PadEdge {None,Keyboard,Screen,Volume}
internal enum PadSlidePhase {Move,Release,Cancel}
internal sealed class BottomPairGesture {
 internal enum Direction {None,Up,Side}
 readonly double pinchThreshold;
 bool anchored,rejected,sideBySide,upward;
 double startX,startY,startSpan;
 internal Direction Intent {get;private set;}
 internal double OffsetX {get;private set;}
 internal double OffsetY {get;private set;}
 internal bool InProgress {get;private set;}
 internal bool Claimed {get;private set;}
 internal bool CanSwipeSide {get{return anchored&&sideBySide&&!rejected&&!upward;}}
 internal BottomPairGesture(double pinchThreshold=0){this.pinchThreshold=pinchThreshold;}
 internal void Reset(){InProgress=Claimed=anchored=rejected=upward=false;Intent=Direction.None;OffsetX=OffsetY=0;}
 internal void Update(TouchFrame frame,double x,double y,bool startsAtEdge,double minimumUp,double minimumSide=0){
  if(!frame.Pressed){Reset();return;}
  if(frame.Count<2){if(InProgress){rejected=true;Claimed=false;Intent=Direction.None;}return;}
  InProgress=true;
  if(frame.Count!=2||!frame.Confident||frame.ButtonDown){rejected=true;Claimed=false;Intent=Direction.None;return;}
  if(frame.SampledContacts!=2||rejected)return;
  if(!anchored){
   anchored=true;startX=x;startY=y;rejected=!startsAtEdge;
   sideBySide=minimumSide>0&&frame.SpanX>frame.SpanY*1.2;
   if(pinchThreshold>0)startSpan=Math.Sqrt(frame.SpanX*frame.SpanX+frame.SpanY*frame.SpanY);
   return;
  }
  OffsetX=x-startX;
  OffsetY=y-startY;
  if(Intent!=Direction.None)return;
  double dx=Math.Abs(OffsetX),up=-OffsetY;
  if(pinchThreshold>0){
   double span=Math.Sqrt(frame.SpanX*frame.SpanX+frame.SpanY*frame.SpanY);
   double spanMoved=Math.Abs(span-startSpan);
   if(spanMoved>=pinchThreshold&&spanMoved>=1.15*Math.Sqrt(dx*dx+up*up)){
    rejected=true;Claimed=false;return;
   }
  }
  bool horizontal=dx>minimumUp*.25&&dx>Math.Abs(up)*1.35;
  if(horizontal&&!sideBySide||up<-minimumUp*.25){rejected=true;Claimed=false;return;}
  bool movingUp=up>=minimumUp*.25&&up>dx*1.35;
  upward|=movingUp;
  Claimed=horizontal||movingUp;
  if(horizontal&&dx>=minimumSide)Intent=Direction.Side;
  else if(up>=minimumUp&&up>dx*1.35)Intent=Direction.Up;
 }
}
internal sealed class EdgeGesture {
 internal event Action<int,PadEdge,double> Motion;
 internal event Action<PadSlidePhase,double,double> Slide;
 internal event Action<PadSlidePhase,double,double> OverviewDrag;
 const double BottomTolerance=9,AppSwipe=9,OverviewSwipe=12;
 const double SideStart=6,TopStart=7.5,SideArming=9,TopArming=11;
 readonly BottomPairGesture bottomPair=new BottomPairGesture(3);
 bool tracking,rejected,active;int finger,serial,candidates;double x0,y0,anchor;long began;
 bool sliding,hadContacts,firstContactAtBottom;
 long sideStarted,padTargetUntil,wheelUntil;
 internal bool FollowingOverview {get;private set;}
 internal bool WindowGestureClaimed {get{return bottomPair.Claimed||sliding||FollowingOverview;}}
 internal static bool PairStartsAtBottom(TouchFrame frame){
  return frame.Height>0&&frame.CenterY-frame.SpanY*.5>=frame.Height-BottomTolerance;
 }
 volatile bool suppress,escape;long last;
 internal bool Suppressing {get{return suppress&&!escape&&Stopwatch.GetTimestamp()-Interlocked.Read(ref last)<Ticks(140);}}
 internal bool ReservePadTarget {get{return !escape&&Stopwatch.GetTimestamp()<Interlocked.Read(ref padTargetUntil);}}
 internal bool BlocksHorizontalWheel {get{return !escape&&Stopwatch.GetTimestamp()<Interlocked.Read(ref wheelUntil);}}
 internal PadEdge Edge {get;private set;}
 static long Ticks(int milliseconds){return Stopwatch.Frequency*milliseconds/1000;}
 internal void EmergencyCancel(){escape=true;suppress=false;Interlocked.Exchange(ref padTargetUntil,0);Interlocked.Exchange(ref wheelUntil,0);}
 internal void Reset(){ResetContacts();Interlocked.Exchange(ref wheelUntil,0);}
 void ResetContacts(){
  FinishGesture(false);bottomPair.Reset();tracking=rejected=active=suppress=escape=hadContacts=firstContactAtBottom=false;
  sideStarted=0;Interlocked.Exchange(ref padTargetUntil,0);Edge=PadEdge.None;
 }
 void ReserveHorizontalSwipe(long now){
  if(sideStarted==0)sideStarted=now;
  long until=bottomPair.Claimed?now+Ticks(140):sideStarted+Ticks(200);
  Interlocked.Exchange(ref padTargetUntil,until);
  Interlocked.Exchange(ref wheelUntil,until);
 }
 void ReleasePadTarget(long now,bool normalRelease){
  if(Interlocked.Exchange(ref padTargetUntil,0)==0)return;
  Interlocked.Exchange(ref wheelUntil,normalRelease&&sliding?now+Ticks(420):0);
 }
 void FinishGesture(bool commit){
  var phase=commit?PadSlidePhase.Release:PadSlidePhase.Cancel;
  if(sliding){sliding=false;if(Slide!=null)Slide(phase,0,0);}
  if(FollowingOverview){FollowingOverview=false;if(OverviewDrag!=null)OverviewDrag(phase,0,0);}
 }
 internal void Update(TouchFrame f,long now){
  Interlocked.Exchange(ref last,now);
  if(!f.Pressed||f.Count==0){
   ReleasePadTarget(now,f.Width>0&&!escape);
   if(f.Width<=0)Interlocked.Exchange(ref wheelUntil,0);
   FinishGesture(f.Width>0&&!escape);
   ResetContacts();
   return;
  }
  if(!hadContacts){hadContacts=true;Interlocked.Exchange(ref wheelUntil,0);}
  if(f.Count>1||bottomPair.InProgress){
   rejected=true;
   // Both fingers must start in the lower strip to reserve a window gesture.
   bool startsAtBottom=PairStartsAtBottom(f);
   bottomPair.Update(f,f.CenterX,f.CenterY,startsAtBottom,OverviewSwipe,AppSwipe);
   suppress=!escape&&bottomPair.Claimed&&f.Count==2;
   if(!escape&&f.Count==2&&f.Confident&&!f.ButtonDown&&bottomPair.CanSwipeSide)ReserveHorizontalSwipe(now);
   else if(escape||f.Count!=2||!f.Confident||f.ButtonDown||f.SampledContacts==2)
    ReleasePadTarget(now,!escape&&f.Count<2&&!f.ButtonDown&&f.Confident&&f.Width>0);
   if(escape||f.Count>2||f.ButtonDown||!f.Confident)FinishGesture(false);
   else if(f.Count<2)FinishGesture(f.Width>0);
   else if(bottomPair.Intent==BottomPairGesture.Direction.Side&&f.SampledContacts==2){
    sliding=true;
    if(Slide!=null)Slide(PadSlidePhase.Move,bottomPair.OffsetX,f.Width);
   }else if(bottomPair.Intent==BottomPairGesture.Direction.Up&&f.SampledContacts==2){
    FollowingOverview=true;
    if(OverviewDrag!=null)OverviewDrag(PadSlidePhase.Move,-bottomPair.OffsetY,f.Height);
   }
   return;
  }
  if(!tracking){tracking=true;finger=f.Id;x0=f.X;y0=f.Y;began=now;Edge=PadEdge.None;
   firstContactAtBottom=f.Width>0&&f.Height>0&&f.Y>=f.Height-BottomTolerance;
   candidates=0;if(f.X<=SideStart)candidates|=1;if(f.X>=f.Width-SideStart)candidates|=2;if(f.Y<=TopStart)candidates|=4;
   Edge=candidates==1?PadEdge.Keyboard:candidates==2?PadEdge.Screen:candidates==4?PadEdge.Volume:PadEdge.None;
   rejected=candidates==0;
  }
  // Direct Manipulation can pick its destination while only the first finger is down.
  // Reserve pointer routing briefly at the origin without blocking wheel or cursor input.
  if(firstContactAtBottom&&!escape&&f.Confident&&!f.ButtonDown&&f.Id==finger&&
     Math.Abs(f.X-x0)<=2&&Math.Abs(f.Y-y0)<=2){
   Interlocked.Exchange(ref padTargetUntil,began+Ticks(260));
  }else{
   firstContactAtBottom=false;
   ReleasePadTarget(now,false);
  }
  if(escape||f.Count!=1||(!active&&!f.Confident)||f.ButtonDown||f.Id!=finger){FinishGesture(false);rejected=true;suppress=false;return;}
  if(rejected)return;
  if(!f.Confident)return;
  double dx=f.X-x0,dy=f.Y-y0;
  double ms=(now-began)*1000.0/System.Diagnostics.Stopwatch.Frequency;
  if(Edge==PadEdge.None){
   suppress=true;
   bool top=(candidates&4)!=0&&f.Y<=TopArming,left=(candidates&1)!=0&&f.X<=SideArming,right=(candidates&2)!=0&&f.X>=f.Width-SideArming;
   if(ms>1200||(!top&&!left&&!right)){rejected=true;suppress=false;return;}
   if(top&&Math.Abs(dx)>=4&&Math.Abs(dx)>=Math.Abs(dy)*2.0)Edge=PadEdge.Volume;
   else if((left||right)&&Math.Abs(dy)>=4&&Math.Abs(dy)>=Math.Abs(dx)*2.0)Edge=left?PadEdge.Keyboard:PadEdge.Screen;
   else{if(Math.Max(Math.Abs(dx),Math.Abs(dy))>=8){rejected=true;suppress=false;}return;}
  }
  double axis=Edge==PadEdge.Volume?dx:-dy;
  double cross=Edge==PadEdge.Volume?Math.Abs(f.Y-y0):Math.Abs(f.X-x0);
  bool inside=Edge==PadEdge.Volume?f.Y<=TopArming:Edge==PadEdge.Keyboard?f.X<=SideArming:f.X>=f.Width-SideArming;
  if(!active&&(!inside||cross>4.5||ms>1200)){rejected=true;suppress=false;return;}
  suppress=true;
  if(!active){if(Math.Abs(axis)<4||Math.Abs(axis)<cross*2.0||ms<35)return;active=true;anchor=axis;serial++;}
  if(Motion!=null)Motion(serial,Edge,axis-anchor);
 }
}
// Win32 wheel filtering cannot see a gesture delivered by Direct Manipulation.
// Route the gesture that begins under the cursor to our inactive window instead.
internal sealed class PadSwipeTarget : NativeWindow,IDisposable {
 const int Radius=4;
 const int Extent=2*Radius+1;
 [StructLayout(LayoutKind.Sequential)]struct DibHeader {
  public uint Size;public int Width,Height;public ushort Planes,Bits;
  public uint Compression,ImageSize;public int XResolution,YResolution;public uint Colors,ImportantColors;
 }
 [StructLayout(LayoutKind.Sequential)]struct Blend {public byte Operation,Flags,Alpha,Format;}
 [StructLayout(LayoutKind.Sequential)]struct CursorInfo {
  public int Size,Flags;public IntPtr Cursor;public System.Drawing.Point Position;
 }
 [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate bool RegisterCapable(IntPtr window,bool enabled);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string name);
 [DllImport("kernel32.dll")]static extern IntPtr GetProcAddress(IntPtr module,IntPtr ordinal);
 [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleDC(IntPtr dc);
 [DllImport("gdi32.dll")]static extern IntPtr CreateDIBSection(IntPtr dc,ref DibHeader header,uint usage,out IntPtr pixels,IntPtr section,uint offset);
 [DllImport("gdi32.dll")]static extern IntPtr SelectObject(IntPtr dc,IntPtr bitmap);
 [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr bitmap);
 [DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr dc);
 [DllImport("user32.dll")]static extern bool UpdateLayeredWindow(IntPtr window,IntPtr desktop,IntPtr position,ref System.Drawing.Size size,
  IntPtr source,ref System.Drawing.Point origin,uint color,ref Blend blend,uint flags);
 [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr window,int command);
 [DllImport("user32.dll")]static extern bool GetPointerType(uint pointer,out uint type);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr LoadCursor(IntPtr library,IntPtr resource);
 [DllImport("user32.dll")]static extern IntPtr SetCursor(IntPtr cursor);
 [DllImport("user32.dll")]static extern bool GetCursorInfo(ref CursorInfo info);
 [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(System.Drawing.Point point);
 static readonly int CursorInfoSize=Marshal.SizeOf(typeof(CursorInfo));
 readonly IntPtr normalCursor=LoadCursor(IntPtr.Zero,new IntPtr(32512));
 readonly IntPtr busyCursor=LoadCursor(IntPtr.Zero,new IntPtr(32514));
 readonly IntPtr startingCursor=LoadCursor(IntPtr.Zero,new IntPtr(32650));
 volatile bool visible;
 readonly bool available;
 System.Drawing.Point center;
 IntPtr gestureCursor;
 int pointerMessages;
 internal int PointerMessages {get{return Volatile.Read(ref pointerMessages);}}
 internal bool Available {get{return available;}}
 internal bool Visible {get{return visible;}}
 internal PadSwipeTarget(){
  IntPtr register=GetProcAddress(GetModuleHandle("user32.dll"),new IntPtr(2689));
  if(register==IntPtr.Zero)return;
  CreateHandle(new CreateParams{Style=unchecked((int)0x80000000),ExStyle=0x08080088,Caption="ModeTapper touchpad gesture"});
  var enable=(RegisterCapable)Marshal.GetDelegateForFunctionPointer(register,typeof(RegisterCapable));
  available=enable(Handle,true)&&PrepareSurface();
 }
 bool PrepareSurface(){
  // A real alpha surface is hittable immediately, including on another UI thread.
  IntPtr dc=CreateCompatibleDC(IntPtr.Zero);
  if(dc==IntPtr.Zero)return false;
  IntPtr bitmap=IntPtr.Zero,original=IntPtr.Zero;
  try{
   var header=new DibHeader{Size=40,Width=Extent,Height=-Extent,Planes=1,Bits=32};
   IntPtr bits;
   bitmap=CreateDIBSection(dc,ref header,0,out bits,IntPtr.Zero,0);
   if(bitmap==IntPtr.Zero||bits==IntPtr.Zero)return false;
   for(int i=0;i<Extent*Extent;i++)Marshal.WriteInt32(bits,i*4,0x01000000);
   original=SelectObject(dc,bitmap);
   if(original==IntPtr.Zero)return false;
   var size=new System.Drawing.Size(Extent,Extent);
   var origin=System.Drawing.Point.Empty;
   var blend=new Blend{Alpha=255,Format=1};
   return UpdateLayeredWindow(Handle,IntPtr.Zero,IntPtr.Zero,ref size,dc,ref origin,0,ref blend,2);
  }finally{
   if(original!=IntPtr.Zero)SelectObject(dc,original);
   if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);
   DeleteDC(dc);
  }
 }
 internal void ShowAtCursor(){
  if(!available)return;
  var cursor=new CursorInfo{Size=CursorInfoSize};
  bool haveCursor=GetCursorInfo(ref cursor);
  if(!visible){
   center=haveCursor?cursor.Position:Cursor.Position;
   gestureCursor=haveCursor&&cursor.Cursor!=IntPtr.Zero&&!IsBusy(cursor.Cursor)?cursor.Cursor:normalCursor;
   visible=SetWindowPos(Handle,new IntPtr(-1),center.X-Radius,center.Y-Radius,0,0,0x51);
  }
  if(visible&&haveCursor&&(cursor.Flags&1)!=0&&IsBusy(cursor.Cursor)&&WindowFromPoint(cursor.Position)==Handle)
   SetCursor(gestureCursor);
 }
 bool IsBusy(IntPtr cursor){return cursor==busyCursor||cursor==startingCursor;}
 internal void FollowCursor(int x,int y){
  if(!visible||Math.Abs(x-center.X)<=Radius/2&&Math.Abs(y-center.Y)<=Radius/2)return;
  if(SetWindowPos(Handle,IntPtr.Zero,x-Radius,y-Radius,0,0,0x15))center=new System.Drawing.Point(x,y);
 }
 internal void Hide(){if(visible){ShowWindow(Handle,0);visible=false;}}
 protected override void WndProc(ref Message m){
  const uint Touchpad=5;
  if(m.Msg==0x0021||m.Msg==0x024b){m.Result=new IntPtr(3);return;} // MA_NOACTIVATE, PA_NOACTIVATE
  if(m.Msg==0x0020){
   SetCursor(gestureCursor!=IntPtr.Zero?gestureCursor:normalCursor);
   m.Result=new IntPtr(1);return;
  }
  if(m.Msg==0x020a||m.Msg==0x020e||m.Msg==0x024e||m.Msg==0x024f){m.Result=IntPtr.Zero;return;}
  if(m.Msg==0x0245||m.Msg==0x0246||m.Msg==0x0247){
   uint type;
   if(GetPointerType((uint)m.WParam.ToInt64()&0xffff,out type)&&type==Touchpad){
    Interlocked.Increment(ref pointerMessages);m.Result=IntPtr.Zero;return;
   }
  }
  base.WndProc(ref m);
 }
 public void Dispose(){if(Handle!=IntPtr.Zero)DestroyHandle();}
}
internal sealed class GestureMouseGate : IDisposable {
 delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
 [StructLayout(LayoutKind.Sequential)]struct Mouse {public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
 [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr module,uint thread);
 [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
 [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
 [DllImport("user32.dll")]static extern bool PostThreadMessage(uint thread,uint msg,IntPtr w,IntPtr l);
 [DllImport("user32.dll")]internal static extern short GetAsyncKeyState(int key);
 [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string name);
 readonly EdgeGesture gesture;readonly Func<bool> suppressTouchTap;readonly System.Threading.Thread thread;readonly System.Threading.ManualResetEvent ready=new System.Threading.ManualResetEvent(false);
 readonly Action<System.Drawing.Point,bool,bool> observeLeftButton;
 readonly Func<ulong,bool> suppressWheel;
 readonly Func<bool> reserveExtraTarget;
 readonly Action cancelExtraTarget;
 Control hookDispatcher;
 PadSwipeTarget target;
 int padTargetPosted,wheelBlocks;
 internal int PadPointerMessages {get{return target==null?0:target.PointerMessages;}}
 internal int HorizontalWheelBlocks {get{return Volatile.Read(ref wheelBlocks);}}
 HookProc callback;IntPtr hook;uint threadId;Exception error;
 int suppressedTouchButtons;
 internal GestureMouseGate(EdgeGesture engine,Func<bool> touchSwipe=null,Action<System.Drawing.Point,bool,bool> observeLeftButton=null,
  Func<ulong,bool> suppressWheel=null,Func<bool> reserveExtraTarget=null,Action cancelExtraTarget=null){
  gesture=engine;suppressTouchTap=touchSwipe;this.observeLeftButton=observeLeftButton;this.suppressWheel=suppressWheel;
  this.reserveExtraTarget=reserveExtraTarget;this.cancelExtraTarget=cancelExtraTarget;
  thread=new System.Threading.Thread(Run){IsBackground=true,Name="ModeTapper edge gestures"};thread.Start();
  if(!ready.WaitOne(3000))throw new InvalidOperationException("Не удалось запустить обработчик жестов");
  if(error!=null){ready.Dispose();throw error;}
 }
 internal bool PadTargetVisible {get{return target!=null&&target.Visible;}}
 bool PadTargetRequested {get{return gesture.ReservePadTarget||reserveExtraTarget!=null&&reserveExtraTarget();}}
 internal static bool IsPromotedTouch(ulong extra){return (extra&0xffffff80UL)==0xff515780UL;}
 void Run(){
  try{
   threadId=GetCurrentThreadId();
   using(var window=new Control())using(var watchdog=new System.Windows.Forms.Timer{Interval=40}){
    IntPtr h=window.Handle;hookDispatcher=window;
    if(gesture!=null){target=new PadSwipeTarget();watchdog.Tick+=(s,e)=>UpdatePadTarget(watchdog);}
    padTargetTimer=watchdog;
    callback=HandleMouse;hook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0);
    if(hook==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    ready.Set();Application.Run();
   }
  }catch(Exception ex){error=ex;ready.Set();}
  finally{if(target!=null)target.Dispose();if(hook!=IntPtr.Zero)UnhookWindowsHookEx(hook);}
 }
 System.Windows.Forms.Timer padTargetTimer;
 internal void SyncPadTarget(){
  if(target==null||!target.Available||PadTargetRequested==target.Visible||Interlocked.Exchange(ref padTargetPosted,1)!=0)return;
  try{hookDispatcher.BeginInvoke(new Action(()=>{Interlocked.Exchange(ref padTargetPosted,0);UpdatePadTarget(padTargetTimer);}));}
  catch(InvalidOperationException){Interlocked.Exchange(ref padTargetPosted,0);}
 }
 void UpdatePadTarget(System.Windows.Forms.Timer watchdog){
  if(PadTargetRequested){target.ShowAtCursor();watchdog.Start();}
  else{target.Hide();watchdog.Stop();}
 }
 IntPtr HandleMouse(int code,IntPtr message,IntPtr data){
  if(code>=0){
   int msg=message.ToInt32();
   if(suppressWheel!=null&&data!=IntPtr.Zero&&(msg==0x20a||msg==0x20e)){
    var m=(Mouse)Marshal.PtrToStructure(data,typeof(Mouse));
    if(suppressWheel(m.Extra.ToUInt64()))return new IntPtr(1);
   }
   if(observeLeftButton!=null&&data!=IntPtr.Zero&&(msg==0x201||msg==0x202)){
    var m=(Mouse)Marshal.PtrToStructure(data,typeof(Mouse));
    observeLeftButton(new System.Drawing.Point(m.X,m.Y),msg==0x201,IsPromotedTouch(m.Extra.ToUInt64()));
   }
   if(gesture!=null){
    bool mouseButton=msg>=0x201&&msg<=0x209||msg==0x20b||msg==0x20c;
    if(mouseButton&&target.Visible){gesture.EmergencyCancel();if(cancelExtraTarget!=null)cancelExtraTarget();target.Hide();}
    if(msg==0x20e&&gesture.BlocksHorizontalWheel){
     if((GetAsyncKeyState(27)&0x8000)==0){Interlocked.Increment(ref wheelBlocks);return new IntPtr(1);}
     gesture.EmergencyCancel();target.Hide();
    }
   }
   bool edge=gesture!=null&&gesture.Suppressing;
   bool button=msg>=0x201&&msg<=0x205;
   if(edge||suppressTouchTap!=null&&button){
    var m=(Mouse)Marshal.PtrToStructure(data,typeof(Mouse));
    if(suppressTouchTap!=null&&button&&IsPromotedTouch(m.Extra.ToUInt64())){
     int mask=msg<0x204?1:2;
     if(msg==0x202||msg==0x205){
      if((suppressedTouchButtons&mask)!=0){suppressedTouchButtons&=~mask;return new IntPtr(1);}
     }else if(suppressTouchTap()){
      suppressedTouchButtons|=mask;
      return new IntPtr(1);
     }else suppressedTouchButtons&=~mask;
    }
    if(edge&&(m.Flags&1)==0){
     if((GetAsyncKeyState(27)&0x8000)!=0||msg==0x201||msg==0x202||msg==0x204||msg==0x207)gesture.EmergencyCancel();
     else if(msg==0x200||msg==0x20A||msg==0x20E)return new IntPtr(1);
    }
   }
   if(gesture!=null&&target.Visible&&msg==0x200&&data!=IntPtr.Zero)
    target.FollowCursor(Marshal.ReadInt32(data),Marshal.ReadInt32(data,4));
  }
  return CallNextHookEx(hook,code,message,data);
 }
 public void Dispose(){if(gesture!=null)gesture.EmergencyCancel();if(threadId!=0)PostThreadMessage(threadId,0x12,IntPtr.Zero,IntPtr.Zero);if(thread.IsAlive)thread.Join(1000);ready.Dispose();}
}
