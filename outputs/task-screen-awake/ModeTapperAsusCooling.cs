using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal enum AsusCoolingPreset { Performance=0, Turbo=1, Silent=2 }
internal enum AsusCoolingAvailability { DriverUnavailable, LockedByAsus, Ready, ReadyWithPostureMismatch }

internal static class AsusCooling {
    const uint ReadStatus=0x53545344, SetStatus=0x53564544;
    const uint ThermalPolicy=0x00120075, DriverControl=0x0022240C;
    const int ConvertibleSlateMode=0x2003;
    const string ArmouryCrate=@"shell:AppsFolder\B9ECED6F.ArmouryCrate_qmba6cd70vzyy!App";
    const string ArmouryProfile=@"HKEY_LOCAL_MACHINE\SOFTWARE\ASUS\Armoury Crate Service\ThrottlePlugin\External";

    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool DeviceIoControl(SafeFileHandle handle,uint control,uint[] input,uint inputSize,[Out]uint[] output,uint outputSize,out uint returned,IntPtr overlapped);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int metric);

    internal static bool IsSupportedModel(){
        string product=Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS","SystemProductName",""));
        return product.StartsWith("ROG Flow Z13 GZ302EA",StringComparison.OrdinalIgnoreCase);
    }

    static SafeFileHandle Open(){
        var handle=CreateFile(@"\\.\ATKACPI",0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
        if(!handle.IsInvalid)return handle;
        int error=Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    static uint Call(SafeFileHandle handle,uint method,uint value){
        uint[] request={method,8,ThermalPolicy,value};
        var response=new uint[4];
        uint returned;
        if(!DeviceIoControl(handle,DriverControl,request,16,response,16,out returned,IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if(returned<4)throw new InvalidOperationException("Драйвер ASUS вернул неполный ответ.");
        return response[0];
    }

    static bool HasThermalPolicy(SafeFileHandle handle){
        uint value=Call(handle,ReadStatus,0);
        return (value&0xFFFF0000)==0x00010000;
    }

    static bool IsLocked(){
        object value=Registry.GetValue(ArmouryProfile,"IsLocked",0);
        if(value==null)return false;
        if(value is int)return (int)value!=0;
        throw new InvalidOperationException("Не удалось прочитать состояние профилей ASUS.");
    }

    internal static bool CanSelect(AsusCoolingAvailability availability){
        return availability==AsusCoolingAvailability.Ready||availability==AsusCoolingAvailability.ReadyWithPostureMismatch;
    }

    static AsusCoolingAvailability ResolveAvailability(){
        if(!IsLocked())return AsusCoolingAvailability.Ready;
        if(GetSystemMetrics(ConvertibleSlateMode)==0)return AsusCoolingAvailability.LockedByAsus;
        var screen=Screen.PrimaryScreen;
        if(screen==null||screen.Bounds.Width<=screen.Bounds.Height)return AsusCoolingAvailability.LockedByAsus;
        int rotation=DisplayOrientation.Read(screen.DeviceName);
        // A landscape desktop and matching hardware sensor distinguish a stale Armoury Crate flag.
        if((rotation==0||rotation==2)&&RotationLock.ReadSensor()==rotation)
            return AsusCoolingAvailability.ReadyWithPostureMismatch;
        return AsusCoolingAvailability.LockedByAsus;
    }

    internal static AsusCoolingAvailability Availability(){
        try{
            var availability=ResolveAvailability();
            if(!CanSelect(availability))return availability;
            using(var handle=Open()){
                return HasThermalPolicy(handle)?availability:AsusCoolingAvailability.DriverUnavailable;
            }
        }catch(Win32Exception){return AsusCoolingAvailability.DriverUnavailable;}
        catch(InvalidOperationException){return AsusCoolingAvailability.DriverUnavailable;}
        catch(IOException){return AsusCoolingAvailability.DriverUnavailable;}
        catch(UnauthorizedAccessException){return AsusCoolingAvailability.DriverUnavailable;}
        catch(SecurityException){return AsusCoolingAvailability.DriverUnavailable;}
    }

    internal static int Select(AsusCoolingPreset preset){
        Safety.Require();
        if(!IsSupportedModel()||!Enum.IsDefined(typeof(AsusCoolingPreset),preset))
            throw new InvalidOperationException("Профиль охлаждения недоступен.");
        if(preset==AsusCoolingPreset.Turbo&&!PowerModes.OnAC)
            throw new InvalidOperationException("Турбо доступен при подключении к сети.");
        using(var handle=Open()){
            var availability=ResolveAvailability();
            if(!CanSelect(availability))throw new InvalidOperationException("ASUS ограничила смену профиля. Проверьте Armoury Crate.");
            if(!HasThermalPolicy(handle))throw new InvalidOperationException("Драйвер ASUS не поддерживает переключение профиля.");
            Safety.Audit("asus.cooling",null,preset.ToString(),availability==AsusCoolingAvailability.ReadyWithPostureMismatch?
                "intent with confirmed desktop posture":"intent");
            if(Call(handle,SetStatus,(uint)preset)!=1){
                Safety.Audit("asus.cooling",null,preset.ToString(),"driver rejected preset");
                throw new InvalidOperationException("Драйвер ASUS отклонил профиль.");
            }
            // On GZ302EA, DSTS confirms the endpoint is present and does not report the selected mode.
            Safety.Audit("asus.cooling",null,preset.ToString(),"driver accepted preset");
            return (int)preset;
        }
    }

    internal static void OpenArmouryCrate(){Process.Start("explorer.exe",ArmouryCrate);}
}
