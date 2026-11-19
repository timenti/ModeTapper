using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

internal static class NativeWindowAnimation {
    internal const string Setting = "animation.native-minimize";
    const uint GetAnimation = 0x48, SetAnimation = 0x49;
    [StructLayout(LayoutKind.Sequential)] struct AnimationInfo { public uint Size; public int Minimize; }
    [DllImport("user32.dll", SetLastError=true)]
    static extern bool SystemParametersInfo(uint action,uint size,ref AnimationInfo data,uint flags);

    internal static string Read(){
        var info=new AnimationInfo{Size=(uint)Marshal.SizeOf(typeof(AnimationInfo))};
        if(!SystemParametersInfo(GetAnimation,info.Size,ref info,0))throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Minimize==0?"0":"1";
    }
    internal static void Write(string value){
        var info=new AnimationInfo{Size=(uint)Marshal.SizeOf(typeof(AnimationInfo)),Minimize=value=="0"?0:1};
        if(!SystemParametersInfo(SetAnimation,info.Size,ref info,2))throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static void Restore(){Safety.RestoreOne(Setting,Read,Write,true);}
}

internal static class WindowMorph {
    internal static Rectangle OverviewFrame(Rectangle window,Rectangle card,double progress,bool elastic){
        if(progress<=0)return window;
        if(progress>=1)return card;
        double arc=elastic?.24*progress*(1-progress):0;
        int width=Blend(window.Width,card.Width,progress+arc);
        int height=Blend(window.Height,card.Height,progress-arc);
        double centerX=window.Left+window.Width*.5+
            (card.Left+card.Width*.5-window.Left-window.Width*.5)*progress;
        double centerY=window.Top+window.Height*.5+
            (card.Top+card.Height*.5-window.Top-window.Height*.5)*progress;
        return new Rectangle((int)Math.Round(centerX-width*.5),(int)Math.Round(centerY-height*.5),width,height);
    }
    internal static Rectangle Unfurl(Rectangle window,Point icon,int iconWidth,double progress){
        if(progress>=1)return window;
        double t=Math.Max(0,progress);
        int width=Blend(iconWidth,window.Width,Math.Pow(t,.82));
        int height=Blend(iconWidth,window.Height,Math.Pow(t,1.20));
        int centerX=Blend(icon.X,window.Left+window.Width/2,t);
        int centerY=Blend(icon.Y,window.Top+window.Height/2,t);
        return new Rectangle(centerX-width/2,centerY-height/2,width,height);
    }
    internal static Rectangle IconReveal(Rectangle window,Point icon,double progress){
        double scale=24d/Math.Max(window.Width,window.Height);
        int width=Math.Max(1,(int)Math.Round(window.Width*scale));
        int height=Math.Max(1,(int)Math.Round(window.Height*scale));
        var origin=new Rectangle(icon.X-width/2,icon.Y-height/2,width,height);
        double t=1-Math.Pow(1-Math.Max(0,Math.Min(1,progress)),3);
        return new Rectangle(Blend(origin.Left,window.Left,t),Blend(origin.Top,window.Top,t),
            Blend(origin.Width,window.Width,t),Blend(origin.Height,window.Height,t));
    }
    internal static Rectangle Collapse(Rectangle window,double progress,int iconWidth){
        int width=Blend(window.Width,iconWidth,progress);
        int height=Blend(window.Height,iconWidth,progress*progress);
        int left=window.Left+(int)Math.Round(iconWidth*progress*.3);
        return new Rectangle(left,window.Top,width,height);
    }
    static int Blend(int from,int to,double progress){return from+(int)Math.Round((to-from)*progress);}
}
