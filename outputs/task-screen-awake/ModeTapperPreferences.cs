using System;
using System.IO;
using System.Security;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal enum PanelTheme { System, Light, Dark }
internal enum PanelSection { Screen, Power, Cooling, Vpn, Gestures, Widgets, Cameras }

internal sealed class ModeTapperPreferences
{
    const int AllSections=127;
    readonly string path;
    readonly JavaScriptSerializer serializer=new JavaScriptSerializer();
    internal PanelTheme Theme {get;private set;}
    internal bool English {get;private set;}
    internal int Sections {get;private set;}

    public sealed class Saved
    {
        public PanelTheme Theme {get;set;}
        public bool English {get;set;}
        public int Sections {get;set;}
        public Saved(){Sections=AllSections;}
    }

    internal ModeTapperPreferences(string folder,bool preview)
    {
        path=preview?null:Path.Combine(folder,"panel-preferences.json");
        Sections=AllSections;
        if(path==null||!File.Exists(path))return;
        try{
            var saved=serializer.Deserialize<Saved>(File.ReadAllText(path));
            if(saved==null)return;
            Theme=Enum.IsDefined(typeof(PanelTheme),saved.Theme)?saved.Theme:PanelTheme.System;
            English=saved.English;
            Sections=saved.Sections&AllSections;
        }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
    }

    internal bool Shows(PanelSection section){return (Sections&(1<<(int)section))!=0;}

    internal bool IsLight
    {
        get{
            if(Theme!=PanelTheme.System)return Theme==PanelTheme.Light;
            try{
                return Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1))!=0;
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(SecurityException){}
            return false;
        }
    }

    internal void Save(PanelTheme theme,bool english,int sections)
    {
        if(path!=null)Safety.Atomic(path,serializer.Serialize(new Saved{Theme=theme,English=english,Sections=sections}));
        Theme=theme;
        English=english;
        Sections=sections;
    }
}
