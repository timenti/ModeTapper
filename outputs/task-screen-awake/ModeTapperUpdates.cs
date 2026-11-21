using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal sealed class ModeTapperUpdates : IDisposable
{
    internal const string ResultFile = "modetapper-update-result.txt";
    internal const string ReleasesPage = "https://github.com/timenti/ModeTapper/releases/latest";
    const string Api = "https://api.github.com/repos/timenti/ModeTapper/releases/latest";
    const int MaximumBytes = 64 * 1024 * 1024;
    static readonly HttpClient Client = new HttpClient {
        Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 256 * 1024
    };
    static readonly Version Current = typeof(ModeTapperUpdates).Assembly.GetName().Version;
    readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    readonly string installed;
    Update available;
    bool busy;
    internal event Action Changed;
    internal string Status { get; private set; }
    internal bool ManualAvailable { get; private set; }
    internal bool CanAct { get { return !busy; } }
    internal string Button { get { return available == null ? "Проверить обновления" : "Обновить до " + available.Version.ToString(3); } }

    internal ModeTapperUpdates(string folder, bool preview)
    {
        installed = Path.Combine(folder, "ModeTapper.exe");
        Status = "Текущая версия " + Current.ToString(3);
        if (preview) return;
        string result = Path.Combine(folder, ResultFile);
        try {
            if (!File.Exists(result)) return;
            Status = File.ReadAllText(result);
            ManualAvailable = Status.StartsWith("Ошибка", StringComparison.Ordinal);
            File.Delete(result);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal async void Run()
    {
        if (busy || lifetime.IsCancellationRequested) return;
        var token = lifetime.Token;
        busy = true;
        ManualAvailable = false;
        bool installing = available != null;
        SetStatus(installing ? "Скачиваем установщик…" : "Проверяем версии на GitHub…");
        try {
            if (!installing) {
                Update candidate;
                using (var request = Request(Api))
                using (var response = await Client.SendAsync(request, token)) {
                    response.EnsureSuccessStatusCode();
                    candidate = ReadRelease(await response.Content.ReadAsStringAsync(), Current);
                }
                if (candidate == null) SetStatus("Уже установлена актуальная версия.");
                else {
                    bool canUpdate = await Task.Run(() => ManagedInstall() && UpdatePublisher.IsTrusted(installed), token);
                    if (canUpdate) {
                        available = candidate;
                        SetStatus("Доступна версия " + candidate.Version.ToString(3) + ".");
                    }
                    else {
                        ManualAvailable = true;
                        SetStatus("Доступна " + candidate.Version.ToString(3) + ". Скачайте установщик вручную.");
                    }
                }
            }
            else {
                var progress = new Progress<int>(percent => SetStatus("Скачиваем установщик · " + percent + "%"));
                string package = await Download(available, installed, progress, token);
                token.ThrowIfCancellationRequested();
                SetStatus("Устанавливаем обновление…");
                using (var child = Process.Start(new ProcessStartInfo(package, "--install-update " + Process.GetCurrentProcess().Id) {
                    UseShellExecute = false, CreateNoWindow = true
                })) {
                    if (child == null) throw new IOException("Не удалось открыть установщик.");
                    await Task.Run(() => child.WaitForExit(), token);
                    if (child.ExitCode != 0) throw new IOException("Установщик не смог начать обновление.");
                }
            }
        }
        catch (Exception ex) {
            if (token.IsCancellationRequested) return;
            available = null;
            ManualAvailable = true;
            try { Safety.Audit("update", null, null, ex.Message); } catch { }
            SetStatus(installing ? "Ошибка обновления. Доступно ручное скачивание." : "Не удалось проверить обновления.");
        }
        finally {
            busy = false;
            if (!token.IsCancellationRequested && Changed != null) Changed();
        }
    }

    bool ManagedInstall()
    {
        string registered = Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Software\ModeTapper", "InstallLocation", ""));
        string folder = Path.GetDirectoryName(installed);
        return String.Equals(registered, folder, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(folder, "modetapper-managed-install.txt")) &&
            String.Equals(installed, System.Windows.Forms.Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    internal void OpenRelease()
    {
        try { using (Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true })) { } }
        catch (System.ComponentModel.Win32Exception) { SetStatus("Не удалось открыть страницу GitHub."); }
    }

    void SetStatus(string status)
    {
        if (lifetime.IsCancellationRequested) return;
        Status = status;
        if (Changed != null) Changed();
    }

    static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ModeTapper/" + Current.ToString(3));
        return request;
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
        var asset = (release.assets ?? new Asset[0]).SingleOrDefault(item => item.name == "ModeTapper.exe");
        if (asset == null || asset.size <= 0 || asset.size > MaximumBytes ||
            asset.digest == null || !Regex.IsMatch(asset.digest, @"\Asha256:[a-fA-F0-9]{64}\z"))
            throw new InvalidDataException("В релизе отсутствует установщик с SHA-256.");
        return new Update {
            Version = version, Length = asset.size, Digest = asset.digest.Substring(7),
            Url = "https://github.com/timenti/ModeTapper/releases/download/" + tag + "/ModeTapper.exe"
        };
    }

    static async Task<string> Download(Update update, string installed, IProgress<int> progress, CancellationToken token)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ModeTapper-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "ModeTapper.exe");
        bool verified = false;
        try {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = Request(update.Url)) {
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false)) {
                    response.EnsureSuccessStatusCode();
                    long? length = response.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value != update.Length)
                        throw new InvalidDataException("Размер установщика отличается от размера в релизе.");
                    using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        await SaveVerified(input, file, update, progress, deadline.Token).ConfigureAwait(false);
                }
            }
            var metadata = FileVersionInfo.GetVersionInfo(file);
            Version version;
            if (metadata.ProductName != "ModeTapper" || !Version.TryParse(metadata.FileVersion, out version) || version != update.Version ||
                !UpdatePublisher.MatchesInstalledPublisher(installed, file))
                throw new InvalidDataException("Не удалось подтвердить версию или издателя установщика.");
            verified = true;
            return file;
        }
        finally {
            if (!verified) {
                try { File.Delete(file); Directory.Delete(folder); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static async Task SaveVerified(Stream input, string file, Update update, IProgress<int> progress, CancellationToken token)
    {
        using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        using (var sha = SHA256.Create()) {
            var buffer = new byte[65536];
            long total = 0;
            int read, shown = -1;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0) {
                total += read;
                if (total > update.Length) throw new InvalidDataException("Установщик превышает размер в релизе.");
                sha.TransformBlock(buffer, 0, read, buffer, 0);
                await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                int percent = (int)(total * 100 / update.Length);
                if (percent != shown) { progress.Report(percent); shown = percent; }
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            string digest = BitConverter.ToString(sha.Hash).Replace("-", "");
            if (total != update.Length || !String.Equals(digest, update.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма установщика не совпала.");
        }
    }

    internal sealed class Update
    {
        internal Version Version;
        internal long Length;
        internal string Digest, Url;
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
        public string digest { get; set; }
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
