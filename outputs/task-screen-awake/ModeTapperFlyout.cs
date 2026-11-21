using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class LumaFlyout : Form {
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point position,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action,uint parameter,out int value,uint flags);
    delegate IntPtr MouseHookProc(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int kind,MouseHookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string module);
    [StructLayout(LayoutKind.Sequential)]struct MonitorRect {
        public int Left,Top,Right,Bottom;
        internal Rectangle Bounds {get{return Rectangle.FromLTRB(Left,Top,Right,Bottom);}}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct MonitorInfo {
        public uint Size;
        public MonitorRect Display,Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;
    }

    enum Item { Always, Tasks, ScreenOff, Rotation, Economy, Balance, Maximum, Armoury, Silent, Performance, Turbo,
        Routing, Sites, Happ, Touchpad, Home, Edges, Animations, Zoom, CodexWidget, TasksWidget, ClockWidget, CalendarWidget, CameraOpen, CameraBlock }
    enum IconKind { Screen, Gauge, Fan, Shield, Gestures, Touchpad, Home, Edges, Plug, Battery, Animations, Zoom, Widgets, Camera }

    const int MenuWidth=356, MenuHeight=772, ContentInset=20, ContentWidth=316, TileWidth=100, TileStep=108;
    const int ScreenY=15, PowerY=152, CoolingY=249, VpnY=346, GesturesY=460, WidgetsY=556, CameraY=667;
    static readonly string versionLabel="ModeTapper "+typeof(LumaFlyout).Assembly.GetName().Version.ToString(3);
    static readonly string[] screenLabels={"Всегда включён","Во время задач","Выключено"};
    static readonly string[] powerLabels={"Экономия","Баланс","Максимум"};
    static readonly string[] fanLabels={"Тихий","Производительный","Турбо"};
    static readonly string[] gestureLabels={"Тачпад","Домой","Грани","Анимации+","Лупа"};
    static readonly IconKind[] gestureIcons={IconKind.Touchpad,IconKind.Home,IconKind.Edges,IconKind.Animations,IconKind.Zoom};
    static readonly AsusCoolingPreset[] fanPresets={AsusCoolingPreset.Silent,AsusCoolingPreset.Performance,AsusCoolingPreset.Turbo};
    static readonly Color background=Color.FromArgb(22,22,22), text=Color.FromArgb(247,247,247);
    static readonly Color muted=Color.FromArgb(172,172,172), faint=Color.FromArgb(108,108,108);
    static readonly Color surface=Color.FromArgb(30,30,30), hoverSurface=Color.FromArgb(42,42,42), border=Color.FromArgb(56,56,56);
    static readonly Color accent=Color.FromArgb(222,222,222), green=Color.FromArgb(110,211,157);
    static readonly Color amber=Color.FromArgb(245,197,87), red=Color.FromArgb(235,125,125);
    static readonly Color[] screenSignals={green,amber,red};

    readonly Func<int> getMode;
    readonly Func<PowerPreset> getPower;
    readonly Func<int?> getCooling;
    readonly Func<AsusCoolingAvailability> getCoolingAvailability;
    readonly Func<bool> isPaused,getVpn,getVpnWaiting,getTouchpad,getHome,getEdges,getAnimations,getZoom;
    readonly Func<DesktopWidgetKind,bool> getWidget;
    readonly Action<DesktopWidgetKind> toggleWidget;
    readonly Func<string> getVpnError;
    readonly Func<bool?> getRotation;
    readonly Func<CameraState> getCamera;
    readonly Func<bool> cameraBusy,cameraLocked;
    readonly Action toggleRotation,openCamera,toggleCamera;
    readonly Action<int> setMode;
    readonly Action<PowerPreset> setPower;
    readonly Action<AsusCoolingPreset> setCooling;
    readonly Action openArmoury,quit,resume,toggleVpn,editSites,openHapp,toggleTouchpad,toggleHome,toggleEdges,toggleAnimations,toggleZoom;
    readonly bool showCooling;
    readonly ToolTip tips=new ToolTip{InitialDelay=550,ReshowDelay=200,AutoPopDelay=5000};
    readonly MouseHookProc outsidePress;
    readonly CarouselFrameClock slide;
    readonly System.Windows.Forms.Timer placementTimer=new System.Windows.Forms.Timer{Interval=90};
    readonly Stopwatch slideTime=new Stopwatch();

    Font regular,small,compact,heading,button;
    float scale=1;
    int hover=-1;
    int keyboardItem=-1;
    IntPtr mouseHook;
    int visibilityRevision;
    bool slidingOut,clipSlide,placementQueued;
    Point edgeLocation,edgeOrigin,slideStart,slideEnd;
    Rectangle slideDisplay,slideWorkArea;
    int slideDuration,displayRotation,placementChecks;

    internal LumaFlyout(Func<int> mode,Action<int> screenMode,Func<PowerPreset> power,Action<PowerPreset> powerMode,
        bool coolingSupported,Func<AsusCoolingAvailability> coolingAvailability,Func<int?> cooling,Action<AsusCoolingPreset> coolingMode,Action armoury,
        Action exit,Func<bool> vpn,Func<string> vpnError,Func<bool> vpnWaiting,Action vpnToggle,Action sites,Action happ,
        Func<bool> touchpad,Action touchpadToggle,Func<bool> home,Action homeToggle,Func<bool> edges,Action edgesToggle,
        Func<bool> animations,Action animationsToggle,Func<bool> zoom,Action zoomToggle,
        Func<DesktopWidgetKind,bool> widget,Action<DesktopWidgetKind> widgetToggle,Func<bool> paused,Action resumeLuma,
        Func<bool?> rotation,Action rotationToggle,Func<CameraState> camera,Func<bool> busy,Func<bool> locked,Action cameraOpen,Action cameraToggle){
        getMode=mode;setMode=screenMode;getPower=power;setPower=powerMode;
        showCooling=coolingSupported;getCoolingAvailability=coolingAvailability;getCooling=cooling;setCooling=coolingMode;openArmoury=armoury;
        getVpn=vpn;getVpnError=vpnError;getVpnWaiting=vpnWaiting;toggleVpn=vpnToggle;editSites=sites;openHapp=happ;
        getTouchpad=touchpad;toggleTouchpad=touchpadToggle;getHome=home;toggleHome=homeToggle;getEdges=edges;toggleEdges=edgesToggle;
        getAnimations=animations;toggleAnimations=animationsToggle;
        getZoom=zoom;toggleZoom=zoomToggle;
        getWidget=widget;toggleWidget=widgetToggle;
        getRotation=rotation;toggleRotation=rotationToggle;getCamera=camera;cameraBusy=busy;cameraLocked=locked;
        openCamera=cameraOpen;toggleCamera=cameraToggle;
        quit=exit;isPaused=paused;resume=resumeLuma;
        outsidePress=OnMouseHook;
        Text="ModeTapper";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;
        AutoScaleMode=AutoScaleMode.None;BackColor=background;DoubleBuffered=true;KeyPreview=true;
        slide=new CarouselFrameClock(this);
        slide.Tick+=(sender,args)=>UpdateSlide();
        placementTimer.Tick+=(sender,args)=>{
            if(--placementChecks<=0)placementTimer.Stop();
            ReconcilePlacement();
        };
        Deactivate+=(sender,args)=>Dismiss();
        SetScale(GetDpiForSystem()/96f);
    }

    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ClassStyle|=0x20000;return p;}}
    protected override void OnHandleCreated(EventArgs e){
        base.OnHandleCreated(e);
        int dark=1,round=1,frame=unchecked((int)0xfffffffe);
        DwmSetWindowAttribute(Handle,20,ref dark,4);
        DwmSetWindowAttribute(Handle,33,ref round,4);
        DwmSetWindowAttribute(Handle,34,ref frame,4);
    }

    int S(float value){return (int)Math.Round(value*scale);}
    Rectangle R(float x,float y,float w,float h){return new Rectangle(S(x),S(y),S(w),S(h));}
    int CoolingOffset {get{return showCooling?20:-97;}}

    void SetScale(float value){
        if(regular!=null&&Math.Abs(value-scale)<.001f)return;
        DisposeFonts();
        scale=value;
        regular=new Font("Segoe UI",12.5f*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        small=new Font("Segoe UI",11*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        compact=new Font("Segoe UI",10.5f*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        heading=new Font("Segoe UI Semibold",14*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        button=new Font("Segoe UI Semibold",12*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        ResizeMenu();
    }

    int HeightInDip {get{return isPaused()?130:MenuHeight+CoolingOffset;}}
    Size MenuSize {get{return new Size(S(MenuWidth),FillsSide(slideDisplay)?slideDisplay.Height:S(HeightInDip));}}
    internal static bool FillsSide(Rectangle display){return display.Width>display.Height&&display.Height>0;}
    void ResizeMenu(){ClientSize=MenuSize;}
    void Fit(Rectangle area){
        float fit=Math.Min((area.Width-16f)/MenuWidth,(area.Height-16f)/HeightInDip);
        SetScale(Math.Min(GetDpiForSystem()/96f,fit));
        ResizeMenu();
    }
    void Present(){
        TopMost=true;hover=-1;keyboardItem=-1;Invalidate();Show();Activate();
    }
    static void ReadPlacement(out Rectangle display,out Rectangle area,out string device){
        var monitor=MonitorFromPoint(Point.Empty,1);
        var info=new MonitorInfo{Size=(uint)Marshal.SizeOf(typeof(MonitorInfo))};
        if(monitor!=IntPtr.Zero&&GetMonitorInfo(monitor,ref info)){
            display=info.Display.Bounds;area=info.Work.Bounds;device=info.Device;return;
        }
        var screen=Screen.PrimaryScreen;
        display=screen.Bounds;area=screen.WorkingArea;device=screen.DeviceName;
    }
    internal void OpenAtScreenEdge(){
        Rectangle display,area;string device;
        ReadPlacement(out display,out area,out device);
        int rotation=DisplayOrientation.Read(device);
        bool reverse=Visible&&slidingOut&&SamePlacement(display,area,rotation);
        Point current=Location;
        StopSlide();
        PreparePlacement(display,area,device,rotation);
        bool animate=AnimationsEnabled();
        Point start=reverse?current:edgeOrigin;
        if(animate)PositionSlide(start);
        else Location=edgeLocation;
        Present();
        if(animate)StartSlide(start,edgeLocation,245,false);
    }
    bool SamePlacement(Rectangle display,Rectangle area,int rotation){
        return slideDisplay==display&&slideWorkArea==area&&displayRotation==rotation;
    }
    void PreparePlacement(Rectangle display,Rectangle area,string device,int rotation){
        slideDisplay=display;
        slideWorkArea=area;
        displayRotation=rotation;
        Fit(FillsSide(display)?display:area);
        EdgePlacement(slideDisplay,Size,rotation,out edgeLocation,out edgeOrigin);
        var swept=Rectangle.Union(new Rectangle(edgeOrigin,Size),new Rectangle(edgeLocation,Size));
        clipSlide=false;
        foreach(var other in Screen.AllScreens){
            if(other.DeviceName!=device&&other.Bounds.IntersectsWith(swept)){clipSlide=true;break;}
        }
    }
    internal static void EdgePlacement(Rectangle display,Size panel,int rotation,out Point end,out Point start){
        int left=display.Left,right=display.Right-panel.Width;
        int top=display.Top,bottom=display.Bottom-panel.Height;
        // Transform the physical upper-right corner with the same rotation as touchscreen coordinates.
        switch(rotation){
            case 1:end=new Point(right,bottom);start=new Point(right,display.Bottom);break;
            case 2:end=new Point(left,bottom);start=new Point(display.Left-panel.Width,bottom);break;
            case 3:end=new Point(left,top);start=new Point(left,display.Top-panel.Height);break;
            default:end=new Point(right,top);start=new Point(display.Right,top);break;
        }
    }
    protected override void WndProc(ref Message message){
        const int DisplayChanged=0x007e, DpiChanged=0x02e0, SettingChanged=0x001a, SetWorkArea=0x002f;
        base.WndProc(ref message);
        if(!Visible)return;
        if(message.Msg==DisplayChanged||message.Msg==DpiChanged||
           message.Msg==SettingChanged&&message.WParam.ToInt64()==SetWorkArea)RefreshPlacement();
    }
    internal void RefreshPlacement(){
        if(!Visible)return;
        placementChecks=6;
        placementTimer.Start();
        QueuePlacement();
    }
    void QueuePlacement(){
        if(placementQueued||!Visible||!IsHandleCreated)return;
        placementQueued=true;
        try{BeginInvoke(new Action(()=>{placementQueued=false;ReconcilePlacement();}));}
        catch(InvalidOperationException){placementQueued=false;}
    }
    void ReconcilePlacement(){
        if(!Visible)return;
        Rectangle display,area;string device;
        ReadPlacement(out display,out area,out device);
        int rotation=DisplayOrientation.Read(device);
        if(rotation<0&&displayRotation>=0)rotation=displayRotation;
        bool changed=!SamePlacement(display,area,rotation);
        if(changed){
            if(slidingOut){Hide();return;}
            StopSlide();
            PreparePlacement(display,area,device,rotation);
        }
        if(!slide.Enabled){
            if(ClientSize!=MenuSize)ResizeMenu();
            if(Location!=edgeLocation)Location=edgeLocation;
        }
    }
    protected override void OnLocationChanged(EventArgs e){
        base.OnLocationChanged(e);
        // Windows can reposition top-level windows after the display-change message.
        if(slide!=null&&!slide.Enabled&&Location!=edgeLocation)QueuePlacement();
    }
    protected override void OnSizeChanged(EventArgs e){
        base.OnSizeChanged(e);
        if(slide!=null&&!slide.Enabled&&ClientSize!=MenuSize)QueuePlacement();
    }
    internal bool EdgeIsOpen {get{return Visible&&!slidingOut;}}

    internal static bool AnimationsEnabled(){
        int enabled;
        return !SystemParametersInfo(0x1042,0,out enabled,0)||enabled!=0;
    }
    internal void Dismiss(){
        if(!Visible||slidingOut)return;
        if(!AnimationsEnabled()){Hide();return;}
        tips.Hide(this);
        StartSlide(Location,edgeOrigin,185,true);
    }
    internal void DismissOutside(Point contact){
        if(!Visible||Bounds.Contains(contact))return;
        int revision=visibilityRevision;
        try{BeginInvoke(new Action(()=>{
            if(Visible&&visibilityRevision==revision)Dismiss();
        }));}catch(InvalidOperationException){}
    }
    static int SlideDistance(Point first,Point last){return Math.Abs(last.X-first.X)+Math.Abs(last.Y-first.Y);}
    void StartSlide(Point start,Point end,int duration,bool closing){
        slideStart=start;slideEnd=end;slidingOut=closing;
        slideDuration=Math.Max(1,duration*SlideDistance(start,end)/Math.Max(1,SlideDistance(edgeOrigin,edgeLocation)));
        slideTime.Restart();
        slide.Start();
    }
    void PositionSlide(Point position){
        if(clipSlide){
            var frame=new Rectangle(position,Size);
            var visible=Rectangle.Intersect(frame,slideDisplay);
            if(visible!=frame){
                visible.Offset(-position.X,-position.Y);
                var previous=Region;
                Region=new Region(visible);
                if(previous!=null)previous.Dispose();
            }else ClearSlideClip();
        }
        if(Location!=position)Location=position;
    }
    void UpdateSlide(){
        double progress=Math.Min(1,slideTime.Elapsed.TotalMilliseconds/slideDuration);
        double eased=slidingOut?progress*progress:1-Math.Pow(1-progress,3);
        if(progress<1){
            PositionSlide(new Point(slideStart.X+(int)Math.Round((slideEnd.X-slideStart.X)*eased),
                slideStart.Y+(int)Math.Round((slideEnd.Y-slideStart.Y)*eased)));
            return;
        }
        if(slidingOut)Hide();
        else{Location=edgeLocation;StopSlide();}
    }
    void ClearSlideClip(){
        var previous=Region;
        if(previous==null)return;
        Region=null;previous.Dispose();
    }
    void StopSlide(){
        slide.Stop();slideTime.Stop();slidingOut=false;
        ClearSlideClip();
    }

    protected override void OnVisibleChanged(EventArgs e){
        visibilityRevision++;
        if(Visible&&mouseHook==IntPtr.Zero)mouseHook=SetWindowsHookEx(14,outsidePress,GetModuleHandle(null),0);
        else if(!Visible){RemoveMouseHook();StopSlide();placementTimer.Stop();}
        base.OnVisibleChanged(e);
    }
    void RemoveMouseHook(){
        if(mouseHook==IntPtr.Zero)return;
        UnhookWindowsHookEx(mouseHook);mouseHook=IntPtr.Zero;
    }
    IntPtr OnMouseHook(int code,IntPtr message,IntPtr data){
        long kind=message.ToInt64();
        if(code>=0&&data!=IntPtr.Zero&&(kind==0x201||kind==0x204||kind==0x207||kind==0x20B)){
            int x=Marshal.ReadInt32(data),y=Marshal.ReadInt32(data,4);
            DismissOutside(new Point(x,y));
        }
        return CallNextHookEx(IntPtr.Zero,code,message,data);
    }

    Rectangle BoundsOf(Item item){
        int index=(int)item;
        if(item<=Item.ScreenOff)return Tile(index,ScreenY+31);
        if(item==Item.Rotation)return R(ContentInset,106,ContentWidth,34);
        if(item<=Item.Maximum)return Tile(index-(int)Item.Economy,PowerY+31);
        if(item==Item.Armoury)return R(256,CoolingY,80,25);
        if(item<=Item.Turbo)return Tile(index-(int)Item.Silent,CoolingY+31);
        if(item==Item.Routing)return R(ContentInset,VpnY+31+CoolingOffset,ContentWidth,34);
        if(item<=Item.Happ)return R(item==Item.Sites?ContentInset:182,VpnY+73+CoolingOffset,154,30);
        if(item>=Item.CodexWidget&&item<=Item.CalendarWidget){
            int slot=index-(int)Item.CodexWidget;
            return R(ContentInset+(slot%2)*162,WidgetsY+31+38*(slot/2)+CoolingOffset,154,33);
        }
        if(item>=Item.CameraOpen)return R(item==Item.CameraOpen?ContentInset:182,CameraY+31+CoolingOffset,154,33);
        return R(ContentInset+64*(index-(int)Item.Touchpad),GesturesY+26+CoolingOffset,60,54);
    }
    Rectangle Tile(int column,int y){return R(ContentInset+TileStep*column,y,TileWidth,54);}

    bool IsVisible(Item item){
        return showCooling||item<Item.Armoury||item>Item.Turbo;
    }
    bool IsEnabled(Item item){
        if(item==Item.Rotation)return getRotation().HasValue;
        if(item==Item.CameraOpen)return !cameraBusy()&&(getCamera()==CameraState.Available||getCamera()==CameraState.Mixed);
        if(item==Item.CameraBlock)return !cameraBusy()&&getCamera()!=CameraState.Unknown&&getCamera()!=CameraState.Missing;
        if(item<Item.Silent||item>Item.Turbo)return true;
        return getCoolingAvailability()==AsusCoolingAvailability.Ready&&(item!=Item.Turbo||PowerModes.OnAC);
    }
    int Hit(Point point){
        if(isPaused()){
            for(int i=0;i<2;i++)if(PausedBounds(i).Contains(point))return i;
            return -1;
        }
        for(int i=0;i<=(int)Item.CameraBlock;i++)if(IsVisible((Item)i)&&BoundsOf((Item)i).Contains(point))return i;
        return -1;
    }

    void TextAt(Graphics g,string label,Font font,Color color,Rectangle rect,TextFormatFlags alignment=TextFormatFlags.Left){
        TextRenderer.DrawText(g,label,font,rect,color,TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|alignment);
    }
    void FillRound(Graphics g,Rectangle rect,Color color,float radius){
        using(var path=TrayApp.RoundRect(rect,S(radius)))using(var brush=new SolidBrush(color))g.FillPath(brush,path);
    }
    void DrawKeyboardFocus(Graphics g,int index,Rectangle rect){
        if(keyboardItem!=index)return;
        var focus=Rectangle.Inflate(rect,-S(2),-S(2));
        using(var outline=TrayApp.RoundRect(focus,S(5)))using(var pen=new Pen(muted))g.DrawPath(pen,outline);
    }
    void Divider(Graphics g,int y){
        using(var brush=new SolidBrush(border))g.FillRectangle(brush,S(ContentInset),S(y),S(ContentWidth),Math.Max(1,S(.5f)));
    }

    void Glyph(Graphics g,IconKind kind,Rectangle rect,Color color,int dial=1){
        var state=g.Save();
        g.TranslateTransform(rect.X,rect.Y);
        g.ScaleTransform(rect.Width/20f,rect.Height/20f);
        using(var pen=new Pen(color,1.30f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}){
            switch(kind){
                case IconKind.Screen:
                    g.DrawRectangle(pen,2.5f,3.2f,15,10.3f);
                    g.DrawLine(pen,10,13.5f,10,17);g.DrawLine(pen,6.6f,17,13.4f,17);
                    if(dial==0){g.DrawLine(pen,6.5f,9.5f,9,7);g.DrawLine(pen,10.2f,9.8f,13.6f,6.4f);}
                    else if(dial==1){g.DrawEllipse(pen,7.2f,5.55f,5.6f,5.6f);g.DrawLines(pen,new[]{new PointF(10,6.65f),new PointF(10,8.35f),new PointF(11.3f,9)});}
                    else if(dial==2)g.DrawLine(pen,7.1f,8.4f,12.9f,8.4f);
                    break;
                case IconKind.Gauge:
                    g.DrawArc(pen,2.5f,4.4f,15,15,186,168);
                    g.DrawLine(pen,3.3f,13,4.6f,12.8f);g.DrawLine(pen,15.4f,12.8f,16.7f,13);
                    double angle=(216+dial*54)*Math.PI/180;
                    g.DrawLine(pen,10,12.7f,10+(float)Math.Cos(angle)*5.3f,12.7f+(float)Math.Sin(angle)*5.3f);
                    using(var pivot=new SolidBrush(color))g.FillEllipse(pivot,9.05f,11.75f,1.9f,1.9f);
                    break;
                case IconKind.Fan:
                    DrawFan(g,color,dial);
                    break;
                case IconKind.Shield:
                    g.DrawLines(pen,new[]{new PointF(10,2.3f),new PointF(16.5f,4.9f),new PointF(16,11.8f),new PointF(13,15.6f),new PointF(10,17.7f),new PointF(7,15.6f),new PointF(4,11.8f),new PointF(3.5f,4.9f),new PointF(10,2.3f)});
                    g.DrawLines(pen,new[]{new PointF(7.1f,10),new PointF(9.2f,12.1f),new PointF(13,8)});
                    break;
                case IconKind.Gestures:
                    g.DrawLine(pen,3,5,17,5);g.DrawLine(pen,6,3,6,7);
                    g.DrawLine(pen,3,10,17,10);g.DrawLine(pen,13,8,13,12);
                    g.DrawLine(pen,3,15,17,15);g.DrawLine(pen,8,13,8,17);
                    break;
                case IconKind.Touchpad:
                    g.DrawLines(pen,new[]{new PointF(16.5f,7),new PointF(16.5f,3.5f),new PointF(3,3.5f),new PointF(3,15.5f),new PointF(7.3f,15.5f)});
                    g.DrawLines(pen,new[]{new PointF(10.2f,17),new PointF(8.2f,13.7f),new PointF(10.4f,13.9f),new PointF(10.4f,8.5f),new PointF(12.4f,7.8f),new PointF(13.2f,11.1f),new PointF(16.3f,11.7f),new PointF(16.3f,17)});
                    break;
                case IconKind.Home:
                    g.DrawLines(pen,new[]{new PointF(3,9),new PointF(10,3),new PointF(17,9)});
                    g.DrawLines(pen,new[]{new PointF(5,8),new PointF(5,16.5f),new PointF(15,16.5f),new PointF(15,8)});
                    g.DrawLine(pen,8.3f,13.4f,11.7f,13.4f);
                    break;
                case IconKind.Edges:
                    g.DrawLines(pen,new[]{new PointF(7,3),new PointF(3,3),new PointF(3,17),new PointF(7,17)});
                    g.DrawLines(pen,new[]{new PointF(13,3),new PointF(17,3),new PointF(17,17),new PointF(13,17)});
                    break;
                case IconKind.Plug:
                    g.DrawLine(pen,7.2f,2.8f,7.2f,7.1f);
                    g.DrawLine(pen,12.8f,2.8f,12.8f,7.1f);
                    g.DrawLine(pen,5.1f,7.1f,14.9f,7.1f);
                    g.DrawLine(pen,6.2f,7.1f,6.2f,10);
                    g.DrawBezier(pen,6.2f,10,6.2f,15.1f,13.8f,15.1f,13.8f,10);
                    g.DrawLine(pen,13.8f,10,13.8f,7.1f);
                    g.DrawLine(pen,10,13.8f,10,17.5f);
                    break;
                case IconKind.Battery:
                    using(var shape=TrayApp.RoundRect(new RectangleF(2,5.8f,14.3f,8.4f),1.65f))g.DrawPath(pen,shape);
                    g.DrawLine(pen,18.3f,8.6f,18.3f,11.4f);
                    if(dial>0)using(var charge=new SolidBrush(color))g.FillRectangle(charge,4,8,10.3f*Math.Min(dial,100)/100,4);
                    break;
                case IconKind.Animations:
                    g.DrawRectangle(pen,3,3,11,7.5f);
                    g.DrawLines(pen,new[]{new PointF(9,13.5f),new PointF(12,16.5f),new PointF(15,13.5f)});
                    g.DrawLine(pen,12,11.5f,12,16);
                    g.DrawLine(pen,16,5,19,5);g.DrawLine(pen,17.5f,3.5f,17.5f,6.5f);
                    break;
                case IconKind.Zoom:
                    g.DrawEllipse(pen,3,3,10.5f,10.5f);g.DrawLine(pen,12.2f,12.2f,17,17);
                    g.DrawLine(pen,6,8.25f,10.5f,8.25f);g.DrawLine(pen,8.25f,6,8.25f,10.5f);
                    break;
                case IconKind.Camera:
                    g.DrawRectangle(pen,2.5f,6,15,11.5f);
                    g.DrawLines(pen,new[]{new PointF(6,6),new PointF(7.5f,3.5f),new PointF(12.5f,3.5f),new PointF(14,6)});
                    g.DrawEllipse(pen,7,8.75f,6,6);
                    break;
                case IconKind.Widgets:
                    using(var a=TrayApp.RoundRect(new RectangleF(2.7f,3,6,14),1.6f))g.DrawPath(pen,a);
                    using(var b=TrayApp.RoundRect(new RectangleF(11.3f,3,6,5.5f),1.4f))g.DrawPath(pen,b);
                    using(var c=TrayApp.RoundRect(new RectangleF(11.3f,11.5f,6,5.5f),1.4f))g.DrawPath(pen,c);
                    break;
            }
        }
        g.Restore(state);
    }
    void DrawFan(Graphics g,Color color,int speed){
        float centerX=speed<0?10:8, centerY=speed<0?10:9.2f;
        using(var stroke=new Pen(color,.85f))using(var blade=new SolidBrush(color))using(var shape=new GraphicsPath()){
            g.DrawEllipse(stroke,centerX-6.6f,centerY-6.6f,13.2f,13.2f);
            var state=g.Save();g.TranslateTransform(centerX,centerY);
            shape.AddBezier(.1f,-1.65f,-.7f,-2.5f,-3.3f,-5.6f,-.65f,-5.35f);
            shape.AddBezier(-.65f,-5.35f,1.45f,-5.45f,2.7f,-3.6f,1.1f,-1.25f);
            shape.CloseFigure();
            for(int i=0;i<5;i++){g.FillPath(blade,shape);g.RotateTransform(72);}
            g.FillEllipse(blade,-.95f,-.95f,1.9f,1.9f);
            g.Restore(state);
            for(int i=0;i<3&&speed>=0;i++){
                float height=2.6f+i*1.8f;
                using(var bar=new SolidBrush(i<=speed?color:Color.FromArgb(70,color)))
                    g.FillRectangle(bar,12.7f+i*2.2f,18.2f-height,1.25f,height);
            }
        }
    }

    void Section(Graphics g,IconKind icon,string label,int y){
        Glyph(g,icon,R(ContentInset,y+4,17,17),muted,-1);
        TextAt(g,label,heading,text,R(ContentInset+25,y,187,25));
    }
    void Card(Graphics g,Item item,string label,IconKind icon,bool selected,Color tint,int dial=1){
        var r=BoundsOf(item);
        bool enabled=IsEnabled(item);
        bool over=hover==(int)item&&enabled;
        bool gesture=item>=Item.Touchpad&&item<=Item.Zoom;
        if(selected||over&&!gesture)FillRound(g,r,over?hoverSurface:Color.FromArgb(38,38,38),6);
        DrawKeyboardFocus(g,(int)item,r);
        var iconRect=new Rectangle(r.X+(r.Width-S(22))/2,r.Y+S(5),S(22),S(22));
        Color ink=enabled?selected||over?text:muted:faint;
        Glyph(g,icon,iconRect,ink,dial);
        if(selected){
            using(var brush=new SolidBrush(tint))g.FillRectangle(brush,r.X+(r.Width-S(16))/2,r.Bottom-S(3),S(16),Math.Max(1,S(.7f)));
        }
        var labelRect=new Rectangle(r.X,r.Y+S(29),r.Width,S(18));
        TextAt(g,label,item==Item.Performance||item==Item.Animations?compact:selected?button:regular,ink,labelRect,TextFormatFlags.HorizontalCenter);
    }
    void ActionRow(Graphics g,Item item,string label){
        var r=BoundsOf(item);
        bool active=IsEnabled(item);
        FillRound(g,r,active&&hover==(int)item?hoverSurface:surface,6);
        DrawKeyboardFocus(g,(int)item,r);
        TextAt(g,label,button,active?text:faint,r,TextFormatFlags.HorizontalCenter);
    }
    void ToggleRow(Graphics g,Item item,string label,bool enabled){
        var r=BoundsOf(item);
        bool active=IsEnabled(item);
        FillRound(g,r,active&&hover==(int)item?hoverSurface:surface,7);
        DrawKeyboardFocus(g,(int)item,r);
        TextAt(g,label,regular,active?text:faint,new Rectangle(r.X+S(12),r.Y,r.Width-S(60),r.Height));
        int top=r.Y+(r.Height-S(17))/2;
        int left=r.Right-S(46);
        FillRound(g,new Rectangle(left,top,S(34),S(17)),enabled?accent:active?Color.FromArgb(87,87,87):border,8);
        using(var brush=new SolidBrush(!active?faint:enabled?surface:text))
            g.FillEllipse(brush,new Rectangle(left+S(enabled?20:3),top+S(3),S(11),S(11)));
    }
    void DrawScreen(Graphics g){
        Section(g,IconKind.Screen,"Режим экрана",ScreenY);
        int selected=getMode();
        for(int i=0;i<screenLabels.Length;i++)
            Card(g,(Item)i,screenLabels[i],IconKind.Screen,i==selected,screenSignals[i],i);
        ToggleRow(g,Item.Rotation,"Блокировка поворота",getRotation()==true);
    }
    void DrawPower(Graphics g){
        Divider(g,PowerY-5);
        var power=PlanSettings.Status();
        bool saver=PlanSettings.EnergySaverOn, onAC=power.ACLineStatus==1;
        Section(g,IconKind.Gauge,"Производительность",PowerY);
        Color stateInk=saver?amber:muted;
        bool hasLevel=power.BatteryPercent<=100;
        bool charging=onAC&&power.BatteryFlag!=255&&(power.BatteryFlag&8)!=0;
        string level=hasLevel?power.BatteryPercent+"%":"";
        string status=onAC?(charging?"Зарядка":"От сети")+(hasLevel?"  "+level:""):level;
        if(power.ACLineStatus!=255||hasLevel){
            int width=TextRenderer.MeasureText(g,status,small,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width;
            int end=S(ContentInset+ContentWidth), gap=S(5), size=S(20);
            int glyphX=end-width-size-gap;
            Glyph(g,onAC?IconKind.Plug:IconKind.Battery,new Rectangle(glyphX,S(PowerY+2),size,size),stateInk,hasLevel?power.BatteryPercent:0);
            TextAt(g,status,small,stateInk,new Rectangle(end-width,S(PowerY),width+1,S(25)));
        }
        PowerPreset preset=getPower();
        for(int i=0;i<powerLabels.Length;i++){
            bool selected=(int)preset==i&&(i!=2||!saver);
            Card(g,Item.Economy+i,powerLabels[i],IconKind.Gauge,selected,i==0?amber:accent,i);
        }
    }
    void DrawCooling(Graphics g){
        Divider(g,CoolingY-5);
        Section(g,IconKind.Fan,"Охлаждение",CoolingY);
        var link=BoundsOf(Item.Armoury);
        if(hover==(int)Item.Armoury)FillRound(g,link,hoverSurface,6);
        DrawKeyboardFocus(g,(int)Item.Armoury,link);
        TextAt(g,"Armoury Crate",small,accent,link,TextFormatFlags.HorizontalCenter);
        int width=TextRenderer.MeasureText(g,"Armoury Crate",small,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width;
        int start=link.Left+(link.Width-width)/2;
        using(var pen=new Pen(muted,Math.Max(1,S(.5f))))g.DrawLine(pen,start,link.Bottom-S(5),start+width,link.Bottom-S(5));
        int? current=getCooling();
        for(int i=0;i<fanPresets.Length;i++)
            Card(g,Item.Silent+i,fanLabels[i],IconKind.Fan,current==(int)fanPresets[i],accent,i);
        string notice=CoolingNotice();
        if(notice!=null)TextAt(g,notice,small,amber,R(ContentInset,CoolingY+86,ContentWidth,20));
    }
    string CoolingNotice(){
        var state=getCoolingAvailability();
        if(state==AsusCoolingAvailability.LockedByAsus)return "Профили заблокированы ASUS. Откройте Armoury Crate.";
        if(state==AsusCoolingAvailability.DriverUnavailable)return "Управление охлаждением ASUS недоступно.";
        return PowerModes.OnAC?null:"Турбо доступен от сети.";
    }
    internal void RefreshCoolingFeedback(){
        if(!Visible)return;
        if(hover>=(int)Item.Silent&&hover<=(int)Item.Turbo){
            var item=(Item)hover;
            Cursor=IsEnabled(item)?Cursors.Hand:Cursors.Default;
            tips.SetToolTip(this,Hint(item));
        }
        Invalidate();
    }
    void DrawVpn(Graphics g){
        int offset=CoolingOffset;
        Divider(g,VpnY-5+offset);
        Section(g,IconKind.Shield,"VPN",VpnY+offset);
        bool vpn=getVpn(),error=!String.IsNullOrEmpty(getVpnError()),waiting=vpn&&getVpnWaiting();
        TextAt(g,error?"Ошибка":waiting?"Нет прокси Happ":vpn?"Включено":"Выключено",small,error?red:waiting?amber:vpn?green:muted,R(214,VpnY+offset,122,25),TextFormatFlags.Right);
        ToggleRow(g,Item.Routing,"Маршрутизация",vpn);
        ActionRow(g,Item.Sites,"Список сайтов");ActionRow(g,Item.Happ,"Открыть Happ");
    }
    void DrawGestures(Graphics g){
        Divider(g,GesturesY-5+CoolingOffset);
        Section(g,IconKind.Gestures,"Жесты",GesturesY+CoolingOffset);
        bool[] enabled={getTouchpad(),getHome(),getEdges(),getAnimations(),getZoom()};
        for(int i=0;i<gestureLabels.Length;i++)
            Card(g,Item.Touchpad+i,gestureLabels[i],gestureIcons[i],enabled[i],accent);
    }
    void DrawWidgets(Graphics g){
        Divider(g,WidgetsY-5+CoolingOffset);
        Section(g,IconKind.Widgets,"Виджеты",WidgetsY+CoolingOffset);
        ToggleRow(g,Item.CodexWidget,"Лимит Codex",getWidget(DesktopWidgetKind.Limit));
        ToggleRow(g,Item.TasksWidget,"Задачи Codex",getWidget(DesktopWidgetKind.Tasks));
        ToggleRow(g,Item.ClockWidget,"Часы",getWidget(DesktopWidgetKind.Clock));
        ToggleRow(g,Item.CalendarWidget,"Календарь",getWidget(DesktopWidgetKind.Calendar));
    }
    void DrawCamera(Graphics g){
        int y=CameraY+CoolingOffset;
        Divider(g,y-5);
        Section(g,IconKind.Camera,"Камеры",y);
        var state=getCamera();
        string status=cameraBusy()?"Переключение":state==CameraState.Blocked?"Выключены":
            state==CameraState.Available?"Включены":state==CameraState.Mixed?"Часть доступна":
            state==CameraState.Missing?"Камер нет":"Недоступно";
        Color ink=cameraBusy()?muted:state==CameraState.Blocked?red:state==CameraState.Available?green:state==CameraState.Mixed?amber:muted;
        int right=S(ContentInset+ContentWidth);
        int width=TextRenderer.MeasureText(g,status,small,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width;
        using(var brush=new SolidBrush(ink))g.FillEllipse(brush,right-width-S(13),S(y+10),S(5),S(5));
        TextAt(g,status,small,ink,new Rectangle(right-width,S(y),width+1,S(25)));
        ActionRow(g,Item.CameraOpen,"Открыть камеру");
        ActionRow(g,Item.CameraBlock,cameraLocked()||state==CameraState.Blocked?"Включить":"Выключить");
    }
    Rectangle PausedBounds(int index){return R(16,index==0?48:87,324,32);}
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        if(isPaused()){
            TextAt(g,"Безопасный режим",heading,text,R(20,14,316,25));
            for(int i=0;i<2;i++){
                var r=PausedBounds(i);
                FillRound(g,r,hover==i?hoverSurface:surface,7);
                DrawKeyboardFocus(g,i,r);
                TextAt(g,i==0?"Возобновить работу ModeTapper":"Выход",button,text,r,TextFormatFlags.HorizontalCenter);
            }
            return;
        }
        DrawScreen(g);DrawPower(g);
        if(showCooling)DrawCooling(g);
        DrawVpn(g);DrawGestures(g);DrawWidgets(g);DrawCamera(g);
        int footerShift=ClientSize.Height-S(HeightInDip);
        var footer=R(ContentInset,743+CoolingOffset,ContentWidth,19);
        var transform=g.Save();
        g.TranslateTransform(0,footerShift);
        Divider(g,737+CoolingOffset);
        g.Restore(transform);
        footer.Offset(0,footerShift);
        TextAt(g,versionLabel,small,muted,footer);
        TextAt(g,"powered by timenti",small,muted,footer,TextFormatFlags.Right);
    }

    string Hint(Item item){
        if(item==Item.Rotation&&!getRotation().HasValue)return "Датчик поворота Windows недоступен";
        if(item==Item.CameraBlock)return "Отключает камеры в Windows, включая камеру входа по лицу";
        if(item>=Item.Silent&&item<=Item.Turbo&&getCoolingAvailability()!=AsusCoolingAvailability.Ready)return CoolingNotice();
        if(item==Item.Turbo&&!PowerModes.OnAC)return "Турбо доступен от сети";
        if(item>=Item.Silent&&item<=Item.Turbo&&getCooling()==(int)fanPresets[(int)item-(int)Item.Silent])return "Последний профиль, применённый ModeTapper";
        if(item==Item.Performance)return "Производительный профиль ASUS";
        if(item==Item.Balance)return "От сети — максимум, от батареи — баланс";
        if(item==Item.Economy)return "Штатное энергосбережение Windows и отключение турбоускорения";
        if(item==Item.Touchpad)return "Умный тачпад";
        if(item==Item.Home)return "Индикатор «Домой»";
        if(item==Item.Edges)return "Грани экрана";
        if(item==Item.Animations)return "Переходы окон к значкам панели задач";
        if(item==Item.Zoom)return "Telegram: щипок приближает к курсору, движение двумя пальцами сдвигает фото";
        return null;
    }
    protected override void OnMouseMove(MouseEventArgs e){
        base.OnMouseMove(e);
        int h=Hit(e.Location);
        if(h==hover&&keyboardItem<0)return;
        keyboardItem=-1;
        hover=h;
        Cursor=h>=0&&(isPaused()||IsEnabled((Item)h))?Cursors.Hand:Cursors.Default;
        tips.SetToolTip(this,h<0||isPaused()?null:Hint((Item)h));
        Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=-1;tips.Hide(this);Invalidate();}
    void InvokeItem(int index){
        if(index<0||slidingOut)return;
        if(isPaused()){
            if(index==0)resume();else{Hide();quit();}
            return;
        }
        var item=(Item)index;
        if(!IsVisible(item)||!IsEnabled(item))return;
        if(item<=Item.ScreenOff)setMode(index);
        else if(item==Item.Rotation)toggleRotation();
        else if(item<=Item.Maximum)setPower((PowerPreset)(index-(int)Item.Economy));
        else if(item==Item.Armoury){Hide();openArmoury();return;}
        else if(item<=Item.Turbo)setCooling(fanPresets[index-(int)Item.Silent]);
        else switch(item){
            case Item.Routing:toggleVpn();break;
            case Item.Sites:Hide();editSites();return;
            case Item.Happ:Hide();openHapp();return;
            case Item.Touchpad:toggleTouchpad();break;
            case Item.Home:toggleHome();break;
            case Item.Edges:toggleEdges();break;
            case Item.Animations:toggleAnimations();break;
            case Item.Zoom:toggleZoom();break;
            case Item.CodexWidget:case Item.TasksWidget:case Item.ClockWidget:case Item.CalendarWidget:
                toggleWidget((DesktopWidgetKind)(index-(int)Item.CodexWidget));break;
            case Item.CameraOpen:Hide();openCamera();return;
            case Item.CameraBlock:toggleCamera();break;
        }
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button==MouseButtons.Left)InvokeItem(Hit(e.Location));}
    protected override void OnKeyDown(KeyEventArgs e){
        base.OnKeyDown(e);
        if(e.KeyCode==Keys.Escape){Dismiss();e.Handled=true;return;}
        if(e.KeyCode==Keys.Enter||e.KeyCode==Keys.Space){InvokeItem(keyboardItem>=0?keyboardItem:hover);e.Handled=true;return;}
        if(e.KeyCode!=Keys.Down&&e.KeyCode!=Keys.Up&&e.KeyCode!=Keys.Tab)return;
        int delta=e.KeyCode==Keys.Up||e.KeyCode==Keys.Tab&&e.Shift?-1:1;
        int count=isPaused()?2:(int)Item.CameraBlock+1;
        if(keyboardItem<0)keyboardItem=hover>=0?hover:delta==1?count-1:0;
        hover=-1;
        do{keyboardItem=(keyboardItem+delta+count)%count;}while(!isPaused()&&(!IsVisible((Item)keyboardItem)||!IsEnabled((Item)keyboardItem)));
        Invalidate();e.Handled=true;
    }
    internal void SavePreview(string path){
        ResizeMenu();
        using(var bitmap=new Bitmap(Width,Height)){
            DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(path,ImageFormat.Png);
        }
    }
    void DisposeFonts(){
        if(regular==null)return;
        regular.Dispose();small.Dispose();compact.Dispose();heading.Dispose();button.Dispose();regular=null;
    }
    protected override void Dispose(bool disposing){if(disposing){RemoveMouseHook();slide.Dispose();placementTimer.Dispose();tips.Dispose();DisposeFonts();}base.Dispose(disposing);}
}
