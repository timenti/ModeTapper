using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class ModeTapperDeviceSupport
{
    const string Bios=@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
    const string Windows=@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    readonly bool preview;
    internal readonly string Model, OperatingSystem;
    internal readonly bool Asus, AsusFlow;
    internal readonly bool LidPresent;
    internal bool Touchpad {get;private set;}
    internal bool TouchScreen {get;private set;}
    internal bool ArmouryInstalled {get;private set;}

    internal ModeTapperDeviceSupport(bool preview=false,bool desktopPreview=false)
    {
        this.preview=preview;
        if(preview){
            Model=desktopPreview?"Windows desktop PC":"ASUS ROG Flow Z13 GZ302EA";
            OperatingSystem="Windows 11";
            Asus=AsusFlow=!desktopPreview;
            LidPresent=!desktopPreview;
            Touchpad=TouchScreen=ArmouryInstalled=!desktopPreview;
            return;
        }
        string maker=Read(Bios,"SystemManufacturer").Trim();
        string product=Read(Bios,"SystemProductName").Trim();
        if(product=="System Product Name"||product=="To Be Filled By O.E.M."||product=="Default string")product="";
        bool flow=IsAsusFlow(product);
        bool asus=flow||IsAsusManufacturer(maker);
        if(flow)product="ROG Flow Z13 GZ302EA";
        if(asus)maker="ASUS";
        Model=String.IsNullOrEmpty(product)?maker:product.StartsWith(maker,StringComparison.OrdinalIgnoreCase)?product:(maker+" "+product).Trim();
        int build;
        string release=Read(Windows,"DisplayVersion");
        if(!Int32.TryParse(Read(Windows,"CurrentBuildNumber"),out build))build=Environment.OSVersion.Version.Build;
        OperatingSystem=(build>=22000?"Windows 11":"Windows 10")+(release.Length==0?"":" "+release)+
            " · "+(Environment.Is64BitOperatingSystem?"64-bit":"32-bit");
        AsusFlow=flow;
        Asus=asus;
        PowerCapabilities power;
        LidPresent=GetPwrCapabilities(out power)&&power.LidPresent!=0;
        Refresh();
    }

    internal static bool IsAsusFlow(string product)
    {
        return product.StartsWith("ROG Flow Z13 GZ302EA",StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAsusFlow()
    {
        return IsAsusFlow(Read(Bios,"SystemProductName"));
    }

    static bool IsAsusManufacturer(string maker)
    {
        return maker.StartsWith("ASUSTeK",StringComparison.OrdinalIgnoreCase)||
            maker.Equals("ASUS",StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAsus()
    {
        return IsAsusFlow()||IsAsusManufacturer(Read(Bios,"SystemManufacturer").Trim());
    }

    internal bool Refresh()
    {
        if(preview)return false;
        bool touchpad=RawTouchpad.HasUsage(5);
        bool touchscreen=HasTouchScreen();
        bool armoury=Asus&&HasPackage("B9ECED6F.ArmouryCrate_qmba6cd70vzyy");
        bool changed=Touchpad!=touchpad||TouchScreen!=touchscreen||ArmouryInstalled!=armoury;
        Touchpad=touchpad;TouchScreen=touchscreen;ArmouryInstalled=armoury;
        return changed;
    }

    internal static bool HasTouchScreen()
    {
        const int digitizer=94, integratedTouch=1, externalTouch=2, ready=0x80;
        int flags=GetSystemMetrics(digitizer);
        return (flags&ready)!=0&&(flags&(integratedTouch|externalTouch))!=0&&RawTouchpad.HasUsage(4);
    }

    internal static Screen TouchDisplay()
    {
        if(Screen.AllScreens.Length==1)return Screen.PrimaryScreen;
        uint count=0;
        if(!GetPointerDevices(ref count,null)||count==0)return Screen.PrimaryScreen;
        var devices=new PointerDevice[count];
        if(!GetPointerDevices(ref count,devices))return Screen.PrimaryScreen;
        const uint touch=3;
        IntPtr monitor=IntPtr.Zero;
        foreach(var device in devices){
            if(device.Type!=touch||device.Monitor==IntPtr.Zero)continue;
            if(monitor!=IntPtr.Zero&&monitor!=device.Monitor)return Screen.PrimaryScreen;
            monitor=device.Monitor;
        }
        foreach(var display in Screen.AllScreens){
            var bounds=display.Bounds;
            if(MonitorFromPoint(new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2),0)==monitor)
                return display;
        }
        return Screen.PrimaryScreen;
    }

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct PointerDevice {
        public uint Orientation;
        public IntPtr Device;
        public uint Type;
        public IntPtr Monitor;
        public uint StartingCursorId;
        public ushort MaxActiveContacts;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=520)]public string Product;
    }
    [DllImport("user32.dll")]static extern bool GetPointerDevices(ref uint count,[Out]PointerDevice[] devices);
    [DllImport("user32.dll")]static extern IntPtr MonitorFromPoint(Point point,uint flags);

    static bool HasPackage(string family)
    {
        uint count=0,length=0;
        int result=GetPackagesByPackageFamily(family,ref count,IntPtr.Zero,ref length,IntPtr.Zero);
        return (result==0||result==122)&&count!=0;
    }

    static string Read(string path,string name)
    {
        try{return Convert.ToString(Registry.GetValue(path,name,""),CultureInfo.InvariantCulture)??"";}
        catch(IOException){}
        catch(UnauthorizedAccessException){}
        catch(SecurityException){}
        return "";
    }

    [StructLayout(LayoutKind.Sequential,Size=128)]
    struct PowerCapabilities {public byte PowerButtonPresent,SleepButtonPresent,LidPresent;}
    [DllImport("user32.dll")]static extern int GetSystemMetrics(int index);
    [DllImport("powrprof.dll")][return:MarshalAs(UnmanagedType.U1)]
    static extern bool GetPwrCapabilities(out PowerCapabilities capabilities);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    static extern int GetPackagesByPackageFamily(string family,ref uint count,IntPtr names,ref uint length,IntPtr packages);
}
