using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed class DesktopIconReservation : IDisposable {
    readonly Control owner;
    readonly Func<IEnumerable<Rectangle>> widgetAreas;
    readonly Timer refresh=new Timer{Interval=1300};
    readonly Timer changed=new Timer{Interval=160};
    readonly List<FileSystemWatcher> watchers=new List<FileSystemWatcher>();
    bool disposed;
    int delayedChecks;

    internal DesktopIconReservation(Control owner,Func<IEnumerable<Rectangle>> widgetAreas){
        this.owner=owner;this.widgetAreas=widgetAreas;
        refresh.Tick+=(sender,args)=>ExcludeIcons(false);
        changed.Tick+=(sender,args)=>{
            ExcludeIcons(true);
            if(--delayedChecks<=0)changed.Stop();
            else changed.Interval=450;
        };
        foreach(var folder in new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)}.Distinct(StringComparer.OrdinalIgnoreCase)){
            if(!Directory.Exists(folder))continue;
            try{
                var watcher=new FileSystemWatcher(folder){NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName};
                watcher.Created+=(sender,args)=>QueueCheck();
                watcher.Renamed+=(sender,args)=>QueueCheck();
                watcher.EnableRaisingEvents=true;
                watchers.Add(watcher);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        refresh.Start();
    }
    void QueueCheck(){
        try{owner.BeginInvoke(new Action(CheckSoon));}catch(InvalidOperationException){}
    }
    internal void CheckSoon(){
        if(disposed)return;
        changed.Stop();changed.Interval=160;delayedChecks=4;changed.Start();
    }
    void ExcludeIcons(bool force){
        if(disposed||Safety.Safe)return;
        var reserved=widgetAreas().Where(r=>!r.IsEmpty).ToArray();
        if(reserved.Length==0)return;
        if(!force&&!DesktopMotionNative.IsDesktopView(DesktopMotionNative.Foreground())&&
           !reserved.Any(r=>DesktopMotionNative.IsDesktopView(DesktopMotionNative.At(new Point(r.Left+r.Width/2,r.Top+r.Height/2)))))return;
        try{DesktopFolderIcons.Reposition(reserved);}
        catch(COMException){}catch(InvalidCastException){}catch(InvalidOperationException){}
        catch(Microsoft.CSharp.RuntimeBinder.RuntimeBinderException){}
    }
    public void Dispose(){
        disposed=true;refresh.Dispose();changed.Dispose();
        foreach(var watcher in watchers)watcher.Dispose();
    }
}

internal static class DesktopFolderIcons {
    internal sealed class Grid {
        internal readonly Point Origin;
        internal readonly Size Step;
        internal Grid(Point origin,Size step){Origin=origin;Step=step;}
        internal Point Snap(Point wanted,Rectangle work,Size size,IEnumerable<Rectangle> occupied){
            Point best=new Point(Math.Max(work.Left,Math.Min(wanted.X,work.Right-size.Width)),
                Math.Max(work.Top,Math.Min(wanted.Y,work.Bottom-size.Height)));
            Point nearest=best;long distance=long.MaxValue,nearestDistance=long.MaxValue;
            int first=Align(work.Left,Origin.X,Step.Width);
            if(first-work.Left>Step.Width-8)first=work.Left;
            int gridRight=first+Math.Max(0,(work.Right-first)/Step.Width)*Step.Width;
            int rightAnchor=gridRight-size.Width;
            var columns=new List<int>();
            for(int x=first;x<=rightAnchor;x+=Step.Width)columns.Add(x);
            if(rightAnchor>=first&&!columns.Contains(rightAnchor))columns.Add(rightAnchor);
            if(rightAnchor>=first&&wanted.X>=rightAnchor-Step.Width/2)wanted.X=rightAnchor;
            foreach(int x in columns)
                for(int y=Align(work.Top,Origin.Y,Step.Height);y<=work.Bottom-size.Height;y+=Step.Height){
                    long dx=(long)x-wanted.X,dy=(long)y-wanted.Y,next=dx*dx+dy*dy;
                    if(next<nearestDistance){nearest=new Point(x,y);nearestDistance=next;}
                    if(next>=distance||occupied.Any(r=>r.IntersectsWith(new Rectangle(x,y,size.Width,size.Height))))continue;
                    best=new Point(x,y);distance=next;
                }
            return distance==long.MaxValue?nearest:best;
        }
    }
    sealed class GridEntry {internal Rectangle Work;internal IntPtr List;internal long Checked;internal Grid Value;}
    static readonly Dictionary<string,GridEntry> grids=new Dictionary<string,GridEntry>();
    [ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ShellServices {
        [PreserveSig]int QueryService(ref Guid service,ref Guid kind,out IntPtr value);
    }
    [ComImport,Guid("000214E2-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ShellBrowser {
        void GetWindow();void ContextSensitiveHelp();void InsertMenus();void SetMenu();void RemoveMenus();
        void SetStatusText();void EnableModeless();void TranslateAccelerator();void BrowseObject();
        void GetViewStateStream();void GetControlWindow();void SendControlMessage();
        [PreserveSig]int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)]out object view);
    }
    [ComImport,Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface FolderView {
        void GetCurrentViewMode();void SetCurrentViewMode();void GetFolder();
        [PreserveSig]int Item(int index,out IntPtr id);
        [PreserveSig]int ItemCount(uint flags,out int count);
        void Items();void GetSelectionMarkedItem();void GetFocusedItem();
        [PreserveSig]int GetItemPosition(IntPtr id,out Point position);
        [PreserveSig]int GetSpacing(out Point spacing);
        void GetDefaultSpacing();
        [PreserveSig]int GetAutoArrange();
        void SelectItem();
        [PreserveSig]int SelectAndPositionItems(uint count,
            [In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=0)]IntPtr[] ids,
            [In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=0)]Point[] positions,uint flags);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetFolderFlags(IntPtr view,uint mask,uint flags);
    sealed class IconLocation {
        internal IntPtr Id;
        internal Point Position;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string type,string name);
    [DllImport("user32.dll")]static extern bool ScreenToClient(IntPtr window,ref Point point);
    [DllImport("user32.dll")]static extern bool ClientToScreen(IntPtr window,ref Point point);
    static readonly Guid ShellWindows=new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid TopLevelBrowser=new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid FolderView2=new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
    const uint ViewItems=2,PositionItem=0x80,AutoArrange=1;

    static void Release(object value){if(value!=null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    static object FindView(){
        object windows=Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindows,true)),dispatch=null,browser=null;
        try{
            object desktop=0,root=Type.Missing;
            int hwnd;
            dispatch=((dynamic)windows).FindWindowSW(ref desktop,ref root,8,out hwnd,1);
            if(dispatch==null)return null;
            var service=TopLevelBrowser;var id=typeof(ShellBrowser).GUID;
            IntPtr native;
            if(((ShellServices)dispatch).QueryService(ref service,ref id,out native)!=0||native==IntPtr.Zero)return null;
            try{browser=Marshal.GetObjectForIUnknown(native);}finally{Marshal.Release(native);}
            object view;
            return ((ShellBrowser)browser).QueryActiveShellView(out view)==0?view:null;
        }finally{Release(browser);Release(dispatch);Release(windows);}
    }
    static bool AllowFreePositions(object view){
        IntPtr native=Marshal.GetIUnknownForObject(view),extended=IntPtr.Zero;
        try{
            var id=FolderView2;
            if(Marshal.QueryInterface(native,ref id,out extended)!=0)return false;
            // IUnknown, 14 IFolderView methods, then the first 7 IFolderView2 methods.
            const int SetCurrentFolderFlagsSlot=24;
            var method=Marshal.ReadIntPtr(Marshal.ReadIntPtr(extended),SetCurrentFolderFlagsSlot*IntPtr.Size);
            var set=(SetFolderFlags)Marshal.GetDelegateForFunctionPointer(method,typeof(SetFolderFlags));
            return set(extended,AutoArrange,0)==0;
        }finally{if(extended!=IntPtr.Zero)Marshal.Release(extended);Marshal.Release(native);}
    }
    static Rectangle ClientArea(IntPtr list,Rectangle area){
        var top=area.Location;var bottom=new Point(area.Right,area.Bottom);
        if(!ScreenToClient(list,ref top)||!ScreenToClient(list,ref bottom))return Rectangle.Empty;
        return Rectangle.FromLTRB(top.X,top.Y,bottom.X,bottom.Y);
    }
    static Rectangle Footprint(Point position,Point spacing){
        return new Rectangle(position.X-spacing.X/5,position.Y,spacing.X,spacing.Y-3);
    }
    static int Phase(int position,int step){return ((position%step)+step)%step;}
    static int Align(int start,int phase,int step){return start+Phase(phase-start,step);}

    internal static Grid ReadGrid(IntPtr desktop,Screen screen){
        var work=screen.WorkingArea;
        IntPtr list=FindWindowEx(desktop,IntPtr.Zero,"SysListView32",null);
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        GridEntry entry;
        if(grids.TryGetValue(screen.DeviceName,out entry)&&entry.List==list&&entry.Work==work&&
           now-entry.Checked<System.Diagnostics.Stopwatch.Frequency*4)return entry.Value;
        Grid grid=null;
        if(list!=IntPtr.Zero)try{grid=QueryGrid(list,work);}
        catch(COMException){}catch(InvalidCastException){}catch(InvalidOperationException){}
        catch(Microsoft.CSharp.RuntimeBinder.RuntimeBinderException){}
        if(grid==null){
            var step=SystemInformation.IconSpacingSize;
            grid=new Grid(work.Location,new Size(Math.Max(32,step.Width),Math.Max(32,step.Height)));
        }
        grids[screen.DeviceName]=new GridEntry{List=list,Work=work,Checked=now,Value=grid};
        return grid;
    }
    static Grid QueryGrid(IntPtr list,Rectangle work){
        object shellView=FindView();
        if(shellView==null)return null;
        try{
            var view=(FolderView)shellView;
            Point spacing;int count;
            if(view.GetSpacing(out spacing)!=0||spacing.X<12||spacing.Y<12||view.ItemCount(ViewItems,out count)!=0)return null;
            var onScreen=new List<Point>();var all=new List<Point>();
            for(int i=0;i<Math.Min(count,32);i++){
                IntPtr id;Point position;
                if(view.Item(i,out id)!=0||id==IntPtr.Zero)continue;
                try{
                    if(view.GetItemPosition(id,out position)!=0||!ClientToScreen(list,ref position))continue;
                    Point cell=Footprint(position,spacing).Location;
                    var phase=new Point(Phase(cell.X,spacing.X),Phase(cell.Y,spacing.Y));
                    all.Add(phase);if(work.Contains(position))onScreen.Add(phase);
                }finally{Marshal.FreeCoTaskMem(id);}
            }
            var origin=IconCellPhase(list,work,spacing)??(onScreen.Count>0?onScreen:all).GroupBy(p=>p)
                .OrderByDescending(g=>g.Count()).Select(g=>(Point?)g.Key).FirstOrDefault()??work.Location;
            return new Grid(origin,new Size(spacing.X,spacing.Y));
        }finally{Release(shellView);}
    }
    static Point? IconCellPhase(IntPtr list,Rectangle work,Point spacing){
        try{
            var root=AutomationElement.FromHandle(list);
            var visible=root.FindAll(TreeScope.Children,Condition.TrueCondition);
            var samples=new List<Point>();
            for(int i=0;i<Math.Min(visible.Count,32);i++){
                var bounds=visible[i].Current.BoundingRectangle;
                if(bounds.IsEmpty||!work.Contains((int)bounds.X,(int)bounds.Y))continue;
                samples.Add(new Point(Phase((int)Math.Round(bounds.X),spacing.X),Phase((int)Math.Round(bounds.Y),spacing.Y)));
            }
            return samples.GroupBy(p=>p).OrderByDescending(g=>g.Count()).Select(g=>(Point?)g.Key).FirstOrDefault();
        }catch(ElementNotAvailableException){return null;}
        catch(InvalidOperationException){return null;}
        catch(COMException){return null;}
    }

    internal static int Reposition(Rectangle[] reserved){
        var desktop=DesktopCodexCard.DesktopView();
        var list=FindWindowEx(desktop,IntPtr.Zero,"SysListView32",null);
        if(desktop==IntPtr.Zero||list==IntPtr.Zero)return 0;
        var zones=reserved.Select(r=>ClientArea(list,Rectangle.Inflate(r,4,4))).Where(r=>!r.IsEmpty).ToArray();
        if(zones.Length==0)return 0;
        var work=Screen.AllScreens.Select(s=>ClientArea(list,s.WorkingArea)).Where(r=>!r.IsEmpty).ToArray();
        var shellView=FindView();
        if(shellView==null)return 0;
        var locations=new List<IconLocation>();
        try{
            var view=(FolderView)shellView;
            int count;Point spacing;
            if(view.ItemCount(ViewItems,out count)!=0||count==0||view.GetSpacing(out spacing)!=0||spacing.X<12||spacing.Y<12)return 0;
            for(int i=0;i<count;i++){
                IntPtr id;Point position;
                if(view.Item(i,out id)!=0||id==IntPtr.Zero)continue;
                if(view.GetItemPosition(id,out position)==0)locations.Add(new IconLocation{Id=id,Position=position});
                else Marshal.FreeCoTaskMem(id);
            }
            var blocked=locations.Where(icon=>zones.Any(zone=>zone.IntersectsWith(Footprint(icon.Position,spacing)))).ToArray();
            if(blocked.Length==0)return 0;
            var free=new List<Point>();
            var occupied=locations.Select(icon=>Footprint(icon.Position,spacing)).ToList();
            int phaseX=Phase(locations[0].Position.X,spacing.X),phaseY=Phase(locations[0].Position.Y,spacing.Y);
            foreach(var area in work){
                for(int x=Align(area.Left+spacing.X/5,phaseX,spacing.X);x<=area.Right-spacing.X*4/5;x+=spacing.X)
                    for(int y=Align(area.Top,phaseY,spacing.Y);y<=area.Bottom-spacing.Y+3;y+=spacing.Y){
                        var point=new Point(x,y);var bounds=Footprint(point,spacing);
                        if(!zones.Any(zone=>zone.IntersectsWith(bounds))&&!occupied.Any(item=>item.IntersectsWith(bounds)))free.Add(point);
                    }
            }
            if(free.Count==0||view.GetAutoArrange()==0&&!AllowFreePositions(shellView))return 0;
            var ids=new List<IntPtr>();var targets=new List<Point>();
            foreach(var icon in blocked){
                if(free.Count==0)break;
                var point=free.OrderBy(p=>Math.Abs((long)p.X-icon.Position.X)+Math.Abs((long)p.Y-icon.Position.Y)).First();
                free.Remove(point);ids.Add(icon.Id);targets.Add(point);
            }
            return view.SelectAndPositionItems((uint)ids.Count,ids.ToArray(),targets.ToArray(),PositionItem)==0?ids.Count:0;
        }finally{
            foreach(var icon in locations)Marshal.FreeCoTaskMem(icon.Id);
            Release(shellView);
        }
    }
}
