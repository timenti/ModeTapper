using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Установка ModeTapper")]
[assembly: AssemblyProduct("ModeTapper")]
[assembly: AssemblyVersion("1.24.16.0")]

internal static class ModeTapperSetup
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--verify-package")
            return InstallFiles.Verify() ? 0 : 1;
        if (args.Length == 1 && args[0] == "--quiet")
            return InstallFiles.Run(InstallFiles.Existing ?? InstallFiles.DefaultPath, true, (step, text) => {}) == null ? 0 : 1;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var setup = new SetupWindow())
        {
            Application.Run(setup);
            return setup.Result;
        }
    }
}

internal sealed class SetupWindow : Form
{
    static readonly Color Background = Color.FromArgb(22, 22, 22);
    static readonly Color Surface = Color.FromArgb(30, 30, 30);
    static readonly Color Border = Color.FromArgb(56, 56, 56);
    static readonly Color TextColor = Color.FromArgb(247, 247, 247);
    static readonly Color Muted = Color.FromArgb(185, 185, 185);
    static readonly Color Green = Color.FromArgb(110, 211, 157);
    static readonly Color Red = Color.FromArgb(235, 125, 125);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    readonly string existing = InstallFiles.Existing;
    readonly TextBox path;
    readonly CheckBox cameras;
    readonly Label status;
    readonly Button browse, cancel, primary;
    readonly SetupProgress progress;
    readonly Font headingFont = new Font("Segoe UI", 24, FontStyle.Bold);
    readonly Font bodyFont = new Font("Segoe UI", 10);
    readonly Font detailFont = new Font("Segoe UI", 9);
    bool busy, complete;
    internal int Result { get; private set; }

    internal SetupWindow()
    {
        Text = "Установка ModeTapper";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(624, 450);
        Font = bodyFont;
        BackColor = Background;
        ForeColor = TextColor;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(30, 24, 30, 22) };
        foreach (int height in new[] { 58, 46, 26, 45, 57, 62, 28 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        header.Controls.Add(new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(40, 40), Margin = new Padding(0, 5, 13, 0), TabStop = false });
        header.Controls.Add(new Label { Text = "ModeTapper", Font = headingFont, ForeColor = TextColor, AutoSize = true, Margin = Padding.Empty });
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(Label(existing == null ? "Установка для текущего пользователя Windows" : "Обновление с сохранением настроек", Muted), 0, 1);
        layout.Controls.Add(Label("Папка установки", TextColor), 0, 2);

        var pathRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 114));
        path = new TextBox { Text = existing ?? InstallFiles.DefaultPath, BackColor = Surface, ForeColor = TextColor, Dock = DockStyle.Top,
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 5, 12, 0), ReadOnly = existing != null, AccessibleName = "Папка установки" };
        browse = Button("Обзор…", false);
        browse.Dock = DockStyle.Top;
        browse.Enabled = existing == null;
        browse.Click += Browse;
        pathRow.Controls.Add(path, 0, 0);
        pathRow.Controls.Add(browse, 1, 0);
        layout.Controls.Add(pathRow, 0, 3);

        var options = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty, Padding = new Padding(10, 3, 10, 3) };
        cameras = new CheckBox { Text = "Управление камерами", Checked = true, Dock = DockStyle.Top, Height = 27, ForeColor = TextColor };
        var cameraNote = Label("Windows запросит разрешение при переключении камер.", Muted);
        cameraNote.Font = detailFont;
        options.Controls.Add(cameraNote);
        options.Controls.Add(cameras);
        layout.Controls.Add(options, 0, 4);

        status = Label("Готово к установке", Muted);
        status.Margin = new Padding(0, 12, 0, 0);
        layout.Controls.Add(status, 0, 5);
        progress = new SetupProgress { Dock = DockStyle.Top, Height = 7, Margin = Padding.Empty, AccessibleName = "Ход установки" };
        layout.Controls.Add(progress, 0, 6);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        primary = Button(existing == null ? "Установить" : "Обновить", true);
        primary.Width = 158;
        primary.Click += Install;
        cancel = Button("Отмена", false);
        cancel.Width = 110;
        cancel.Margin = new Padding(0, 0, 10, 0);
        cancel.Click += (sender, e) => Close();
        actions.Controls.Add(primary);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 7);
        AcceptButton = primary;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int enabled = 1;
        DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
    }

    Label Label(string text, Color color)
    {
        return new Label { Text = text, ForeColor = color, Dock = DockStyle.Fill, Margin = Padding.Empty, UseMnemonic = false };
    }

    Button Button(string text, bool accent)
    {
        var button = new Button { Text = text, Height = 37, Margin = Padding.Empty, FlatStyle = FlatStyle.Flat,
            BackColor = accent ? Green : Surface, ForeColor = accent ? Background : TextColor, UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = accent ? Green : Border;
        button.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(143, 225, 180) : Color.FromArgb(42, 42, 42);
        return button;
    }

    void Browse(object sender, EventArgs e)
    {
        using (var dialog = new FolderBrowserDialog { Description = "Выберите папку, в которой будет создана папка ModeTapper", ShowNewFolderButton = true })
            if (dialog.ShowDialog(this) == DialogResult.OK)
                path.Text = String.Equals(Path.GetFileName(dialog.SelectedPath), "ModeTapper", StringComparison.OrdinalIgnoreCase)
                    ? dialog.SelectedPath : Path.Combine(dialog.SelectedPath, "ModeTapper");
    }

    async void Install(object sender, EventArgs e)
    {
        if (complete) { Close(); return; }
        busy = true;
        path.ReadOnly = true;
        primary.Enabled = cancel.Enabled = cameras.Enabled = browse.Enabled = false;
        status.ForeColor = Muted;
        progress.Reset();
        string destination = path.Text;
        bool camera = cameras.Checked;
        var updates = new Progress<KeyValuePair<int, string>>(update => {
            status.Text = update.Value;
            progress.Advance(update.Key);
        });
        var report = (IProgress<KeyValuePair<int, string>>)updates;
        string error = await Task.Run(() => InstallFiles.Run(destination, camera,
            (value, text) => report.Report(new KeyValuePair<int, string>(value, text))));
        busy = false;
        cancel.Enabled = primary.Enabled = true;
        if (error == null)
        {
            complete = true;
            Result = 0;
            status.ForeColor = Green;
            status.Text = "ModeTapper установлен. Панель доступна из меню «Пуск».";
            progress.Advance(100);
            primary.Text = "Готово";
            cancel.Visible = false;
        }
        else
        {
            Result = 1;
            status.ForeColor = Red;
            status.Text = error;
            progress.Reset();
            cameras.Enabled = true;
            path.ReadOnly = existing != null;
            browse.Enabled = existing == null;
            primary.Text = "Повторить";
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var icon = Icon;
            if (icon != null) icon.Dispose();
            headingFont.Dispose();
            bodyFont.Dispose();
            detailFont.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class SetupProgress : Control
    {
        readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 16 };
        double shown;
        int target;

        internal SetupProgress()
        {
            DoubleBuffered = true;
            animation.Tick += (sender, e) => {
                shown += (target - shown) * .16;
                if (Math.Abs(target - shown) < .2) { shown = target; animation.Stop(); }
                Invalidate();
            };
        }

        internal void Reset() { animation.Stop(); shown = target = 0; Invalidate(); }
        internal void Advance(int value) { target = value; animation.Start(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var track = new SolidBrush(Border))
            using (var fill = new SolidBrush(Green))
            {
                e.Graphics.FillRectangle(track, ClientRectangle);
                e.Graphics.FillRectangle(fill, 0, 0, (int)(Width * shown / 100), Height);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) animation.Dispose();
            base.Dispose(disposing);
        }
    }
}

internal static class InstallFiles
{
    const string AppKey = @"Software\ModeTapper";
    const string Marker = "modetapper-managed-install.txt";
    const string Program = "ModeTapper.exe";
    const string Camera = "ModeTapper-camera-control.exe";
    static readonly string Legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luma");
    internal static readonly string DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModeTapper");
    static readonly string[] Required = { Program, Program + ".config", "ModeTapper.ico", Camera, "LICENSE.txt", "rules.default.pac" };
    static readonly string[] Preferences = { "mode.txt", "power-preset.txt", "cooling-preset.txt", "touchpad-enabled.txt", "home-indicator-enabled.txt",
        "screen-edges-enabled.txt", "keyboard-level.txt", "touchpad-zoom-enabled.txt", "animation-plus-enabled.txt", "codex-widget.json", "desktop-widgets.json" };

    internal static string Existing
    {
        get
        {
            string saved = Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\" + AppKey, "InstallLocation", ""));
            if (!String.IsNullOrEmpty(saved) && File.Exists(Path.Combine(saved, Marker))) return saved;
            return File.Exists(Path.Combine(DefaultPath, Marker)) ? DefaultPath : null;
        }
    }

    static ZipArchive OpenPackage()
    {
        Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("ModeTapper.Payload");
        if (payload == null) throw new InvalidDataException("В установщике отсутствует пакет ModeTapper.");
        return new ZipArchive(payload, ZipArchiveMode.Read);
    }

    internal static bool Verify()
    {
        try
        {
            using (var archive = OpenPackage())
                foreach (string name in Required)
                    using (var file = Entry(archive, name).Open()) file.CopyTo(Stream.Null);
            return true;
        }
        catch { return false; }
    }

    static ZipArchiveEntry Entry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry == null || entry.Length == 0) throw new InvalidDataException("В установщике отсутствует " + name);
        return entry;
    }

    internal static string Run(string selected, bool cameras, Action<int, string> progress)
    {
        using (var gate = new Mutex(false, @"Local\ModeTapperSetup"))
        {
            bool acquired;
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return "Установщик ModeTapper уже запущен.";
            try { return Install(selected, cameras, progress); }
            finally { gate.ReleaseMutex(); }
        }
    }

    static string Install(string selected, bool cameras, Action<int, string> progress)
    {
        string directory = null, backup = null, previous = null;
        var changed = new List<string>();
        bool restart = false, retainBackup = false;
        using (var guard = OpenAsusGuard())
        try
        {
            progress(10, "Проверяем файлы и папку установки…");
            if (String.IsNullOrWhiteSpace(selected) || !Path.IsPathRooted(selected.Trim()))
                return "Укажите полный путь к папке установки.";
            directory = Path.GetFullPath(selected.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (directory.Length <= 3 || directory.StartsWith(@"\\", StringComparison.Ordinal))
                return "Выберите отдельную папку на локальном диске.";
            string existing = Existing;
            if (existing != null && !SamePath(existing, directory))
                return "Обновление устанавливается в папку текущей версии.";
            var destination = Directory.CreateDirectory(directory);
            if ((destination.Attributes & FileAttributes.ReparsePoint) != 0)
                return "Выберите папку, которая не является ссылкой.";
            string marker = Path.Combine(directory, Marker);
            if (existing == null && !File.Exists(marker) && Directory.GetFileSystemEntries(directory).Length > 0)
                return "Выберите пустую папку для ModeTapper.";

            string executable = Path.Combine(directory, Program);
            previous = existing != null ? executable :
                File.Exists(Path.Combine(Legacy, "luma-managed-install.txt")) && File.Exists(Path.Combine(Legacy, "Luma.exe")) ? Path.Combine(Legacy, "Luma.exe") : null;
            using (var archive = OpenPackage())
            {
                foreach (string name in Required) Entry(archive, name);
                progress(25, "Подготавливаем обновление…");
                if (previous != null) restart = StopPrevious(previous);
                if (!File.Exists(marker)) File.WriteAllText(marker, "ModeTapper managed installation");
                backup = Path.Combine(Path.GetTempPath(), "ModeTapper-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);
                progress(46, "Копируем программу и компоненты…");
                foreach (string name in Required)
                {
                    if (name == "rules.default.pac") continue;
                    if (name == Camera && !cameras)
                    {
                        string installedCamera = Path.Combine(directory, Camera);
                        if (File.Exists(installedCamera)) { File.Copy(installedCamera, Path.Combine(backup, Camera)); File.Delete(installedCamera); changed.Add(Camera); }
                        continue;
                    }
                    Copy(Entry(archive, name), directory, name, backup, changed);
                }
                InitializeProfile(directory, archive, previous);
            }
            progress(74, "Добавляем ModeTapper в меню «Пуск»…");
            RegisterShortcuts(executable);
            using (var key = Registry.CurrentUser.CreateSubKey(AppKey)) key.SetValue("InstallLocation", directory);
            progress(92, "Запускаем ModeTapper…");
            int pid = Start(executable, "--background");
            if (guard != null) WaitForTray(directory, pid);
            return null;
        }
        catch (Exception ex)
        {
            bool restored = true;
            for (int index = changed.Count - 1; index >= 0; index--)
            {
                string name = changed[index], target = Path.Combine(directory, name), old = Path.Combine(backup, name);
                try { if (File.Exists(old)) File.Copy(old, target, true); else File.Delete(target); }
                catch (IOException) { restored = false; }
                catch (UnauthorizedAccessException) { restored = false; }
            }
            retainBackup = !restored;
            if (restored && restart && previous != null) try { Start(previous, "--background"); } catch { }
            return ex.Message + (restored ? "" : " Часть файлов осталась в папке восстановления " + backup);
        }
        finally
        {
            if (backup != null && !retainBackup)
                try
                {
                    foreach (string file in Directory.GetFiles(backup)) File.Delete(file);
                    Directory.Delete(backup);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    static bool SamePath(string first, string second)
    {
        return String.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }

    static EventWaitHandle OpenAsusGuard()
    {
        try
        {
            EventWaitHandle guard;
            EventWaitHandle.TryOpenExisting("ATK Package AsMonStartupTask Application Event", out guard);
            return guard;
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    static bool StopPrevious(string path)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
        try
        {
            var running = new List<Process>();
            foreach (var process in processes)
            {
                try { if (!process.HasExited && SamePath(process.MainModule.FileName, path)) running.Add(process); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (running.Count == 0) return false;
            using (var stop = Process.Start(new ProcessStartInfo(path, "--stop") { UseShellExecute = false, CreateNoWindow = true }))
                if (!stop.WaitForExit(5000)) throw new IOException("Закройте ModeTapper перед обновлением.");
            foreach (var process in running)
                if (!process.WaitForExit(10000)) throw new IOException("Закройте ModeTapper перед обновлением.");
            return true;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    static void Copy(ZipArchiveEntry entry, string directory, string name, string backup, List<string> changed)
    {
        string target = Path.Combine(directory, name);
        string staging = Path.Combine(directory, ".install-" + Guid.NewGuid().ToString("N"));
        if (File.Exists(target)) File.Copy(target, Path.Combine(backup, name));
        try
        {
            entry.ExtractToFile(staging);
            if (File.Exists(target)) File.Replace(staging, target, null); else File.Move(staging, target);
            changed.Add(name);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    static void InitializeProfile(string location, ZipArchive archive, string previous)
    {
        if (previous != null && SamePath(previous, Path.Combine(Legacy, "Luma.exe")))
        {
            foreach (string file in Preferences)
            {
                string source = Path.Combine(Legacy, file), target = Path.Combine(location, file);
                if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
            }
            TakeLegacyData(location, "safety");
            TakeLegacyData(location, "vpn");
        }
        string vpn = Path.Combine(location, "vpn");
        if (Directory.Exists(vpn)) return;
        Directory.CreateDirectory(vpn);
        Entry(archive, "rules.default.pac").ExtractToFile(Path.Combine(vpn, "rules.template.pac"));
        File.WriteAllText(Path.Combine(vpn, "custom-rules.json"), "{\"direct\":[],\"proxy\":[]}");
        File.WriteAllText(Path.Combine(vpn, "disabled.flag"), "");
    }

    static void TakeLegacyData(string location, string folder)
    {
        string source = Path.Combine(Legacy, folder), target = Path.Combine(location, folder);
        if (!Directory.Exists(source) || Directory.Exists(target)) return;
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }

    static void RegisterShortcuts(string executable)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        try
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            Shortcut(shell, Path.Combine(programs, "ModeTapper.lnk"), executable, "--side-panel");
            if (!HasStartupTask(executable)) Shortcut(shell, Path.Combine(startup, "ModeTapper.lnk"), executable, "--background");
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    static void Shortcut(dynamic shell, string path, string executable, string arguments)
    {
        dynamic link = shell.CreateShortcut(path);
        try
        {
            link.TargetPath = executable;
            link.Arguments = arguments;
            link.WorkingDirectory = Path.GetDirectoryName(executable);
            link.Description = "ModeTapper";
            link.Save();
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    static bool HasStartupTask(string executable)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        try
        {
            scheduler.Connect();
            dynamic tasks = scheduler.GetFolder(@"\");
            foreach (string name in new[] { "ModeTapper", "Luma" })
                try
                {
                    dynamic task = tasks.GetTask(name);
                    if (task.Enabled && SamePath((string)task.Definition.Actions.Item(1).Path, executable)) return true;
                }
                catch (COMException) { }
            return false;
        }
        catch (COMException) { return false; }
        finally { Marshal.ReleaseComObject(scheduler); }
    }

    static int Start(string executable, string arguments)
    {
        using (var app = Process.Start(new ProcessStartInfo(executable, arguments) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)
        }))
        {
            if (app == null) throw new IOException("Не удалось запустить ModeTapper.");
            return app.Id;
        }
    }

    static void WaitForTray(string directory, int pid)
    {
        var wait = Stopwatch.StartNew();
        string status = Path.Combine(directory, "tray-status.json");
        while (wait.ElapsedMilliseconds < 6500)
        {
            try
            {
                string state = File.ReadAllText(status);
                if (state.Contains("\"pid\":" + pid + ",") && state.Contains("\"sideButtonStoreBlocked\":true")) return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { return; }
            Thread.Sleep(100);
        }
    }
}
