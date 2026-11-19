using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class CodexActivityRow {
    internal string Id,Title,Action;
    internal bool Active;
}

internal sealed class DesktopUtilityWidgets : IDisposable {
    sealed class RecentThread {
        internal string Id,Title;
        internal DateTime Updated;
    }
    sealed class Widget {
        internal DesktopWidgetKind Kind;
        internal WidgetCorner Corner;
        internal bool Enabled=true;
        internal WidgetPosition Position;
        internal DesktopCodexCard Card;
    }
    readonly Widget[] widgets={
        new Widget{Kind=DesktopWidgetKind.Tasks,Corner=WidgetCorner.BottomRight},
        new Widget{Kind=DesktopWidgetKind.Clock,Corner=WidgetCorner.TopRight},
        new Widget{Kind=DesktopWidgetKind.Calendar,Corner=WidgetCorner.TopRight}
    };
    readonly string preferences,index;
    readonly Func<IEnumerable<Rectangle>> otherWidgets;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly Dictionary<string,RecentThread> threads=new Dictionary<string,RecentThread>(StringComparer.OrdinalIgnoreCase);
    RecentThread[] recents=new RecentThread[0];
    DateTime indexWrite;
    internal event Action Moved;

    internal DesktopUtilityWidgets(string directory,string sessionIndex,Func<IEnumerable<Rectangle>> occupied){
        preferences=Path.Combine(directory,"desktop-widgets.json");index=sessionIndex;otherWidgets=occupied;
        try{
            var saved=json.Deserialize<Dictionary<string,Dictionary<string,object>>>(File.ReadAllText(preferences));
            if(saved==null)return;
            foreach(var widget in widgets){
                Dictionary<string,object> values;
                object show,device,x,y;
                if(!saved.TryGetValue(widget.Kind.ToString(),out values)||values==null)continue;
                if(values.TryGetValue("enabled",out show)&&show is bool)widget.Enabled=(bool)show;
                if(values.TryGetValue("monitor",out device)&&device is string&&
                   values.TryGetValue("x",out x)&&x is int&&values.TryGetValue("y",out y)&&y is int)
                    widget.Position=new WidgetPosition((string)device,new Point(Math.Max(0,(int)x),Math.Max(0,(int)y)));
            }
        }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
    }
    Widget Get(DesktopWidgetKind kind){return widgets[(int)kind-1];}
    internal bool Enabled(DesktopWidgetKind kind){return Get(kind).Enabled;}
    internal bool TaskReadingNeeded {get{var card=Get(DesktopWidgetKind.Tasks).Card;return card!=null&&card.Exposed;}}
    internal IEnumerable<Rectangle> Areas {get{return widgets.Where(w=>w.Card!=null).Select(w=>w.Card.ScreenArea);}}
    void Save(){
        var data=widgets.ToDictionary(w=>w.Kind.ToString(),w=>new{enabled=w.Enabled,monitor=w.Position==null?null:w.Position.Monitor,
            x=w.Position==null?(int?)null:w.Position.Offset.X,y=w.Position==null?(int?)null:w.Position.Offset.Y});
        Safety.Atomic(preferences,json.Serialize(data));
    }
    internal void Toggle(DesktopWidgetKind kind){
        var widget=Get(kind);
        widget.Enabled=!widget.Enabled;
        try{Save();}
        catch{widget.Enabled=!widget.Enabled;throw;}
        if(!widget.Enabled)Remove(widget);
    }
    static void Remove(Widget widget){if(widget.Card!=null){widget.Card.Dispose();widget.Card=null;}}
    internal void Tick(TaskReader reader){
        var desktop=DesktopCodexCard.DesktopView();
        var now=DateTime.Now;
        foreach(var widget in widgets){
            if(widget.Card!=null&&(!widget.Card.IsHandleCreated||widget.Card.Desktop!=desktop))Remove(widget);
            if(!widget.Enabled||desktop==IntPtr.Zero)continue;
            try{
                if(widget.Card==null){
                    var card=DesktopCodexCard.Attach(desktop,widget.Corner,widget.Position,otherWidgets,widget.Kind);
                    if(card==null)continue;
                    widget.Card=card;
                    card.PositionChanged+=position=>{
                        widget.Position=position;
                        try{Save();}catch(IOException){}catch(UnauthorizedAccessException){}
                        if(Moved!=null)Moved();
                    };
                    if(Moved!=null)Moved();
                }else widget.Card.Place(widget.Corner,widget.Position);
                if(!widget.Card.Exposed)continue;
                if(widget.Kind==DesktopWidgetKind.Tasks)widget.Card.PresentTasks(TaskRows(reader),reader.Running.Count);
                else widget.Card.PresentTime(now);
            }catch(ExternalException){Remove(widget);}catch(InvalidOperationException){Remove(widget);}
        }
    }
    internal bool Pulse(){
        bool taskRevealed=false;
        foreach(var widget in widgets){
            var card=widget.Card;
            if(card==null||!card.UpdateExposure())continue;
            if(widget.Kind==DesktopWidgetKind.Tasks)taskRevealed=true;
            else card.PresentTime(DateTime.Now);
        }
        return taskRevealed;
    }
    void RefreshRecentThreads(){
        DateTime write=File.GetLastWriteTimeUtc(index);
        if(write==indexWrite)return;
        var next=new Dictionary<string,RecentThread>(StringComparer.OrdinalIgnoreCase);
            try{
                using(var file=new FileStream(index,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                using(var reader=new StreamReader(file)){
                    string line;
                    while((line=reader.ReadLine())!=null){
                        Dictionary<string,object> data;
                        try{data=json.Deserialize<Dictionary<string,object>>(line);}
                        catch(ArgumentException){continue;}catch(InvalidOperationException){continue;}
                        object id,name,updated;Guid thread;DateTime date;
                        if(data==null||!data.TryGetValue("id",out id)||!Guid.TryParse(Convert.ToString(id),out thread)||
                           !data.TryGetValue("thread_name",out name)||String.IsNullOrWhiteSpace(Convert.ToString(name)))continue;
                        if(!data.TryGetValue("updated_at",out updated)||!DateTime.TryParse(Convert.ToString(updated),null,
                           DateTimeStyles.RoundtripKind,out date))date=DateTime.MinValue;
                        next[thread.ToString()]=new RecentThread{Id=thread.ToString(),Title=(string)name,Updated=date.ToUniversalTime()};
                    }
                }
                threads.Clear();foreach(var pair in next)threads[pair.Key]=pair.Value;
                recents=threads.Values.OrderByDescending(thread=>thread.Updated).ToArray();
                indexWrite=write;
            }catch(IOException){}catch(UnauthorizedAccessException){}
    }
    static string ThreadId(string path){
        var name=Path.GetFileNameWithoutExtension(path);Guid id;
        return name.Length>=36&&Guid.TryParse(name.Substring(name.Length-36),out id)?id.ToString():"";
    }
    internal CodexActivityRow[] TaskRows(TaskReader reader){
        RefreshRecentThreads();
        var rows=new List<CodexActivityRow>();
        var added=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in reader.Running){
            string id=ThreadId(path);
            if(id.Length>0&&!added.Add(id))continue;
            RecentThread thread;
            LogState state=reader.Files[path];
            rows.Add(new CodexActivityRow{Id=id,Active=true,Action=state.CurrentAction,
                Title=threads.TryGetValue(id,out thread)?thread.Title:"Задача Codex"});
        }
        var waiting=new HashSet<string>(reader.Files.Where(pair=>pair.Value.Active&&pair.Value.Waiting)
            .Select(pair=>ThreadId(pair.Key)),StringComparer.OrdinalIgnoreCase);
        int recentCount=0;
        foreach(var thread in recents){
            if(!added.Add(thread.Id))continue;
            rows.Add(new CodexActivityRow{Id=thread.Id,Title=thread.Title,
                Action=waiting.Contains(thread.Id)?"Ожидает ответа":""});
            if(++recentCount==20)break;
        }
        return rows.ToArray();
    }
    public void Dispose(){foreach(var widget in widgets)Remove(widget);}
}
