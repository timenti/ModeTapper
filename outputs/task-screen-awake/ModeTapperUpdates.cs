using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class ModeTapperUpdates : IDisposable
{
    internal const string ReleasesPage = "https://github.com/timenti/ModeTapper/releases/latest";
    const string Api = "https://api.github.com/repos/timenti/ModeTapper/releases/latest";
    static readonly HttpClient Client = new HttpClient {
        Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 256 * 1024
    };
    static readonly Version Current = typeof(ModeTapperUpdates).Assembly.GetName().Version;
    readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    bool busy;
    Update available;
    string statusEnglish="";
    internal event Action Changed;
    internal string Status { get; private set; }
    internal bool ManualAvailable { get; private set; }
    internal bool CanAct { get { return !busy; } }
    internal string Button { get { return busy ? "Проверяем…" : available != null ? "Обновить" : "Проверить обновления"; } }
    internal string DisplayStatus(bool english){return english?statusEnglish:Status;}

    internal ModeTapperUpdates() { Status = ""; }

    internal async void Run()
    {
        if (busy || lifetime.IsCancellationRequested) return;
        if (available != null) {
            if (Open(available.Url))
                SetStatus("Откройте скачанный установщик.", "Open the downloaded installer.");
            return;
        }
        var token = lifetime.Token;
        busy = true;
        ManualAvailable = false;
        SetStatus("Проверяем версии на GitHub…","Checking GitHub releases…");
        try {
            var check = CheckAsync(token);
            await Task.WhenAll(check, Task.Delay(1100, token));
            token.ThrowIfCancellationRequested();
            available = await check;
            if (available == null) {
                SetStatus("Уже установлена актуальная версия.","ModeTapper is up to date.");
                return;
            }

            string next = available.Version.ToString(3);
            SetStatus("Доступна версия " + next + ".", "Version " + next + " is available.");
        }
        catch (Exception ex) {
            if (token.IsCancellationRequested) return;
            ManualAvailable = true;
            try {
                var cause=ex.GetBaseException();
                Safety.Audit("update",null,null,cause.GetType().Name+": "+cause.Message);
            }catch{}
            SetStatus("Не удалось проверить обновления.", "Could not check for updates.");
        }
        finally {
            busy = false;
            if (!token.IsCancellationRequested && Changed != null) Changed();
        }
    }

    internal void OpenRelease() { Open(ReleasesPage); }

    bool Open(string url)
    {
        try {
            using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
            return true;
        }
        catch (System.ComponentModel.Win32Exception) {
            SetStatus("Не удалось открыть GitHub.", "Could not open GitHub.");
            return false;
        }
    }

    void SetStatus(string status,string english)
    {
        if (lifetime.IsCancellationRequested) return;
        Status = status;
        statusEnglish=english;
        if (Changed != null) Changed();
    }

    static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ModeTapper/" + Current.ToString(3));
        return request;
    }

    internal static async Task<Update> CheckAsync(CancellationToken token)
    {
        using(var request=Request(Api))
        using(var response=await Client.SendAsync(request,token).ConfigureAwait(false)){
            response.EnsureSuccessStatusCode();
            return ReadRelease(await response.Content.ReadAsStringAsync().ConfigureAwait(false),Current);
        }
    }

    internal static Update ReadRelease(string json, Version current)
    {
        var release = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 }.Deserialize<Release>(json);
        if (release == null || release.draft || release.prerelease) return null;
        Version parsed;
        string tag = release.tag_name;
        if (String.IsNullOrEmpty(tag) || tag[0] != 'v' || !Version.TryParse(tag.Substring(1), out parsed) ||
            parsed.Build < 0 || parsed.Revision != -1 || tag != "v" + parsed.ToString(3))
            throw new InvalidDataException("Не удалось прочитать версию релиза.");
        var version = new Version(parsed.Major, parsed.Minor, parsed.Build, 0);
        if (version <= current) return null;
        const string name = "ModeTapper.exe";
        if (release.assets == null || !release.assets.Any(asset => asset != null && asset.name == name && asset.size > 0))
            throw new InvalidDataException("В релизе отсутствует установщик ModeTapper.");
        return new Update {
            Version = version,
            Url = "https://github.com/timenti/ModeTapper/releases/download/" + tag + "/" + name
        };
    }

    internal sealed class Update
    {
        internal Version Version;
        internal string Url;
    }

    sealed class Release
    {
        public string tag_name { get; set; }
        public bool draft { get; set; }
        public bool prerelease { get; set; }
        public Asset[] assets { get; set; }
    }

    sealed class Asset
    {
        public string name { get; set; }
        public long size { get; set; }
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
