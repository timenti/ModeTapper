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
[assembly: AssemblyVersion("1.24.17.0")]

internal static class ModeTapperSetup
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--verify-package")
            return InstallFiles.Verify() ? 0 : 1;
        if (args.Length == 1 && args[0] == "--quiet")
            return InstallFiles.Run(InstallFiles.Existing ?? InstallFiles.DefaultPath, true, true, false, (step, text) => {}) == null ? 0 : 1;

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
    static readonly Color Surface = Color.FromArgb(31, 31, 31);
    static readonly Color Border = Color.FromArgb(58, 58, 58);
    static readonly Color Ink = Color.FromArgb(246, 246, 246);
    static readonly Color Muted = Color.FromArgb(179, 179, 179);
    static readonly Color Green = Color.FromArgb(110, 211, 157);
    static readonly Color Red = Color.FromArgb(235, 125, 125);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    readonly string existing = InstallFiles.Existing;
    readonly Version installedVersion;
    readonly bool fresh, update, newer, needsRepair;
    readonly TextBox path;
    readonly SetupToggle cameras, vpn;
    readonly Label status;
    readonly Button browse, cancel, primary, repair;
    readonly SetupProgress progress;
    readonly Font headingFont, bodyFont, detailFont, titleFont;
    readonly float scale;
    bool busy, complete;
    internal int Result { get; private set; }

    internal SetupWindow()
    {
        fresh = existing == null;
        installedVersion = InstallFiles.InstalledVersion(existing);
        needsRepair = !fresh && installedVersion == null;
        update = installedVersion != null && installedVersion < InstallFiles.PackageVersion;
        newer = installedVersion != null && installedVersion > InstallFiles.PackageVersion;

        Text = "Установщик ModeTapper";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        var working = Screen.PrimaryScreen.WorkingArea;
        scale = Math.Min(GetDpiForSystem() / 96f, Math.Min((working.Width - 24) / 620f, (working.Height - 70) / 585f));
        AutoScaleMode = AutoScaleMode.None;
        headingFont = new Font("Segoe UI", D(35), FontStyle.Bold, GraphicsUnit.Pixel);
        bodyFont = new Font("Segoe UI", D(14), FontStyle.Regular, GraphicsUnit.Pixel);
        detailFont = new Font("Segoe UI", D(13), FontStyle.Regular, GraphicsUnit.Pixel);
        titleFont = new Font("Segoe UI Semibold", D(15), FontStyle.Regular, GraphicsUnit.Pixel);
        Font = bodyFont;
        ClientSize = new Size(D(620), D(490));
        BackColor = Background;
        ForeColor = Ink;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = Inset(28, 22, 28, 20), Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var hero = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, ColumnCount = 2, RowCount = 2,
            Padding = Inset(16, 8, 14, 7), Margin = Inset(0, 0, 0, 12) };
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(61)));
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hero.RowStyles.Add(new RowStyle(SizeType.Percent, 66));
        hero.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        Bitmap logoImage;
        using (var sizedIcon = new Icon(Icon, new Size(D(42), D(42)))) logoImage = sizedIcon.ToBitmap();
        var logo = new PictureBox { Image = logoImage, SizeMode = PictureBoxSizeMode.CenterImage, Dock = DockStyle.Fill, Margin = Inset(0, 0, 12, 0) };
        logo.Disposed += (sender, args) => logoImage.Dispose();
        hero.Controls.Add(logo, 0, 0);
        hero.SetRowSpan(logo, 2);
        hero.Controls.Add(MakeLabel("ModeTapper", Ink, headingFont), 1, 0);
        hero.Controls.Add(MakeLabel("Версия " + InstallFiles.PackageVersion.ToString(3) + "  ·  Windows 11 x64", Muted, detailFont), 1, 1);
        AddRow(layout, hero, 107);

        string summary = fresh ? "Выберите папку и компоненты для первой установки." :
            newer ? "Установлена версия " + installedVersion.ToString(3) + ". Для её обслуживания нужен более новый установщик." :
            update ? "Версия " + installedVersion.ToString(3) + " будет обновлена. Ваши настройки сохранятся." :
            needsRepair ? "Найдена установка ModeTapper. Можно восстановить файлы и ярлыки." :
            "ModeTapper уже установлен. Отсюда можно добавить компоненты или восстановить файлы.";
        AddRow(layout, MakeLabel(summary, Ink, bodyFont), 43);

        path = new TextBox { Text = existing ?? InstallFiles.DefaultPath, BackColor = Surface, ForeColor = Ink,
            Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle, Margin = Inset(0, 5, 10, 0),
            ReadOnly = !fresh, AccessibleName = "Папка установки" };
        browse = MakeButton("Выбрать…", false);
        browse.Dock = DockStyle.Fill;
        browse.Click += Browse;
        if (fresh)
        {
            AddRow(layout, MakeLabel("Папка установки", Muted, detailFont), 21);
            var pathRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = Inset(0, 0, 0, 7) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(101)));
            pathRow.Controls.Add(path, 0, 0);
            pathRow.Controls.Add(browse, 1, 0);
            AddRow(layout, pathRow, 42);
        }

        cameras = new SetupToggle("Управление камерами");
        vpn = new SetupToggle("Маршрутизация VPN");
        if (!newer)
        {
            bool addCameras = fresh || !InstallFiles.HasCameras(existing);
            bool addVpn = fresh || !InstallFiles.HasVpn(existing);
            if (addCameras || addVpn)
                AddRow(layout, MakeLabel("Дополнительные компоненты", Muted, detailFont), 26);
            if (addCameras) AddRow(layout, Option("Управление камерами", "Включение и отключение камер из боковой панели.", cameras), 67);
            if (addVpn) AddRow(layout, Option("Маршрутизация VPN", "Правила для сайтов через локальный прокси Happ.", vpn), 67);
        }

        status = MakeLabel(newer ? "" : fresh ? "Компоненты можно добавить позже тем же установщиком." :
            update ? "Установленные компоненты сохранятся." : "", Muted, detailFont);
        AddRow(layout, status, 42);
        progress = new SetupProgress { Dock = DockStyle.Top, Height = D(5), Margin = Inset(0, 3, 0, 0), AccessibleName = "Ход установки" };
        AddRow(layout, progress, 15);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(145)));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(100)));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(187)));
        repair = MakeButton("Восстановить", false);
        repair.Dock = DockStyle.Fill;
        repair.Click += (sender, args) => Install(true);
        repair.Visible = !fresh && !update && !newer;
        primary = MakeButton("", true);
        primary.Dock = DockStyle.Fill;
        primary.Margin = Inset(9, 0, 0, 0);
        primary.Click += (sender, args) => Install(false);
        cancel = MakeButton("Закрыть", false);
        cancel.Dock = DockStyle.Fill;
        cancel.Click += (sender, args) => Close();
        actions.Controls.Add(repair, 0, 0);
        actions.Controls.Add(cancel, 2, 0);
        actions.Controls.Add(primary, 3, 0);
        AddRow(layout, actions, 41);

        int rowsHeight = 0;
        foreach (RowStyle row in layout.RowStyles) rowsHeight += (int)row.Height;
        ClientSize = new Size(D(620), rowsHeight + layout.Padding.Vertical);
        cameras.CheckedChanged += (sender, args) => UpdateAction();
        vpn.CheckedChanged += (sender, args) => UpdateAction();
        UpdateAction();
        CancelButton = cancel;
        ActiveControl = AcceptButton as Control;
    }

    int D(int value) { return (int)Math.Round(value * scale); }
    Padding Inset(int left, int top, int right, int bottom) { return new Padding(D(left), D(top), D(right), D(bottom)); }

    void AddRow(TableLayoutPanel layout, Control control, int height)
    {
        int row = layout.RowCount;
        layout.RowCount = row + 1;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, D(height)));
        layout.Controls.Add(control, 0, row);
    }

    static Label MakeLabel(string text, Color color, Font font)
    {
        return new Label { Text = text, ForeColor = color, Font = font, Dock = DockStyle.Fill,
            Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    }

    Control Option(string title, string description, SetupToggle toggle)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, ColumnCount = 2, RowCount = 2,
            Padding = Inset(14, 6, 10, 6), Margin = Inset(0, 0, 0, 7) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(62)));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 53));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 47));
        var name = MakeLabel(title, Ink, titleFont);
        var note = MakeLabel(description, Muted, detailFont);
        card.Controls.Add(name, 0, 0);
        card.Controls.Add(note, 0, 1);
        card.Controls.Add(toggle, 1, 0);
        card.SetRowSpan(toggle, 2);
        name.Click += (s, e) => { if (toggle.Enabled) toggle.Checked = !toggle.Checked; };
        note.Click += (s, e) => { if (toggle.Enabled) toggle.Checked = !toggle.Checked; };
        return card;
    }

    static Button MakeButton(string text, bool accent)
    {
        var button = new Button { Text = text, Margin = Padding.Empty, FlatStyle = FlatStyle.Flat,
            BackColor = accent ? Green : Surface, ForeColor = accent ? Background : Ink, UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = accent ? Green : Border;
        button.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(145, 225, 183) : Color.FromArgb(43, 43, 43);
        return button;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1, round = 2;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    void UpdateAction()
    {
        bool adding = cameras.Checked || vpn.Checked;
        bool showPrimary = !newer && (fresh || update || needsRepair || adding);
        bool showRepair = !fresh && !update && !newer && !needsRepair;
        primary.Visible = showPrimary;
        primary.Text = fresh ? "Установить" : update ? "Обновить" : needsRepair ? "Восстановить" : "Установить компоненты";
        repair.Visible = showRepair;
        AcceptButton = showPrimary ? primary : showRepair ? repair : cancel;
    }

    void Browse(object sender, EventArgs e)
    {
        using (var dialog = new FolderBrowserDialog { Description = "Выберите папку для ModeTapper", ShowNewFolderButton = true })
            if (dialog.ShowDialog(this) == DialogResult.OK)
                path.Text = String.Equals(Path.GetFileName(dialog.SelectedPath), "ModeTapper", StringComparison.OrdinalIgnoreCase)
                    ? dialog.SelectedPath : Path.Combine(dialog.SelectedPath, "ModeTapper");
    }

    async void Install(bool restoring)
    {
        if (complete) { Close(); return; }
        if (busy || newer) return;
        busy = true;
        path.ReadOnly = true;
        primary.Enabled = repair.Enabled = cancel.Enabled = cameras.Enabled = vpn.Enabled = browse.Enabled = false;
        status.ForeColor = Muted;
        progress.Reset();
        string destination = path.Text;
        bool addCameras = cameras.Checked, addVpn = vpn.Checked;
        var updates = new Progress<KeyValuePair<int, string>>(change => {
            status.Text = change.Value;
            progress.Advance(change.Key);
        });
        var report = (IProgress<KeyValuePair<int, string>>)updates;
        string error = await Task.Run(() => InstallFiles.Run(destination, addCameras, addVpn, restoring || needsRepair,
            (value, message) => report.Report(new KeyValuePair<int, string>(value, message))));
        busy = false;
        primary.Enabled = repair.Enabled = cancel.Enabled = true;
        if (error == null)
        {
            complete = true;
            Result = 0;
            status.ForeColor = Green;
            status.Text = restoring || needsRepair ? "Файлы и ярлыки восстановлены. Ваши настройки сохранены." :
                fresh || update ? "ModeTapper готов. Откройте его из меню «Пуск»." : "Компоненты добавлены. Откройте боковую панель.";
            progress.Advance(100);
            primary.Text = "Готово";
            primary.Visible = true;
            AcceptButton = primary;
            repair.Visible = cancel.Visible = false;
        }
        else
        {
            Result = 1;
            status.ForeColor = Red;
            status.Text = error;
            progress.Reset();
            cameras.Enabled = vpn.Enabled = true;
            path.ReadOnly = !fresh;
            browse.Enabled = fresh;
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
            titleFont.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class SetupToggle : CheckBox
    {
        internal SetupToggle(string name)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            AccessibleName = name;
            AccessibleDescription = "Переключатель дополнительного компонента";
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
            BackColor = Surface;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int height = Math.Min(Height - 8, Width / 2);
            int width = height * 2;
            if (height < 4) return;
            var track = new Rectangle((Width - width) / 2, (Height - height) / 2, width, height);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(track.X, track.Y, height, height, 90, 180);
                path.AddArc(track.Right - height, track.Y, height, height, 270, 180);
                path.CloseFigure();
                using (var pigment = new SolidBrush(Checked && Enabled ? Green : Border))
                    e.Graphics.FillPath(pigment, path);
            }
            int inset = Math.Max(3, height / 6);
            int x = Checked ? track.Right - height + inset : track.Left + inset;
            using (var pigment = new SolidBrush(Checked && Enabled ? Background : Muted))
                e.Graphics.FillEllipse(pigment, x, track.Top + inset, height - 2 * inset, height - 2 * inset);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(track, 2, 2), Ink, Surface);
        }

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
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
    const string VpnTemplate = @"vpn\rules.template.pac";
    static readonly string Legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luma");
    internal static readonly string DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModeTapper");
    internal static readonly Version PackageVersion = Assembly.GetExecutingAssembly().GetName().Version;
    static readonly string[] CoreFiles = { Program, Program + ".config", "ModeTapper.ico", "LICENSE.txt" };
    static readonly string[] PackageFiles = { Program, Program + ".config", "ModeTapper.ico", Camera, "LICENSE.txt", "rules.default.pac" };
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

    internal static Version InstalledVersion(string location)
    {
        if (location == null) return null;
        try { return AssemblyName.GetAssemblyName(Path.Combine(location, Program)).Version; }
        catch (IOException) { return null; }
        catch (BadImageFormatException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static bool HasCameras(string location) { return location != null && File.Exists(Path.Combine(location, Camera)); }
    internal static bool HasVpn(string location) { return location != null && File.Exists(Path.Combine(location, VpnTemplate)); }

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
                foreach (string name in PackageFiles)
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

    internal static string Run(string selected, bool cameras, bool vpn, bool repair, Action<int, string> progress)
    {
        using (var gate = new Mutex(false, @"Local\ModeTapperSetup"))
        {
            bool acquired;
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return "Установщик ModeTapper уже запущен.";
            try { return Install(selected, cameras, vpn, repair, progress); }
            finally { gate.ReleaseMutex(); }
        }
    }

    static string Install(string selected, bool cameras, bool vpn, bool repair, Action<int, string> progress)
    {
        string directory = null, backup = null, previous = null;
        var changed = new List<string>();
        var createdDirectories = new List<string>();
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
                foreach (string name in PackageFiles) Entry(archive, name);
                var version = InstalledVersion(existing);
                if (version != null && version > PackageVersion)
                    return "Установлена более новая версия ModeTapper.";
                bool copyCore = repair || version == null || version < PackageVersion;
                bool copyCamera = (cameras || HasCameras(existing)) && (copyCore || !HasCameras(existing));
                bool copyVpn = (vpn || HasVpn(existing)) && (copyCore || !HasVpn(existing));
                if (!copyCore && !copyCamera && !copyVpn) return null;
                progress(25, "Подготавливаем обновление…");
                if (previous != null) restart = StopPrevious(previous);
                backup = Path.Combine(Path.GetTempPath(), "ModeTapper-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);
                CreateTextIfMissing(directory, Marker, "ModeTapper managed installation", changed);
                progress(46, "Копируем программу и компоненты…");
                if (copyCore)
                    foreach (string name in CoreFiles) Copy(Entry(archive, name), directory, name, backup, changed);
                if (copyCamera) Copy(Entry(archive, Camera), directory, Camera, backup, changed);
                InitializeProfile(directory, previous, copyVpn, changed, createdDirectories);
                if (copyVpn)
                {
                    string vpnFolder = ProfileFolder(directory, "vpn", createdDirectories);
                    bool existingRules = File.Exists(Path.Combine(vpnFolder, "custom-rules.json"));
                    Copy(Entry(archive, "rules.default.pac"), directory, VpnTemplate, backup, changed);
                    CreateTextIfMissing(directory, @"vpn\custom-rules.json", "{\"direct\":[],\"proxy\":[]}", changed);
                    if (!existingRules) CreateTextIfMissing(directory, @"vpn\disabled.flag", "", changed);
                }
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
                string name = changed[index], target = Path.Combine(directory, name), old = BackupPath(backup, name);
                try { if (File.Exists(old)) File.Copy(old, target, true); else File.Delete(target); }
                catch (IOException) { restored = false; }
                catch (UnauthorizedAccessException) { restored = false; }
            }
            foreach (string created in createdDirectories)
            {
                try
                {
                    if (Directory.Exists(created) && Directory.GetFileSystemEntries(created).Length == 0) Directory.Delete(created);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
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
        if (File.Exists(target)) File.Copy(target, BackupPath(backup, name));
        try
        {
            entry.ExtractToFile(staging);
            if (File.Exists(target)) File.Replace(staging, target, null); else File.Move(staging, target);
            changed.Add(name);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    static string BackupPath(string backup, string name) { return Path.Combine(backup, name.Replace('\\', '_')); }

    static void CreateTextIfMissing(string directory, string name, string contents, List<string> changed)
    {
        string target = Path.Combine(directory, name);
        if (File.Exists(target)) return;
        changed.Add(name);
        File.WriteAllText(target, contents);
    }

    static void InitializeProfile(string location, string previous, bool vpn, List<string> changed, List<string> createdDirectories)
    {
        if (previous != null && SamePath(previous, Path.Combine(Legacy, "Luma.exe")))
        {
            foreach (string file in Preferences)
            {
                CopyLegacyFile(location, file, changed);
            }
            TakeLegacyData(location, "safety", changed, createdDirectories);
            if (vpn) TakeLegacyData(location, "vpn", changed, createdDirectories);
        }
    }

    static void CopyLegacyFile(string location, string name, List<string> changed)
    {
        string source = Path.Combine(Legacy, name), target = Path.Combine(location, name);
        if (!File.Exists(source) || File.Exists(target)) return;
        changed.Add(name);
        File.Copy(source, target);
    }

    static string ProfileFolder(string location, string name, List<string> createdDirectories)
    {
        string path = Path.Combine(location, name);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            createdDirectories.Add(path);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Папка настроек является ссылкой.");
        return path;
    }

    static void TakeLegacyData(string location, string folder, List<string> changed, List<string> createdDirectories)
    {
        string source = Path.Combine(Legacy, folder), target = Path.Combine(location, folder);
        if (!Directory.Exists(source) || Directory.Exists(target)) return;
        ProfileFolder(location, folder, createdDirectories);
        foreach (string file in Directory.GetFiles(source))
        {
            string name = Path.Combine(folder, Path.GetFileName(file));
            if (!String.Equals(name, VpnTemplate, StringComparison.OrdinalIgnoreCase)) CopyLegacyFile(location, name, changed);
        }
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
