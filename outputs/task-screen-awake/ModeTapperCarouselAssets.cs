using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

internal static class CarouselBackdropImage {
    internal static Bitmap Create(Bitmap desktop,int radius) {
        int width=desktop.Width,height=desktop.Height;
        var canvas=new Rectangle(0,0,width,height);
        var pixels=new int[width*height];
        var scratch=new int[pixels.Length];
        var source=desktop.LockBits(canvas,ImageLockMode.ReadOnly,PixelFormat.Format32bppRgb);
        try { Marshal.Copy(source.Scan0,pixels,0,pixels.Length); }
        finally { desktop.UnlockBits(source); }
        radius=Math.Max(1,radius);
        // Two passes in each direction create a smooth kernel at the display's pixel size.
        Blur(pixels,scratch,width,height,1,width,radius);
        Blur(scratch,pixels,height,width,width,1,radius);
        Blur(pixels,scratch,width,height,1,width,radius);
        Blur(scratch,pixels,height,width,width,1,radius);
        for(int i=0;i<pixels.Length;i++){
            int pixel=pixels[i];
            int red=Tint((pixel>>16)&255),green=Tint((pixel>>8)&255),blue=Tint(pixel&255);
            pixels[i]=unchecked((int)0xff000000)|(red<<16)|(green<<8)|blue;
        }
        var result=new Bitmap(width,height,PixelFormat.Format32bppRgb);
        try{
            var output=result.LockBits(canvas,ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);
            try { Marshal.Copy(pixels,0,output.Scan0,pixels.Length); }
            finally { result.UnlockBits(output); }
            return result;
        }catch{result.Dispose();throw;}
    }
    static int Tint(int channel){return (channel*181+20*75+128)>>8;}
    static void Blur(int[] source,int[] output,int length,int lines,int step,int pitch,int radius){
        int scale=65536/(2*radius+1);
        for(int line=0;line<lines;line++){
            int offset=line*pitch;
            int first=source[offset],last=source[offset+(length-1)*step];
            int red=((first>>16)&255)*radius,green=((first>>8)&255)*radius,blue=(first&255)*radius;
            for(int i=0;i<=radius;i++){
                int pixel=source[offset+Math.Min(i,length-1)*step];
                red+=(pixel>>16)&255;
                green+=(pixel>>8)&255;
                blue+=pixel&255;
            }
            for(int i=0;i<length;i++){
                output[offset+i*step]=(((red*scale+32768)>>16)<<16)|(((green*scale+32768)>>16)<<8)|((blue*scale+32768)>>16);
                int entering=i+radius+1<length?source[offset+(i+radius+1)*step]:last;
                int leaving=i>=radius?source[offset+(i-radius)*step]:first;
                red+=((entering>>16)&255)-((leaving>>16)&255);
                green+=((entering>>8)&255)-((leaving>>8)&255);
                blue+=(entering&255)-(leaving&255);
            }
        }
    }
}

internal static class CarouselAppImages {
    const string AppsFolder=@"shell:AppsFolder\";
    const string SettingsId="windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey{public Guid Format;public uint Id;}
    [StructLayout(LayoutKind.Explicit,Size=24)] struct PropertyValue{[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public IntPtr Pointer;}
    [StructLayout(LayoutKind.Sequential)] struct ImageSize{public int Width,Height;}
    [StructLayout(LayoutKind.Sequential)] struct BitmapObject{public int Type,Width,Height,RowBytes;public ushort Planes,BitsPerPixel;public IntPtr Bits;}
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo{public uint Size;public int Width,Height;public ushort Planes,Depth;public uint Compression,ImageSize;public int XPels,YPels;public uint Colors,Important;}
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface PropertyStore {
        [PreserveSig]int Count(out uint count);
        [PreserveSig]int At(uint index,out PropertyKey key);
        [PreserveSig]int Get(ref PropertyKey key,out PropertyValue value);
    }
    [ComImport,Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ImageFactory {[PreserveSig]int GetImage(ImageSize size,uint flags,out IntPtr bitmap);}
    delegate bool ChildWindow(IntPtr window,IntPtr state);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr window,ref Guid id,out PropertyStore store);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string name,IntPtr context,ref Guid id,out ImageFactory result);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved,uint apartment);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropertyValue value);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,uint process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr handle,ref uint length,StringBuilder name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr handle,uint flags,StringBuilder path,ref int length);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window,ChildWindow callback,IntPtr state);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll",EntryPoint="GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr window,int index);
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")] static extern int GetBitmapObject(IntPtr bitmap,int bytes,out BitmapObject info);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr device);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr device);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc,IntPtr bitmap,uint first,uint height,[Out]int[] data,ref BitmapInfo info,uint usage);

    internal static Bitmap Load(IntPtr window,string title){
        int init=CoInitializeEx(IntPtr.Zero,0);
        try{
            string id=WindowId(window);
            uint owner;GetWindowThreadProcessId(window,out owner);
            string path;
            string processId=ProcessId(owner,out path);
            if(String.IsNullOrEmpty(id))id=processId;
            if(String.IsNullOrEmpty(id)){
                EnumChildWindows(window,(child,state)=>{
                    uint pid;GetWindowThreadProcessId(child,out pid);
                    if(pid==0||pid==owner)return true;
                    string childPath;
                    id=ProcessId(pid,out childPath);
                    return String.IsNullOrEmpty(id);
                },IntPtr.Zero);
            }
            if(String.IsNullOrEmpty(id)&&title=="Настройки")id=SettingsId;
            Bitmap bitmap=String.IsNullOrEmpty(id)?null:ShellImage(AppsFolder+id);
            if(bitmap==null&&!String.IsNullOrEmpty(path))bitmap=ShellImage(path);
            if(bitmap!=null)return bitmap;
            IntPtr icon;
            SendMessageTimeout(window,0x7f,new IntPtr(1),new IntPtr(GetDpiForWindow(window)),2,35,out icon);
            if(icon==IntPtr.Zero)icon=GetClassLongPtr(window,-14);
            if(icon==IntPtr.Zero)icon=GetClassLongPtr(window,-34);
            if(icon==IntPtr.Zero)return null;
            using(var copy=(Icon)Icon.FromHandle(icon).Clone())return copy.ToBitmap();
        }finally{if(init>=0)CoUninitialize();}
    }
    internal static string WindowId(IntPtr window){
        Guid iid=typeof(PropertyStore).GUID;
        PropertyStore store;
        if(SHGetPropertyStoreForWindow(window,ref iid,out store)<0||store==null)return null;
        try{
            var key=new PropertyKey{Format=new Guid("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"),Id=5};
            PropertyValue value;
            int result=store.Get(ref key,out value);
            try{return result>=0&&value.Type==31?Marshal.PtrToStringUni(value.Pointer):null;}
            finally{PropVariantClear(ref value);}
        }finally{Marshal.ReleaseComObject(store);}
    }
    static string ProcessId(uint pid,out string path){
        path=null;
        IntPtr handle=OpenProcess(0x1000,false,pid);
        if(handle==IntPtr.Zero)return null;
        try{
            var executable=new StringBuilder(1024);
            int length=executable.Capacity;
            if(QueryFullProcessImageName(handle,0,executable,ref length))path=executable.ToString();
            var id=new StringBuilder(256);
            uint count=(uint)id.Capacity;
            return GetApplicationUserModelId(handle,ref count,id)==0?id.ToString():null;
        }finally{CloseHandle(handle);}
    }
    static Bitmap ShellImage(string path){
        Guid iid=typeof(ImageFactory).GUID;
        ImageFactory factory;
        if(SHCreateItemFromParsingName(path,IntPtr.Zero,ref iid,out factory)<0||factory==null)return null;
        try{
            IntPtr bitmap;
            if(factory.GetImage(new ImageSize{Width=96,Height=96},1|4,out bitmap)<0||bitmap==IntPtr.Zero)return null;
            try{return ReadBitmap(bitmap);}
            finally{DeleteObject(bitmap);}
        }finally{Marshal.ReleaseComObject(factory);}
    }
    static Bitmap ReadBitmap(IntPtr bitmap){
        BitmapObject shape;
        if(GetBitmapObject(bitmap,Marshal.SizeOf(typeof(BitmapObject)),out shape)==0||shape.Width<=0||shape.Height<=0)return null;
        var info=new BitmapInfo{Size=40,Width=shape.Width,Height=-shape.Height,Planes=1,Depth=32};
        var pixels=new int[shape.Width*shape.Height];
        IntPtr dc=CreateCompatibleDC(IntPtr.Zero);
        if(dc==IntPtr.Zero)return null;
        try{if(GetDIBits(dc,bitmap,0,(uint)shape.Height,pixels,ref info,0)!=shape.Height)return null;}
        finally{DeleteDC(dc);}
        if(!Array.Exists(pixels,pixel=>(pixel>>24)!=0))
            for(int i=0;i<pixels.Length;i++)pixels[i]|=unchecked((int)0xff000000);
        var output=new Bitmap(shape.Width,shape.Height,PixelFormat.Format32bppPArgb);
        try{
            var bits=output.LockBits(new Rectangle(0,0,shape.Width,shape.Height),ImageLockMode.WriteOnly,output.PixelFormat);
            try{Marshal.Copy(pixels,0,bits.Scan0,pixels.Length);}
            finally{output.UnlockBits(bits);}
            return output;
        }catch{output.Dispose();throw;}
    }
    internal static void DrawFallback(Graphics graphics,Rectangle area,string title){
        var saved=graphics.Save();
        graphics.TranslateTransform(area.Left,area.Top);
        graphics.ScaleTransform(area.Width/24f,area.Height/24f);
        graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var pen=new Pen(Color.FromArgb(230,230,235),1.6f)){
            if(title=="Настройки"){
                graphics.DrawEllipse(pen,5,5,14,14);
                graphics.DrawEllipse(pen,9,9,6,6);
                for(int i=0;i<8;i++){
                    double angle=i*Math.PI/4;
                    float x=(float)Math.Cos(angle),y=(float)Math.Sin(angle);
                    graphics.DrawLine(pen,12+7*x,12+7*y,12+10*x,12+10*y);
                }
            }else{
                graphics.DrawRectangle(pen,3,4,18,16);
                graphics.DrawLine(pen,3,9,21,9);
            }
        }
        graphics.Restore(saved);
    }
}
