using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Установка ModeTapper")]
[assembly: AssemblyProduct("ModeTapper")]
[assembly: AssemblyVersion("1.24.15.0")]

internal static class ModeTapperSetup
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--verify-package") return Install(true) == null ? 0 : 1;
        if (args.Length == 1 && args[0] == "--quiet") {
            string error = Install(false);
            if (error != null) Console.Error.WriteLine(error);
            return error == null ? 0 : 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        int result = 0;
        using (var window = new Form {
            Text = "Установка ModeTapper", StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ClientSize = new Size(510, 270), AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10), Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
        })
        using (var headingFont = new Font(window.Font.FontFamily, 20, FontStyle.Bold))
        {
            var title = new Label { Text = "ModeTapper", Font = headingFont, AutoSize = true, Location = new Point(24, 20) };
            var status = new Label {
                Text = "Быстрые настройки, жесты и карусель окон для Windows.\r\n\r\nУстановка для текущего пользователя.",
                Location = new Point(26, 70), Size = new Size(458, 92)
            };
            var cameras = new CheckBox {
                Text = "Управление камерами (понадобятся права администратора)",
                Location = new Point(26, 166), Size = new Size(458, 30)
            };
            var install = new Button { Text = "Установить", Location = new Point(245, 210), Size = new Size(116, 36) };
            var cancel = new Button { Text = "Отмена", Location = new Point(369, 210), Size = new Size(116, 36) };
            bool busy = false;
            window.Controls.AddRange(new Control[] { title, status, cameras, install, cancel });
            window.AcceptButton = install;
            window.CancelButton = cancel;
            cancel.Click += (sender, e) => window.Close();
            window.FormClosing += (sender, e) => { if (busy) e.Cancel = true; };
            install.Click += async (sender, e) => {
                busy = true;
                bool withCameras = cameras.Checked;
                install.Enabled = cancel.Enabled = cameras.Enabled = false;
                status.Text = "Устанавливаем ModeTapper…";
                string error = await Task.Run(() => Install(false, withCameras));
                busy = false;
                if (error == null) {
                    status.Text = "ModeTapper установлен. Панель доступна в меню «Пуск».";
                    install.Visible = cameras.Visible = false;
                    cancel.Text = "Готово";
                    window.AcceptButton = cancel;
                    result = 0;
                } else {
                    result = 1;
                    status.Text = "Установка не завершилась. Можно повторить попытку.";
                    install.Enabled = cameras.Enabled = true;
                    MessageBox.Show(window, error, window.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                cancel.Enabled = true;
            };
            Application.Run(window);
        }
        return result;
    }

    private static string Install(bool verify, bool cameraControls = false)
    {
        string staging = Path.Combine(Path.GetTempPath(), "ModeTapperSetup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("ModeTapper.Payload"))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries) {
                    string path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Недопустимый путь внутри установщика.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path);
                }
            }
            string script = Path.Combine(staging, "work", "Install-ModeTapper.ps1");
            if (!File.Exists(script) || !File.Exists(Path.Combine(staging, "work", "ModeTapper.exe"))
                || !File.Exists(Path.Combine(staging, "work", "ModeTapper-camera-control.exe")))
                throw new InvalidDataException("В установщике отсутствуют файлы ModeTapper.");
            if (verify) return null;
            string runner = Path.Combine(staging, "install.ps1");
            string errorFile = Path.Combine(staging, "install-error.txt");
            File.WriteAllText(runner,
                "$ErrorActionPreference='Stop'\r\ntry { & (Join-Path $PSScriptRoot 'work/Install-ModeTapper.ps1')" +
                (cameraControls ? " -InstallCameraControls" : "") + " } " +
                "catch { [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'install-error.txt'), $_.Exception.Message); exit 1 }\r\n");
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string shell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var launch = new ProcessStartInfo(shell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + runner + "\"") {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = staging
            };
            launch.EnvironmentVariables.Remove("PSModulePath");
            using (var process = Process.Start(launch)) {
                process.WaitForExit();
                if (process.ExitCode != 0)
                    return File.Exists(errorFile) ? File.ReadAllText(errorFile) : "Код ошибки " + process.ExitCode;
            }
            return null;
        }
        catch (Exception error) { return error.Message; }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
