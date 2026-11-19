using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class ProxySnapshot { public int Flags {get;set;} public string Server {get;set;} public string Bypass {get;set;} public string AutoUrl {get;set;} }
internal static class WindowsProxy {
    const string KeyPath="Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
    [StructLayout(LayoutKind.Explicit)] struct Value {[FieldOffset(0)]public int Number;[FieldOffset(0)]public IntPtr Text;}
    [StructLayout(LayoutKind.Sequential)] struct Option {public int Id;public Value Data;}
    [StructLayout(LayoutKind.Sequential)] struct Options {public int Size;public IntPtr Connection;public int Count;public int Error;public IntPtr Items;}
    [DllImport("wininet.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool InternetQueryOption(IntPtr session,int option,ref Options options,ref int size);
    [DllImport("wininet.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool InternetSetOption(IntPtr session,int option,ref Options options,int size);
    [DllImport("wininet.dll",SetLastError=true)] static extern bool InternetSetOption(IntPtr session,int option,IntPtr data,int size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr p);
    internal static ProxySnapshot Query(){
        int itemSize=Marshal.SizeOf(typeof(Option));IntPtr mem=Marshal.AllocHGlobal(itemSize*4);
        try{
            for(int i=0;i<4;i++)Marshal.StructureToPtr(new Option{Id=i+1},IntPtr.Add(mem,i*itemSize),false);
            var opts=new Options{Size=Marshal.SizeOf(typeof(Options)),Count=4,Items=mem};int bytes=opts.Size;
            if(!InternetQueryOption(IntPtr.Zero,75,ref opts,ref bytes))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var s=new ProxySnapshot();
            for(int i=0;i<4;i++){
                var o=(Option)Marshal.PtrToStructure(IntPtr.Add(mem,i*itemSize),typeof(Option));
                if(i==0)s.Flags=o.Data.Number;
                else{string value=o.Data.Text==IntPtr.Zero?null:Marshal.PtrToStringUni(o.Data.Text);if(i==1)s.Server=value;else if(i==2)s.Bypass=value;else s.AutoUrl=value;if(o.Data.Text!=IntPtr.Zero)GlobalFree(o.Data.Text);}
            }return s;
        }finally{Marshal.FreeHGlobal(mem);}
    }
    internal static string Canonical(ProxySnapshot s){return new JavaScriptSerializer().Serialize(new ProxySnapshot{Flags=s.Flags,Server=s.Server??"",Bypass=s.Bypass??"",AutoUrl=s.AutoUrl??""});}
    internal static void Apply(ProxySnapshot s){var serializer=new JavaScriptSerializer();Safety.Change("proxy",()=>Canonical(Query()),value=>ApplyRaw(serializer.Deserialize<ProxySnapshot>(value)),Canonical(s));}
    internal static void ApplyRaw(ProxySnapshot s){
        int itemSize=Marshal.SizeOf(typeof(Option));IntPtr mem=Marshal.AllocHGlobal(itemSize*4);var strings=new List<IntPtr>();
        try{
            string[] values={s.Server,s.Bypass,s.AutoUrl};
            for(int i=0;i<4;i++){
                var o=new Option{Id=i+1};if(i==0)o.Data.Number=s.Flags;else{o.Data.Text=String.IsNullOrEmpty(values[i-1])?IntPtr.Zero:Marshal.StringToHGlobalUni(values[i-1]);if(o.Data.Text!=IntPtr.Zero)strings.Add(o.Data.Text);}
                Marshal.StructureToPtr(o,IntPtr.Add(mem,i*itemSize),false);
            }
            var opts=new Options{Size=Marshal.SizeOf(typeof(Options)),Count=4,Items=mem};
            if(!InternetSetOption(IntPtr.Zero,75,ref opts,opts.Size))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            InternetSetOption(IntPtr.Zero,39,IntPtr.Zero,0);InternetSetOption(IntPtr.Zero,37,IntPtr.Zero,0);
        }finally{foreach(var p in strings)Marshal.FreeHGlobal(p);Marshal.FreeHGlobal(mem);}
    }
    internal static RegistryKey OpenWatchKey(){return Registry.CurrentUser.OpenSubKey(KeyPath,false);}
}
internal sealed class PacServer : IDisposable {
    readonly TcpListener listener;volatile byte[] bytes;
    internal PacServer(byte[] body){bytes=body;listener=new TcpListener(IPAddress.Loopback,18600);listener.Start(16);listener.BeginAcceptTcpClient(Accept,null);}
    internal void Update(byte[] body){bytes=body;}
    void Accept(IAsyncResult result){
        TcpClient client;
        try{client=listener.EndAcceptTcpClient(result);listener.BeginAcceptTcpClient(Accept,null);}catch(ObjectDisposedException){return;}catch(SocketException){return;}
        using(client)try{
            client.ReceiveTimeout=2000;client.SendTimeout=2000;client.NoDelay=true;
            using(var stream=client.GetStream()){
                byte[] head=new byte[8192];int used=0;
                while(used<head.Length){int n=stream.Read(head,used,head.Length-used);if(n<=0)return;used+=n;if(Encoding.ASCII.GetString(head,0,used).Contains("\r\n\r\n"))break;}
                string request=Encoding.ASCII.GetString(head,0,used);string first=request.Split('\n')[0];
                bool good=first.StartsWith("GET /proxy.pac",StringComparison.Ordinal)||first.StartsWith("HEAD /proxy.pac",StringComparison.Ordinal);
                byte[] body=good?bytes:Encoding.UTF8.GetBytes("Not found");
                string header="HTTP/1.1 "+(good?"200 OK":"404 Not Found")+"\r\nContent-Type: "+(good?"application/x-ns-proxy-autoconfig":"text/plain")+"\r\nContent-Length: "+body.Length+"\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
                byte[] response=Encoding.ASCII.GetBytes(header);stream.Write(response,0,response.Length);if(!first.StartsWith("HEAD ",StringComparison.Ordinal))stream.Write(body,0,body.Length);
            }
        }catch(IOException){}catch(SocketException){}
    }
    public void Dispose(){listener.Stop();}
}
internal static class HappLoopback {
    internal const string DefaultRoute="PROXY 127.0.0.1:10809; SOCKS5 127.0.0.1:10808";
    [DllImport("iphlpapi.dll")]static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int kind,uint reserved);
    [DllImport("kernel32.dll")]static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder image,ref int length);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    sealed class Listener {internal IPAddress Address;internal int Port,Owner;}
    static IEnumerable<Listener> ReadListeners(int family){
        const int listenerTable=3;
        int size=0;
        if(GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,listenerTable,0)!=122||size<4)return new Listener[0];
        IntPtr buffer=Marshal.AllocHGlobal(size);
        try{
            if(GetExtendedTcpTable(buffer,ref size,false,family,listenerTable,0)!=0)return new Listener[0];
            bool ipv4=family==2;
            int stride=ipv4?24:56,count=Math.Min(Marshal.ReadInt32(buffer),(size-4)/stride);
            var rows=new List<Listener>();
            for(int i=0;i<count;i++){
                int row=4+i*stride,portOffset=row+(ipv4?8:20);
                byte[] address=new byte[ipv4?4:16];
                Marshal.Copy(IntPtr.Add(buffer,row+(ipv4?4:0)),address,0,address.Length);
                var ip=new IPAddress(address);
                if(ip.Equals(IPAddress.Any))ip=IPAddress.Loopback;
                else if(ip.Equals(IPAddress.IPv6Any))ip=IPAddress.IPv6Loopback;
                if(!IPAddress.IsLoopback(ip))continue;
                int port=Marshal.ReadByte(buffer,portOffset)*256+Marshal.ReadByte(buffer,portOffset+1);
                if(port>0)rows.Add(new Listener{Address=ip,Port=port,Owner=Marshal.ReadInt32(buffer,row+(ipv4?20:52))});
            }
            return rows;
        }finally{Marshal.FreeHGlobal(buffer);}
    }
    static bool IsHappOwner(int pid){
        if(pid<=0)return false;
        try{
            using(var process=Process.GetProcessById(pid)){
                switch(process.ProcessName.ToLowerInvariant()){
                    case "happ":case "happd":case "xray":break;
                    default:return false;
                }
            }
        }catch(ArgumentException){return false;}
        catch(InvalidOperationException){return false;}
        IntPtr handle=OpenProcess(0x1000,false,pid);
        if(handle==IntPtr.Zero)return false;
        try{
            var image=new StringBuilder(512);int length=image.Capacity;
            if(!QueryFullProcessImageName(handle,0,image,ref length))return false;
            string path=image.ToString();
            return path.EndsWith(@"\Happ\Happ.exe",StringComparison.OrdinalIgnoreCase)||
                path.EndsWith(@"\Happ\happd.exe",StringComparison.OrdinalIgnoreCase)||
                path.EndsWith(@"\Happ\core\xray.exe",StringComparison.OrdinalIgnoreCase);
        }finally{CloseHandle(handle);}
    }
    static string Route(Listener listener,bool socks){
        string host=listener.Address.AddressFamily==AddressFamily.InterNetworkV6?"["+listener.Address+"]":listener.Address.ToString();
        return (socks?"SOCKS5 ":"PROXY ")+host+":"+listener.Port;
    }
    static string Probe(Listener listener,bool socks){
        try{
            using(var client=new TcpClient(listener.Address.AddressFamily)){
                var connect=client.BeginConnect(listener.Address,listener.Port,null,null);
                using(var ready=connect.AsyncWaitHandle){
                    if(!ready.WaitOne(160))return null;
                    client.EndConnect(connect);
                }
                client.ReceiveTimeout=160;client.SendTimeout=160;
                using(var stream=client.GetStream()){
                    byte[] request=socks?new byte[]{5,1,0}:Encoding.ASCII.GetBytes("OPTIONS * HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
                    stream.Write(request,0,request.Length);
                    if(socks){
                        if(stream.ReadByte()!=5||stream.ReadByte()!=0)return null;
                    }else foreach(char letter in "HTTP/")if(stream.ReadByte()!=letter)return null;
                }
                return Route(listener,socks);
            }
        }catch(SocketException){}catch(IOException){}
        return null;
    }
    internal static string Detect(){
        var owners=new Dictionary<int,bool>();
        var known=new List<Listener>();
        foreach(var endpoint in ReadListeners(2).Concat(ReadListeners(23))){
            bool ours;
            if(!owners.TryGetValue(endpoint.Owner,out ours))owners[endpoint.Owner]=ours=IsHappOwner(endpoint.Owner);
            if(ours)known.Add(endpoint);
        }
        string http=null,socks=null;
        foreach(var endpoint in known.OrderBy(e=>e.Port!=10809&&e.Port!=10808)){
            if(http==null&&endpoint.Port==10809)http=Route(endpoint,false);
            if(socks==null&&endpoint.Port==10808)socks=Probe(endpoint,true);
            if(endpoint.Port!=10809&&endpoint.Port!=10808){
                if(socks==null)socks=Probe(endpoint,true);
                if(socks==null||!socks.EndsWith(":"+endpoint.Port,StringComparison.Ordinal))
                    if(http==null)http=Probe(endpoint,false);
            }
            if(http!=null&&socks!=null)break;
        }
        return http==null?socks:socks==null?http:http+"; "+socks;
    }
}
internal sealed class VpnController : IDisposable {
    internal const string BaseUrl="http://127.0.0.1:18600/proxy.pac";
    readonly string folder;readonly Control dispatcher;
    readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024};
    readonly AutoResetEvent registryEvent=new AutoResetEvent(false);
    RegistryKey watchKey;RegisteredWaitHandle registryWait;FileSystemWatcher files;PacServer server;ProxySnapshot prior;
    internal bool Enabled {get;private set;}internal bool Waiting {get;private set;}internal string Error {get;private set;}
    readonly System.Windows.Forms.Timer availability=new System.Windows.Forms.Timer();
    internal event Action Changed;volatile bool ending;string currentUrl="",route;int pacSize,revision;
    bool scanning,pacNeedsApply;
    [DllImport("advapi32.dll")]static extern int RegNotifyChangeKeyValue(IntPtr key,bool subtree,uint filter,IntPtr change,bool async);
    internal string RulesPath {get{return Path.Combine(folder,"custom-rules.json");}}
    internal VpnController(string directory,Control owner){
        folder=directory;dispatcher=owner;Error="";
        try{
            prior=WindowsProxy.Query();string backup=Path.Combine(folder,"previous-proxy.json");
            if(IsOurs(prior.AutoUrl)&&File.Exists(backup))prior=json.Deserialize<ProxySnapshot>(File.ReadAllText(backup));
            else File.WriteAllText(backup,json.Serialize(prior));
            server=new PacServer(BuildRules());
            watchKey=WindowsProxy.OpenWatchKey();ArmRegistry();registryWait=ThreadPool.RegisterWaitForSingleObject(registryEvent,(s,t)=>{ArmRegistry();Post(CheckProxy);},null,Timeout.Infinite,false);
            files=new FileSystemWatcher(folder,"custom-rules.json"){NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.FileName};
            files.Changed+=(s,e)=>Post(Reload);files.Created+=(s,e)=>Post(Reload);files.Renamed+=(s,e)=>Post(Reload);files.EnableRaisingEvents=true;
            availability.Interval=1800;availability.Tick+=(s,e)=>CheckProxy();
            SetEnabled(!File.Exists(Path.Combine(folder,"disabled.flag")));
        }catch{Dispose();throw;}
    }
    void Post(Action action){if(!ending&&!dispatcher.IsDisposed)try{dispatcher.BeginInvoke(action);}catch(InvalidOperationException){}}
    void ArmRegistry(){if(ending||watchKey==null)return;try{RegNotifyChangeKeyValue(watchKey.Handle.DangerousGetHandle(),false,0x10000004,registryEvent.SafeWaitHandle.DangerousGetHandle(),true);}catch(ObjectDisposedException){}}
    internal static bool IsOurs(string s){return s!=null&&s.StartsWith(BaseUrl,StringComparison.OrdinalIgnoreCase);}
    byte[] BuildRules(){return BuildRules(route);}
    byte[] BuildRules(string selectedRoute){
        var config=json.Deserialize<Dictionary<string,string[]>>(File.ReadAllText(RulesPath));
        var direct=new Dictionary<string,int>();var proxy=new Dictionary<string,int>();
        foreach(string kind in new[]{"direct","proxy"}){string[] list;if(!config.TryGetValue(kind,out list)||list==null)continue;foreach(string raw in list){string host=raw.Trim().TrimEnd('.').ToLowerInvariant();if(host.Length==0)continue;if(host.Contains("://")||host.Contains("/")||host.Contains(" "))throw new InvalidDataException("Нужны домены без https:// и пути: "+host);(kind=="direct"?direct:proxy)[host]=1;}}
        string text=File.ReadAllText(Path.Combine(folder,"rules.template.pac")).Replace("__CUSTOM_DIRECT__",json.Serialize(direct)).Replace("__CUSTOM_PROXY__",json.Serialize(proxy));
        if(selectedRoute!=null){
            if(!text.Contains(HappLoopback.DefaultRoute))throw new InvalidDataException("В шаблоне ModeTapper не найдена строка прокси Happ.");
            text=text.Replace(HappLoopback.DefaultRoute,selectedRoute);
        }
        byte[] body=Encoding.UTF8.GetBytes(text);pacSize=body.Length;return body;
    }
    void ApplyPac(){
        var url=BaseUrl+"?v="+DateTime.UtcNow.Ticks;
        WindowsProxy.Apply(new ProxySnapshot{Flags=5,AutoUrl=url,Server=prior.Server,Bypass=prior.Bypass});
        currentUrl=url;pacNeedsApply=false;
    }
    void Restore(){if(prior!=null&&IsOurs(WindowsProxy.Query().AutoUrl)){WindowsProxy.ApplyRaw(prior);Safety.Audit("proxy.restore",null,null,"restored prior owned PAC");}}
    internal void SetEnabled(bool value){
        try{
            revision++;
            if(value){Enabled=true;Waiting=true;CheckProxy();availability.Start();if(File.Exists(Path.Combine(folder,"disabled.flag")))File.Delete(Path.Combine(folder,"disabled.flag"));}
            else{availability.Stop();Restore();Enabled=false;Waiting=false;route=null;File.WriteAllText(Path.Combine(folder,"disabled.flag"),"");}
            Report("");
        }catch(Exception ex){Report(ex.Message);}
    }
    void CheckProxy(){
        if(!Enabled||ending||scanning)return;
        scanning=true;int request=revision;
        ThreadPool.QueueUserWorkItem(_=>{
            string result=null,error=null;
            try{result=HappLoopback.Detect();}catch(Exception ex){error=ex.Message;}
            Post(()=>{
                scanning=false;
                if(!Enabled||request!=revision)return;
                try{UpdateRoute(result);if(error!=null)Report(error);}
                catch(Exception ex){Report(ex.Message);}
            });
        });
    }
    void UpdateRoute(string next){
        bool changed=next!=route,wasWaiting=Waiting;
        var now=WindowsProxy.Query();
        if(changed&&next!=null){server.Update(BuildRules(next));pacNeedsApply=true;}
        route=next;
        Waiting=route==null;
        if(Waiting)Restore();
        else if(String.IsNullOrEmpty(now.AutoUrl)||IsOurs(now.AutoUrl)){
            if(!IsOurs(now.AutoUrl)||pacNeedsApply||(now.Flags&4)==0)ApplyPac();
        }else{
            Report("Windows уже использует другой сценарий прокси.");
            return;
        }
        if(changed||wasWaiting!=Waiting||Error.Length!=0)Report("");
    }
    internal void Reload(){if(ending)return;try{
        server.Update(BuildRules());
        if(Enabled&&route!=null){var now=WindowsProxy.Query();if(String.IsNullOrEmpty(now.AutoUrl)||IsOurs(now.AutoUrl))ApplyPac();}
        Report("");CheckProxy();
    }catch(Exception ex){Report(ex.Message);}}
    void Report(string error){Error=error;try{File.WriteAllText(Path.Combine(folder,"status.json"),json.Serialize(new{enabled=Enabled,waitingForHapp=Waiting,pacBytes=pacSize,pacUrl=currentUrl,processId=Process.GetCurrentProcess().Id,error=error,updatedUtc=DateTime.UtcNow.ToString("o")}));}catch{}if(Changed!=null)Changed();}
    public void Dispose(){
        if(ending)return;ending=true;availability.Stop();availability.Dispose();
        if(files!=null)files.Dispose();if(registryWait!=null)registryWait.Unregister(null);
        try{Restore();Enabled=false;Report("");}catch(Exception ex){Report(ex.Message);}
        if(server!=null)server.Dispose();if(watchKey!=null)watchKey.Dispose();registryEvent.Dispose();
    }
}
