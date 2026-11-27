using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed class CodexWeeklyLimit {
    internal readonly int Remaining;
    internal readonly DateTime ResetUtc;
    internal readonly long AvailableResets;
    internal CodexWeeklyLimit(int remaining,DateTime resetUtc,long availableResets=0){
        Remaining=remaining;ResetUtc=resetUtc;AvailableResets=availableResets;
    }

    internal static CodexWeeklyLimit FromResponse(IDictionary<string,object> result){
        var byId=ObjectAt(result,"rateLimitsByLimitId");
        var limit=ObjectAt(byId,"codex")??ObjectAt(result,"rateLimits");
        object id;
        if(limit==null||limit.TryGetValue("limitId",out id)&&id is string&&!String.Equals((string)id,"codex",StringComparison.OrdinalIgnoreCase))return null;
        foreach(string name in new[]{"primary","secondary"}){
            var window=ObjectAt(limit,name);
            long minutes,used,reset;
            if(!ReadInteger(window,"windowDurationMins",out minutes)||minutes!=7*24*60||
               !ReadInteger(window,"usedPercent",out used)||used<0||
               !ReadInteger(window,"resetsAt",out reset)||reset<=0||reset>253402300799L)continue;
            var epoch=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc);
            long available;
            ReadInteger(ObjectAt(result,"rateLimitResetCredits"),"availableCount",out available);
            return new CodexWeeklyLimit((int)(100-Math.Min(100,used)),epoch.AddSeconds(reset),Math.Max(0,available));
        }
        return null;
    }
    internal static IDictionary<string,object> ObjectAt(IDictionary<string,object> map,string key){
        object value;
        return map!=null&&map.TryGetValue(key,out value)?value as IDictionary<string,object>:null;
    }
    static bool ReadInteger(IDictionary<string,object> map,string key,out long number){
        object value;
        number=0;
        if(map==null||!map.TryGetValue(key,out value))return false;
        if(value is int){number=(int)value;return true;}
        if(value is long){number=(long)value;return true;}
        return false;
    }
    internal static string Duration(TimeSpan remaining,bool compact=false){
        if(remaining<=TimeSpan.Zero)return null;
        long minutes=(long)Math.Ceiling(remaining.TotalMinutes);
        long hours=minutes/60;
        if(compact){
            if(hours>=24)return hours/24+"д"+(hours%24==0?"":" "+hours%24+"ч");
            if(hours>0)return hours+"ч"+(minutes%60==0?"":" "+minutes%60+"м");
            return minutes+"м";
        }
        if(hours>=24){
            string days=Quantity(hours/24,"день","дня","дней");
            return hours%24==0?days:days+" "+Quantity(hours%24,"час","часа","часов");
        }
        if(hours>0)return minutes%60==0?Quantity(hours,"час","часа","часов"):
            Quantity(hours,"час","часа","часов")+" "+minutes%60+" мин";
        return Quantity(minutes,"минуту","минуты","минут");
    }
    internal static string Quantity(long value,string one,string few,string many){
        long last=value%10,end=value%100;
        string word=end>=11&&end<=14?many:last==1?one:last>=2&&last<=4?few:many;
        return value.ToString(CultureInfo.InvariantCulture)+" "+word;
    }
}

internal static class CodexWidgetSource {
    const string PackageFamily="OpenAI.Codex_2p2nqsd0c76g0";
    [ComImport,Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ApplicationActivationManager {
        [PreserveSig]int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)]string app,[MarshalAs(UnmanagedType.LPWStr)]string arguments,uint options,out uint pid);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern int GetPackagesByPackageFamily(string family,ref uint count,IntPtr names,ref uint length,IntPtr storage);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern int GetPackagePathByFullName(string fullName,ref uint length,StringBuilder path);

    internal static string PackagePath(){
        uint count=0,length=0;
        if(GetPackagesByPackageFamily(PackageFamily,ref count,IntPtr.Zero,ref length,IntPtr.Zero)!=122||count==0||length==0)return null;
        IntPtr names=Marshal.AllocHGlobal(checked((int)count*IntPtr.Size));
        IntPtr storage=IntPtr.Zero;
        try{
            storage=Marshal.AllocHGlobal(checked((int)length*2));
            if(GetPackagesByPackageFamily(PackageFamily,ref count,names,ref length,storage)!=0)return null;
            for(int i=0;i<count;i++){
                string name=Marshal.PtrToStringUni(Marshal.ReadIntPtr(names,i*IntPtr.Size));
                uint size=0;
                if(GetPackagePathByFullName(name,ref size,null)!=122||size==0)continue;
                var path=new StringBuilder((int)size);
                if(GetPackagePathByFullName(name,ref size,path)==0&&File.Exists(Path.Combine(path.ToString(),@"app\resources\codex.exe")))return path.ToString();
            }
            return null;
        }finally{if(storage!=IntPtr.Zero)Marshal.FreeHGlobal(storage);Marshal.FreeHGlobal(names);}
    }
    internal static string FindExecutable(){
        string package=PackagePath();
        if(package!=null)return Path.Combine(package,@"app\resources\codex.exe");
        foreach(string entry in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)){
            string directory=entry.Trim().Trim('"');
            if(directory.Length<3||!Path.IsPathRooted(directory))continue;
            string path=Path.Combine(directory,"codex.exe");
            if(File.Exists(path))return path;
        }
        return null;
    }
    internal static Bitmap LoadIcon(){
        string package=PackagePath();
        if(package==null)return null;
        string path=Path.Combine(package,@"assets\Square44x44Logo.scale-200.png");
        if(!File.Exists(path))return null;
        using(var image=Image.FromFile(path))return new Bitmap(image);
    }
    internal static uint OpenChatGPT(){
        string package=PackagePath();
        if(package!=null){
            string executable=Path.Combine(package,@"app\ChatGPT.exe");
            IntPtr foreground=DesktopMotionNative.Foreground(), existing=IntPtr.Zero;
            foreach(var process in Process.GetProcessesByName("ChatGPT")){
                using(process){
                    try{
                        IntPtr window=process.MainWindowHandle;
                        if(!DesktopMotionNative.IsShown(window)||
                           !String.Equals(process.MainModule.FileName,executable,StringComparison.OrdinalIgnoreCase))continue;
                        if(existing==IntPtr.Zero||window==foreground)existing=window;
                    }catch(InvalidOperationException){}catch(Win32Exception){}
                }
            }
            uint owner=DesktopMotionNative.ProcessOf(existing);
            if(owner!=0&&DesktopMotionNative.ActivateInPlace(existing))return owner;
        }
        var clsid=new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c");
        var manager=(ApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid));
        try{
            uint pid;
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(PackageFamily+"!App",null,0,out pid));
            return pid;
        }finally{Marshal.ReleaseComObject(manager);}
    }
    internal static void OpenLink(string url){
        using(var process=Process.Start(new ProcessStartInfo(url){UseShellExecute=true})){}
    }
    internal static void OpenUsage(){
        OpenLink("codex://settings");
        ThreadPool.QueueUserWorkItem(_=>SelectUsage());
    }
    static void SelectUsage(){
        var names=new OrCondition(new PropertyCondition(AutomationElement.NameProperty,"Использование и оплата"),
            new PropertyCondition(AutomationElement.NameProperty,"Usage & billing"),
            new PropertyCondition(AutomationElement.NameProperty,"Usage and billing"));
        for(int attempt=0;attempt<20;attempt++){
            foreach(var process in Process.GetProcessesByName("ChatGPT"))using(process){
                try{
                    var app=process.MainWindowHandle;
                    if(!DesktopMotionNative.IsApp(app)||DesktopMotionNative.ClassOf(app)!="Chrome_WidgetWin_1")continue;
                    var root=AutomationElement.FromHandle(app);
                    foreach(AutomationElement link in root.FindAll(TreeScope.Descendants,names)){
                        if(link.Current.IsOffscreen||link.Current.ControlType!=ControlType.Button)continue;
                        object action;
                        if(link.TryGetCurrentPattern(InvokePattern.Pattern,out action)){
                            ((InvokePattern)action).Invoke();return;
                        }
                    }
                }catch(ArgumentException){}catch(COMException){}catch(InvalidOperationException){}catch(Win32Exception){}
                catch(ElementNotAvailableException){}
            }
            Thread.Sleep(200);
        }
    }
    internal static CodexWeeklyLimit Query(string executable){
        var json=new JavaScriptSerializer();
        using(var completed=new ManualResetEvent(false))
        using(var process=new Process{StartInfo=new ProcessStartInfo(executable,"app-server --listen stdio://"){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,
            WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        }}){
            CodexWeeklyLimit limit=null;
            bool answered=false;
            process.OutputDataReceived+=(sender,args)=>{
                if(args.Data==null){completed.Set();return;}
                try{
                    var message=json.DeserializeObject(args.Data) as IDictionary<string,object>;
                    object id;
                    if(message==null||!message.TryGetValue("id",out id)||!(id is int))return;
                    var result=CodexWeeklyLimit.ObjectAt(message,"result");
                    if((int)id==1&&result!=null){
                        process.StandardInput.WriteLine("{\"method\":\"initialized\"}");
                        process.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
                        process.StandardInput.Flush();
                        return;
                    }
                    if((int)id!=1&&(int)id!=2)return;
                    if(result!=null){limit=CodexWeeklyLimit.FromResponse(result);answered=true;}
                    completed.Set();
                }catch(ArgumentException){}catch(InvalidOperationException){completed.Set();}catch(IOException){completed.Set();}
            };
            if(!process.Start())throw new IOException();
            try{
                process.BeginErrorReadLine();process.BeginOutputReadLine();
                process.StandardInput.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"modetapper-widget\",\"title\":\"ModeTapper\",\"version\":\"1.0\"}}}");
                process.StandardInput.Flush();
                if(!completed.WaitOne(12000)||!answered)throw new IOException();
                return limit;
            }finally{
                try{process.StandardInput.Close();}catch(IOException){}
                if(!process.WaitForExit(350))try{process.Kill();}catch(InvalidOperationException){}
                process.WaitForExit();
            }
        }
    }
}

internal enum WidgetCorner {TopLeft,TopRight,BottomLeft,BottomRight}
internal sealed class WidgetPosition {
    internal readonly string Monitor;
    internal readonly Point Offset;
    internal WidgetPosition(string monitor,Point offset){Monitor=monitor;Offset=offset;}
}

internal sealed class CodexWidget : IDisposable {
    readonly Control dispatcher;
    readonly Func<IEnumerable<Rectangle>> otherWidgets;
    readonly string preference;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    DesktopCodexCard card;
    CodexWeeklyLimit limit;
    string status="Обновление…";
    bool enabled=true,working,disposed,wasOnDesktop;
    WidgetCorner corner;
    WidgetPosition position;
    int generation;
    DateTime nextRefresh,lastRequest;

    internal bool Enabled {get{return enabled;}}
    internal Rectangle Area {get{return card==null?Rectangle.Empty:card.ScreenArea;}}
    internal event Action Moved;
    internal CodexWidget(string directory,Control owner,Func<IEnumerable<Rectangle>> occupied){
        dispatcher=owner;otherWidgets=occupied;preference=Path.Combine(directory,"codex-widget.json");
        try{
            var settings=json.DeserializeObject(File.ReadAllText(preference)) as IDictionary<string,object>;
            object value;
            if(settings!=null&&settings.TryGetValue("enabled",out value)&&value is bool)enabled=(bool)value;
            if(settings!=null&&settings.TryGetValue("corner",out value)&&value is int&&(int)value>=0&&(int)value<=3)corner=(WidgetCorner)(int)value;
            object x,y,monitor;
            if(settings!=null&&settings.TryGetValue("x",out x)&&x is int&&settings.TryGetValue("y",out y)&&y is int&&
               settings.TryGetValue("monitor",out monitor)&&monitor is string){
                position=new WidgetPosition((string)monitor,new Point(Math.Max(0,(int)x),Math.Max(0,(int)y)));
            }
        }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
    }
    void Save(bool show,WidgetPosition location){
        Safety.Atomic(preference,json.Serialize(new{enabled=show,corner=(int)corner,monitor=location==null?null:location.Monitor,
            x=location==null?(int?)null:location.Offset.X,y=location==null?(int?)null:location.Offset.Y}));
    }
    internal void SetEnabled(bool show){
        if(show==enabled)return;
        Save(show,position);
        enabled=show;generation++;nextRefresh=DateTime.MinValue;
        Tick();
    }
    void RememberPosition(WidgetPosition location){
        position=location;
        try{Save(enabled,location);}catch(IOException){}catch(UnauthorizedAccessException){}
        if(Moved!=null)Moved();
    }
    internal void Tick(){
        if(disposed)return;
        if(!enabled){RemoveCard();return;}
        IntPtr desktop=DesktopCodexCard.DesktopView();
        if(card!=null&&(!card.IsHandleCreated||card.Desktop!=desktop))RemoveCard();
        try{
            if(card==null&&desktop!=IntPtr.Zero){
                card=DesktopCodexCard.Attach(desktop,corner,position,otherWidgets);
                if(card!=null){
                    card.PositionChanged+=RememberPosition;
                    card.Present(limit,status);
                    if(Moved!=null)Moved();
                }
            }else if(card!=null){card.Place(corner,position);if(card.Exposed)card.UpdateCountdown();}
        }catch(ExternalException){RemoveCard();}catch(InvalidOperationException){RemoveCard();}
        DateTime now=DateTime.UtcNow;
        bool onDesktop=card!=null&&card.Exposed;
        if(onDesktop&&!wasOnDesktop&&now-lastRequest>=TimeSpan.FromSeconds(20))nextRefresh=now;
        wasOnDesktop=onDesktop;
        if(!onDesktop||working||now<nextRefresh)return;
        working=true;lastRequest=now;
        int request=generation;
        ThreadPool.QueueUserWorkItem(_=>{
            CodexWeeklyLimit result=null;
            string failure="Не удалось обновить";
            try{
                string path=CodexWidgetSource.FindExecutable();
                if(path==null)failure="Codex не найден";
                else{
                    result=CodexWidgetSource.Query(path);
                    failure=result==null?"Нет недельных данных":"";
                }
            }catch(Win32Exception){}catch(IOException){}catch(InvalidOperationException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
            try{dispatcher.BeginInvoke(new Action(()=>Apply(request,result,failure)));}catch(InvalidOperationException){}
        });
    }
    internal void Pulse(){
        if(card==null)return;
        bool revealed=card.UpdateExposure();
        if(!card.Exposed)wasOnDesktop=false;
        if(revealed){card.UpdateCountdown();Tick();}
    }
    void Apply(int request,CodexWeeklyLimit result,string failure){
        working=false;
        if(disposed||!enabled)return;
        if(request!=generation){Tick();return;}
        DateTime now=DateTime.UtcNow;
        if(result!=null&&result.ResetUtc>now){
            limit=result;status="";
            nextRefresh=now.AddMinutes(1);
            if(result.ResetUtc<nextRefresh){
                DateTime reset=result.ResetUtc.AddSeconds(5);
                nextRefresh=reset>now.AddSeconds(20)?reset:now.AddSeconds(20);
            }
        }else{
            if(failure=="Нет недельных данных"||limit!=null&&limit.ResetUtc<=now)limit=null;
            status=limit!=null?"Данные устарели":String.IsNullOrEmpty(failure)?"Ожидание сброса":failure;
            nextRefresh=now.AddMinutes(1);
        }
        if(card!=null)card.Present(limit,status);
    }
    void RemoveCard(){if(card!=null){card.Dispose();card=null;}}
    public void Dispose(){disposed=true;RemoveCard();}
}

internal enum DesktopWidgetKind { Limit, Tasks, Clock, Calendar }

internal sealed class DesktopCodexCard : Control {
    enum CalendarPage { Days, Months, Years }
    // One layout grid for all desktop cards. Combined sizes include the gap between cells.
    const int CellWidth=200,CellHeight=88,CellGap=12;
    const int TaskRowTop=56,TaskRowHeight=39,TaskRowGap=5,TaskInset=12;
    static readonly Size Small=new Size(CellWidth,CellHeight);
    static readonly Size Medium=new Size(CellWidth,CellHeight*2+CellGap);
    [DllImport("user32.dll")]static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")]static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]static extern bool GetPointerType(uint id,out uint type);
    [DllImport("user32.dll")]static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")]static extern bool ScreenToClient(IntPtr window,ref Point point);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string type,string name);
    [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    static readonly CultureInfo Russian=CultureInfo.GetCultureInfo("ru-RU");
    readonly IntPtr desktop,dpiContext;
    readonly Bitmap icon;
    readonly DesktopWidgetKind kind;
    Func<IEnumerable<Rectangle>> otherWidgets;
    readonly System.Windows.Forms.Timer hold=new System.Windows.Forms.Timer{Interval=1000};
    readonly System.Windows.Forms.Timer touchRelease=new System.Windows.Forms.Timer{Interval=45};
    readonly System.Windows.Forms.Timer spinner=new System.Windows.Forms.Timer{Interval=45};
    Font number,title,caption,taskTitle,taskHeading;
    float scale;
    Rectangle placement;
    CodexWeeklyLimit limit;
    string status="Обновление…",remainingTime;
    DateTime widgetTime=DateTime.Now,calendarMonth,selectedDate;
    CalendarPage calendarPage;
    int firstYear;
    CodexActivityRow[] activityRows=new CodexActivityRow[0];
    int activeCount,taskOffset,taskSwipeOffset;
    bool taskSwipe;
    bool pressed,dragging,movedBeforeHold,exposed;
    int pointerId;
    long pressedAt;
    Point pressPoint,grabOffset,touchAction;
    WidgetPosition droppedPosition;
    internal IntPtr Desktop {get{return desktop;}}
    internal bool Exposed {get{return exposed;}}
    internal event Action<WidgetPosition> PositionChanged;

    internal static IntPtr DesktopView(){
        IntPtr shell=GetShellWindow();
        if(shell==IntPtr.Zero)return IntPtr.Zero;
        IntPtr view=FindWindowEx(shell,IntPtr.Zero,"SHELLDLL_DefView",null);
        if(view!=IntPtr.Zero)return view;
        uint pid=DesktopMotionNative.ProcessOf(shell);
        IntPtr worker=IntPtr.Zero;
        while((worker=FindWindowEx(IntPtr.Zero,worker,"WorkerW",null))!=IntPtr.Zero){
            if(DesktopMotionNative.ProcessOf(worker)!=pid)continue;
            view=FindWindowEx(worker,IntPtr.Zero,"SHELLDLL_DefView",null);
            if(view!=IntPtr.Zero)return view;
        }
        return IntPtr.Zero;
    }
    internal static DesktopCodexCard Attach(IntPtr desktop,WidgetCorner corner,WidgetPosition position,
        Func<IEnumerable<Rectangle>> occupied,DesktopWidgetKind kind=DesktopWidgetKind.Limit){
        IntPtr context=GetWindowDpiAwarenessContext(desktop);
        if(context==IntPtr.Zero)return null;
        IntPtr previous=SetThreadDpiAwarenessContext(context);
        if(previous==IntPtr.Zero)return null;
        DesktopCodexCard result=null;
        try{
            result=new DesktopCodexCard(desktop,context,kind){otherWidgets=occupied};
            result.CreateControl();
            result.Place(corner,position);
            return result;
        }catch{if(result!=null)result.Dispose();throw;}
        finally{SetThreadDpiAwarenessContext(previous);}
    }
    internal DesktopCodexCard(IntPtr parent,IntPtr context,DesktopWidgetKind kind=DesktopWidgetKind.Limit){
        desktop=parent;dpiContext=context;this.kind=kind;
        if(kind==DesktopWidgetKind.Limit)
            try{icon=CodexWidgetSource.LoadIcon();}catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(ExternalException){}
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        SetStyle(ControlStyles.StandardClick|ControlStyles.StandardDoubleClick,false);
        string label=kind==DesktopWidgetKind.Clock?"Часы":kind==DesktopWidgetKind.Calendar?"Календарь":kind==DesktopWidgetKind.Tasks?"Активные задачи Codex":"Недельный лимит Codex";
        Name=kind==DesktopWidgetKind.Limit?"LumaCodexWidget":"ModeTapper"+kind+"Widget";Text="ModeTapper · "+label;
        AccessibleName=label;TabStop=false;Cursor=Cursors.Hand;
        BackColor=Color.FromArgb(24,24,24);
        hold.Tick+=(sender,args)=>{
            hold.Stop();
            if(pressed&&!movedBeforeHold){dragging=true;Cursor=Cursors.SizeAll;Invalidate();}
        };
        touchRelease.Tick+=(sender,args)=>{
            touchRelease.Stop();
            if(!pressed)Activate(touchAction);
        };
        spinner.Tick+=(sender,args)=>{if(exposed)Invalidate(R(20,TaskRowTop,16,TaskVisibleRows*TaskRowHeight));};
        ScaleFonts(Math.Max(96,GetDpiForWindow(desktop))/96f);
    }
    protected override CreateParams CreateParams {
        get{var p=base.CreateParams;p.Parent=desktop;p.Style|=0x44000000;p.ExStyle|=0x08000004;return p;}
    }
    internal void Place(WidgetCorner corner,WidgetPosition saved){
        if(pressed)return;
        IntPtr previous=SetThreadDpiAwarenessContext(dpiContext);
        if(previous==IntPtr.Zero)return;
        try{
            ScaleFonts(Math.Max(96,GetDpiForWindow(desktop))/96f);
            Screen screen=saved==null?null:Array.Find(Screen.AllScreens,item=>item.DeviceName==saved.Monitor);
            screen=screen??Screen.PrimaryScreen;
            var work=screen.WorkingArea;
            bool left=corner==WidgetCorner.TopLeft||corner==WidgetCorner.BottomLeft;
            bool top=corner==WidgetCorner.TopLeft||corner==WidgetCorner.TopRight;
            var standard=kind==DesktopWidgetKind.Tasks||kind==DesktopWidgetKind.Calendar?Medium:Small;
            var grid=DesktopFolderIcons.ReadGrid(desktop,screen);
            int width=S(standard.Width),height=S(standard.Height);
            if(kind==DesktopWidgetKind.Tasks)width=Math.Min(work.Width,width+grid.Step.Width);
            int x=saved!=null?work.Left+S(Math.Min(saved.Offset.X,work.Width)):left?work.Left:work.Right-width;
            int y=saved!=null?work.Top+S(Math.Min(saved.Offset.Y,work.Height)):top?
                work.Top+S(18+(kind==DesktopWidgetKind.Calendar?CellHeight+CellGap:0)):work.Bottom-height-S(18);
            PlaceAt(SnapPosition(new Point(x,y),screen,grid,width,height),width,height);
        }finally{SetThreadDpiAwarenessContext(previous);}
    }
    Point SnapPosition(Point point,Screen screen,DesktopFolderIcons.Grid grid,int width,int height){
        var current=ScreenArea;
        var occupied=otherWidgets==null?new Rectangle[0]:otherWidgets().Where(r=>r!=current).ToArray();
        return grid.Snap(point,screen.WorkingArea,new Size(width,height),occupied);
    }
    bool PlaceAt(Point point,int width,int height){
        if(!ScreenToClient(desktop,ref point))return false;
        var target=new Rectangle(point,new Size(width,height));
        if(target==placement)return false;
        if(Size!=target.Size){
            Size=target.Size;
            taskOffset=Math.Min(taskOffset,Math.Max(0,activityRows.Length-TaskVisibleRows));
            UpdateSpinner();
            var previous=Region;
            using(var shape=TrayApp.RoundRect(new RectangleF(0,0,Width,Height),S(16)))Region=new Region(shape);
            if(previous!=null)previous.Dispose();
        }
        if(!SetWindowPos(Handle,IntPtr.Zero,target.X,target.Y,target.Width,target.Height,0x50))return false;
        placement=target;
        return true;
    }
    void BeginPress(Point point,int id){
        if(pressed)return;
        touchRelease.Stop();
        pressed=true;pointerId=id;pressPoint=point;pressedAt=Stopwatch.GetTimestamp();
        Point origin=PointToScreen(Point.Empty);
        grabOffset=new Point(point.X-origin.X,point.Y-origin.Y);
        dragging=movedBeforeHold=false;droppedPosition=null;
        taskSwipe=kind==DesktopWidgetKind.Tasks&&id!=0&&CanScrollTasks&&grabOffset.Y>=S(TaskRowTop);
        taskSwipeOffset=taskOffset;
        hold.Start();
        if(id==0)Capture=true;
    }
    void MovePress(Point point){
        if(!pressed)return;
        if(!dragging){
            if(Math.Abs(point.X-pressPoint.X)>S(8)||Math.Abs(point.Y-pressPoint.Y)>S(8)){
                movedBeforeHold=true;hold.Stop();
                if(taskSwipe)SetTaskOffset(taskSwipeOffset+(int)Math.Round((double)(pressPoint.Y-point.Y)/S(TaskRowHeight)));
            }
            return;
        }
        Screen screen=Screen.FromPoint(point);
        Rectangle work=screen.WorkingArea;
        var grid=DesktopFolderIcons.ReadGrid(desktop,screen);
        int width=kind==DesktopWidgetKind.Tasks?Math.Min(work.Width,S(CellWidth)+grid.Step.Width):Width;
        var next=SnapPosition(new Point(point.X-grabOffset.X,point.Y-grabOffset.Y),screen,grid,width,Height);
        if(PlaceAt(next,width,Height))droppedPosition=new WidgetPosition(screen.DeviceName,
            new Point((int)Math.Round((next.X-work.Left)/scale),(int)Math.Round((next.Y-work.Top)/scale)));
    }
    void EndPress(bool cancel){
        if(!pressed)return;
        bool open=!cancel&&!dragging&&!movedBeforeHold&&Stopwatch.GetTimestamp()-pressedAt<Stopwatch.Frequency;
        bool mouse=pointerId==0;
        var next=droppedPosition;
        hold.Stop();pressed=dragging=false;droppedPosition=null;
        if(mouse&&Capture)Capture=false;
        pointerId=0;Cursor=Cursors.Hand;PaintIfExposed();
        if(next!=null&&PositionChanged!=null)PositionChanged(next);
        else if(open){
            // Let Windows finish the original touch contact before raising another window.
            var action=PointToClient(pressPoint);
            if(mouse)BeginInvoke(new Action(()=>Activate(action)));
            else{touchAction=action;touchRelease.Start();}
        }
    }
    void Activate(Point point){
        int x=(int)(point.X/scale),y=(int)(point.Y/scale);
        try{
            if(kind==DesktopWidgetKind.Clock){CodexWidgetSource.OpenLink("ms-clock:");return;}
            if(kind==DesktopWidgetKind.Calendar){
                ActivateCalendar(x,y);return;
            }
            if(kind==DesktopWidgetKind.Tasks){
                if(y>=TaskRowTop&&CanScrollTasks&&x>=CardWidth-20){
                    var track=TaskScrollTrack();
                    SetTaskOffset((int)Math.Round((point.Y-track.Top)/(double)track.Height*TaskMaximumOffset));
                    return;
                }
                int row=y<TaskRowTop?-1:(y-TaskRowTop)/TaskRowHeight;Guid id;
                if(row>=0&&row<TaskVisibleRows&&taskOffset+row<activityRows.Length&&TaskBounds(row).Contains(point)&&Guid.TryParse(activityRows[taskOffset+row].Id,out id)){
                    CodexWidgetSource.OpenLink("codex://threads/"+id.ToString());return;
                }
                if(y>=TaskRowTop)return;
            }
            if(kind==DesktopWidgetKind.Limit&&x>=119){
                CodexWidgetSource.OpenUsage();return;
            }
            CodexWidgetSource.OpenChatGPT();
        }catch(Win32Exception){}catch(COMException){}catch(FileNotFoundException){}
    }
    int CardWidth {get{return (int)Math.Round(Width/scale);}}
    int TaskVisibleRows {get{return Math.Max(1,((int)Math.Round(Height/scale)-TaskRowTop-TaskInset+TaskRowGap)/TaskRowHeight);}}
    int TaskMaximumOffset {get{return Math.Max(0,activityRows.Length-TaskVisibleRows);}}
    bool CanScrollTasks {get{return TaskMaximumOffset>0;}}
    Rectangle TaskBounds(int row){
        return R(TaskInset,TaskRowTop+row*TaskRowHeight,CardWidth-2*TaskInset-(CanScrollTasks?11:0),TaskRowHeight-TaskRowGap);
    }
    Rectangle TaskScrollTrack(){return R(CardWidth-15,TaskRowTop+1,4,TaskVisibleRows*TaskRowHeight-TaskRowGap-2);}
    void SetTaskOffset(int offset){
        int next=Math.Max(0,Math.Min(TaskMaximumOffset,offset));
        if(next==taskOffset)return;
        taskOffset=next;
        UpdateSpinner();
        PaintIfExposed();
    }
    DateTime DisplayedMonth {get{return calendarMonth==DateTime.MinValue?new DateTime(widgetTime.Year,widgetTime.Month,1):calendarMonth;}}
    bool ShowToday {get{return selectedDate==DateTime.MinValue
        ?DisplayedMonth!=new DateTime(widgetTime.Year,widgetTime.Month,1):selectedDate!=widgetTime.Date;}}
    string CalendarHeader {
        get{
            if(calendarPage==CalendarPage.Months)return "Месяц";
            if(calendarPage==CalendarPage.Years)return firstYear+"–"+(firstYear+11);
            return Russian.TextInfo.ToTitleCase(DisplayedMonth.ToString("MMMM",Russian));
        }
    }
    int YearHeaderLeft {
        get{
            int width=TextRenderer.MeasureText(CalendarHeader,title,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width;
            return Math.Min(91,14+(int)Math.Ceiling(width/scale)+9);
        }
    }
    void SetCalendarYear(int year){
        calendarMonth=new DateTime(Math.Max(1,Math.Min(9999,year)),DisplayedMonth.Month,1);
        selectedDate=DateTime.MinValue;
    }
    void MoveCalendar(int direction){
        var month=DisplayedMonth;
        if(calendarPage==CalendarPage.Years)firstYear=Math.Max(1,Math.Min(9988,firstYear+direction*12));
        else if(calendarPage==CalendarPage.Months)SetCalendarYear(month.Year+direction);
        else if(!(direction<0&&month==DateTime.MinValue)&&!(direction>0&&month.Year==9999&&month.Month==12)){
            calendarMonth=month.AddMonths(direction);
            selectedDate=DateTime.MinValue;
        }
        PaintIfExposed();
    }
    void ActivateCalendar(int x,int y){
        if(y>=168){
            if(x>=118&&ShowToday){
                widgetTime=DateTime.Now;calendarMonth=selectedDate=DateTime.MinValue;calendarPage=CalendarPage.Days;
            }else if(x<118&&calendarPage!=CalendarPage.Days)calendarPage=CalendarPage.Days;
        }else if(y<32){
            if(x>=170){MoveCalendar(1);return;}
            if(x>=145){MoveCalendar(-1);return;}
            if(x>=YearHeaderLeft-4){
                if(calendarPage==CalendarPage.Years)calendarPage=CalendarPage.Months;
                else{firstYear=Math.Max(1,Math.Min(9988,DisplayedMonth.Year/12*12));calendarPage=CalendarPage.Years;}
            }else calendarPage=calendarPage==CalendarPage.Months?CalendarPage.Days:CalendarPage.Months;
        }else if(x>=11&&x<186){
            DateTime month=DisplayedMonth;
            if(calendarPage==CalendarPage.Days&&y>=53){
                int day=(y-53)/19*7+(x-11)/25-((int)month.DayOfWeek+6)%7+1;
                if(day>0&&day<=DateTime.DaysInMonth(month.Year,month.Month))selectedDate=new DateTime(month.Year,month.Month,day);
            }else if(calendarPage!=CalendarPage.Days&&y>=40&&y<156){
                int slot=(y-40)/29*3+(x-11)/59;
                if(calendarPage==CalendarPage.Months){
                    calendarMonth=new DateTime(month.Year,slot+1,1);
                    selectedDate=DateTime.MinValue;calendarPage=CalendarPage.Days;
                }else{
                    SetCalendarYear(firstYear+slot);calendarPage=CalendarPage.Months;
                }
            }
        }
        PaintIfExposed();
    }
    void PaintIfExposed(){if(exposed)Invalidate();}
    bool CoversPoint(int x,int y){
        return WindowFromPoint(PointToScreen(new Point(x,y)))==Handle;
    }
    void KeepAboveDesktopIcons(){
        IntPtr icons=FindWindowEx(desktop,IntPtr.Zero,"SysListView32",null);
        if(icons==IntPtr.Zero||!IsWindowVisible(icons))return;
        const uint previousSibling=3,noMoveResizeOrActivate=0x13;
        // Explorer can raise the icon list after the cards have already been placed.
        for(IntPtr sibling=GetWindow(Handle,previousSibling);sibling!=IntPtr.Zero;sibling=GetWindow(sibling,previousSibling)){
            if(sibling!=icons)continue;
            SetWindowPos(Handle,IntPtr.Zero,0,0,0,0,noMoveResizeOrActivate);
            return;
        }
    }
    internal bool UpdateExposure(){
        bool ready=Visible&&IsHandleCreated;
        if(ready)KeepAboveDesktopIcons();
        int inset=S(12),middleX=Width/2,middleY=Height/2;
        bool next=ready&&Width>2*inset&&Height>2*inset&&
            (CoversPoint(middleX,middleY)||CoversPoint(inset,inset)||CoversPoint(Width-inset-1,inset)||
             CoversPoint(inset,Height-inset-1)||CoversPoint(Width-inset-1,Height-inset-1));
        if(next==exposed)return false;
        exposed=next;
        UpdateSpinner();
        PaintIfExposed();
        return exposed;
    }
    void UpdateSpinner(){
        if(kind!=DesktopWidgetKind.Tasks)return;
        bool animate=exposed&&activityRows.Skip(taskOffset).Take(TaskVisibleRows).Any(row=>row.Active)&&LumaFlyout.AnimationsEnabled();
        if(animate&&!spinner.Enabled)spinner.Start();
        else if(!animate&&spinner.Enabled)spinner.Stop();
    }
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(pressed&&pointerId==0&&!Capture)EndPress(true);}
    void ScaleFonts(float value){
        if(scale==value)return;
        scale=value;DisposeFonts();
        number=new Font("Segoe UI Semibold",26*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        title=new Font("Segoe UI Semibold",13*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        caption=new Font("Segoe UI",11.5f*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        if(kind==DesktopWidgetKind.Tasks){
            taskTitle=new Font("Segoe UI Semibold",14*scale,FontStyle.Regular,GraphicsUnit.Pixel);
            taskHeading=new Font("Segoe UI Semibold",16*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        }
    }
    int S(float n){return (int)Math.Round(n*scale);}
    Rectangle R(float x,float y,float width,float height){return new Rectangle(S(x),S(y),S(width),S(height));}
    string ResetLabel {get{return limit==null?status:remainingTime??"Обновление…";}}
    string ResetBadge {get{return limit!=null&&status.Length==0&&limit.AvailableResets>0?
        CodexWeeklyLimit.Quantity(limit.AvailableResets,"сброс","сброса","сбросов"):null;}}
    internal void Present(CodexWeeklyLimit value,string message){
        limit=value;status=message;
        UpdateCountdown(true);
    }
    internal void UpdateCountdown(bool repaint=false){
        TimeSpan duration=limit==null?TimeSpan.Zero:limit.ResetUtc-DateTime.UtcNow;
        string value=CodexWeeklyLimit.Duration(duration,true);
        if(!repaint&&remainingTime==value)return;
        remainingTime=value;
        string badge=ResetBadge;
        AccessibleDescription=limit==null?status:limit.Remaining+"% осталось. "+(value==null?ResetLabel:"Сброс через "+CodexWeeklyLimit.Duration(duration))+
            (badge==null?"":". Доступно: "+badge)+(status.Length==0?"":". "+status);
        PaintIfExposed();
    }
    internal Rectangle ScreenArea {
        get{return IsHandleCreated?new Rectangle(PointToScreen(Point.Empty),ClientSize):Rectangle.Empty;}
    }
    internal void PresentTime(DateTime value){
        bool changed=widgetTime.Date!=value.Date||kind==DesktopWidgetKind.Clock&&widgetTime.ToString("HH:mm")!=value.ToString("HH:mm");
        widgetTime=value;
        if(changed){
            AccessibleDescription=kind==DesktopWidgetKind.Clock?value.ToString("HH:mm",Russian):value.ToString("D",Russian);
            PaintIfExposed();
        }
    }
    internal void PresentTasks(CodexActivityRow[] rows,int count){
        if(activeCount==count&&activityRows.Length==rows.Length){
            bool same=true;
            for(int i=0;i<rows.Length;i++){
                var a=activityRows[i];var b=rows[i];
                if(a.Id!=b.Id||a.Title!=b.Title||a.Action!=b.Action||a.Active!=b.Active||a.Waiting!=b.Waiting){same=false;break;}
            }
            if(same)return;
        }
        activityRows=rows;activeCount=count;
        taskOffset=Math.Min(taskOffset,Math.Max(0,rows.Length-TaskVisibleRows));
        AccessibleDescription="В работе: "+count+". "+(rows.Length==0?"Чатов пока нет":
            String.Join(", ",rows.Select(r=>r.Title+" · "+r.Action)));
        UpdateSpinner();PaintIfExposed();
    }
    protected override void WndProc(ref Message message){
        if((kind==DesktopWidgetKind.Calendar||kind==DesktopWidgetKind.Tasks)&&message.Msg==0x020a){
            int wheel=(short)(message.WParam.ToInt64()>>16);
            if(wheel!=0){
                if(kind==DesktopWidgetKind.Calendar)MoveCalendar(wheel>0?-1:1);
                else SetTaskOffset(taskOffset-Math.Sign(wheel)*Math.Max(1,Math.Abs(wheel)/120));
            }
            message.Result=IntPtr.Zero;return;
        }
        if(message.Msg==0x21||message.Msg==0x024b){message.Result=new IntPtr(3);return;}
        if(message.Msg==0x7b){message.Result=IntPtr.Zero;return;}
        if(message.Msg==0x02c9){message.Result=new IntPtr(1);return;}
        if(message.Msg==0x1f)EndPress(true);
        if(message.Msg>=0x0200&&message.Msg<=0x0203){
            // Own the capture through button-up. WinForms can release it before OnMouseUp.
            if(!GestureMouseGate.IsPromotedTouch(unchecked((ulong)GetMessageExtraInfo().ToInt64()))){
                long packed=message.LParam.ToInt64();
                var point=PointToScreen(new Point((short)packed,(short)(packed>>16)));
                if(message.Msg==0x0201||message.Msg==0x0203)BeginPress(point,0);
                else if(pressed&&pointerId==0){
                    MovePress(point);
                    if(message.Msg==0x0202)EndPress(false);
                }
            }
            message.Result=IntPtr.Zero;return;
        }
        int id=(int)(message.WParam.ToInt64()&0xffff);
        if(message.Msg==0x024c&&pressed&&pointerId==id)EndPress(true);
        if(message.Msg>=0x0245&&message.Msg<=0x0247){
            uint type;
            if(pointerId==id&&pressed||GetPointerType((uint)id,out type)&&(type==2||type==3)){
                long packed=message.LParam.ToInt64();
                var point=new Point((short)packed,(short)(packed>>16));
                if(message.Msg==0x0246)BeginPress(point,id);
                else if(pressed&&pointerId==id){
                    bool canceled=(message.WParam.ToInt64()&0x80000000L)!=0;
                    if(!canceled)MovePress(point);
                    if(canceled||message.Msg==0x0247)EndPress(canceled);
                }
                message.Result=IntPtr.Zero;return;
            }
        }
        base.WndProc(ref message);
    }
    protected override void OnPaint(PaintEventArgs e){
        if(!exposed&&desktop!=IntPtr.Zero)return;
        var g=e.Graphics;
        g.SmoothingMode=SmoothingMode.AntiAlias;
        g.InterpolationMode=InterpolationMode.HighQualityBicubic;
        if(ClientSize.Width<2||ClientSize.Height<2)return;
        var bounds=new RectangleF(.5f,.5f,Width-1,Height-1);
        using(var surface=TrayApp.RoundRect(bounds,S(16)))
        using(var fill=new LinearGradientBrush(bounds,Color.FromArgb(36,36,36),Color.FromArgb(23,23,23),60f))
        using(var edge=new Pen(dragging?Color.FromArgb(122,122,122):Color.FromArgb(69,69,69))){g.FillPath(fill,surface);g.DrawPath(edge,surface);}
        if(kind!=DesktopWidgetKind.Limit){DrawUtility(g);return;}
        var logo=R(13,12,34,34);
        using(var shape=TrayApp.RoundRect(logo,S(9)))
        using(var fill=new SolidBrush(Color.FromArgb(63,63,63))){
            g.FillPath(fill,shape);
            if(icon!=null){var state=g.Save();g.SetClip(shape);g.DrawImage(icon,logo);g.Restore(state);}
            else{
                using(var pen=new Pen(Color.Gainsboro,Math.Max(1,S(1.4f))){StartCap=LineCap.Round,EndCap=LineCap.Round}){
                    g.DrawLines(pen,new[]{new Point(S(20),S(23)),new Point(S(26),S(29)),new Point(S(20),S(35))});
                    g.DrawLine(pen,S(29),S(35),S(37),S(35));
                }
            }
        }
        string badge=ResetBadge;
        TextAt(g,"Codex",title,Color.FromArgb(249,249,249),R(54,17,68,23));
        TextAt(g,limit==null?"—":limit.Remaining.ToString(Russian)+"%",number,Color.FromArgb(249,249,249),
            R(119,9,69,37),TextFormatFlags.Right);
        var track=R(13,53,174,4);
        using(var shape=TrayApp.RoundRect(track,track.Height/2f))
        using(var fill=new SolidBrush(Color.FromArgb(62,62,62)))g.FillPath(fill,shape);
        int length=limit==null?0:(int)Math.Round(track.Width*limit.Remaining/100.0);
        if(length>0){
            var filled=new Rectangle(track.X,track.Y,length,track.Height);
            using(var shape=TrayApp.RoundRect(filled,Math.Min(filled.Width,filled.Height)/2f))
            using(var fill=new SolidBrush(Color.FromArgb(226,226,226)))g.FillPath(fill,shape);
        }
        if(remainingTime!=null&&status.Length==0){
            using(var pen=new Pen(Color.Silver,Math.Max(1,S(.75f)))){
                g.DrawEllipse(pen,R(13,68,9,9));
                g.DrawLines(pen,new[]{new Point(S(17.5f),S(70)),new Point(S(17.5f),S(72.5f)),new Point(S(19.5f),S(73.5f))});
            }
            TextAt(g,ResetLabel,caption,Color.Silver,R(27,65,85,15));
        }else TextAt(g,limit!=null&&status.Length>0?"Устарели":ResetLabel,caption,Color.Silver,R(13,65,105,15));
        if(badge!=null){
            var pill=R(120,63,67,18);
            using(var shape=TrayApp.RoundRect(pill,pill.Height/2f))
            using(var fill=new SolidBrush(Color.FromArgb(245,245,245)))g.FillPath(fill,shape);
            TextAt(g,badge,caption,Color.FromArgb(27,27,27),pill,TextFormatFlags.HorizontalCenter);
        }
    }
    void DrawUtility(Graphics g){
        var ink=Color.FromArgb(247,247,247);
        if(kind==DesktopWidgetKind.Clock){
            using(var clock=new Font("Segoe UI Semibold",48*scale,FontStyle.Regular,GraphicsUnit.Pixel))
                TextAt(g,widgetTime.ToString("HH:mm",Russian),clock,ink,R(12,5,129,76),TextFormatFlags.HorizontalCenter);
            using(var divider=new Pen(Color.FromArgb(67,67,67),Math.Max(1,S(.5f))))
                g.DrawLine(divider,S(145),S(20),S(145),S(68));
            string weekday=widgetTime.ToString("ddd",Russian).TrimEnd('.').ToUpper(Russian);
            TextAt(g,weekday,caption,Color.Silver,R(151,19,39,19),TextFormatFlags.HorizontalCenter);
            TextAt(g,widgetTime.Day.ToString(Russian),number,ink,R(151,36,39,33),TextFormatFlags.HorizontalCenter);
            return;
        }
        if(kind==DesktopWidgetKind.Tasks){
            DrawTasks(g,ink);
            return;
        }
        DateTime first=DisplayedMonth;
        int yearX=YearHeaderLeft;
        TextAt(g,CalendarHeader,title,ink,R(13,6,yearX-15,24));
        TextAt(g,first.Year.ToString(Russian)+" г.",title,ink,R(yearX,6,143-yearX,24));
        TextAt(g,"‹",title,Color.Silver,R(145,5,25,24),TextFormatFlags.HorizontalCenter);
        TextAt(g,"›",title,Color.Silver,R(170,5,22,24),TextFormatFlags.HorizontalCenter);
        if(ShowToday)TextAt(g,"Сегодня",caption,ink,R(118,168,66,16),TextFormatFlags.Right);
        if(calendarPage!=CalendarPage.Days){
            TextAt(g,"Назад",caption,Color.Silver,R(13,168,72,16));
            DrawCalendarChoices(g,first,ink);
            return;
        }
        if(selectedDate!=DateTime.MinValue)TextAt(g,selectedDate.ToString("d MMM, ddd",Russian),caption,Color.Silver,R(13,168,98,16));
        string[] weekdays={"Пн","Вт","Ср","Чт","Пт","Сб","Вс"};
        for(int i=0;i<7;i++)TextAt(g,weekdays[i],caption,Color.Silver,R(11+25*i,33,25,18),TextFormatFlags.HorizontalCenter);
        int offset=((int)first.DayOfWeek+6)%7;
        for(int day=1;day<=DateTime.DaysInMonth(first.Year,first.Month);day++){
            int slot=offset+day-1;
            float x=11+25*(slot%7),y=53+19*(slot/7);
            DateTime date=first.AddDays(day-1);
            bool today=date==widgetTime.Date,selected=date==selectedDate;
            if(today||selected)using(var brush=new SolidBrush(today?ink:Color.FromArgb(79,79,79)))
                g.FillEllipse(brush,R(x+3,y+.5f,19,17.5f));
            TextAt(g,day.ToString(Russian),caption,today?Color.FromArgb(31,31,31):ink,R(x,y,25,19),TextFormatFlags.HorizontalCenter);
        }
    }
    void DrawTasks(Graphics g,Color ink){
        var badge=R(CardWidth-105,10,93,34);
        using(var shape=TrayApp.RoundRect(badge,S(9)))
        using(var fill=new SolidBrush(Color.FromArgb(50,50,50)))
        using(var edge=new Pen(Color.FromArgb(72,72,72),Math.Max(1,S(.7f)))){
            g.FillPath(fill,shape);g.DrawPath(edge,shape);
        }
        TextAt(g,"Задачи Codex",taskHeading,ink,R(TaskInset+2,10,CardWidth-125,34));
        TextAt(g,activeCount.ToString(Russian),number,ink,R(CardWidth-102,9,36,35),TextFormatFlags.HorizontalCenter);
        TextAt(g,"в работе",caption,Color.Silver,R(CardWidth-65,10,48,34));
        using(var divider=new Pen(Color.FromArgb(66,66,66),Math.Max(1,S(.7f))))
            g.DrawLine(divider,S(TaskInset),S(50),S(CardWidth-TaskInset),S(50));
        if(activityRows.Length==0){
            TextAt(g,"Чатов пока нет",taskTitle,Color.Silver,R(TaskInset,TaskRowTop,CardWidth-2*TaskInset,116),TextFormatFlags.HorizontalCenter);
            return;
        }
        for(int i=0;i<TaskVisibleRows&&i+taskOffset<activityRows.Length;i++){
            var row=activityRows[i+taskOffset];
            var bounds=TaskBounds(i);
            int y=TaskRowTop+i*TaskRowHeight;
            using(var shape=TrayApp.RoundRect(bounds,S(7)))
            using(var fill=new SolidBrush(row.Active?Color.FromArgb(49,49,49):Color.FromArgb(39,39,39)))
                g.FillPath(fill,shape);
            if(row.Active)DrawSpinner(g,R(22,y+11,12,12));
            else using(var mark=new SolidBrush(row.Waiting?Color.FromArgb(236,191,104):Color.FromArgb(147,147,147)))
                g.FillEllipse(mark,R(24,y+13,8,8));
            int width=CardWidth-(CanScrollTasks?80:69);
            TextAt(g,row.Title,taskTitle,ink,R(43,y+1,width,18));
            TextAt(g,row.Action,caption,row.Waiting?Color.FromArgb(236,191,104):Color.Silver,R(43,y+18,width,14));
        }
        if(!CanScrollTasks)return;
        var track=TaskScrollTrack();
        int height=Math.Max(S(13),track.Height*TaskVisibleRows/activityRows.Length);
        var thumb=new Rectangle(track.X,track.Y+(track.Height-height)*taskOffset/TaskMaximumOffset,track.Width,height);
        using(var rail=TrayApp.RoundRect(track,track.Width/2f))
        using(var knob=TrayApp.RoundRect(thumb,thumb.Width/2f))
        using(var railFill=new SolidBrush(Color.FromArgb(53,53,53)))
        using(var knobFill=new SolidBrush(Color.FromArgb(134,134,134))){
            g.FillPath(railFill,rail);g.FillPath(knobFill,knob);
        }
    }
    void DrawCalendarChoices(Graphics g,DateTime first,Color ink){
        for(int i=0;i<12;i++){
            int x=11+(i%3)*59,y=40+(i/3)*29;
            bool months=calendarPage==CalendarPage.Months;
            bool selected=months?i+1==first.Month:firstYear+i==first.Year;
            var cell=R(x+2,y+2,55,25);
            if(selected)using(var shape=TrayApp.RoundRect(cell,S(6)))
            using(var brush=new SolidBrush(Color.FromArgb(70,70,70)))g.FillPath(brush,shape);
            string text=months?Russian.TextInfo.ToTitleCase(new DateTime(first.Year,i+1,1).ToString("MMM",Russian)):
                (firstYear+i).ToString(Russian);
            TextAt(g,text,caption,ink,cell,TextFormatFlags.HorizontalCenter);
        }
    }
    void DrawSpinner(Graphics g,Rectangle bounds){
        float angle=spinner.Enabled?(float)((Stopwatch.GetTimestamp()%(Stopwatch.Frequency*2L))*180.0/Stopwatch.Frequency):-90f;
        using(var track=new Pen(Color.FromArgb(75,75,75),Math.Max(1,S(1.1f))))g.DrawEllipse(track,bounds);
        using(var indicator=new Pen(Color.FromArgb(234,234,234),Math.Max(1,S(1.25f))){StartCap=LineCap.Round,EndCap=LineCap.Round})
            g.DrawArc(indicator,bounds,angle,245);
    }
    static void TextAt(Graphics g,string text,Font font,Color color,Rectangle bounds,TextFormatFlags align=TextFormatFlags.Left){
        TextRenderer.DrawText(g,text,font,bounds,color,align|TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
    }
    void DisposeFonts(){
        if(number!=null){number.Dispose();title.Dispose();caption.Dispose();}
        if(taskTitle!=null){taskTitle.Dispose();taskHeading.Dispose();}
    }
    protected override void Dispose(bool disposing){if(disposing){EndPress(true);hold.Dispose();touchRelease.Dispose();spinner.Dispose();DisposeFonts();if(icon!=null)icon.Dispose();}base.Dispose(disposing);}
}
