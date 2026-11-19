using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Timer=System.Windows.Forms.Timer;
using Microsoft.Win32;
using System.Windows.Forms;

internal sealed class TouchpadPinch {
    bool tracking, claimed, panning, rejected;
    double startX, startY, startSpan;

    internal void Reset(){tracking=claimed=panning=rejected=false;}

    internal bool Update(TouchFrame frame,Action<double> updateZoom,Action<double,double> updatePan=null){
        if(!frame.Pressed){Reset();return false;}
        if(frame.Count>2||frame.ButtonDown){
            if(tracking||frame.Count>=2)rejected=true;
            return claimed||panning;
        }
        if(frame.Count!=2||frame.SampledContacts!=2||!frame.Confident||rejected)return claimed||panning;
        double span=Math.Sqrt(frame.SpanX*frame.SpanX+frame.SpanY*frame.SpanY);
        if(!tracking){
            if(span<5)return false;
            tracking=true;startX=frame.CenterX;startY=frame.CenterY;startSpan=span;
            return false;
        }
        double dx=frame.CenterX-startX,dy=frame.CenterY-startY;
        if(!claimed&&!panning){
            double travel=Math.Sqrt(dx*dx+dy*dy),spread=Math.Abs(span-startSpan);
            if(updatePan!=null&&travel>=3.5&&travel>spread*1.45)panning=true;
            else if(travel>=7&&travel>spread*1.3){rejected=true;return false;}
            else if(spread>=Math.Max(1.8,startSpan*.055)&&spread>=travel*1.2)claimed=true;
            else return false;
        }
        if(panning)updatePan(dx,dy);
        else updateZoom(Math.Max(.25,span/startSpan));
        return true;
    }
}

internal sealed class PhotoZoomSteps {
    double lastPosition, sentPosition;
    long lastStep;
    internal void Reset(){lastPosition=sentPosition=0;lastStep=0;}
    internal void Update(double ratio,long now,Func<int,bool> step){
        double position=Math.Log(ratio);
        double direction=position-lastPosition;
        double remaining=position-sentPosition;
        lastPosition=position;
        double threshold=lastStep==0?.055:.145;
        if(Math.Abs(remaining)<threshold||remaining*direction<=0||
           lastStep!=0&&now-lastStep<Stopwatch.Frequency/18)return;
        if(step(Math.Sign(remaining))){sentPosition=position;lastStep=now;}
    }
}

internal sealed class PhotoZoomLimit {
    IntPtr window;
    int? before, settled;
    int outward, changed, stationary;
    internal void Reset(){window=IntPtr.Zero;before=settled=null;outward=changed=stationary=0;}
    internal bool Confirmed(IntPtr candidate,int? sample){
        return candidate==window&&sample.HasValue&&sample==settled&&stationary>=2&&(changed>0||outward>=16);
    }
    internal void Outward(IntPtr candidate,int? sample){
        if(candidate!=window||!before.HasValue&&settled.HasValue&&settled!=sample){Reset();window=candidate;}
        if(!sample.HasValue)return;
        if(before.HasValue&&before!=sample)Finish(candidate,sample);
        if(!before.HasValue)before=sample;
        outward++;
    }
    internal bool Finish(IntPtr candidate,int? sample){
        if(candidate!=window||!before.HasValue||!sample.HasValue)return false;
        if(sample==before)stationary++;
        else{changed++;stationary=0;}
        settled=sample;before=null;
        return Confirmed(candidate,sample);
    }
}

internal sealed class TelegramPhotoZoom {
    [StructLayout(LayoutKind.Sequential)]struct Keyboard {
        public ushort Key,Scan;
        public uint Flags,Time;
        public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)]struct Mouse {
        public int X,Y;
        public uint Data,Flags,Time;
        public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Explicit)]struct InputValue {
        [FieldOffset(0)]public Keyboard Keyboard;
        [FieldOffset(0)]public Mouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]struct Input {public uint Kind;public InputValue Value;}
    [DllImport("user32.dll")]static extern uint SendInput(uint count,Input[] input,int size);
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,uint message,IntPtr state,IntPtr point);
    [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr window,out ClientRect bounds);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder text,int capacity);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,IntPtr process);
    [DllImport("user32.dll")]static extern bool GetGUIThreadInfo(uint thread,ref GuiState state);
    [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")]static extern bool StretchBlt(IntPtr dst,int x,int y,int width,int height,
        IntPtr src,int sourceX,int sourceY,int sourceWidth,int sourceHeight,int operation);
    [StructLayout(LayoutKind.Sequential)]struct ClientRect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]struct GuiState {
        public uint Size,Flags;
        public IntPtr Active,Focus,Capture,Menu,Move,Caret;
        public ClientRect CaretBounds;
    }
    const uint InputTag=0x4c5a4f4d;
    static readonly int InputSize=Marshal.SizeOf(typeof(Input));
    static readonly IntPtr LeftButton=new IntPtr(1);
    readonly Timer dragWatch=new Timer{Interval=65};
    IntPtr zoomWindow,dragWindow,releasedWindow,guardWindow,targetWindow;
    int zoomSteps;
    Point dragStart,dragLast;
    long lastMove,lastPanReport,wheelUntil,targetUntil;
    const double PanPixelsPerMillimeter=18;

    internal TelegramPhotoZoom(){dragWatch.Tick+=(sender,args)=>CheckDrag();}
    internal IntPtr ZoomWindow {get{return Interlocked.CompareExchange(ref zoomWindow,IntPtr.Zero,IntPtr.Zero);}}
    internal static Rectangle WindowControls(IntPtr window,Rectangle bounds){
        var dpi=Math.Max(96,GetDpiForWindow(window))/96f;
        int width=Math.Min(bounds.Width,(int)Math.Ceiling(116*dpi));
        return new Rectangle(bounds.Right-width,bounds.Top,width,Math.Min(bounds.Height,(int)Math.Ceiling(28*dpi)));
    }
    internal static bool IsMediaPoint(IntPtr window,Point point){
        Rectangle bounds;
        return DesktopMotionNative.BoundsOf(window,out bounds)&&bounds.Contains(point)&&!WindowControls(window,bounds).Contains(point);
    }
    internal static int? ReadPhotoScene(IntPtr window){
        Rectangle bounds;
        if(window==IntPtr.Zero||DesktopMotionNative.Foreground()!=window||!DesktopMotionNative.BoundsOf(window,out bounds)||
           bounds.Width<300||bounds.Height<200)return null;
        bounds=Rectangle.Intersect(bounds,Screen.FromHandle(window).Bounds);
        bounds.Inflate(-bounds.Width/8,-bounds.Height/8);
        if(bounds.Width<=0||bounds.Height<=0)return null;
        IntPtr dc=GetDC(IntPtr.Zero);
        if(dc==IntPtr.Zero)return null;
        try{
            const int size=8;
            using(var sample=new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppRgb)){
                using(var graphics=Graphics.FromImage(sample)){
                    IntPtr destination=graphics.GetHdc();
                    try{
                        if(!StretchBlt(destination,0,0,size,size,dc,bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x00cc0020))return null;
                    }finally{graphics.ReleaseHdc(destination);}
                }
                int fingerprint=17;
                for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                    fingerprint=unchecked(fingerprint*31+sample.GetPixel(x,y).ToArgb());
                return fingerprint;
            }
        }finally{ReleaseDC(IntPtr.Zero,dc);}
    }

    internal static IntPtr FindViewer(Point point){
        IntPtr window=DesktopMotionNative.At(point);
        if(window==IntPtr.Zero||DesktopMotionNative.Foreground()!=window||
           !DesktopMotionNative.ClassOf(window).StartsWith("Qt",StringComparison.Ordinal)||
           !HasViewerTitle(window)||!IsMediaPoint(window,point))return IntPtr.Zero;
        try{
            using(var process=Process.GetProcessById((int)DesktopMotionNative.ProcessOf(window))){
                if(String.Equals(process.ProcessName,"Telegram",StringComparison.OrdinalIgnoreCase))return window;
            }
        }catch(ArgumentException){}catch(InvalidOperationException){}
        return IntPtr.Zero;
    }

    static bool HasViewerTitle(IntPtr window){
        var text=new StringBuilder(96);
        GetWindowText(window,text,text.Capacity);
        string title=text.ToString();
        return String.Equals(title,"Media viewer",StringComparison.OrdinalIgnoreCase)||title=="Просмотр медиа";
    }

    internal bool CanPan(IntPtr window){return window!=IntPtr.Zero&&window==zoomWindow&&zoomSteps>0;}

    internal bool Step(IntPtr window,int direction){
        if(window==IntPtr.Zero||DesktopMotionNative.Foreground()!=window||DesktopMotionNative.At(Cursor.Position)!=window||
           !IsMediaPoint(window,Cursor.Position)||
           !HasViewerTitle(window))return false;
        if(Down(0x10)||Down(0x11)||Down(0x12)||Down(0x5b)||Down(0x5c))return false;
        const ushort control=0xa2;
        // The viewer discards its leftover vertical delta on a horizontal event.
        // One unit clears that remainder without advancing the media gallery.
        uint sent=SendInput(4,new[]{Key(control,false),Wheel(1,0x1000),Wheel(direction*120,0x800),Key(control,true)},InputSize);
        if(sent>0&&sent<4)SendInput(1,new[]{Key(control,true)},InputSize);
        if(sent<3)return false;
        if(zoomWindow!=window){zoomWindow=window;zoomSteps=0;}
        zoomSteps=Math.Max(0,Math.Min(16,zoomSteps+direction));
        return true;
    }

    internal bool Pan(IntPtr window,double dx,double dy){
        if(!CanPan(window)||DesktopMotionNative.Foreground()!=window||Down(1)||Down(27)||!IsMediaPoint(window,Cursor.Position)){
            EndPan();return false;
        }
        long now=Stopwatch.GetTimestamp();
        lastPanReport=now;
        if(dragWindow==IntPtr.Zero){
            ClientRect bounds;
            if(!HasViewerTitle(window)||!GetClientRect(window,out bounds)||bounds.Right<160||bounds.Bottom<140)return false;
            dragStart=new Point(bounds.Right/2,bounds.Bottom/2);
            if(!PostMessage(window,0x201,LeftButton,Pack(dragStart)))return false;
            dragWindow=window;dragLast=dragStart;lastMove=0;releasedWindow=IntPtr.Zero;dragWatch.Start();
        }
        if(dragWindow!=window){EndPan();return false;}
        var next=new Point(dragStart.X+(int)Math.Round(dx*PanPixelsPerMillimeter),
            dragStart.Y+(int)Math.Round(dy*PanPixelsPerMillimeter));
        if(next==dragLast||now-lastMove<Stopwatch.Frequency/65)return true;
        if(!PostMessage(window,0x200,LeftButton,Pack(next))){EndPan();return false;}
        dragLast=next;lastMove=now;
        return true;
    }

    internal void EndPan(){
        if(dragWindow==IntPtr.Zero)return;
        releasedWindow=dragWindow;dragWindow=IntPtr.Zero;
        PostMessage(releasedWindow,0x202,IntPtr.Zero,Pack(dragLast));
        dragWatch.Start();
    }

    void CheckDrag(){
        if(dragWindow!=IntPtr.Zero){
            if(DesktopMotionNative.Foreground()!=dragWindow||Down(27)||Down(1)||
               Stopwatch.GetTimestamp()-lastPanReport>Stopwatch.Frequency/6)EndPan();
            return;
        }
        dragWatch.Stop();
        IntPtr window=releasedWindow;releasedWindow=IntPtr.Zero;
        if(window==IntPtr.Zero||Down(1))return;
        var state=new GuiState{Size=(uint)Marshal.SizeOf(typeof(GuiState))};
        uint thread=GetWindowThreadProcessId(window,IntPtr.Zero);
        if(thread!=0&&GetGUIThreadInfo(thread,ref state)&&state.Capture==window)
            PostMessage(window,0x1f,IntPtr.Zero,IntPtr.Zero);
    }

    internal void GuardWheel(IntPtr window){
        Interlocked.Exchange(ref guardWindow,window);
        Interlocked.Exchange(ref wheelUntil,Stopwatch.GetTimestamp()+Stopwatch.Frequency*320/1000);
    }

    internal bool BlocksWheel(ulong extra){
        if(extra==InputTag||Stopwatch.GetTimestamp()>=Interlocked.Read(ref wheelUntil))return false;
        IntPtr window=Interlocked.CompareExchange(ref guardWindow,IntPtr.Zero,IntPtr.Zero);
        return window!=IntPtr.Zero&&DesktopMotionNative.Foreground()==window&&!Down(27);
    }

    internal void ReserveTarget(IntPtr window,long until){
        Interlocked.Exchange(ref targetWindow,window);
        Interlocked.Exchange(ref targetUntil,until);
    }
    internal void ReleaseTarget(){Interlocked.Exchange(ref targetUntil,0);}
    internal bool TargetRequested(){
        if(Stopwatch.GetTimestamp()>=Interlocked.Read(ref targetUntil)||Down(27)||Down(1))return false;
        IntPtr window=Interlocked.CompareExchange(ref targetWindow,IntPtr.Zero,IntPtr.Zero);
        return window!=IntPtr.Zero&&DesktopMotionNative.Foreground()==window;
    }

    internal void Reset(){
        EndPan();ReleaseTarget();GuardWheel(IntPtr.Zero);zoomWindow=IntPtr.Zero;zoomSteps=0;
    }

    internal void Close(){Reset();CheckDrag();dragWatch.Dispose();}

    static IntPtr Pack(Point point){return new IntPtr((ushort)point.X|((long)(ushort)point.Y<<16));}
    static bool Down(int key){return (GestureMouseGate.GetAsyncKeyState(key)&0x8000)!=0;}
    static Input Wheel(int delta,uint flags){
        return new Input{Value=new InputValue{Mouse=new Mouse{Data=unchecked((uint)delta),Flags=flags,Extra=new UIntPtr(InputTag)}}};
    }
    static Input Key(ushort key,bool up){
        return new Input{Kind=1,Value=new InputValue{Keyboard=new Keyboard{Key=key,Flags=up?2u:0}}};
    }
}

internal static class NativeTouchpadZoom {
    [DllImport("user32.dll",SetLastError=true)]
    static extern bool SystemParametersInfo(uint action,uint size,[In,Out]uint[] data,uint flags);
    const uint SettingsSize=44, ZoomFlag=0x100, Broadcast=2;
    const string NativeKey=@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    static string Marker(string folder){return Path.Combine(folder,"native-pinch-suspended.txt");}
    static uint[] Read(){
        var settings=new uint[11];settings[0]=1;
        if(!SystemParametersInfo(0xae,SettingsSize,settings,0))throw new Win32Exception(Marshal.GetLastWin32Error());
        return settings;
    }
    static void Write(uint[] settings,bool enabled){
        settings[4]=enabled?settings[4]|ZoomFlag:settings[4]&~ZoomFlag;
        if(!SystemParametersInfo(0xaf,SettingsSize,settings,Broadcast))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(((Read()[4]&ZoomFlag)!=0)!=enabled)throw new InvalidOperationException("Windows не переключила щипок тачпада");
    }
    internal static void Restore(string folder){
        // Clean up the session-only override left by Luma 1.21.3.
        string marker=Marker(folder);
        if(!File.Exists(marker))return;
        bool saved=Convert.ToInt32(Registry.GetValue(NativeKey,"ZoomEnabled",1))!=0;
        uint[] settings=Read();
        if(((settings[4]&ZoomFlag)!=0)!=saved)Write(settings,saved);
        File.Delete(marker);
    }
}

internal static class MagnificationRuntime {
    [DllImport("Magnification.dll")]static extern bool MagInitialize();
    [DllImport("Magnification.dll")]static extern bool MagUninitialize();
    static int users;
    internal static bool Acquire(){
        if(users==0&&!MagInitialize())return false;
        users++;return true;
    }
    internal static void Release(){if(--users==0)MagUninitialize();}
}

internal sealed class ScreenLens : NativeWindow,IDisposable {
    [StructLayout(LayoutKind.Sequential)]struct Matrix {
        public float X,M12,M13,M21,Y,M23,M31,M32,M33;
    }
    [StructLayout(LayoutKind.Sequential)]struct Source {
        public int Left,Top,Right,Bottom;
        internal Source(Rectangle value){Left=value.Left;Top=value.Top;Right=value.Right;Bottom=value.Bottom;}
    }
    [DllImport("Magnification.dll")]static extern bool MagSetWindowTransform(IntPtr window,ref Matrix matrix);
    [DllImport("Magnification.dll")]static extern bool MagSetWindowSource(IntPtr window,Source source);
    [DllImport("Magnification.dll")]static extern bool MagSetWindowFilterList(IntPtr window,uint mode,int count,[In]IntPtr[] windows);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr CreateWindowEx(uint ex,string type,string title,uint style,
        int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")]static extern bool SetLayeredWindowAttributes(IntPtr window,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr window,int mode);
    [DllImport("user32.dll")]static extern bool InvalidateRect(IntPtr window,IntPtr bounds,bool erase);
    [DllImport("user32.dll")]static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]static extern int SetWindowRgn(IntPtr window,IntPtr region,bool redraw);
    [DllImport("gdi32.dll")]static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
    [DllImport("gdi32.dll")]static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")]static extern int CombineRgn(IntPtr destination,IntPtr first,IntPtr second,int mode);
    [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr item);

    readonly Timer clock=new Timer{Interval=16};
    readonly bool photoMode;
    IntPtr child,target,foreground;
    Rectangle display,lastPosition,lastSource,photoArea,photoControls;
    Point photoAnchor,gestureAnchor;
    PointF focus;
    PointF photoOrigin,panOrigin;
    float wanted=1,painted=1,starting=1;
    bool initialized,visible,pinching;
    long lingerUntil,lastFrame;
    internal bool Visible {get{return visible;}}
    internal float Scale {get{return wanted;}}

    internal ScreenLens(bool photoMode=false){this.photoMode=photoMode;clock.Tick+=(sender,args)=>Render();}

    internal static bool SystemMagnifierActive {
        get{return Object.Equals(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\ScreenMagnifier","RunningState",0),1);}
    }

    internal static bool UsesNativeZoom(Point point){
        IntPtr root=DesktopMotionNative.At(point);
        if(root==IntPtr.Zero)return false;
        try{
            using(var process=Process.GetProcessById((int)DesktopMotionNative.ProcessOf(root))){
                switch(process.ProcessName.ToLowerInvariant()){
                    case "chrome":case "msedge":case "firefox":case "browser":case "opera":
                    case "brave":case "vivaldi":case "waterfox":case "librewolf":case "photos":case "microsoft.photos":
                        return true;
                }
            }
        }catch(ArgumentException){}
        catch(InvalidOperationException){}
        return false;
    }

    internal void Begin(Point point){
        var root=DesktopMotionNative.At(point);
        var bounds=Screen.FromPoint(point).Bounds;
        if(target!=root||display!=bounds)Hide();
        target=root;display=bounds;foreground=DesktopMotionNative.Foreground();
        gestureAnchor=point;
        if(!visible)focus=point;
        starting=wanted;pinching=true;lingerUntil=0;
    }

    internal bool IsPhoto(IntPtr window){return photoMode&&visible&&target==window;}
    internal bool BeginPhoto(IntPtr window,Point point){
        Rectangle bounds;
        if(window==IntPtr.Zero||!DesktopMotionNative.BoundsOf(window,out bounds))return false;
        var screen=Screen.FromHandle(window).Bounds;
        Rectangle area=Rectangle.Intersect(screen,bounds);
        if(area.Width<180||area.Height<120)return false;
        Point anchor=new Point(Clamp(point.X,area.Left,area.Right-1),Clamp(point.Y,area.Top,area.Bottom-1));
        if(target!=window||photoArea!=area){
            Hide();photoOrigin=anchor;
        }else{
            var source=PhotoSource(painted);
            photoOrigin=new PointF(source.Left+(anchor.X-photoArea.Left)/painted,
                source.Top+(anchor.Y-photoArea.Top)/painted);
        }
        target=foreground=window;display=screen;photoArea=area;
        photoControls=Rectangle.Intersect(area,TelegramPhotoZoom.WindowControls(window,bounds));
        photoAnchor=anchor;panOrigin=photoOrigin;
        starting=wanted;pinching=true;clock.Interval=16;
        return true;
    }

    internal void MovePhoto(double x,double y){
        if(!photoMode||!visible||!pinching)return;
        photoOrigin=new PointF(panOrigin.X-(float)(x*18/wanted),panOrigin.Y-(float)(y*18/wanted));
    }

    internal void Zoom(double ratio){
        if(!pinching||target==IntPtr.Zero)return;
        float next=(float)Math.Max(1,Math.Min(4,starting*ratio));
        if(next==1||next==4||Math.Abs(next-wanted)>=.008f*wanted)wanted=next;
        if(wanted<=1.015f&&!visible){HideWindow();return;}
        if(!visible)Render();
        if(visible&&!clock.Enabled)clock.Start();
    }

    internal void End(){
        if(!pinching)return;
        pinching=false;
        if(photoMode)clock.Interval=100;
        else lingerUntil=Stopwatch.GetTimestamp()+Stopwatch.Frequency*4;
    }

    void Open(){
        if(child!=IntPtr.Zero)return;
        if(!MagnificationRuntime.Acquire())throw new InvalidOperationException("Лупа недоступна");
        initialized=true;
        try{
            const int hostEx=0x080800a8; // no activation, layered, click-through, tool window, topmost
            CreateHandle(new CreateParams{Style=unchecked((int)0x82000000),ExStyle=hostEx});
            child=CreateWindowEx(0,"Magnifier",null,0x50000000,0,0,1,1,Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(child==IntPtr.Zero||!SetLayeredWindowAttributes(Handle,0,255,2)||
               !MagSetWindowFilterList(child,0,1,new[]{Handle}))
                throw new InvalidOperationException("Не удалось открыть лупу ModeTapper");
        }catch{Close();throw;}
    }

    void Render(){
        Point point=Cursor.Position;
        if(Safety.Safe||DesktopMotionNative.Foreground()!=foreground||
           (GestureMouseGate.GetAsyncKeyState(27)&0x8000)!=0||
           !photoMode&&(!display.Contains(point)||(GestureMouseGate.GetAsyncKeyState(1)&0x8000)!=0||
             !pinching&&Stopwatch.GetTimestamp()>=lingerUntil)){Hide();return;}
        Rectangle windowBounds;
        if(photoMode&&(!DesktopMotionNative.BoundsOf(target,out windowBounds)||
           Rectangle.Intersect(display,windowBounds)!=photoArea)){Hide();return;}
        Open();
        long now=Stopwatch.GetTimestamp();
        double elapsed=lastFrame==0?16:Math.Min(40,1000d*(now-lastFrame)/Stopwatch.Frequency);
        lastFrame=now;
        float factor=visible?Follow(painted,wanted,elapsed,pinching?13:26,.006f):wanted;
        if(factor<=1.015f&&wanted<=1.015f){HideWindow();return;}
        float scale=Math.Max(96,GetDpiForWindow(target))/96f;
        int w=Math.Min((int)(360*scale),display.Width/2),h=Math.Min((int)(236*scale),display.Height/2);
        int gap=(int)(26*scale);
        if(!photoMode){
            Point desired=pinching?gestureAnchor:point;
            focus=new PointF(Follow(focus.X,desired.X,elapsed,17,.65f),Follow(focus.Y,desired.Y,elapsed,17,.65f));
            point=new Point((int)Math.Round(focus.X),(int)Math.Round(focus.Y));
        }
        int left=point.X+gap+w<=display.Right?point.X+gap:point.X-gap-w;
        var position=photoMode?photoArea:
            new Rectangle(Clamp(left,display.Left,display.Right-w),Clamp(point.Y-h/2,display.Top,display.Bottom-h),w,h);
        w=position.Width;h=position.Height;
        if(position.Size!=lastPosition.Size){
            var region=CreateLensRegion(position,(int)(20*scale));
            if(region!=IntPtr.Zero&&SetWindowRgn(Handle,region,false)==0)DeleteObject(region);
            if(!SetWindowPos(child,IntPtr.Zero,0,0,w,h,0x14)){Close();return;}
        }
        if(factor!=painted){
            var matrix=new Matrix{X=factor,Y=factor,M33=1};
            if(!MagSetWindowTransform(child,ref matrix)){Close();return;}
            painted=factor;
        }
        int sourceW=(int)Math.Ceiling(w/factor),sourceH=(int)Math.Ceiling(h/factor);
        var source=photoMode?PhotoSource(factor):
            new Rectangle(Clamp(point.X-sourceW/2,display.Left,display.Right-sourceW),
                Clamp(point.Y-sourceH/2,display.Top,display.Bottom-sourceH),sourceW,sourceH);
        bool sourceChanged=source!=lastSource;
        if(sourceChanged&&!MagSetWindowSource(child,new Source(source))){Close();return;}
        if(position!=lastPosition||!visible){
            if(!SetWindowPos(Handle,new IntPtr(-1),position.X,position.Y,w,h,visible?0x54u:0x50u)){Close();return;}
            visible=true;
            lastPosition=position;
        }
        lastSource=source;
        InvalidateRect(child,IntPtr.Zero,false);
        if(sourceChanged)UpdateWindow(child);
    }

    IntPtr CreateLensRegion(Rectangle position,int corner){
        if(!photoMode)return CreateRoundRectRgn(0,0,position.Width+1,position.Height+1,corner,corner);
        IntPtr region=CreateRectRgn(0,0,position.Width,position.Height);
        if(region==IntPtr.Zero)return region;
        Rectangle controls=photoControls;
        if(controls.IsEmpty)return region;
        controls.Offset(-position.Left,-position.Top);
        IntPtr cutout=CreateRectRgn(controls.Left,controls.Top,controls.Right,controls.Bottom);
        if(cutout!=IntPtr.Zero){
            CombineRgn(region,region,cutout,4);
            DeleteObject(cutout);
        }
        return region;
    }

    Rectangle PhotoSource(float factor){
        int width=(int)Math.Ceiling(photoArea.Width/factor),height=(int)Math.Ceiling(photoArea.Height/factor);
        int x=(int)Math.Round(photoOrigin.X-(photoAnchor.X-photoArea.Left)/factor);
        int y=(int)Math.Round(photoOrigin.Y-(photoAnchor.Y-photoArea.Top)/factor);
        int top=photoControls.IsEmpty?photoArea.Top:Math.Min(photoControls.Bottom,photoArea.Bottom-height);
        return new Rectangle(Clamp(x,photoArea.Left,photoArea.Right-width),
            Clamp(y,top,photoArea.Bottom-height),width,height);
    }

    static int Clamp(int value,int min,int max){return Math.Max(min,Math.Min(max,value));}
    internal static float Follow(float current,float target,double elapsed,double response,float closeEnough){
        if(Math.Abs(target-current)<=closeEnough)return target;
        return current+(target-current)*(float)(1-Math.Exp(-elapsed/response));
    }
    void HideWindow(){clock.Stop();lastFrame=0;if(visible)ShowWindow(Handle,0);visible=false;}
    internal void Hide(){HideWindow();pinching=false;wanted=starting=1;target=IntPtr.Zero;}
    void Close(){
        Hide();
        if(Handle!=IntPtr.Zero)DestroyHandle();
        child=IntPtr.Zero;painted=1;lastPosition=lastSource=Rectangle.Empty;
        if(initialized){MagnificationRuntime.Release();initialized=false;}
    }
    protected override void WndProc(ref Message message){
        if(message.Msg==0x84){message.Result=new IntPtr(-1);return;}
        if(message.Msg==0x21){message.Result=new IntPtr(3);return;}
        base.WndProc(ref message);
    }
    public void Dispose(){Close();clock.Dispose();}
}
