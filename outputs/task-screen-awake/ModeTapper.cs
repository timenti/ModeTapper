using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
[assembly:System.Reflection.AssemblyTitle("ModeTapper")]
[assembly:System.Reflection.AssemblyProduct("ModeTapper")]
[assembly:System.Reflection.AssemblyDescription("Экран, питание и выборочные маршруты VPN")]
[assembly:System.Reflection.AssemblyCompany("TIMENTI")]
[assembly:System.Reflection.AssemblyVersion("1.24.19.0")]
[assembly:System.Reflection.AssemblyFileVersion("1.24.19.0")]

internal static class PowerModes {
    internal static readonly Guid Best=new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
    internal static readonly Guid Balanced=Guid.Empty;
    internal static readonly Guid Economy=new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
    [DllImport("powrprof.dll")] internal static extern uint PowerSetUserConfiguredACPowerMode(ref Guid mode);
    [DllImport("powrprof.dll")] internal static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid mode);
    [DllImport("powrprof.dll")] internal static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
    [DllImport("powrprof.dll")] internal static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
    internal static bool OnAC {get{return SystemInformation.PowerStatus.PowerLineStatus==PowerLineStatus.Online;}}
    internal static Guid Current() { Guid g;uint result=OnAC?PowerGetUserConfiguredACPowerMode(out g):PowerGetUserConfiguredDCPowerMode(out g);Check(result);return g; }
    internal static void Check(uint result) {if(result!=0)throw new System.ComponentModel.Win32Exception((int)result);}
    internal static string ReadSide(bool ac){Guid prior;Check(ac?PowerGetUserConfiguredACPowerMode(out prior):PowerGetUserConfiguredDCPowerMode(out prior));return prior.ToString();}
    internal static void WriteSide(bool ac,string value){Guid mode=new Guid(value);Check(ac?PowerSetUserConfiguredACPowerMode(ref mode):PowerSetUserConfiguredDCPowerMode(ref mode));}
    internal static void SetSide(bool ac,Guid value){Safety.Change(ac?"power.ac":"power.dc",()=>ReadSide(ac),text=>WriteSide(ac,text),value.ToString());}
    internal static void SetCurrent(Guid g) {SetSide(OnAC,g);}
    internal static void SetPair(Guid onAc,Guid onBattery) {Guid ac,dc;Check(PowerGetUserConfiguredACPowerMode(out ac));Check(PowerGetUserConfiguredDCPowerMode(out dc));try{SetSide(true,onAc);SetSide(false,onBattery);}catch{SetSide(true,ac);SetSide(false,dc);throw;}}
    internal static void Defaults(){SetPair(Best,Balanced);}
    internal static int Test() {
        Guid ac,dc;Check(PowerGetUserConfiguredACPowerMode(out ac));Check(PowerGetUserConfiguredDCPowerMode(out dc));
        try {foreach(Guid g in new[]{Best,Balanced,Economy}) {SetCurrent(g);if(Current()!=g)return 1;} Defaults();Guid a,d;Check(PowerGetUserConfiguredACPowerMode(out a));Check(PowerGetUserConfiguredDCPowerMode(out d));return a==Best&&d==Balanced?0:2;}
        finally {Check(PowerSetUserConfiguredACPowerMode(ref ac));Check(PowerSetUserConfiguredDCPowerMode(ref dc));}
    }
}
internal sealed class LogState {
    internal long Offset;
    internal bool Active;
    internal bool Waiting;
    internal string Turn = "";
    internal string WaitCall = "";
    internal DateTime Started;
    internal DateTime LastActivity;
    internal string Activity="Думает";
    internal readonly List<KeyValuePair<string,string>> Tools=new List<KeyValuePair<string,string>>();
    internal string CurrentAction {get{return Tools.Count==0?Activity:Tools[Tools.Count-1].Value;}}
    internal StringBuilder Pending = new StringBuilder();
    internal Decoder Decoder = Encoding.UTF8.GetDecoder();
}
internal sealed class TaskReader {
    internal static readonly TimeSpan ActivityWindow=TimeSpan.FromSeconds(120);
    static readonly TimeSpan ToolActivityWindow=TimeSpan.FromHours(1);
    internal readonly Dictionary<string,LogState> Files = new Dictionary<string,LogState>();
    internal readonly List<string> Running=new List<string>();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    internal void EventLine(LogState state, string line, DateTime since) {
        if(line.IndexOf("\"event_msg\"",StringComparison.Ordinal)<0&&
           line.IndexOf("\"function_call",StringComparison.Ordinal)<0&&line.IndexOf("\"custom_tool_call",StringComparison.Ordinal)<0&&
           line.IndexOf("\"reasoning\"",StringComparison.Ordinal)<0&&line.IndexOf("\"phase\"",StringComparison.Ordinal)<0)return;
        Dictionary<string,object> ev;
        try { ev = json.DeserializeObject(line) as Dictionary<string,object>; } catch { return; }
        if (ev == null || !ev.ContainsKey("type") || !ev.ContainsKey("payload") || !ev.ContainsKey("timestamp")) return;
        var p = ev["payload"] as Dictionary<string,object>;
        if (p == null || !p.ContainsKey("type")) return;
        string kind = (string)p["type"];
        DateTime when;
        if (!DateTime.TryParse(Convert.ToString(ev["timestamp"]), null, System.Globalization.DateTimeStyles.RoundtripKind, out when)) return;
        when = when.ToUniversalTime();
        if (when < since) return;
        string turn = p.ContainsKey("turn_id") ? Convert.ToString(p["turn_id"]) : "";
        string recordType=(string)ev["type"];
        if(recordType=="response_item") {
            if(!state.Active)return;
            state.LastActivity=when;
            string call=p.ContainsKey("call_id")?Convert.ToString(p["call_id"]):"";
            string tool=p.ContainsKey("name")?Convert.ToString(p["name"]):"";
            if(kind=="function_call"&&IsBlockingPrompt(p.ContainsKey("name")?Convert.ToString(p["name"]):"")){
                state.Waiting=true;
                state.WaitCall=call;
            }
            if((kind=="function_call"||kind=="custom_tool_call")&&call.Length>0){
                state.Tools.RemoveAll(item=>item.Key==call);
                state.Tools.Add(new KeyValuePair<string,string>(call,ToolAction(tool)));
            }else if(kind=="function_call_output"||kind=="custom_tool_call_output"){
                if(call.Length>0)state.Tools.RemoveAll(item=>item.Key==call);
                if(state.Waiting&&call.Length>0&&call==state.WaitCall){state.Waiting=false;state.WaitCall="";state.LastActivity=when;}
                state.Activity="Думает";
            }else if(kind=="reasoning")state.Activity="Думает";
            else if(kind=="message"&&p.ContainsKey("role")&&Convert.ToString(p["role"])=="assistant"&&
                p.ContainsKey("phase")&&Convert.ToString(p["phase"])=="final_answer"){
                state.Activity="Готовит ответ";
            }
            return;
        }
        if(recordType!="event_msg")return;
        if(kind=="task_started"){
            state.Active=true;state.Waiting=false;state.WaitCall="";state.Turn=turn;state.Started=state.LastActivity=when;
            state.Activity="Думает";state.Tools.Clear();return;
        }
        if(!state.Active||(!String.IsNullOrEmpty(turn)&&turn!=state.Turn))return;
        if (kind=="task_complete"||kind=="turn_aborted"||kind=="task_aborted"||kind=="task_cancelled"||kind=="turn_completed")state.Active=false;
        else if(kind=="task_paused"||kind=="turn_paused"||kind=="awaiting_input"){state.Waiting=true;state.WaitCall="";}
        else if(kind=="task_resumed"||kind=="turn_resumed"){state.Waiting=false;state.WaitCall="";state.LastActivity=when;}
        else if(kind=="item_completed"||kind=="item_started"||kind=="agent_message_delta"||kind=="agent_reasoning_delta"||kind=="token_count"){
            state.LastActivity=when;
            if(kind=="agent_message_delta")state.Activity="Пишет ответ";
            else if(kind=="agent_reasoning_delta")state.Activity="Думает";
            else if(kind=="item_started"&&p.ContainsKey("item")){
                var item=p["item"] as Dictionary<string,object>;
                object type;
                if(item!=null&&item.TryGetValue("type",out type)){
                    switch(Convert.ToString(type)){
                        case "CommandExecution":state.Activity="Выполняет команду";break;
                        case "FileChange":state.Activity="Меняет файлы";break;
                        case "WebSearch":state.Activity="Ищет в интернете";break;
                        case "ImageView":state.Activity="Просматривает изображение";break;
                    }
                }
            }
        }
    }
    static string ToolAction(string tool){
        if(tool.IndexOf("apply_patch",StringComparison.OrdinalIgnoreCase)>=0)return "Меняет файлы";
        if(tool.IndexOf("search",StringComparison.OrdinalIgnoreCase)>=0||tool.IndexOf("web",StringComparison.OrdinalIgnoreCase)>=0)return "Ищет информацию";
        if(tool.IndexOf("view_image",StringComparison.OrdinalIgnoreCase)>=0)return "Просматривает изображение";
        if(tool=="wait"||tool.IndexOf("wait_",StringComparison.OrdinalIgnoreCase)>=0)return "Проверяет выполнение";
        if(tool=="exec"||tool=="exec_command")return "Работает с инструментами";
        return "Работает";
    }
    static bool IsBlockingPrompt(string name){return name=="request_user_input"||name=="request_permissions"||name=="request_approval"||name.EndsWith(".request_user_input",StringComparison.Ordinal);}
    internal static bool IsRunning(LogState state,DateTime since,DateTime now){
        TimeSpan allowedSilence=state.Tools.Count>0?ToolActivityWindow:ActivityWindow;
        return state.Active&&!state.Waiting&&state.Started>=since&&state.LastActivity<=now.AddSeconds(10)&&now-state.LastActivity<=allowedSilence;
    }
    internal int Read(string root, DateTime since) {
        Running.Clear();
        if (!Directory.Exists(root)) return 0;
        var fresh=new HashSet<string>();DateTime now=DateTime.UtcNow;
        foreach (string path in Directory.EnumerateFiles(root,"*.jsonl",SearchOption.AllDirectories)) {
            if(File.GetLastWriteTimeUtc(path)<now-ToolActivityWindow)continue;
            LogState s;
            if (!Files.TryGetValue(path,out s)) { s = new LogState(); Files[path] = s; }
            try {
                using (var f = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete)) {
                    if (f.Length < s.Offset) { s = new LogState(); Files[path] = s; }
                    fresh.Add(path);
                    if (f.Length == s.Offset) continue;
                    f.Seek(s.Offset,SeekOrigin.Begin);
                    byte[] buffer = new byte[32768]; char[] chars = new char[32770]; int n;
                    while ((n = f.Read(buffer,0,buffer.Length)) > 0) {
                        s.Offset += n;
                        int count = s.Decoder.GetChars(buffer,0,n,chars,0,false);
                        int start = 0;
                        for (int i=0;i<count;i++) if(chars[i]=='\n') {
                            s.Pending.Append(chars,start,i-start);
                            EventLine(s,s.Pending.ToString(),since);
                            s.Pending.Length=0; start=i+1;
                        }
                        if(start<count) s.Pending.Append(chars,start,count-start);
                    }
                }
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach(string path in new List<string>(Files.Keys))
            if(!fresh.Contains(path))Files.Remove(path);else if(IsRunning(Files[path],since,now))Running.Add(path);
        Running.Sort((left,right)=>Files[right].LastActivity.CompareTo(Files[left].LastActivity));
        return Running.Count;
    }
}
internal sealed class TrayApp : ApplicationContext {
    [DllImport("kernel32.dll")] internal static extern uint SetThreadExecutionState(uint flags);
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex","sessions");
    readonly string folder = AppDomain.CurrentDomain.BaseDirectory;
    readonly ModeTapperUpdates updates;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer widgetVisibility = new System.Windows.Forms.Timer{Interval=220};
    readonly TaskReader reader = new TaskReader();
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly LumaFlyout flyout;
    readonly Control dispatcher=new Control();
    VpnController vpn;string vpnError="";SmartTouchpad smartPad;HomeIndicator home;DesktopEffects desktopEffects;
    CodexWidget codexWidget;
    DesktopUtilityWidgets utilityWidgets;
    DesktopIconReservation desktopIcons;
    CameraState cameraState;
    bool cameraBusy,cameraLocked;
    CameraStatusToast cameraToast;
    int rotationRevision;
    readonly EventWaitHandle stopEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\LumaStopV2");RegisteredWaitHandle stopWait;
    internal const string SidePanelEvent="Local\\LumaSidePanelV1";
    EventWaitHandle sidePanelEvent;
    RegisteredWaitHandle sidePanelWait;
    AsusSideButton sideButton;
    KeyboardLightResume keyboardResume;
    int mode=1, count=0;
    const uint Continuous=0x80000000u,SystemRequired=1,DisplayRequired=2;
    uint executionFlags=Continuous;
    LidMonitor lid;
    string lidOverride,displayOverride;
    readonly PowerSelection powerSelection=new PowerSelection();
    readonly bool coolingSupported;
    readonly bool vpnInstalled,camerasInstalled;
    AsusCoolingAvailability coolingAvailability;
    bool? coolingOnAc;
    bool coolingRestorePending;
    AsusCoolingPreset? preferredCooling;
    int? coolingProfile;
    string error="", lastStatus="";
    DateTime heartbeat=DateTime.MinValue;
    internal TrayApp(bool preview=false,bool uiTest=false,bool showSidePanel=false) {
        int saved;
        try { if(Int32.TryParse(File.ReadAllText(Path.Combine(folder,"mode.txt")),out saved) && saved>=0 && saved<=2) mode=saved; } catch { }
        if(Safety.Safe)mode=2;
        IntPtr handle=dispatcher.Handle;
        if(!preview&&!Safety.Safe)try{lid=new LidMonitor();}catch(Exception ex){Safety.Audit("lid.monitor",null,null,ex.Message);}
        string vpnFolder=Path.Combine(folder,"vpn");
        vpnInstalled=preview||File.Exists(Path.Combine(vpnFolder,"rules.template.pac"));
        camerasInstalled=preview||File.Exists(Path.Combine(folder,"ModeTapper-camera-control.exe"));
        if(!preview&&!Safety.Safe&&vpnInstalled)try{vpn=new VpnController(vpnFolder,dispatcher);}catch(Exception ex){vpnError=ex.Message;}
        if(!preview&&!Safety.Safe){
            codexWidget=new CodexWidget(folder,dispatcher,WidgetAreas);
            utilityWidgets=new DesktopUtilityWidgets(folder,Path.Combine(Path.GetDirectoryName(root),"session_index.jsonl"),WidgetAreas);
            desktopIcons=new DesktopIconReservation(dispatcher,WidgetAreas);
            codexWidget.Moved+=desktopIcons.CheckSoon;
            utilityWidgets.Moved+=desktopIcons.CheckSoon;
            home=new HomeIndicator(folder,dispatcher);
            smartPad=new SmartTouchpad(folder,dispatcher,home,()=>{if(flyout!=null&&flyout.Visible)flyout.Hide();});
            try {
                NativeWindowAnimation.Restore();
                home.AnimationPlus=File.Exists(AnimationPreference)&&File.ReadAllText(AnimationPreference)=="1";
            }catch(Exception ex){Report("animations.start",ex.Message,false);}
        }else if(!preview&&uiTest){home=new HomeIndicator(folder,dispatcher,true);}
        coolingSupported=preview||AsusCooling.IsSupportedModel();
        if(preview)coolingAvailability=AsusCoolingAvailability.Ready;
        coolingProfile=preview?(int?)AsusCoolingPreset.Performance:null;
        if(!preview&&coolingSupported)preferredCooling=LoadCoolingPreset();
        updates=new ModeTapperUpdates(folder,preview);
        flyout=new LumaFlyout(()=>mode,SetMode,()=>powerSelection.Current,ChangePower,
            coolingSupported,()=>coolingAvailability,()=>coolingProfile,ChangeCooling,OpenArmouryCrate,
            ()=>ExitThread(),vpnInstalled,()=>preview||(vpn!=null&&vpn.Enabled),()=>vpn==null?vpnError:vpn.Error,()=>vpn!=null&&vpn.Waiting,
            ToggleVpn,EditVpnSites,OpenHapp,()=>preview||(smartPad!=null&&smartPad.Enabled),ToggleTouchpad,
            ()=>preview||(home!=null&&home.Enabled),ToggleHome,()=>preview||(home!=null&&home.EdgesEnabled),ToggleScreenEdges,
            ()=>home!=null&&home.AnimationPlus,ToggleAnimations,
            ()=>preview||smartPad!=null&&smartPad.ZoomEnabled,ToggleTouchpadZoom,
            kind=>preview||WidgetEnabled(kind),ToggleWidget,
            ()=>!preview&&Safety.Safe,ResumeFromMenu,
            ()=>preview?(bool?)false:RotationLock.Current(),ToggleRotation,
            camerasInstalled,()=>preview?CameraState.Available:cameraState,()=>cameraBusy,()=>cameraLocked,OpenCamera,ToggleCamera,updates,
            new ModeTapperPreferences(folder,preview));
        updates.Changed+=()=>flyout.Invalidate();
        if(vpn!=null)vpn.Changed+=()=>flyout.Invalidate();
        if(smartPad!=null)smartPad.Changed+=()=>flyout.Invalidate();
        if(home!=null){
            home.Changed+=()=>flyout.Invalidate();
            flyout.VisibleChanged+=(s,e)=>home.ObservePanelTouch(flyout.Visible);
            home.ScreenContactStarted+=flyout.DismissOutside;
        }
        if(!preview&&!Safety.Safe){
            desktopEffects=new DesktopEffects(dispatcher,()=>flyout.Visible||home.CarouselVisible,home.ObserveCaptionTouch);
            try{desktopEffects.SetEnabled(home.AnimationPlus);}
            catch(Exception ex){Report("animations.desktop",ex.Message,false);}
        }
        if(preview)return;
        WindowsEnergySaver.Observe();
        stopWait=ThreadPool.RegisterWaitForSingleObject(stopEvent,(s,t)=>{try{dispatcher.BeginInvoke(new Action(()=>ExitThread()));}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
        sidePanelEvent=new EventWaitHandle(false,EventResetMode.AutoReset,SidePanelEvent);
        sidePanelWait=ThreadPool.RegisterWaitForSingleObject(sidePanelEvent,(s,t)=>{
            try{dispatcher.BeginInvoke(new Action(ToggleSidePanel));}catch(InvalidOperationException){}
        },null,Timeout.Infinite,false);
        if(coolingSupported&&!Safety.Safe)try{sideButton=new AsusSideButton(dispatcher,flyout,RefreshPanel,()=>ShowOverview(true));}
        catch(Exception ex){Safety.Audit("asus.command-center",null,null,ex.Message);}
        if(coolingSupported&&!Safety.Safe)try{keyboardResume=new KeyboardLightResume(folder);}
        catch(Exception ex){Safety.Audit("keyboard.resume",null,null,ex.Message);}
        timer.Interval=5000; timer.Tick+=(s,e)=>Tick();
        widgetVisibility.Tick+=(s,e)=>{
            if(codexWidget!=null)codexWidget.Pulse();
            if(utilityWidgets!=null&&utilityWidgets.Pulse())Tick();
        };
        if(!Safety.Safe)ApplyPower(LoadPowerPreset(),false);else Tick();
        timer.Start();
        if(utilityWidgets!=null)widgetVisibility.Start();
        if(showSidePanel)dispatcher.BeginInvoke(new Action(ToggleSidePanel));
    }
    void ToggleSidePanel(){
        if(flyout.EdgeIsOpen){flyout.Dismiss();return;}
        RefreshPanel();flyout.OpenAtScreenEdge();
    }
    void RefreshPanel(){
        RefreshCooling();
        if(!camerasInstalled)return;
        cameraState=CameraDevices.State();
        cameraLocked=cameraState==CameraState.Mixed&&CameraDevices.HasLock();
    }
    void ToggleRotation(){
        if(Safety.Safe)return;
        bool wasLocked=RotationLock.Current()==true;
        if(!RotationLock.Toggle()){
            Report("rotation.lock","Windows не подтвердила изменение блокировки поворота.",true);return;
        }
        int revision=++rotationRevision;
        flyout.RefreshPlacement();
        if(!wasLocked)return;
        ThreadPool.QueueUserWorkItem(_=>{
            int orientation=RotationLock.ReadSensor();
            if(orientation<0)return;
            try{dispatcher.BeginInvoke(new Action(()=>{
                if(revision!=rotationRevision||Safety.Safe)return;
                DisplayOrientation.ApplySensor(orientation);
                flyout.RefreshPlacement();
            }));}catch(InvalidOperationException){}
        });
    }
    void OpenCamera(){
        try{CameraDevices.LaunchCamera();}
        catch(Exception ex){Report("camera.open",ex.Message,true);}
    }
    void ToggleCamera(){
        if(Safety.Safe||cameraBusy)return;
        bool block=cameraState!=CameraState.Blocked&&!cameraLocked;
        cameraBusy=true;
        flyout.Invalidate();
        ThreadPool.QueueUserWorkItem(_=>{
            int result;
            try{result=CameraDevices.Request(block);}
            catch(Exception){result=7;}
            try{dispatcher.BeginInvoke(new Action(()=>{
                cameraBusy=false;RefreshPanel();flyout.Invalidate();
                if(result!=0){
                    if(result==1223)return;
                    string message=result==6?"Установи компонент управления камерами через установщик ModeTapper.":
                        "Windows не подтвердила переключение камер (код "+result+").";
                    Report("camera.devices",message,true);return;
                }
                if(cameraToast==null)cameraToast=new CameraStatusToast();
                cameraToast.Display(cameraState);
            }));}catch(InvalidOperationException){}
        });
    }
    void ShowOverview(bool toggle){if(home!=null&&!Safety.Safe){flyout.Hide();home.OpenOverview(toggle);}}
    void ResumeFromMenu(){
        try{
            Safety.CheckCanResume();
            using(var next=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--resume"){UseShellExecute=false,CreateNoWindow=true})){
                if(next==null)throw new InvalidOperationException("Не удалось перезапустить ModeTapper");
            }
        }catch(Exception ex){MessageBox.Show(flyout,ex.Message,"ModeTapper",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    void ToggleScreenEdges(){if(home!=null)home.SetEdges(!home.EdgesEnabled);}
    void ToggleHome(){if(home!=null)home.SetEnabled(!home.Enabled);}
    void ToggleTouchpad(){if(smartPad!=null)smartPad.SetEnabled(!smartPad.Enabled);}
    void ToggleTouchpadZoom(){
        if(smartPad==null||Safety.Safe)return;
        try{smartPad.SetZoom(!smartPad.ZoomEnabled);}
        catch(Exception ex){Report("touchpad.zoom",ex.Message,true);}
    }
    IEnumerable<Rectangle> WidgetAreas(){
        if(codexWidget!=null)yield return codexWidget.Area;
        if(utilityWidgets!=null)foreach(var area in utilityWidgets.Areas)yield return area;
    }
    bool WidgetEnabled(DesktopWidgetKind kind){
        return kind==DesktopWidgetKind.Limit?codexWidget!=null&&codexWidget.Enabled:
            utilityWidgets!=null&&utilityWidgets.Enabled(kind);
    }
    void ToggleWidget(DesktopWidgetKind kind){
        if(codexWidget==null)return;
        try{
            if(kind==DesktopWidgetKind.Limit)codexWidget.SetEnabled(!codexWidget.Enabled);
            else{utilityWidgets.Toggle(kind);utilityWidgets.Tick(reader);}
            desktopIcons.CheckSoon();
        }
        catch(Exception ex){Report("widgets.toggle",ex.Message,true);}
    }
    string AnimationPreference {get{return Path.Combine(folder,"animation-plus-enabled.txt");}}
    void ToggleAnimations(){
        if(home==null||Safety.Safe)return;
        bool next=!home.AnimationPlus;
        try{
            Safety.Atomic(AnimationPreference,next?"1":"0");
            home.AnimationPlus=next;
            if(desktopEffects!=null)desktopEffects.SetEnabled(next);
        }catch(Exception ex){Report("animations.toggle",ex.Message,true);}
    }
    void Report(string operation,string message,bool interactive){
        try{Safety.Audit(operation,null,null,message);}catch{}
        if(interactive)MessageBox.Show(message,"ModeTapper",MessageBoxButtons.OK,MessageBoxIcon.Warning);
    }
    void ToggleVpn(){if(vpn!=null)vpn.SetEnabled(!vpn.Enabled);else Report("vpn.toggle",vpnError,true);}
    void EditVpnSites(){try{using(var editor=new VpnSitesForm(Path.Combine(folder,"vpn","custom-rules.json")))editor.ShowDialog();}catch(Exception ex){Report("vpn.sites",ex.Message,true);}}
    void OpenHapp(){try{Process.Start(@"C:\Program Files\FlyFrogLLC\Happ\Happ.exe");}catch(Exception ex){Report("vpn.happ",ex.Message,true);}}
    PowerPreset LoadPowerPreset(){
        string file=Path.Combine(folder,"power-preset.txt");
        PowerPreset preset;
        try{if(File.Exists(file)&&Enum.TryParse(File.ReadAllText(file).Trim(),out preset)&&Enum.IsDefined(typeof(PowerPreset),preset))return preset;}
        catch{}
        return PowerPreset.Balance;
    }
    void ChangePower(PowerPreset preset){ApplyPower(preset,true);}
    void ApplyPower(PowerPreset preset,bool interactive){
        try{
            powerSelection.Select(preset);
            string file=Path.Combine(folder,"power-preset.txt");
            string value=preset.ToString();
            if(!File.Exists(file)||File.ReadAllText(file).Trim()!=value)Safety.Atomic(file,value);
            Tick();
        }
        catch(Exception ex){Report("power.select",ex.Message,interactive);}
    }
    string CoolingPreference {get{return Path.Combine(folder,"cooling-preset.txt");}}
    AsusCoolingPreset? LoadCoolingPreset(){
        try{
            AsusCoolingPreset preset;
            if(File.Exists(CoolingPreference)&&Enum.TryParse(File.ReadAllText(CoolingPreference).Trim(),out preset)&&
                Enum.IsDefined(typeof(AsusCoolingPreset),preset))return preset;
        }catch(Exception ex){Safety.Audit("asus.cooling.preference",null,null,ex.Message);}
        return null;
    }
    void RefreshCooling(){
        if(!coolingSupported||Safety.Safe)return;
        bool onAc=PowerModes.OnAC;
        var availability=AsusCooling.Availability();
        if(coolingOnAc!=onAc||coolingAvailability!=availability){
            coolingOnAc=onAc;
            coolingAvailability=availability;
            coolingProfile=null;
            coolingRestorePending=preferredCooling.HasValue;
            flyout.RefreshCoolingFeedback();
        }
        if(availability!=AsusCoolingAvailability.Ready||!coolingRestorePending||
            (!onAc&&preferredCooling==AsusCoolingPreset.Turbo))return;
        coolingRestorePending=false;
        try{coolingProfile=AsusCooling.Select(preferredCooling.Value);}
        catch(Exception ex){Safety.Audit("asus.cooling.restore",null,preferredCooling.ToString(),ex.Message);}
    }
    void ChangeCooling(AsusCoolingPreset preset){
        try{
            bool onAc=PowerModes.OnAC;
            coolingProfile=AsusCooling.Select(preset);
            coolingOnAc=onAc;
            preferredCooling=preset;
            coolingRestorePending=false;
            Safety.Atomic(CoolingPreference,preset.ToString());
            Tick();
        }catch(Exception ex){RefreshCooling();Report("asus.cooling",ex.Message,true);}
    }
    void OpenArmouryCrate(){
        try{AsusCooling.OpenArmouryCrate();}
        catch(Exception ex){Report("asus.armoury-crate",ex.Message,true);}
    }
    internal static GraphicsPath RoundRect(RectangleF r,float radius) {
        var p=new GraphicsPath();float d=radius*2;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    static Bitmap IconBitmap(int size) {
        var bitmap=new Bitmap(size,size);using(var g=Graphics.FromImage(bitmap)) {
            g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.Clear(Color.Transparent);g.ScaleTransform(size/32f,size/32f);
            var r=new RectangleF(1,1,30,30);
            using(var path=RoundRect(r,7.5f))using(var brush=new LinearGradientBrush(r,Color.FromArgb(43,43,43),Color.FromArgb(12,12,12),58f))g.FillPath(brush,path);
            using(var path=RoundRect(new RectangleF(1.5f,1.5f,29,29),7))using(var pen=new Pen(Color.FromArgb(90,90,90),.8f))g.DrawPath(pen,path);
            using(var light=new SolidBrush(Color.FromArgb(246,246,246)))
                g.FillPolygon(light,new[]{new PointF(17.7f,5.4f),new PointF(8.1f,17),new PointF(15.5f,17),new PointF(21.7f,12.8f),new PointF(16.6f,12.8f)});
            using(var silver=new SolidBrush(Color.FromArgb(180,180,180)))
                g.FillPolygon(silver,new[]{new PointF(23.7f,13.6f),new PointF(14,26.6f),new PointF(15.2f,19.1f),new PointF(10.3f,19.1f),new PointF(16.7f,14.9f),new PointF(20.8f,14.9f)});
        }return bitmap;
    }
    internal static void ExportIcon(string path) {
        int[] sizes={16,20,24,32,40,48,64,128,256};var images=new List<byte[]>();
        foreach(int size in sizes)using(var bmp=IconBitmap(size))using(var m=new MemoryStream()){bmp.Save(m,System.Drawing.Imaging.ImageFormat.Png);images.Add(m.ToArray());}
        using(var f=File.Create(path))using(var w=new BinaryWriter(f)){w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++){w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)0);w.Write((byte)0);w.Write((ushort)1);w.Write((ushort)32);w.Write(images[i].Length);w.Write(offset);offset+=images[i].Length;}
            foreach(var image in images)w.Write(image);
        }
    }
    internal void RenderPreview(string path,bool settings=false) {
        flyout.SavePreview(path,settings);
    }
    void SetMode(int value) {
        if(Safety.Safe)return;mode=value;
        try {File.WriteAllText(Path.Combine(folder,"mode.txt"),mode.ToString());}catch{}
        Tick();
    }
    void Tick() {
        error=""; count=0;
        if(codexWidget!=null)codexWidget.Tick();
        if(flyout.Visible){RefreshPanel();flyout.Invalidate();}
        else RefreshCooling();
        if(!Safety.Safe)Clean("power-plan",powerSelection.RefreshPlan);
        try {
            if(mode==1||utilityWidgets!=null&&utilityWidgets.TaskReadingNeeded) {
                DateTime oldest=DateTime.MaxValue;
                foreach(string name in new[]{"ChatGPT","codex"}) foreach(var p in Process.GetProcessesByName(name)) {
                    using(p) {try {DateTime start=p.StartTime.ToUniversalTime();if(start<oldest)oldest=start;}catch{}}
                }
                if(oldest!=DateTime.MaxValue){int running=reader.Read(root,oldest);if(mode==1)count=running;}
                else{reader.Files.Clear();reader.Running.Clear();}
            }
        } catch(Exception ex) {error=ex.GetType().Name;}
        if(utilityWidgets!=null)utilityWidgets.Tick(reader);
        bool want=mode==0 || (mode==1 && count>0 && error=="");
        if(Safety.Safe)want=false;
        bool lidClosed=lid!=null&&lid.Closed;
        bool sleepOnRelease=false;
        try{
            if(want&&lid!=null)lidOverride=PlanSettings.KeepLidOpen(lidOverride);
            else if(lidOverride!=null){PlanSettings.ReleaseLid(lidOverride);lidOverride=null;sleepOnRelease=lidClosed;}
        }catch(Exception ex){error="Lid policy: "+ex.Message;}
        try{
            if(want&&!lidClosed)displayOverride=PlanSettings.KeepDisplayOn(displayOverride);
            else if(displayOverride!=null){PlanSettings.ReleaseDisplay(displayOverride);displayOverride=null;}
        }catch(Exception ex){error="Display policy: "+ex.Message;}
        uint requested=Continuous|(want?SystemRequired|(lidClosed?0:DisplayRequired):0);
        if(requested!=executionFlags) {
            Safety.Audit("execution-state",executionFlags.ToString("X8"),requested.ToString("X8"),"request");
            if(SetThreadExecutionState(requested)!=0)executionFlags=requested;
            else error="Windows rejected power request";
        }
        bool holding=(executionFlags&SystemRequired)!=0;
        if(sleepOnRelease&&!holding)dispatcher.BeginInvoke(new Action(()=>{
            if(lid!=null&&lid.Closed&&lidOverride==null&&(executionFlags&SystemRequired)==0)
                Clean("closed-lid-sleep",LidMonitor.SleepNow);
        }));
        bool saverOn=PlanSettings.EnergySaverOn;
        Guid profile=Guid.Empty;
        try{profile=PowerModes.Current();}catch{}
        bool animationPlus=home!=null&&home.AnimationPlus;
        bool desktopAnimationReady=desktopEffects!=null&&desktopEffects.Ready;
        string desktopAnimation=desktopEffects==null?null:desktopEffects.LastEffect;
        int desktopLaunches=desktopEffects==null?0:desktopEffects.DesktopLaunches;
        int desktopLaunchFrames=desktopEffects==null?0:desktopEffects.LastDesktopFrames;
        string key=mode+":"+count+":"+executionFlags+":"+lidClosed+":"+lidOverride+":"+powerSelection.Current+":"+saverOn+":"+profile+":"+coolingAvailability+":"+coolingProfile+":"+animationPlus+":"+desktopAnimationReady+":"+desktopAnimation+":"+desktopLaunches+":"+error;
        if(key!=lastStatus || (DateTime.UtcNow-heartbeat).TotalSeconds>=60) {
            try {File.WriteAllText(Path.Combine(folder,"tray-status.json"),json.Serialize(new {mode=mode==0?"green-always":mode==1?"yellow-tasks":"red-off",activeTasks=count,holding=holding,displayHeld=(executionFlags&DisplayRequired)!=0,lidClosed=lidClosed,lidOverride=lidOverride!=null,powerProfile=profile,powerPreset=powerSelection.Current.ToString(),maxSaver=powerSelection.Current==PowerPreset.Economy,windowsEnergySaver=saverOn,coolingProfile=coolingProfile,coolingAvailability=coolingSupported?coolingAvailability.ToString():null,sideButtonReady=sideButton!=null,sideButtonHoldReady=sideButton!=null&&sideButton.HoldReady,sideButtonStoreBlocked=sideButton!=null&&sideButton.StoreLaunchBlocked,animationPlus=animationPlus,desktopAnimationReady,desktopAnimation,desktopLaunches,desktopLaunchFrames,safeMode=Safety.Safe,error=error,pid=Process.GetCurrentProcess().Id,updatedUtc=DateTime.UtcNow.ToString("o")}));} catch{}
            lastStatus=key;heartbeat=DateTime.UtcNow;
        }
    }
    static void Clean(string name,Action action){try{action();}catch(Exception ex){try{Safety.Audit("shutdown."+name,null,null,ex.Message);}catch{}}}
    protected override void ExitThreadCore() {
        Clean("keyboard.resume",()=>{if(keyboardResume!=null)keyboardResume.Dispose();});
        Clean("stop-wait",()=>{if(stopWait!=null)stopWait.Unregister(null);stopEvent.Dispose();});
        Clean("side-panel",()=>{if(sidePanelWait!=null)sidePanelWait.Unregister(null);if(sidePanelEvent!=null)sidePanelEvent.Dispose();});
        Clean("side-button",()=>{if(sideButton!=null)sideButton.Dispose();});
        Clean("animations.desktop",()=>{if(desktopEffects!=null)desktopEffects.Dispose();});
        Clean("widgets",()=>{if(codexWidget!=null)codexWidget.Dispose();});
        Clean("desktop-widgets",()=>{widgetVisibility.Dispose();if(desktopIcons!=null)desktopIcons.Dispose();if(utilityWidgets!=null)utilityWidgets.Dispose();});
        Clean("device-controls",()=>{if(cameraToast!=null)cameraToast.Dispose();});
        Clean("touchpad",()=>{if(smartPad!=null)smartPad.Dispose();});Clean("home",()=>{if(home!=null)home.Dispose();});Clean("proxy",()=>{if(vpn!=null)vpn.Dispose();});
        Clean("timer",()=>{timer.Stop();timer.Dispose();});
        Clean("display-policy",()=>PlanSettings.ReleaseDisplay(displayOverride));
        Clean("lid-policy",()=>PlanSettings.ReleaseLid(lidOverride));Clean("power-selection",powerSelection.Dispose);
        Clean("energy-status",WindowsEnergySaver.StopObserving);
        Clean("lid-monitor",()=>{if(lid!=null)lid.Dispose();});SetThreadExecutionState(Continuous);
        Clean("ui",()=>{updates.Dispose();dispatcher.Dispose();flyout.Dispose();});
        try{File.WriteAllText(Path.Combine(folder,"tray-status.json"),json.Serialize(new {mode="closed",holding=false,activeTasks=0,updatedUtc=DateTime.UtcNow.ToString("o")}));}catch{}
        base.ExitThreadCore();
    }
    internal static int SelfTest() {
        var r=new TaskReader();var a=new LogState();var b=new LogState();DateTime since=DateTime.UtcNow.AddMinutes(-1);
        Func<string,string,string> line=(kind,id)=>"{\"timestamp\":\""+DateTime.UtcNow.ToString("o")+"\",\"type\":\"event_msg\",\"payload\":{\"type\":\""+kind+"\",\"turn_id\":\""+id+"\"}}";
        r.EventLine(a,line("task_started","a"),since);if(!a.Active)return 1;
        r.EventLine(b,line("task_started","b"),since);r.EventLine(a,line("task_complete","a"),since);if(a.Active||!b.Active)return 2;
        r.EventLine(b,line("turn_aborted","b"),since);if(b.Active)return 3;
        r.EventLine(a,line("task_started","new"),since);r.EventLine(a,line("task_complete","old"),since);if(!a.Active)return 4;
        uint first=SetThreadExecutionState(0x80000003u);uint held=SetThreadExecutionState(0x80000000u);uint clear=SetThreadExecutionState(0x80000000u);
        if(first==0||(held&3)!=3||(clear&3)!=0)return 5;
        if(!TaskReader.IsRunning(a,since,DateTime.UtcNow))return 6;
        r.EventLine(a,line("turn_paused","new"),since);
        if(TaskReader.IsRunning(a,since,DateTime.UtcNow))return 7;
        r.EventLine(a,line("task_resumed","new"),since);
        if(!TaskReader.IsRunning(a,since,DateTime.UtcNow)||TaskReader.IsRunning(a,since,DateTime.UtcNow.AddMinutes(3)))return 8;
        string prefix="{\"timestamp\":\""+DateTime.UtcNow.ToString("o")+"\",\"type\":\"response_item\",\"payload\":";
        r.EventLine(a,prefix+"{\"type\":\"function_call\",\"name\":\"request_user_input_async\",\"call_id\":\"async\"}}",since);
        if(!TaskReader.IsRunning(a,since,DateTime.UtcNow))return 9;
        r.EventLine(a,prefix+"{\"type\":\"function_call\",\"name\":\"request_user_input\",\"call_id\":\"prompt\"}}",since);
        if(TaskReader.IsRunning(a,since,DateTime.UtcNow))return 10;
        r.EventLine(a,prefix+"{\"type\":\"function_call_output\",\"call_id\":\"async\"}}",since);
        if(TaskReader.IsRunning(a,since,DateTime.UtcNow))return 11;
        r.EventLine(a,prefix+"{\"type\":\"function_call_output\",\"call_id\":\"prompt\"}}",since);
        if(!TaskReader.IsRunning(a,since,DateTime.UtcNow))return 12;
        if(!CarouselCatalog.IsShellSurface("ShellHost","Windows.UI.Core.CoreWindow")||CarouselCatalog.IsShellSurface("explorer","CabinetWClass"))return 13;
        if(!TestBottomEdge())return 14;
        r.EventLine(a,prefix+"{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"long\"}}",since);
        if(!TaskReader.IsRunning(a,since,a.LastActivity.AddMinutes(3)))return 15;
        r.EventLine(a,prefix+"{\"type\":\"function_call_output\",\"call_id\":\"long\"}}",since);
        if(TaskReader.IsRunning(a,since,a.LastActivity.AddMinutes(3)))return 16;
        return 0;
    }
    static bool TestBottomEdge(){
        var gesture=new EdgeGesture();int step=0,overview=0;double slideDelta=0;
        PadEdge motion=PadEdge.None;
        gesture.Slide+=(phase,dx,width)=>{
            if(phase==PadSlidePhase.Move)slideDelta=dx;
            else if(phase==PadSlidePhase.Release)step=-Math.Sign(slideDelta);
        };
        gesture.OverviewDrag+=(phase,up,height)=>{if(phase==PadSlidePhase.Release)overview++;};
        gesture.Motion+=(id,edge,delta)=>motion=edge;
        long time=Stopwatch.GetTimestamp();
        Action<double,double,bool> feed=(x,y,pressed)=>{
            time+=Stopwatch.Frequency/20;
            gesture.Update(new TouchFrame{Count=pressed?1:0,Id=1,Width=110,Height=64,X=x,Y=y,Pressed=pressed,Confident=true},time);
        };
        feed(35,59,true);feed(55,60,true);feed(0,0,false);
        if(step!=0||overview!=0)return false;
        feed(50,60,true);feed(51,45,true);feed(53,39,true);feed(0,0,false);
        if(step!=0||overview!=0)return false;
        Action<double,double,bool> pair=(x,y,sideBySide)=>{
            time+=Stopwatch.Frequency/20;
            gesture.Update(new TouchFrame{Count=2,SampledContacts=2,Width=110,Height=64,CenterX=x,CenterY=y,SpanX=sideBySide?24:1,SpanY=sideBySide?1:18,Pressed=true,Confident=true},time);
        };
        pair(35,59,true);pair(55,60,true);feed(0,0,false);
        if(step!=-1||overview!=0)return false;
        pair(65,60,true);pair(45,60,true);feed(0,0,false);
        if(step!=1||overview!=0)return false;
        step=0;
        pair(35,59,false);pair(55,59,false);feed(0,0,false);
        if(step!=0||overview!=0)return false;
        pair(50,59,true);pair(50,44,true);pair(52,38,true);feed(0,0,false);
        pair(50,45,true);pair(50,23,true);feed(0,0,false);
        if(step!=0||overview!=1)return false;
        feed(50,51,true);feed(50,61,true);feed(65,61,true);feed(0,0,false);
        feed(38,61,true);feed(40,61,true);feed(0,0,false);
        if(step!=0||overview!=1)return false;
        feed(2,60,true);feed(2,52,true);feed(0,0,false);
        return step==0&&overview==1&&motion==PadEdge.Keyboard;
    }
}
internal static class Program {
    [DllImport("user32.dll")]static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]static extern bool SetProcessDPIAware();
    [STAThread] static int Main(string[] args) {
        try{SetProcessDpiAwarenessContext(new IntPtr(-4));}catch(EntryPointNotFoundException){SetProcessDPIAware();}
        if(args.Length>0 && args[0]=="--stop"){try{using(var e=EventWaitHandle.OpenExisting("Local\\LumaStopV2"))e.Set();}catch(WaitHandleCannotBeOpenedException){}return 0;}
        if(args.Length>0 && args[0]=="--self-test")return TrayApp.SelfTest();

        if(args.Length>1 && args[0]=="--export-icon"){TrayApp.ExportIcon(args[1]);return 0;}
        if(args.Length>1 && args[0]=="--render-preview"){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);using(var app=new TrayApp(true))app.RenderPreview(args[1],args.Length==3&&args[2]=="settings");return 0;}
        string command=args.Length>0?args[0]:"--side-panel";
        bool maintenance=command=="--safe-mode"||command=="--rollback"||command=="--resume"||command=="--carousel-test"||command=="--power-test"||command=="--restore-lid-sleep";
        if(maintenance)try{using(var stop=EventWaitHandle.OpenExisting("Local\\LumaStopV2"))stop.Set();}catch(WaitHandleCannotBeOpenedException){}
        using(var mutex=new Mutex(false,"Local\\CodexScreenAwakeTrayV1")){
            bool acquired=false;try{
                try{acquired=mutex.WaitOne(maintenance?10000:0);}catch(AbandonedMutexException){acquired=true;}
                if(!acquired){
                    if(command=="--side-panel")try{using(var request=EventWaitHandle.OpenExisting(TrayApp.SidePanelEvent))request.Set();}catch(WaitHandleCannotBeOpenedException){return 3;}
                    return maintenance?3:0;
                }
                Safety.Init(AppDomain.CurrentDomain.BaseDirectory);
                PlanSettings.RecoverOverrides();
                try{NativeTouchpadZoom.Restore(AppDomain.CurrentDomain.BaseDirectory);}
                catch(Exception ex){Safety.Audit("touchpad.zoom.restore",null,null,ex.Message);}
                try{NativeWindowAnimation.Restore();}catch(Exception ex){Safety.Pause("Window animation recovery failed: "+ex.Message);}
                if(command=="--restore-lid-sleep")try{PlanSettings.EnableWindowsLidSleep();}catch(Exception ex){Safety.Audit("lid.baseline",null,null,ex.Message);return 5;}
                if(command=="--power-test"){Safety.Require();return PowerModes.Test();}
                if(command=="--rollback")return Safety.Rollback();
                if(command=="--resume")Safety.Resume();
                if(command=="--safe-mode")Safety.Pause("Explicit --safe-mode");
                if(command=="--carousel-test")Safety.PauseForSession();
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                try{Application.Run(new TrayApp(false,command=="--carousel-test",command=="--side-panel"));}finally{TrayApp.SetThreadExecutionState(0x80000000u);}
            }finally{if(acquired)mutex.ReleaseMutex();}
        }
        return 0;
    }
}
