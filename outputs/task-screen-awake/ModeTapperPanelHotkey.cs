using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal struct PanelShortcut : IEquatable<PanelShortcut>
{
    internal readonly Keys Key;
    internal readonly uint Modifiers;
    internal static readonly PanelShortcut Default=new PanelShortcut(Keys.Space,3);

    internal PanelShortcut(Keys key,uint modifiers){Key=key;Modifiers=modifiers&15;}
    internal bool Valid {get{return (int)Key>0&&(int)Key<=254&&Key!=Keys.Escape&&Key!=Keys.F12&&!IsModifier(Key);}}
    public bool Equals(PanelShortcut other){return Key==other.Key&&Modifiers==other.Modifiers;}

    internal static bool IsModifier(Keys key){
        return key==Keys.ShiftKey||key==Keys.LShiftKey||key==Keys.RShiftKey||
            key==Keys.ControlKey||key==Keys.LControlKey||key==Keys.RControlKey||
            key==Keys.Menu||key==Keys.LMenu||key==Keys.RMenu||key==Keys.LWin||key==Keys.RWin;
    }

    internal static PanelShortcut Read(Keys keyData){
        uint mods=0;
        if((keyData&Keys.Alt)!=0)mods|=1;
        if((keyData&Keys.Control)!=0)mods|=2;
        if((keyData&Keys.Shift)!=0)mods|=4;
        if(GetKeyState(0x5B)<0||GetKeyState(0x5C)<0)mods|=8;
        return new PanelShortcut(keyData&Keys.KeyCode,mods);
    }

    public override string ToString(){
        string key=(string)new KeysConverter().ConvertToInvariantString(Key);
        return ((Modifiers&2)!=0?"Ctrl + ":"")+((Modifiers&1)!=0?"Alt + ":"")+
            ((Modifiers&4)!=0?"Shift + ":"")+((Modifiers&8)!=0?"Win + ":"")+key;
    }

    [DllImport("user32.dll")]static extern short GetKeyState(int key);
}

internal sealed class ModeTapperPanelHotkey : NativeWindow,IDisposable
{
    const int First=0x6410,Second=0x6411;
    const uint NoRepeat=0x4000;
    readonly Action invoke;
    int registered;
    PanelShortcut current;
    internal bool Active {get{return registered!=0;}}

    internal ModeTapperPanelHotkey(Action toggle){
        invoke=toggle;
        CreateHandle(new CreateParams{Parent=new IntPtr(-3)});
    }

    internal bool TryChange(PanelShortcut next,Action persist){
        if(!next.Valid)return false;
        if(Active&&current.Equals(next)){if(persist!=null)persist();return true;}
        int candidate=registered==First?Second:First;
        if(!RegisterHotKey(Handle,candidate,next.Modifiers|NoRepeat,(uint)next.Key))return false;
        try{if(persist!=null)persist();}
        catch{UnregisterHotKey(Handle,candidate);throw;}
        if(Active)UnregisterHotKey(Handle,registered);
        current=next;registered=candidate;
        return true;
    }

    protected override void WndProc(ref Message message){
        if(message.Msg==0x0312&&Active&&message.WParam.ToInt32()==registered){
            invoke();return;
        }
        base.WndProc(ref message);
    }

    public void Dispose(){
        if(Active)UnregisterHotKey(Handle,registered);
        registered=0;
        DestroyHandle();
    }

    [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]static extern bool UnregisterHotKey(IntPtr window,int id);
}
