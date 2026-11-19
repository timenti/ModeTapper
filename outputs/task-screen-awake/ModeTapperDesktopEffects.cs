using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed class DesktopEffects : IDisposable {
    enum ActionKind { Taskbar, Minimize, Close, ShellLaunch, CaptionProbe }
    sealed class Pending {
        internal ActionKind Kind;
        internal IntPtr Window, Foreground, ExpectedRestore, EarlyShield;
        internal DesktopTaskbarButtons.Entry Button;
        internal Point Press, Anchor;
        internal Rectangle Area, BeforeBounds;
        internal Rectangle ShieldBounds;
        internal bool HoldingLaunch;
        internal bool PreparedLaunch;
        internal Rectangle SettlingBounds;
        internal IntPtr SettlingWindow;
        internal HashSet<IntPtr> VisibleBefore;
        internal long Pressed, Released, Changed, Sampled, StableSince;
        internal int? Signature;
        internal string SourceTitle;
        internal string ForegroundTitle;
        internal bool CleanBackdrop, AnimateContraction, PreviewStarted;
        internal bool DesktopLaunch, WaitingForSecondClick;
        internal bool ForegroundWasShown;
        internal long ContractionObserved;
    }
    sealed class LaunchLocation {
        internal Point Icon;
        internal Rectangle Monitor,Window;
        internal long Observed;
    }

    readonly Control dispatcher;
    readonly Func<bool> blocked;
    readonly Action<Point,bool> touchCaption;
    readonly DesktopTaskbarButtons taskbar=new DesktopTaskbarButtons();
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly List<LaunchLocation> launchLocations=new List<LaunchLocation>();
    delegate void ShellEvent(IntPtr hook,uint kind,IntPtr window,int objectId,int child,uint thread,uint time);
    [DllImport("user32.dll")]static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,ShellEvent callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")]static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("oleacc.dll")]static extern int AccessibleObjectFromEvent(IntPtr window,int objectId,int childId,
        [MarshalAs(UnmanagedType.IDispatch)]out object accessible,[MarshalAs(UnmanagedType.Struct)]out object child);
    readonly ShellEvent shellEvent;
    IntPtr selectionHook,invokeHook,foregroundHook,showHook;
    GestureMouseGate observer;
    DesktopEffectSurface surface;
    Pending pending;
    IntPtr lastShellWindow;
    long lastShellPress;
    Point lastShellPoint;
    string lastShellTitle;
    bool disposed;
    internal bool Ready { get{return observer!=null;} }
    internal string LastEffect { get;private set; }
    internal int DesktopLaunches {get;private set;}
    internal int LastDesktopFrames {get;private set;}

    internal DesktopEffects(Control dispatcher,Func<bool> blocked,Action<Point,bool> touchCaption){
        this.dispatcher=dispatcher;this.blocked=blocked;this.touchCaption=touchCaption;
        shellEvent=OnShellEvent;
    }

    internal void SetEnabled(bool enabled){
        if(disposed||enabled==Ready)return;
        if(!enabled){
            observer.Dispose();observer=null;
            if(selectionHook!=IntPtr.Zero){UnhookWinEvent(selectionHook);selectionHook=IntPtr.Zero;}
            if(invokeHook!=IntPtr.Zero){UnhookWinEvent(invokeHook);invokeHook=IntPtr.Zero;}
            if(foregroundHook!=IntPtr.Zero){UnhookWinEvent(foregroundHook);foregroundHook=IntPtr.Zero;}
            if(showHook!=IntPtr.Zero){UnhookWinEvent(showHook);showHook=IntPtr.Zero;}
            End();
            if(surface!=null)surface.DropCachedPicture();
            return;
        }
        observer=new GestureMouseGate(null,null,ObserveButton);
        selectionHook=SetWinEventHook(0x8005,0x8007,IntPtr.Zero,shellEvent,0,0,2);
        invokeHook=SetWinEventHook(0x8013,0x8013,IntPtr.Zero,shellEvent,0,0,2);
        foregroundHook=SetWinEventHook(3,3,IntPtr.Zero,shellEvent,0,0,2);
        showHook=SetWinEventHook(0x8000,0x8000,IntPtr.Zero,shellEvent,0,0,2);
        taskbar.Refresh();
    }
    void OnShellEvent(IntPtr hook,uint kind,IntPtr window,int objectId,int childId,uint thread,uint time){
        if(kind==3){
            if(pending!=null&&pending.Kind==ActionKind.ShellLaunch&&pending.Released!=0)Advance();
            return;
        }
        if(kind==0x8000){
            if(objectId==0&&childId==0&&pending!=null&&pending.Kind==ActionKind.ShellLaunch&&
               !pending.WaitingForSecondClick&&!pending.HoldingLaunch&&pending.EarlyShield==IntPtr.Zero&&window!=pending.Window&&
               window!=pending.Foreground&&!pending.VisibleBefore.Contains(window)&&
               !DesktopMotionNative.IsShown(window)&&DesktopMotionNative.IsOpeningWindow(window,false)){
                try{ShieldOpeningWindow(window);}
                catch(ExternalException){End();}
                catch(ArgumentException){End();}
            }
            return;
        }
        if(disposed||!Ready||Safety.Safe||blocked()||objectId!=-4||childId<=0||
           DesktopMotionNative.ClassOf(window)!="SysListView32"||!DesktopMotionNative.IsDesktopView(DesktopMotionNative.RootOf(window)))return;
        if(pending!=null&&pending.DesktopLaunch){
            if(kind==0x8013&&pending.WaitingForSecondClick)ConfirmDesktopLaunch(pending.Anchor);
            return;
        }
        if(pending!=null||surface!=null&&surface.Active||!LumaFlyout.AnimationsEnabled())return;
        object accessible=null,child;
        try{
            if(AccessibleObjectFromEvent(window,objectId,childId,out accessible,out child)!=0||accessible==null)return;
            int x,y,width,height;
            ((dynamic)accessible).accLocation(out x,out y,out width,out height,child);
            if(width<=0||height<=0)return;
            var point=new Point(x+width/2,y+Math.Min(height/2,20));
            var root=DesktopMotionNative.RootOf(window);
            if(DesktopMotionNative.At(point)!=root)return;
            long now=clock.ElapsedMilliseconds;
            IntPtr foreground=DesktopMotionNative.Foreground();
            Arm(point,now,foreground,root,false,null);
            if(kind==0x8013&&pending!=null&&pending.DesktopLaunch)ConfirmDesktopLaunch(point);
        }catch(ExternalException){End();}catch(ArgumentException){End();}
        catch(Microsoft.CSharp.RuntimeBinder.RuntimeBinderException){}
        finally{if(accessible!=null&&Marshal.IsComObject(accessible))Marshal.ReleaseComObject(accessible);}
    }
    void ConfirmDesktopLaunch(Point point,bool pointerStillDown=false){
        pending.Press=point;pending.Anchor=point;
        pending.Pressed=clock.ElapsedMilliseconds;
        pending.Released=pointerStillDown?0:Math.Max(1,pending.Pressed);
        pending.WaitingForSecondClick=false;
        if(pointerStillDown){
            try{HoldKnownLaunch();}
            catch(ExternalException){surface.ReleaseLaunchCover();}
            catch(ArgumentException){surface.ReleaseLaunchCover();}
            return;
        }
        Advance();
    }
    LaunchLocation PreviousLaunch(){
        return launchLocations.Find(item=>item.Monitor==pending.Area&&
            Math.Abs(item.Icon.X-pending.Anchor.X)<=22&&Math.Abs(item.Icon.Y-pending.Anchor.Y)<=22&&
            clock.ElapsedMilliseconds-item.Observed<=5*60*1000);
    }
    void HoldKnownLaunch(){
        if(!pending.PreparedLaunch||DesktopTarget(pending,DesktopMotionNative.Foreground()))return;
        if(!surface.HoldLaunchCover())return;
        if(DesktopTarget(pending,DesktopMotionNative.Foreground())){surface.ReleaseLaunchCover();return;}
        pending.HoldingLaunch=true;
    }
    void RememberLaunch(Rectangle bounds){
        if(!pending.DesktopLaunch||bounds.Width<120||bounds.Height<90)return;
        var previous=PreviousLaunch();
        if(previous!=null)launchLocations.Remove(previous);
        if(launchLocations.Count>=24)launchLocations.RemoveAt(0);
        launchLocations.Add(new LaunchLocation{Icon=pending.Anchor,Window=bounds,Monitor=pending.Area,Observed=clock.ElapsedMilliseconds});
    }
    void ObserveButton(Point point,bool down,bool touch){
        if(disposed||dispatcher.IsDisposed)return;
        long now=clock.ElapsedMilliseconds;
        IntPtr foreground=down?DesktopMotionNative.Foreground():IntPtr.Zero;
        IntPtr hit=down?DesktopMotionNative.InputRoot(point):IntPtr.Zero;
        try{dispatcher.BeginInvoke(new Action(()=>OnPointer(point,down,touch,now,foreground,hit)));}
        catch(InvalidOperationException){}
    }
    void OnPointer(Point point,bool down,bool touch,long time,IntPtr foregroundAtPress,IntPtr hit){
        if(disposed||!Ready)return;
        bool shellView=down&&DesktopMotionNative.IsShellView(hit);
        if(touch&&!shellView&&!blocked()&&touchCaption!=null)touchCaption(point,down);
        if(!down){
            if(pending==null)return;
            if(pending.WaitingForSecondClick)return;
            if(pending.Kind!=ActionKind.ShellLaunch&&
               (Math.Abs(point.X-pending.Press.X)>24||Math.Abs(point.Y-pending.Press.Y)>24)){End();return;}
            pending.Released=Math.Max(1,time);
            bool steady=Math.Abs(point.X-pending.Press.X)<=6&&Math.Abs(point.Y-pending.Press.Y)<=6;
            try{
                if(pending.Kind==ActionKind.Close&&steady){
                    if(!surface.PrepareDismissal(pending.BeforeBounds)){End();return;}
                }else if(steady&&pending.Kind==ActionKind.Minimize&&pending.AnimateContraction&&surface.HasWarmBackdrop&&
                   !DesktopMotionNative.IsMinimized(pending.Window)){
                    surface.StartContraction(pending.BeforeBounds,pending.Anchor,IntPtr.Zero);
                    pending.PreviewStarted=true;
                }else if(steady&&pending.Kind==ActionKind.Taskbar&&DesktopMotionNative.IsMinimized(pending.ExpectedRestore))
                    pending.PreviewStarted=surface.PreviewRestore(pending.ExpectedRestore,pending.Anchor,pending.CleanBackdrop);
            }catch(ExternalException){End();return;}
            catch(ArgumentException){End();return;}
            Advance();
            return;
        }
        bool shell=false;
        string sourceTitle=null;
        if(shellView){
            var size=SystemInformation.DoubleClickSize;
            bool desktop=DesktopMotionNative.IsDesktopView(hit);
            bool same=hit==lastShellWindow||desktop&&lastShellWindow!=IntPtr.Zero&&DesktopMotionNative.IsDesktopView(lastShellWindow);
            int tolerance=desktop?(int)Math.Round(7*DesktopMotionNative.WindowScale(hit)):2;
            shell=same&&time-lastShellPress<=SystemInformation.DoubleClickTime&&
                Math.Abs(point.X-lastShellPoint.X)<=Math.Max(tolerance,size.Width/2)&&
                Math.Abs(point.Y-lastShellPoint.Y)<=Math.Max(tolerance,size.Height/2);
            sourceTitle=lastShellTitle;
            lastShellWindow=shell?IntPtr.Zero:hit;lastShellPress=time;lastShellPoint=point;
            if(!shell)lastShellTitle=DesktopMotionNative.TitleOf(hit);
        }else lastShellWindow=IntPtr.Zero;
        bool blockedNow=Safety.Safe||blocked()||!LumaFlyout.AnimationsEnabled();
        if(shell&&!blockedNow&&pending!=null&&pending.WaitingForSecondClick&&DesktopMotionNative.IsDesktopView(hit)){
            ConfirmDesktopLaunch(point,true);
            return;
        }
        if(pending!=null||surface!=null&&surface.Active)End();
        if(blockedNow||clock.ElapsedMilliseconds-time>(shellView?400:180))return;
        try{Arm(point,time,foregroundAtPress,hit,shell,sourceTitle);}
        catch(ExternalException){End();}
        catch(ArgumentException){End();}
    }
    void Arm(Point point,long time,IntPtr foregroundAtPress,IntPtr hit,bool shell,string sourceTitle){
        if(hit==IntPtr.Zero)return;
        IntPtr foreground=DesktopMotionNative.Foreground();
        var screen=Screen.FromPoint(point);
        var candidate=new Pending{Press=point,Foreground=foreground,Pressed=time,Area=screen.Bounds};
        string windowClass=DesktopMotionNative.ClassOf(hit);
        bool desktop=DesktopMotionNative.IsDesktopView(hit);
        if(shell||desktop){
            candidate.Kind=ActionKind.ShellLaunch;candidate.Window=hit;candidate.Anchor=point;
            candidate.Foreground=foregroundAtPress;
            candidate.ForegroundTitle=DesktopMotionNative.TitleOf(foregroundAtPress);
            candidate.ForegroundWasShown=DesktopMotionNative.IsExposed(foregroundAtPress);
            candidate.SourceTitle=sourceTitle;candidate.DesktopLaunch=desktop;
            candidate.WaitingForSecondClick=desktop&&!shell;
            candidate.VisibleBefore=DesktopMotionNative.VisibleApps();
        }else if(windowClass=="Shell_TrayWnd"||windowClass=="Shell_SecondaryTrayWnd"){
            DesktopTaskbarButtons.Entry button=taskbar.At(point);
            taskbar.Refresh();
            candidate.Kind=ActionKind.Taskbar;
            candidate.Button=button;
            if(DesktopMotionNative.IsApp(foregroundAtPress))candidate.Foreground=foregroundAtPress;
            candidate.Window=candidate.Foreground;
            candidate.Anchor=button==null?point:button.Center;
            if(surface!=null)candidate.ExpectedRestore=surface.ExpectedRestore(point,button,foregroundAtPress);
            candidate.VisibleBefore=DesktopMotionNative.VisibleApps();
        }else{
            Rectangle frame;
            float scale=DesktopMotionNative.WindowScale(hit);
            if(!DesktopMotionNative.IsApp(hit)||!DesktopMotionNative.BoundsOf(hit,out frame)||
               point.Y<frame.Top-5*scale||point.Y>frame.Top+64*scale||point.X<frame.Right-165*scale)return;
            int caption=DesktopMotionNative.CaptionButton(hit,point);
            if(caption!=8&&caption!=20){
                candidate.Kind=ActionKind.CaptionProbe;
                candidate.Window=hit;
                TryAutomationCaption(candidate,hit,frame);
                CoverAction(candidate,foreground);
                return;
            }
            SetCaptionAction(candidate,hit,frame,caption);
        }
        CoverAction(candidate,foreground);
    }
    void SetCaptionAction(Pending candidate,IntPtr window,Rectangle frame,int caption){
        candidate.Kind=caption==8?ActionKind.Minimize:ActionKind.Close;
        candidate.Window=window;
        candidate.Anchor=taskbar.AnchorFor(window,frame,candidate.Area);
    }
    void TryAutomationCaption(Pending candidate,IntPtr window,Rectangle frame){
        ThreadPool.QueueUserWorkItem(_=>{
            int caption=DesktopMotionNative.AutomationCaption(candidate.Press);
            if(caption==0||disposed||dispatcher.IsDisposed)return;
            try{dispatcher.BeginInvoke(new Action(()=>{
                if(disposed||!Ready||blocked()||Safety.Safe||pending!=candidate||candidate.Kind!=ActionKind.CaptionProbe)return;
                try{
                    SetCaptionAction(candidate,window,frame,caption);
                    candidate.AnimateContraction=surface.CanContract(window,candidate.Area);
                    if(candidate.Kind!=ActionKind.Close&&!surface.ShowPrepared()){End();return;}
                    Advance();
                }catch(ExternalException){End();}
                catch(ArgumentException){End();}
            }));}catch(InvalidOperationException){}
        });
    }
    void CoverAction(Pending candidate,IntPtr foreground){
        Rectangle oldBounds;
        if(DesktopMotionNative.IsApp(candidate.Window)&&DesktopMotionNative.BoundsOf(candidate.Window,out oldBounds))
            candidate.BeforeBounds=Rectangle.Intersect(candidate.Area,oldBounds);
        bool confirmedCaption=candidate.Kind==ActionKind.Minimize||candidate.Kind==ActionKind.Close;
        bool caption=confirmedCaption||candidate.Kind==ActionKind.CaptionProbe;
        if(caption&&candidate.BeforeBounds.IsEmpty)return;
        Bitmap snapshot=DesktopMotionNative.Capture(candidate.Area);
        if(snapshot==null)return;
        IntPtr afterCapture=DesktopMotionNative.Foreground();
        bool appChanged=afterCapture!=foreground&&
            !(candidate.Kind==ActionKind.Taskbar&&DesktopMotionNative.IsTaskbar(afterCapture));
        if(caption&&appChanged&&DesktopMotionNative.IsApp(candidate.Window)){
            snapshot.Dispose();return;
        }
        candidate.CleanBackdrop=candidate.Kind==ActionKind.Taskbar&&!appChanged&&
            (candidate.ExpectedRestore==IntPtr.Zero||candidate.ExpectedRestore!=afterCapture||DesktopMotionNative.IsMinimized(afterCapture));
        if(surface==null){
            surface=new DesktopEffectSurface(()=>Safety.Safe||blocked()||(GestureMouseGate.GetAsyncKeyState(27)&0x8000)!=0);
            surface.Waiting+=Advance;
            surface.Finished+=End;
        }
        candidate.AnimateContraction=candidate.Kind!=ActionKind.ShellLaunch&&!candidate.BeforeBounds.IsEmpty&&surface.CanContract(candidate.Window,candidate.Area);
        bool knownRestore=candidate.Kind==ActionKind.Taskbar&&candidate.Button!=null&&
            !DesktopTaskbarButtons.Matches(candidate.Button,candidate.Window)&&candidate.CleanBackdrop;
        bool taskbarMinimize=candidate.Kind==ActionKind.Taskbar&&DesktopTaskbarButtons.Matches(candidate.Button,candidate.Window);
        bool close=candidate.Kind==ActionKind.Close;
        bool shell=candidate.Kind==ActionKind.ShellLaunch;
        bool show=confirmedCaption&&!close||taskbarMinimize||knownRestore;
        bool prepaint=candidate.Kind==ActionKind.CaptionProbe;
        if(!surface.Prepare(candidate.Area,snapshot,show,shell)||
           prepaint&&!surface.PrepaintCover()){End();return;}
        pending=candidate;
        if(close&&!surface.PrepareDismissal(candidate.BeforeBounds,false)){End();return;}
        if(candidate.WaitingForSecondClick){
            var previous=PreviousLaunch();
            if(previous==null)return;
            var bounds=Rectangle.Intersect(candidate.Area,Rectangle.Inflate(previous.Window,64,64));
            if((long)bounds.Width*bounds.Height*5>(long)candidate.Area.Width*candidate.Area.Height*4)return;
            candidate.PreparedLaunch=surface.PrepareLaunchCover(bounds);
            if(candidate.PreparedLaunch)candidate.ShieldBounds=bounds;
        }
    }
    void Advance(){
        if(pending==null)return;
        long now=clock.ElapsedMilliseconds;
        int timeout=pending.WaitingForSecondClick?Math.Max(950,SystemInformation.DoubleClickTime+25):
            pending.Kind==ActionKind.ShellLaunch?2400:720;
        if(Safety.Safe||blocked()||(GestureMouseGate.GetAsyncKeyState(27)&0x8000)!=0||
           now-pending.Pressed>timeout){End();return;}
        if(pending.WaitingForSecondClick){
            if(DesktopTarget(pending,DesktopMotionNative.Foreground()))ConfirmDesktopLaunch(pending.Anchor);
            else return;
        }
        if(pending!=null&&pending.HoldingLaunch&&now-pending.Pressed>150){
            surface.ReleaseLaunchCover();pending.HoldingLaunch=false;pending.ShieldBounds=Rectangle.Empty;
        }
        if(pending==null||pending.Released==0)return;
        try{
            if(pending.Kind==ActionKind.ShellLaunch){AdvanceShellLaunch(now);return;}
            if(pending.Kind==ActionKind.CaptionProbe){
                var window=pending.Window;
                int caption=DesktopMotionNative.IsMinimized(window)?8:!DesktopMotionNative.IsShown(window)?20:0;
                if(caption==0){if(now-pending.Released>=205)End();return;}
                SetCaptionAction(pending,window,pending.BeforeBounds,caption);
            }
            if(pending.PreviewStarted&&pending.Kind==ActionKind.Taskbar){
                IntPtr target=pending.ExpectedRestore;
                if(DesktopMotionNative.Foreground()==target&&DesktopMotionNative.IsApp(target)){
                    LastEffect="open";pending=null;
                }else if(now-pending.Released>=90)RevealDesktop();
                return;
            }
            bool minimizing=pending.Kind!=ActionKind.Close&&!pending.BeforeBounds.IsEmpty&&DesktopMotionNative.IsMinimized(pending.Window);
            bool closing=pending.Kind==ActionKind.Close&&!DesktopMotionNative.IsShown(pending.Window);
            if(closing){
                if(!surface.Dismiss(pending.BeforeBounds)){End();return;}
                LastEffect="close";
                pending=null;
                return;
            }
            if(minimizing){
                if(!pending.AnimateContraction||!surface.HasWarmBackdrop){
                    if(pending.ContractionObserved==0)pending.ContractionObserved=now;
                    if(!ReadyToCapture(pending,pending.BeforeBounds,now)&&now-pending.ContractionObserved<65)return;
                    if(!surface.StartColdContraction(pending.BeforeBounds,pending.Anchor,pending.Window)){
                        End();return;
                    }
                    LastEffect="minimize";
                    pending=null;
                    return;
                }
                if(!surface.ShowPrepared()){End();return;}
                if(pending.PreviewStarted)surface.ConfirmMinimized(pending.Window);
                else surface.StartContraction(pending.BeforeBounds,pending.Anchor,pending.Window);
                LastEffect="minimize";
                pending=null;
                return;
            }
            if(pending.PreviewStarted){
                if(now-pending.Released>=80)RevealDesktop();
                return;
            }
            IntPtr active=DesktopMotionNative.Foreground();
            if(pending.Kind==ActionKind.Taskbar&&DesktopMotionNative.IsApp(active)&&
               (!pending.VisibleBefore.Contains(active)||active==pending.ExpectedRestore||
                active!=pending.Foreground&&DesktopTaskbarButtons.Matches(pending.Button,active))){
                Rectangle bounds;
                if(!DesktopMotionNative.BoundsOf(active,out bounds))return;
                bounds=Rectangle.Intersect(pending.Area,bounds);
                if(bounds.Width<120||bounds.Height<90){End();return;}
                if(surface.TryExpandCached(active,bounds,pending.Anchor,pending.CleanBackdrop)){
                    LastEffect="open";pending=null;return;
                }
                if(pending.SettlingWindow!=active||pending.SettlingBounds!=bounds){
                    pending.SettlingWindow=active;pending.SettlingBounds=bounds;
                    pending.Changed=now;pending.Signature=null;pending.Sampled=0;
                }
                if(!ReadyToCapture(pending,bounds,now)&&now-pending.Changed<70)return;
                if(surface.Expand(active,bounds,pending.Anchor,pending.CleanBackdrop)){
                    LastEffect="open";
                    pending=null;
                }else End();
                return;
            }
            if(pending.Kind==ActionKind.Taskbar&&active!=pending.Foreground&&pending.VisibleBefore.Contains(active)){End();return;}
            if(pending.Kind!=ActionKind.Taskbar&&active!=pending.Foreground&&active!=pending.Window&&
               now-pending.Released>=100){RevealDesktop();return;}
            if(now-pending.Released>(pending.Kind==ActionKind.Taskbar?440:205))End();
        }catch(ExternalException){End();}
        catch(ArgumentException){End();}
    }
    void AdvanceShellLaunch(long now){
        IntPtr active=DesktopMotionNative.Foreground();
        bool navigation=active==pending.Window&&DesktopMotionNative.TitleOf(active)!=pending.SourceTitle;
        if(DesktopMotionNative.IsApp(active)&&(navigation||DesktopTarget(pending,active))){
            Rectangle bounds;
            if(!DesktopMotionNative.BoundsOf(active,out bounds))return;
            bounds=Rectangle.Intersect(pending.Area,bounds);
            if(pending.SettlingBounds!=bounds)RememberLaunch(bounds);
            // A navigation has no new window to reveal. An unguarded launch has
            // already been handed to the compositor by the time it is active.
            if(navigation||(!pending.HoldingLaunch&&pending.EarlyShield!=active)||!pending.ShieldBounds.Contains(bounds)||
               bounds.Width<120||bounds.Height<90){
                LastEffect="shell-native";End();return;
            }
            if(pending.SettlingWindow!=active||pending.SettlingBounds!=bounds){
                pending.SettlingWindow=active;pending.SettlingBounds=bounds;pending.Changed=now;
            }
            if(now-pending.Changed<28)return;
            if(pending.HoldingLaunch&&!surface.SwitchLaunchCover(active,bounds)){
                LastEffect="shell-native";End();return;
            }
            if(!surface.ExpandFromIcon(active,bounds,pending.Anchor)){End();return;}
            LastEffect="shell-open";pending=null;return;
        }
        if(now-pending.Released>=780)End();
    }
    void ShieldOpeningWindow(IntPtr window){
        Rectangle geometry;
        if(!DesktopMotionNative.BoundsOf(window,out geometry))return;
        geometry=Rectangle.Intersect(pending.Area,geometry);
        if(geometry.Width<120||geometry.Height<90)return;
        if(pending.Released==0&&geometry.Contains(pending.Press))return;
        if(!surface.ShieldOpeningTarget(window,geometry)){
            LastEffect="shell-native";End();return;
        }
        pending.EarlyShield=window;
        pending.ShieldBounds=geometry;
    }
    static bool DesktopTarget(Pending action,IntPtr active){
        return active!=action.Window&&DesktopMotionNative.IsApp(active)&&
            (active!=action.Foreground||action.DesktopLaunch&&
             (DesktopMotionNative.TitleOf(active)!=action.ForegroundTitle||!action.ForegroundWasShown&&DesktopMotionNative.IsExposed(active)));
    }
    void RevealDesktop(){KeepUnconfirmedRestore();pending=null;surface.FadeToDesktop();}
    static bool ReadyToCapture(Pending action,Rectangle region,long now){
        if(now-action.Sampled<12||region.IsEmpty)return false;
        action.Sampled=now;
        var sample=DesktopMotionNative.ReadScene(region);
        if(!sample.HasValue){action.Signature=null;return false;}
        if(sample.Value.Signature!=action.Signature){
            action.Signature=sample.Value.Signature;
            action.StableSince=now;
            return false;
        }
        return now-action.StableSince>=(sample.Value.MostlyWhite?64:16);
    }
    void KeepUnconfirmedRestore(){
        if(pending!=null&&pending.PreviewStarted&&pending.Kind==ActionKind.Taskbar&&
           DesktopMotionNative.IsMinimized(pending.ExpectedRestore))surface.RetainUnconfirmedRestore(pending.ExpectedRestore);
    }
    void End(){
        KeepUnconfirmedRestore();pending=null;
        if(surface==null)return;
        if(surface.CompletedDesktopFrames>0){
            DesktopLaunches++;
            LastDesktopFrames=surface.CompletedDesktopFrames;
        }
        surface.Reset();
    }
    public void Dispose(){
        if(disposed)return;
        SetEnabled(false);disposed=true;
        if(surface!=null){surface.Dispose();surface=null;}
    }
}

internal sealed class DesktopTaskbarButtons {
    internal sealed class Entry {
        internal Rectangle Bounds;
        internal string Name,Id;
        internal Point Center {get{return new Point(Bounds.Left+Bounds.Width/2,Bounds.Top+Bounds.Height/2);}}
    }
    volatile Entry[] buttons=new Entry[0];
    long lastRefresh;
    int reading;

    internal void Refresh(){
        long now=Stopwatch.GetTimestamp();
        if(now-Interlocked.Read(ref lastRefresh)<Stopwatch.Frequency*3||
           Interlocked.CompareExchange(ref reading,1,0)!=0)return;
        Interlocked.Exchange(ref lastRefresh,now);
        ThreadPool.QueueUserWorkItem(_=>{
            try{
                var found=new List<Entry>();
                foreach(var bar in DesktopMotionNative.Taskbars()){
                    var root=AutomationElement.FromHandle(bar);
                    var condition=new PropertyCondition(AutomationElement.ClassNameProperty,"Taskbar.TaskListButtonAutomationPeer");
                    foreach(AutomationElement element in root.FindAll(TreeScope.Descendants,condition)){
                        var item=element.Current;
                        var region=item.BoundingRectangle;
                        if(item.IsOffscreen||region.IsEmpty||region.Width<8||region.Height<8)continue;
                        found.Add(new Entry{Name=item.Name??"",Id=item.AutomationId??"",Bounds=Rectangle.FromLTRB((int)region.Left,(int)region.Top,(int)region.Right,(int)region.Bottom)});
                    }
                }
                buttons=found.ToArray();
            }catch(InvalidOperationException){}
            catch(COMException){}
            catch(ArgumentException){}
            finally{Interlocked.Exchange(ref reading,0);}
        });
    }
    internal Entry At(Point point){return Array.Find(buttons,entry=>entry.Bounds.Contains(point));}
    internal static bool Matches(Entry button,IntPtr window){
        if(button==null||window==IntPtr.Zero)return false;
        string title=DesktopMotionNative.TitleOf(window);
        string name=button.Name.Split('—')[0].Trim();
        if(title.Length>0&&name.Length>0&&(button.Name.IndexOf(title,StringComparison.OrdinalIgnoreCase)>=0||
           title.StartsWith(name,StringComparison.OrdinalIgnoreCase)))return true;
        string id=CarouselAppImages.WindowId(window);
        return !String.IsNullOrEmpty(id)&&String.Equals(button.Id,"Appid: "+id,StringComparison.OrdinalIgnoreCase);
    }
    internal Point AnchorFor(IntPtr handle,Rectangle window,Rectangle area){
        string id=CarouselAppImages.WindowId(handle);
        if(!String.IsNullOrEmpty(id)){
            var exact=Array.Find(buttons,button=>String.Equals(button.Id,"Appid: "+id,StringComparison.OrdinalIgnoreCase));
            if(exact!=null)return exact.Center;
        }
        string title=DesktopMotionNative.TitleOf(handle);
        foreach(var button in buttons){
            string name=button.Name.Split('—')[0].Trim();
            if(name.Length>0&&title.Length>0&&(button.Name.IndexOf(title,StringComparison.OrdinalIgnoreCase)>=0||
               title.StartsWith(name,StringComparison.OrdinalIgnoreCase)))return button.Center;
        }
        return new Point(Math.Max(area.Left+12,Math.Min(area.Right-12,window.Left+window.Width/2)),area.Bottom);
    }
}

internal sealed class TouchCaptionMinimize : IDisposable {
    struct Contact {
        internal IntPtr Window;
        internal Point Point;
        internal long At;
    }
    readonly System.Windows.Forms.Timer fallback=new System.Windows.Forms.Timer{Interval=85};
    Contact raw, promoted;
    IntPtr pendingWindow;
    int finger;
    internal int Fallbacks {get;private set;}
    internal event Action Handled;

    internal TouchCaptionMinimize(){
        fallback.Tick+=(sender,args)=>{
            fallback.Stop();
            var window=pendingWindow;pendingWindow=IntPtr.Zero;
            if(!Safety.Safe&&DesktopMotionNative.Foreground()==window&&DesktopMotionNative.IsApp(window)&&
               DesktopMotionNative.Minimize(window)){
                Fallbacks++;
                if(Handled!=null)Handled();
            }
        };
    }

    internal void Update(TouchFrame frame,Point point,bool first,double scale){
        if(frame.Width<=0||Safety.Safe){Cancel();return;}
        if(first){
            raw=new Contact();
            if(frame.SampledContacts!=1||frame.Count>1||frame.ButtonDown)return;
            raw=Begin(point,scale);finger=frame.Id;
        }else if(raw.Window!=IntPtr.Zero){
            if(frame.Pressed){
                if(frame.SampledContacts!=1||frame.Count>1||frame.Id!=finger||frame.ButtonDown||
                   !Near(point,raw.Point,scale))raw=new Contact();
            }else{
                var contact=raw;raw=new Contact();Complete(contact);
            }
        }
    }

    internal void Promoted(Point point,bool down,double scale){
        if(Safety.Safe){Cancel();return;}
        if(down)promoted=Begin(point,scale);
        else{
            var contact=promoted;promoted=new Contact();
            if(Near(point,contact.Point,scale))Complete(contact);
        }
    }

    Contact Begin(Point point,double scale){
        fallback.Stop();pendingWindow=IntPtr.Zero;
        IntPtr window=DesktopMotionNative.Foreground();
        Rectangle bounds;
        if(!DesktopMotionNative.BoundsOf(window,out bounds)||!bounds.Contains(point)||
           point.Y>bounds.Top+96*scale||point.X<bounds.Right-270*scale||
           DesktopMotionNative.At(point)!=window||!DesktopMotionNative.IsApp(window)||
           ScreenLens.SystemMagnifierActive||DesktopMotionNative.CaptionButton(window,point)!=8)return new Contact();
        return new Contact{Window=window,Point=point,At=Stopwatch.GetTimestamp()};
    }

    void Complete(Contact contact){
        IntPtr window=contact.Window;
        if(window==IntPtr.Zero||pendingWindow==window||Stopwatch.GetTimestamp()-contact.At>Stopwatch.Frequency*.65||
           DesktopMotionNative.Foreground()!=window||!DesktopMotionNative.IsApp(window)||
           DesktopMotionNative.CaptionButton(window,contact.Point)!=8)return;
        // Native handling has priority and may already have minimized the same window.
        pendingWindow=window;fallback.Start();
    }

    static bool Near(Point first,Point second,double scale){
        return Math.Abs(first.X-second.X)<=12*scale&&Math.Abs(first.Y-second.Y)<=12*scale;
    }
    internal void Cancel(){fallback.Stop();raw=promoted=new Contact();pendingWindow=IntPtr.Zero;}
    public void Dispose(){fallback.Dispose();}
}

internal static class DesktopMotionNative {
    internal struct SceneSample {
        internal int Signature;
        int white;
        internal bool MostlyWhite {get{return white>=12;}}
        internal void Add(Color color){
            Signature=unchecked(31*Signature+color.ToArgb());
            if(color.R>=235&&color.G>=235&&color.B>=235)white++;
        }
    }
    [StructLayout(LayoutKind.Sequential)]struct Rect {public int Left,Top,Right,Bottom;}
    delegate bool Visit(IntPtr window,IntPtr state);
    [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]static extern bool ShowWindowAsync(IntPtr window,int mode);
    [DllImport("user32.dll")]static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out Rect value);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder name,int capacity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder name,int capacity);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern IntPtr GetWindowStyle(IntPtr window,int index);
    [DllImport("user32.dll")]static extern bool EnumWindows(Visit visitor,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wparam,IntPtr lparam,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr window,IntPtr device);
    [DllImport("gdi32.dll")]static extern bool BitBlt(IntPtr destination,int x,int y,int width,int height,IntPtr source,int sourceX,int sourceY,int operation);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out Rect value,int size);
    [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")]static extern int DwmValue(IntPtr window,int attribute,out int value,int size);
    static readonly uint OwnProcess=(uint)Process.GetCurrentProcess().Id;

    internal static IntPtr Foreground(){return GetForegroundWindow();}
    internal static float WindowScale(IntPtr window){return Math.Max(96,GetDpiForWindow(window))/96f;}
    internal static uint ProcessOf(IntPtr window){uint pid;GetWindowThreadProcessId(window,out pid);return pid;}
    internal static IntPtr At(Point point){return GetAncestor(WindowFromPoint(point),2);}
    internal static IntPtr RootOf(IntPtr window){return GetAncestor(window,2);}
    internal static IntPtr InputRoot(Point point){
        IntPtr target=WindowFromPoint(point);
        var root=RootOf(target);
        return ProcessOf(target)==OwnProcess&&IsDesktopView(root)?IntPtr.Zero:root;
    }
    internal static bool IsExposed(IntPtr window){
        Rectangle bounds;
        return IsApp(window)&&BoundsOf(window,out bounds)&&
            At(new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2))==window;
    }
    internal static bool IsTaskbar(IntPtr window){
        string kind=ClassOf(window);
        return kind=="Shell_TrayWnd"||kind=="Shell_SecondaryTrayWnd";
    }
    internal static bool IsShellView(IntPtr window){
        string kind=ClassOf(window);
        return kind=="CabinetWClass"||kind=="ExploreWClass"||IsDesktopClass(kind);
    }
    internal static bool IsDesktopView(IntPtr window){return IsDesktopClass(ClassOf(window));}
    static bool IsDesktopClass(string kind){return kind=="Progman"||kind=="WorkerW";}
    internal static bool IsMinimized(IntPtr window){return window!=IntPtr.Zero&&IsIconic(window);}
    internal static bool Minimize(IntPtr window){return ShowWindowAsync(window,6);}
    internal static bool IsShown(IntPtr window){return IsWindow(window)&&IsWindowVisible(window);}
    internal static bool ActivateInPlace(IntPtr window){
        const int showInCurrentState=5,restoreMinimized=9;
        if(!IsShown(window))return false;
        if(IsMinimized(window))ShowWindowAsync(window,restoreMinimized);
        else if(Foreground()!=window&&!SetForegroundWindow(window))ShowWindowAsync(window,showInCurrentState);
        return true;
    }
    internal static string ClassOf(IntPtr window){var text=new StringBuilder(150);GetClassName(window,text,text.Capacity);return text.ToString();}
    internal static string TitleOf(IntPtr window){var text=new StringBuilder(280);GetWindowText(window,text,text.Capacity);return text.ToString();}
    internal static bool BoundsOf(IntPtr window,out Rectangle bounds){
        Rect value;
        if((DwmGetWindowAttribute(window,9,out value,16)!=0||value.Right<=value.Left)&&!GetWindowRect(window,out value)){
            bounds=Rectangle.Empty;return false;
        }
        bounds=Rectangle.FromLTRB(value.Left,value.Top,value.Right,value.Bottom);
        return bounds.Width>0&&bounds.Height>0;
    }
    internal static Rectangle OuterBounds(IntPtr window){
        Rect value;
        return GetWindowRect(window,out value)?Rectangle.FromLTRB(value.Left,value.Top,value.Right,value.Bottom):Rectangle.Empty;
    }
    internal static bool IsApp(IntPtr window){
        return IsShown(window)&&!IsMinimized(window)&&IsOpeningWindow(window);
    }
    internal static bool IsOpeningWindow(IntPtr window,bool requireTitle=true){
        if(window==IntPtr.Zero||RootOf(window)!=window||GetWindow(window,4)!=IntPtr.Zero||
           (GetWindowStyle(window,-20).ToInt64()&0x08000080)!=0)return false;
        uint pid;GetWindowThreadProcessId(window,out pid);
        if(pid==0||pid==OwnProcess)return false;
        int cloaked;if(DwmValue(window,14,out cloaked,4)==0&&cloaked!=0)return false;
        string klass=ClassOf(window);
        if(klass=="#32770"||klass=="Progman"||klass=="WorkerW"||klass=="Shell_TrayWnd"||klass=="Shell_SecondaryTrayWnd"||
           klass=="Windows.UI.Core.CoreWindow"||klass=="XamlExplorerHostIslandWindow")return false;
        return requireTitle?TitleOf(window).Length>0:(GetWindowStyle(window,-16).ToInt64()&0x00c40000)!=0;
    }
    internal static int CaptionButton(IntPtr window,Point point){
        long position=(ushort)point.X|((long)(ushort)point.Y<<16);
        IntPtr hit;
        return SendMessageTimeout(window,0x84,IntPtr.Zero,new IntPtr(position),0x2,12,out hit)!=IntPtr.Zero?hit.ToInt32():0;
    }
    internal static int AutomationCaption(Point point){
        try{
            AutomationElement item=AutomationElement.FromPoint(new System.Windows.Point(point.X,point.Y));
            for(int depth=0;item!=null&&depth<3;depth++,item=TreeWalker.RawViewWalker.GetParent(item)){
                var info=item.Current;
                if(info.ControlType!=ControlType.Button||!info.IsEnabled)continue;
                string name=info.Name??"",id=info.AutomationId??"";
                if(name=="Закрыть"||name=="Закрыть окно"||String.Equals(name,"Close",StringComparison.OrdinalIgnoreCase)||
                   String.Equals(id,"Close",StringComparison.OrdinalIgnoreCase)||String.Equals(id,"CloseButton",StringComparison.OrdinalIgnoreCase))return 20;
                if(name=="Свернуть"||name=="Свернуть окно"||String.Equals(name,"Minimize",StringComparison.OrdinalIgnoreCase)||
                   String.Equals(id,"Minimize",StringComparison.OrdinalIgnoreCase)||String.Equals(id,"MinimizeButton",StringComparison.OrdinalIgnoreCase))return 8;
            }
        }catch(InvalidOperationException){}
        catch(COMException){}
        catch(ArgumentException){}
        return 0;
    }
    internal static HashSet<IntPtr> VisibleApps(){
        var set=new HashSet<IntPtr>();
        EnumWindows((window,state)=>{if(IsApp(window))set.Add(window);return true;},IntPtr.Zero);
        return set;
    }
    internal static int WorkspaceKey(Rectangle area,IntPtr excluded){
        int key=0;
        EnumWindows((window,state)=>{
            Rectangle bounds;
            if(window!=excluded&&IsApp(window)&&BoundsOf(window,out bounds)&&bounds.IntersectsWith(area))
                key^=unchecked(window.GetHashCode()*397+ProcessOf(window).GetHashCode()*17+bounds.GetHashCode());
            return true;
        },IntPtr.Zero);
        return key;
    }
    internal static List<IntPtr> Taskbars(){
        var list=new List<IntPtr>();
        EnumWindows((window,state)=>{string type=ClassOf(window);if(type=="Shell_TrayWnd"||type=="Shell_SecondaryTrayWnd")list.Add(window);return true;},IntPtr.Zero);
        return list;
    }
    internal static Bitmap Capture(Rectangle bounds){
        var screen=GetDC(IntPtr.Zero);
        if(screen==IntPtr.Zero)return null;
        Bitmap bitmap=null;
        try{
            bitmap=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppRgb);
            using(var graphics=Graphics.FromImage(bitmap)){
                IntPtr target=graphics.GetHdc();
                try{if(!BitBlt(target,0,0,bounds.Width,bounds.Height,screen,bounds.Left,bounds.Top,0x40cc0020))return null;}
                finally{graphics.ReleaseHdc(target);}
            }
            var result=bitmap;bitmap=null;return result;
        }finally{
            ReleaseDC(IntPtr.Zero,screen);
            if(bitmap!=null)bitmap.Dispose();
        }
    }
    internal static SceneSample? ReadScene(Rectangle bounds){
        var sample=new SceneSample{Signature=17};
        for(int column=1;column<=2;column++){
            int x=bounds.Left+bounds.Width*column/3;
            using(var strip=Capture(new Rectangle(x,bounds.Top,1,bounds.Height))){
                if(strip==null)return null;
                for(int row=1;row<=7;row++)sample.Add(strip.GetPixel(0,bounds.Height*row/8));
            }
        }
        return sample;
    }
}

internal sealed class DesktopEffectSurface : Form {
    const double OpeningSeconds=.31, ClosingSeconds=.27, DismissSeconds=.255, HandoffSeconds=.065;
    const double InitialMotionSeconds=.008;
    [DllImport("user32.dll")]static extern bool SetWindowDisplayAffinity(IntPtr window,uint affinity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool SetProp(IntPtr window,string property,IntPtr value);
    [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("winmm.dll")]static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]static extern uint timeEndPeriod(uint period);
    readonly CarouselFrameClock frames;
    readonly System.Windows.Forms.Timer cacheExpiry=new System.Windows.Forms.Timer{Interval=60000};
    readonly Func<bool> blocked;
    readonly Stopwatch clock=new Stopwatch();
    DesktopFrameBitmap backdrop, picture, cachedPicture, warmBackdrop, contractionBackdrop, canvas;
    DesktopOpeningWindow desktopPlayback;
    DesktopClosingWindow closePlayback;
    DesktopClosingWindow launchCover;
    Rectangle appBounds, frame, pictureSource, cachedBounds, cachedSource, warmArea;
    IntPtr cacheWindow, minimizingWindow, warmWindow, restoringWindow;
    uint cacheProcess,warmProcess;
    int warmWorkspace;
    long cachedAt,cachedMinimizedAt,minimizedAt;
    Point anchor,cachedAnchor,warmAnchor;
    double bend;
    byte alpha;
    bool opening,highResolution,ending,towardBottom,coverReady,desktopOpening,dismissing;
    int desktopFrames;
    internal int CompletedDesktopFrames {get;private set;}
    internal event Action Waiting;
    internal event Action Finished;
    internal bool HasWarmBackdrop {get{return warmBackdrop!=null;}}
    internal bool Active {get{return Visible||desktopPlayback!=null&&desktopPlayback.Visible||
        closePlayback!=null&&closePlayback.Visible||launchCover!=null&&launchCover.Visible;}}

    internal DesktopEffectSurface(Func<bool> blocked){
        this.blocked=blocked;
        FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;
        ShowInTaskbar=false;TopMost=true;
        frames=new CarouselFrameClock(this);
        frames.Tick+=(s,e)=>TickFrame();
        cacheExpiry.Tick+=(s,e)=>DropCachedPicture();
    }
    protected override bool ShowWithoutActivation{get{return true;}}
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x080800a0;return p;}}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);SetProp(Handle,"NonRudeHWND",new IntPtr(1));}
    internal bool Prepare(Rectangle area,Bitmap screenshot,bool show,bool nativeCover=false){
        using(screenshot){Reset();backdrop=new DesktopFrameBitmap(screenshot);}
        Bounds=area;
        if(nativeCover){
            if(!IsHandleCreated)CreateHandle();
            desktopPlayback=new DesktopOpeningWindow();
            if(!desktopPlayback.PrepareBackdrop())return false;
        }else if(!SetWindowDisplayAffinity(Handle,0x11))return false;
        if(show&&!ShowPrepared())return false;
        highResolution=timeBeginPeriod(1)==0;
        frames.Start();
        return true;
    }
    internal bool ShowPrepared(){
        if(Active)return true;
        if(!PrepaintCover())return false;
        coverReady=false;
        Show();
        SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x13);
        return true;
    }
    internal bool PrepaintCover(){
        if(coverReady)return true;
        if(canvas==null)canvas=new DesktopFrameBitmap(ClientSize);
        coverReady=PaintScene(ClientRectangle);
        return coverReady;
    }
    internal bool Expand(IntPtr window,Rectangle bounds,Point origin,bool rememberBackdrop){
        if(!UseOpeningBackdrop(window,rememberBackdrop))return false;
        using(var snapshot=DesktopMotionNative.Capture(bounds)){
            if(snapshot==null||!DesktopMotionNative.IsShown(window)||DesktopMotionNative.IsMinimized(window))return false;
            picture=new DesktopFrameBitmap(snapshot);
        }
        pictureSource=new Rectangle(Point.Empty,bounds.Size);
        opening=true;
        if(!ShowPrepared())return false;
        BeginMotion(bounds,origin,true);
        return true;
    }
    internal bool ExpandFromIcon(IntPtr window,Rectangle bounds,Point origin){
        if(!DesktopMotionNative.IsShown(window)||DesktopMotionNative.IsMinimized(window))return false;
        if(desktopPlayback==null||!desktopPlayback.Attach(window,bounds,origin))return false;
        restoringWindow=window;
        desktopOpening=true;
        BeginMotion(bounds,origin,true);
        return true;
    }
    internal bool ShieldOpeningTarget(IntPtr window,Rectangle bounds){
        return desktopPlayback!=null&&desktopPlayback.ShieldBeforeShow(window,bounds);
    }
    internal bool PrepareLaunchCover(Rectangle bounds){
        var source=bounds;source.Offset(-Left,-Top);
        launchCover=new DesktopClosingWindow(backdrop,source,"ModeTapper Launch Cover");
        if(launchCover.Prepare(bounds))return true;
        ReleaseLaunchCover();return false;
    }
    internal bool HoldLaunchCover(){
        if(launchCover!=null&&launchCover.Show()&&DesktopOpeningWindow.CommitComposition())return true;
        ReleaseLaunchCover();return false;
    }
    internal bool SwitchLaunchCover(IntPtr active,Rectangle bounds){
        bool ready=desktopPlayback.ShieldHeldWindow(active,bounds,launchCover.Handle);
        if(ready)ReleaseLaunchCover();
        return ready;
    }
    internal void ReleaseLaunchCover(){
        if(launchCover==null)return;
        launchCover.Dispose();launchCover=null;
    }
    bool IsCached(IntPtr window){
        return cachedPicture!=null&&window==cacheWindow&&DesktopMotionNative.ProcessOf(window)==cacheProcess&&
            Stopwatch.GetTimestamp()-cachedAt<=Stopwatch.Frequency*60;
    }
    internal IntPtr ExpectedRestore(Point point,DesktopTaskbarButtons.Entry button,IntPtr foreground){
        if(!IsCached(cacheWindow)||cacheWindow==foreground&&!DesktopMotionNative.IsMinimized(cacheWindow))return IntPtr.Zero;
        bool sameIcon=NearIcon(point,cachedAnchor)||warmWindow==cacheWindow&&NearIcon(point,warmAnchor);
        return sameIcon||DesktopTaskbarButtons.Matches(button,cacheWindow)?cacheWindow:IntPtr.Zero;
    }
    static bool NearIcon(Point point,Point icon){return Math.Abs(point.X-icon.X)<=23&&Math.Abs(point.Y-icon.Y)<=20;}
    internal bool TryExpandCached(IntPtr window,Rectangle bounds,Point origin,bool rememberBackdrop){
        if(!IsCached(window)||!cachedBounds.IntersectsWith(Bounds)||!UseOpeningBackdrop(window,rememberBackdrop)||!ShowPrepared())return false;
        // DWM can report intermediate bounds while a recently minimized window is restoring.
        if(Math.Abs(bounds.Width-cachedBounds.Width)<16&&Math.Abs(bounds.Height-cachedBounds.Height)<16&&
           Math.Abs(bounds.Left-cachedBounds.Left)<16&&Math.Abs(bounds.Top-cachedBounds.Top)<16)bounds=cachedBounds;
        picture=cachedPicture;cachedPicture=null;pictureSource=cachedSource;
        BeginMotion(bounds,origin,true);
        return true;
    }
    internal bool PreviewRestore(IntPtr window,Point origin,bool beforeAction){
        if(!CanContract(window,Bounds))return false;
        return TryExpandCached(window,cachedBounds,origin,beforeAction);
    }
    internal void RetainUnconfirmedRestore(IntPtr window){minimizingWindow=window;minimizedAt=cachedMinimizedAt;}
    bool UseOpeningBackdrop(IntPtr window,bool beforeAction){
        bool recentMinimize=window==cacheWindow&&cachedMinimizedAt!=0&&
            Stopwatch.GetTimestamp()-cachedMinimizedAt<Stopwatch.Frequency/5;
        if((!beforeAction||recentMinimize)&&CanContract(window,Bounds)){
            backdrop.Dispose();backdrop=warmBackdrop;warmBackdrop=null;
            restoringWindow=window;
        }else if(!beforeAction)return false;
        else if(!recentMinimize)restoringWindow=window;
        return true;
    }
    internal bool CanContract(IntPtr window,Rectangle area){
        return warmBackdrop!=null&&warmWindow==window&&warmArea==area&&
            DesktopMotionNative.ProcessOf(window)==warmProcess&&DesktopMotionNative.WorkspaceKey(area,window)==warmWorkspace;
    }
    internal void RememberMinimized(IntPtr window,Rectangle bounds,Point origin){
        ConfirmMinimized(window);appBounds=bounds;anchor=origin;
        picture=backdrop;pictureSource=bounds;pictureSource.Offset(-Left,-Top);
    }
    internal void ConfirmMinimized(IntPtr window){
        minimizingWindow=window;
        minimizedAt=window==IntPtr.Zero?0:Stopwatch.GetTimestamp();
    }
    internal void StartContraction(Rectangle bounds,Point origin,IntPtr window){
        RememberMinimized(window,bounds,origin);
        cacheExpiry.Stop();cacheExpiry.Start();
        BeginMotion(bounds,origin,false);
    }
    internal bool StartColdContraction(Rectangle bounds,Point origin,IntPtr window){
        using(var clean=DesktopMotionNative.Capture(Bounds)){
            if(clean==null)return false;
            contractionBackdrop=new DesktopFrameBitmap(clean);
        }
        if(!ShowPrepared())return false;
        StartContraction(bounds,origin,window);
        return true;
    }
    internal bool PrepareDismissal(Rectangle bounds,bool show=true){
        if(bounds.IsEmpty)return false;
        if(closePlayback==null){
            var source=bounds;source.Offset(-Left,-Top);
            closePlayback=new DesktopClosingWindow(backdrop,source);
            if(!closePlayback.Prepare(bounds))return false;
        }
        return !show||closePlayback.Show();
    }
    internal bool Dismiss(Rectangle bounds){
        if(!PrepareDismissal(bounds))return false;
        if(Visible)Hide();
        dismissing=true;
        BeginMotion(bounds,Point.Empty,false);
        return true;
    }
    void BeginMotion(Rectangle bounds,Point origin,bool expand){
        opening=expand;ending=false;appBounds=bounds;anchor=origin;
        towardBottom=origin.Y>=bounds.Top+bounds.Height/2;
        if(desktopPlayback==null&&closePlayback==null)SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x13);
        clock.Restart();
        UpdateFrame(InitialMotionSeconds/MotionSeconds);
    }
    double MotionSeconds {get{return dismissing?DismissSeconds:opening?(desktopOpening?.38:OpeningSeconds):ClosingSeconds;}}
    void TickFrame(){
        try{RenderMotion();}
        catch(ExternalException){if(Finished!=null)Finished();}
    }
    void RenderMotion(){
        if(blocked()){if(Finished!=null)Finished();return;}
        if(!ending&&Waiting!=null)Waiting();
        if(!Active)return;
        if(desktopPlayback!=null)desktopPlayback.RefreshBackdrop();
        if(!ending){
            if(picture==null&&!desktopOpening&&!dismissing)return;
            double elapsed=clock.Elapsed.TotalSeconds+InitialMotionSeconds,duration=MotionSeconds;
            if(elapsed<duration){UpdateFrame(elapsed/duration);return;}
            UpdateFrame(1);
            if(dismissing){if(Finished!=null)Finished();return;}
            if(desktopOpening){
                CompletedDesktopFrames=desktopFrames;
            }
            FadeToDesktop();
            return;
        }
        double fade=clock.Elapsed.TotalSeconds/HandoffSeconds;
        if(fade<=0)return;
        if(fade>=1){if(Finished!=null)Finished();return;}
        if(desktopPlayback!=null||closePlayback!=null)return;
        if(!canvas.Present(Handle,Rectangle.Empty,(byte)Math.Round(255*(1-fade*fade*(3-2*fade))))&&Finished!=null)Finished();
    }
    internal void FadeToDesktop(){ending=true;clock.Restart();}
    void UpdateFrame(double time){
        double t=.54*time+.46*time*time*(3-2*time);
        double progress=opening?t:1-t;
        if(dismissing){
            var bounds=appBounds;
            bounds.Inflate(-(int)Math.Round(bounds.Width*.13*t),-(int)Math.Round(bounds.Height*.13*t));
            if(!closePlayback.Fade(bounds,(byte)(255*progress)))throw new ExternalException("Could not present the close animation.");
            return;
        }
        if(desktopOpening){
            if(!desktopPlayback.Place(WindowMorph.IconReveal(appBounds,anchor,time)))
                throw new ExternalException("Could not compose the window animation.");
            desktopFrames++;
            return;
        }
        Rectangle next=WindowMorph.Unfurl(appBounds,anchor,24,progress);
        next.Offset(-Left,-Top);
        var dirty=frame.IsEmpty?ClientRectangle:Rectangle.Union(frame,next);
        frame=next;
        alpha=(byte)(opening?255:255*Math.Min(1,progress*5));
        bend=opening?0:.38*Math.Pow(Math.Sin(Math.PI*progress),2);
        if(!PaintScene(dirty))throw new ExternalException("Could not present the window animation.");
    }
    protected override void OnPaintBackground(PaintEventArgs e){}
    bool PaintScene(Rectangle dirty){
        // Keep the backdrop in the same native surface as the moving pixels.
        var clip=Rectangle.Intersect(dirty,ClientRectangle);
        if(clip.IsEmpty)return true;
        var desktop=opening||picture==null?backdrop:contractionBackdrop??warmBackdrop??backdrop;
        desktop.Copy(canvas.Device,clip.Location,clip);
        if(picture!=null&&alpha>0&&frame.IntersectsWith(clip))
            picture.DrawTapered(canvas.Device,pictureSource,frame,anchor.X-Left,towardBottom,bend,alpha);
        return canvas.Present(Handle,clip,255);
    }
    internal void Reset(){
        frames.Stop();clock.Stop();
        if(highResolution){timeEndPeriod(1);highResolution=false;}
        if(Visible)Hide();
        ReleaseLaunchCover();
        if(desktopPlayback!=null){desktopPlayback.Hide();desktopPlayback.Dispose();desktopPlayback=null;}
        if(closePlayback!=null){closePlayback.Dispose();closePlayback=null;}
        if(canvas!=null){canvas.Dispose();canvas=null;}
        ending=false;coverReady=false;desktopOpening=false;dismissing=false;
        desktopFrames=CompletedDesktopFrames=0;
        frame=Rectangle.Empty;
        if(minimizingWindow!=IntPtr.Zero&&picture!=null){
            if(cachedPicture!=null)cachedPicture.Dispose();
            cacheWindow=minimizingWindow;cacheProcess=DesktopMotionNative.ProcessOf(cacheWindow);
            cachedPicture=picture;cachedSource=pictureSource;cachedBounds=appBounds;cachedAnchor=anchor;cachedAt=Stopwatch.GetTimestamp();
            cachedMinimizedAt=minimizedAt;
            if(contractionBackdrop!=null){
                if(warmBackdrop!=null)warmBackdrop.Dispose();
                warmBackdrop=contractionBackdrop;contractionBackdrop=null;
                warmWindow=cacheWindow;warmProcess=cacheProcess;warmAnchor=anchor;warmArea=Bounds;
                warmWorkspace=DesktopMotionNative.WorkspaceKey(warmArea,warmWindow);
            }
            cacheExpiry.Stop();cacheExpiry.Start();
            if(backdrop==picture)backdrop=null;
            picture=null;
        }
        if(contractionBackdrop!=null){contractionBackdrop.Dispose();contractionBackdrop=null;}
        if(restoringWindow!=IntPtr.Zero&&backdrop!=null){
            if(warmBackdrop!=null)warmBackdrop.Dispose();
            warmBackdrop=backdrop;backdrop=null;
            warmWindow=restoringWindow;warmProcess=DesktopMotionNative.ProcessOf(warmWindow);
            warmAnchor=anchor;
            warmArea=Bounds;warmWorkspace=DesktopMotionNative.WorkspaceKey(warmArea,warmWindow);
            cacheExpiry.Stop();cacheExpiry.Start();
        }
        restoringWindow=IntPtr.Zero;
        minimizingWindow=IntPtr.Zero;minimizedAt=0;
        if(picture!=null&&picture!=backdrop)picture.Dispose();
        picture=null;
        if(backdrop!=null){backdrop.Dispose();backdrop=null;}
    }
    internal void DropCachedPicture(){
        cacheExpiry.Stop();
        if(cachedPicture!=null){cachedPicture.Dispose();cachedPicture=null;}
        if(warmBackdrop!=null){warmBackdrop.Dispose();warmBackdrop=null;}
    }
    protected override void Dispose(bool disposing){if(disposing){Reset();frames.Dispose();DropCachedPicture();cacheExpiry.Dispose();}base.Dispose(disposing);}
}

// Cover the shell launch before attaching the live window to the same surface.
internal sealed class DesktopOpeningWindow : Form {
    [StructLayout(LayoutKind.Sequential)]struct Rect {
        public int Left,Top,Right,Bottom;
        internal Rect(Rectangle frame){Left=frame.Left;Top=frame.Top;Right=frame.Right;Bottom=frame.Bottom;}
    }
    [StructLayout(LayoutKind.Sequential)]struct Size2 {public int Width,Height;}
    [StructLayout(LayoutKind.Sequential)]struct ThumbnailProps {
        public uint Flags;public Rect Destination,Source;public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)]public bool Visible,ClientOnly;
    }
    [DllImport("dwmapi.dll")]static extern int DwmRegisterThumbnail(IntPtr destination,IntPtr source,out IntPtr thumbnail);
    [DllImport("dwmapi.dll")]static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail,out Size2 size);
    [DllImport("dwmapi.dll")]static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail,ref ThumbnailProps properties);
    [DllImport("dwmapi.dll")]static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")]static extern int DwmFlush();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool SetProp(IntPtr window,string property,IntPtr value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr CreateWindowEx(uint ex,string type,string title,uint style,
        int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")]static extern bool SetLayeredWindowAttributes(IntPtr window,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]static extern bool InvalidateRect(IntPtr window,IntPtr rect,bool erase);
    [DllImport("user32.dll")]static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")]static extern bool DestroyWindow(IntPtr window);
    [DllImport("magnification.dll")]static extern bool MagSetWindowSource(IntPtr window,Rect area);
    [DllImport("magnification.dll")]static extern bool MagSetWindowFilterList(IntPtr window,uint mode,int count,[In]IntPtr[] excluded);
    IntPtr thumbnail;
    WindowPreview preview;
    IntPtr backdropControl;
    bool magnificationInitialized;
    Rectangle crop;

    internal DesktopOpeningWindow(){
        Text="ModeTapper Desktop Reveal";
        FormBorderStyle=FormBorderStyle.None;
        StartPosition=FormStartPosition.Manual;
        AutoScaleMode=AutoScaleMode.None;
        ShowInTaskbar=false;TopMost=true;Enabled=false;
        Size=new Size(1,1);
    }
    protected override bool ShowWithoutActivation{get{return true;}}
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x080800a0;return p;}}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);SetProp(Handle,"NonRudeHWND",new IntPtr(1));}
    internal bool PrepareBackdrop(){
        CreateHandle();
        if(!MagnificationRuntime.Acquire())return false;
        magnificationInitialized=true;
        if(!SetLayeredWindowAttributes(Handle,0,255,2))return false;
        backdropControl=CreateWindowEx(0,"Magnifier",null,0x50000000,0,0,Width,Height,
            Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        return backdropControl!=IntPtr.Zero;
    }
    internal bool ShieldBeforeShow(IntPtr target,Rectangle bounds){
        if(DesktopMotionNative.IsShown(target)||!ShowShield(target,bounds,IntPtr.Zero,true))return false;
        return CommitComposition()&&!DesktopMotionNative.IsShown(target);
    }
    internal bool ShieldHeldWindow(IntPtr target,Rectangle bounds,IntPtr cover){
        return ShowShield(target,bounds,cover,false)&&CommitComposition();
    }
    internal static bool CommitComposition(){return DwmFlush()==0;}
    bool ShowShield(IntPtr target,Rectangle bounds,IntPtr behind,bool beforeShow){
        if(!ApplyFilter(target))return false;
        Bounds=bounds;
        if(!SetWindowPos(backdropControl,IntPtr.Zero,0,0,bounds.Width,bounds.Height,0x14)||
           !MagSetWindowSource(backdropControl,new Rect(bounds)))return false;
        if(beforeShow&&DesktopMotionNative.IsShown(target))return false;
        Show();
        if(behind!=IntPtr.Zero)SetWindowPos(Handle,behind,0,0,0,0,0x13);
        RefreshBackdrop();
        UpdateWindow(backdropControl);
        return true;
    }
    bool ApplyFilter(IntPtr target){
        var exceptions=preview==null?new[]{Handle,target}:new[]{Handle,target,preview.Handle};
        return MagSetWindowFilterList(backdropControl,0,exceptions.Length,exceptions);
    }
    internal void RefreshBackdrop(){InvalidateRect(backdropControl,IntPtr.Zero,false);}
    protected override void OnPaintBackground(PaintEventArgs e){}
    internal bool Attach(IntPtr target,Rectangle visible,Point origin){
        var icon=new Rectangle(origin.X-12,origin.Y-12,24,24);
        preview=new WindowPreview(Rectangle.Union(visible,icon));
        if(!ApplyFilter(target))return false;
        if(DwmRegisterThumbnail(preview.Handle,target,out thumbnail)<0)return false;
        Size2 size;
        if(DwmQueryThumbnailSourceSize(thumbnail,out size)<0||size.Width<1||size.Height<1)return false;
        Rectangle outer=DesktopMotionNative.OuterBounds(target);
        crop=new Rectangle(0,0,size.Width,size.Height);
        if(outer.Width==size.Width&&outer.Height==size.Height&&outer.Contains(visible)){
            crop=visible;crop.Offset(-outer.X,-outer.Y);
        }
        return true;
    }
    internal bool Place(Rectangle destination){
        destination.Offset(-preview.Left,-preview.Top);
        var properties=new ThumbnailProps{Flags=1|2|4|8|16,Opacity=255,Visible=true,
            Destination=new Rect(destination),Source=new Rect(crop)};
        if(DwmUpdateThumbnailProperties(thumbnail,ref properties)<0)return false;
        if(!preview.Visible)preview.Show();
        return true;
    }
    protected override void Dispose(bool disposing){
        if(disposing){
            if(preview!=null&&preview.Visible)preview.Hide();
            if(thumbnail!=IntPtr.Zero){DwmUnregisterThumbnail(thumbnail);thumbnail=IntPtr.Zero;}
            if(preview!=null){preview.Dispose();preview=null;}
            if(backdropControl!=IntPtr.Zero){DestroyWindow(backdropControl);backdropControl=IntPtr.Zero;}
            if(magnificationInitialized){MagnificationRuntime.Release();magnificationInitialized=false;}
        }
        base.Dispose(disposing);
    }
    sealed class WindowPreview : Form {
        internal WindowPreview(Rectangle area){
            FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;
            AutoScaleMode=AutoScaleMode.None;ShowInTaskbar=false;TopMost=true;Enabled=false;
            BackColor=TransparencyKey=Color.FromArgb(1,2,3);
            Bounds=area;
        }
        protected override bool ShowWithoutActivation{get{return true;}}
        protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x080800a0;return p;}}
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);SetProp(Handle,"NonRudeHWND",new IntPtr(1));}
    }
}

// Prepare the first frame while hidden and use the same native surface throughout.
internal sealed class DesktopClosingWindow : NativeWindow,IDisposable {
    [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool SetProp(IntPtr window,string property,IntPtr value);
    readonly DesktopFrameBitmap picture,buffer;
    readonly Rectangle source;
    Size lastSize;
    internal bool Visible {get;private set;}

    internal DesktopClosingWindow(DesktopFrameBitmap picture,Rectangle source,string caption="ModeTapper Window Dismiss"){
        this.picture=picture;this.source=source;
        buffer=new DesktopFrameBitmap(source.Size);
        try{
            CreateHandle(new CreateParams{Caption=caption,Style=unchecked((int)0x80000000),ExStyle=0x080800a8});
            SetProp(Handle,"NonRudeHWND",new IntPtr(1));
        }catch{Dispose();throw;}
    }
    internal bool Prepare(Rectangle bounds){
        picture.Copy(buffer.Device,Point.Empty,source);
        lastSize=bounds.Size;
        return buffer.PresentAt(Handle,bounds,255);
    }
    internal bool Fade(Rectangle bounds,byte opacity){
        if(bounds.Size!=lastSize){
            picture.DrawTapered(buffer.Device,source,new Rectangle(Point.Empty,bounds.Size),0,false,0,255);
            lastSize=bounds.Size;
        }
        return buffer.PresentAt(Handle,bounds,opacity);
    }
    internal bool Show(){
        if(!Visible)Visible=SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x53);
        return Visible;
    }
    public void Dispose(){
        if(Handle!=IntPtr.Zero)DestroyHandle();
        Visible=false;
        buffer.Dispose();
    }
}

// One buffered paint composites the backdrop and the moving window together.
internal sealed class DesktopFrameBitmap : IDisposable {
    const int SourceCopy=0x00cc0020;
    [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("gdi32.dll")]static extern IntPtr SelectObject(IntPtr dc,IntPtr bitmap);
    [DllImport("gdi32.dll")]static extern bool BitBlt(IntPtr target,int x,int y,int w,int h,IntPtr source,int sx,int sy,int operation);
    [DllImport("gdi32.dll")]static extern bool StretchBlt(IntPtr target,int x,int y,int w,int h,IntPtr source,int sx,int sy,int sw,int sh,int operation);
    [DllImport("msimg32.dll")]static extern bool AlphaBlend(IntPtr target,int x,int y,int w,int h,IntPtr source,int sx,int sy,int sw,int sh,uint blend);
    [StructLayout(LayoutKind.Sequential)]struct LayerInfo {
        public uint Size;
        public IntPtr Destination,Position,Dimensions,Source,Origin;
        public uint Key;
        public IntPtr Blend;
        public uint Flags;
        public IntPtr Dirty;
    }
    [DllImport("user32.dll")]static extern bool UpdateLayeredWindowIndirect(IntPtr window,ref LayerInfo info);
    [DllImport("user32.dll")]static extern bool UpdateLayeredWindow(IntPtr window,IntPtr screen,ref Point position,ref Size size,
        IntPtr source,ref Point origin,uint key,ref int blend,uint flags);
    IntPtr dc,bitmap,original;
    IntPtr update;
    internal IntPtr Device {get{return dc;}}

    internal DesktopFrameBitmap(Bitmap image){
        Initialize(image);
    }
    internal DesktopFrameBitmap(Size size){
        using(var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppRgb))Initialize(image);
        try{update=Marshal.AllocHGlobal(20);}catch{Dispose();throw;}
    }
    void Initialize(Bitmap image){
        dc=CreateCompatibleDC(IntPtr.Zero);
        try{
            if(dc==IntPtr.Zero)throw new ExternalException("Could not create a window animation buffer.");
            bitmap=image.GetHbitmap();
            original=SelectObject(dc,bitmap);
            if(original==IntPtr.Zero)throw new ExternalException("Could not select a window animation bitmap.");
        }catch{Dispose();throw;}
    }
    internal bool Present(IntPtr window,Rectangle dirty,byte alpha){
        Marshal.WriteInt32(update,alpha<<16);
        Marshal.WriteInt32(update,4,dirty.Left);
        Marshal.WriteInt32(update,8,dirty.Top);
        Marshal.WriteInt32(update,12,dirty.Right);
        Marshal.WriteInt32(update,16,dirty.Bottom);
        var info=new LayerInfo{Size=(uint)Marshal.SizeOf(typeof(LayerInfo)),Source=dc,Blend=update,
            Flags=2,Dirty=dirty.IsEmpty?IntPtr.Zero:IntPtr.Add(update,4)};
        return UpdateLayeredWindowIndirect(window,ref info);
    }
    internal bool PresentAt(IntPtr window,Rectangle area,byte alpha){
        Point position=area.Location,origin=Point.Empty;
        Size size=area.Size;
        int blend=alpha<<16;
        return UpdateLayeredWindow(window,IntPtr.Zero,ref position,ref size,dc,ref origin,0,ref blend,2);
    }
    internal void Copy(IntPtr target,Point location,Rectangle area){
        BitBlt(target,location.X,location.Y,area.Width,area.Height,dc,area.X,area.Y,SourceCopy);
    }
    internal void DrawTapered(IntPtr target,Rectangle source,Rectangle bounds,int anchorX,bool towardBottom,double bend,byte alpha){
        if(bend<.0005&&alpha==255&&bounds.Size==source.Size){Copy(target,bounds.Location,source);return;}
        if(bend<.0025){
            DrawStrip(target,bounds,source,alpha);
            return;
        }
        const int bands=12;
        double tilt=Math.Max(-1,Math.Min(1,(anchorX-bounds.Left-bounds.Width*.5)/bounds.Width));
        for(int i=0;i<bands;i++){
            double row=(i+.5)/bands;
            if(!towardBottom)row=1-row;
            double amount=bend*row*row;
            int inset=(int)Math.Round(bounds.Width*amount*.5);
            int shift=(int)Math.Round(bounds.Width*amount*tilt*.4);
            int y=bounds.Height*i/bands, sy=source.Height*i/bands;
            DrawStrip(target,new Rectangle(bounds.Left+inset+shift,bounds.Top+y,bounds.Width-2*inset,bounds.Height*(i+1)/bands-y),
                new Rectangle(source.Left,source.Top+sy,source.Width,source.Height*(i+1)/bands-sy),alpha);
        }
    }
    void DrawStrip(IntPtr target,Rectangle to,Rectangle from,byte alpha){
        if(to.Width<=0||to.Height<=0||from.Width<=0||from.Height<=0)return;
        if(alpha==255)StretchBlt(target,to.X,to.Y,to.Width,to.Height,dc,from.X,from.Y,from.Width,from.Height,SourceCopy);
        else AlphaBlend(target,to.X,to.Y,to.Width,to.Height,dc,from.X,from.Y,from.Width,from.Height,(uint)alpha<<16);
    }
    public void Dispose(){
        if(update!=IntPtr.Zero){Marshal.FreeHGlobal(update);update=IntPtr.Zero;}
        if(dc!=IntPtr.Zero){if(original!=IntPtr.Zero)SelectObject(dc,original);DeleteDC(dc);dc=IntPtr.Zero;}
        if(bitmap!=IntPtr.Zero){DeleteObject(bitmap);bitmap=IntPtr.Zero;}
    }
}
