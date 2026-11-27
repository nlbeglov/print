using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

// Установщик для друзей: одна кнопка «Установить».
// Внутри (ресурс payload.zip): EpsPrint.exe, Updater.exe, update.ini, README.txt, firewall.bat, gs\bin\…
// Ставит в %LOCALAPPDATA%\Programs\EpsPrint (права администратора не нужны),
// создаёт ярлыки и одним запросом UAC закрывает программе и Ghostscript доступ в интернет.
namespace EpsPrintSetup
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }

    class SetupForm : Form
    {
        const string RuleName = "EpsPrint - no internet";
        const string ShortcutName = "EPS Печать.lnk";

        readonly string target = Path.Combine(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "EpsPrint");

        Label status;
        ProgressBar bar;
        Button installBtn, removeBtn;
        CheckBox desktopCb;

        public SetupForm()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            Text = "Установка EPS Печать " + v.Major + "." + v.Minor + "." + v.Build;
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(500, 250);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var title = new Label { Text = "EPS Печать", Font = new Font("Segoe UI", 16f, FontStyle.Bold), Left = 20, Top = 16, AutoSize = true };
            status = new Label { Left = 20, Top = 62, Width = 460, Height = 64, Text = "Нажмите «Установить». Всё нужное уже внутри, ничего скачивать не надо." };
            desktopCb = new CheckBox { Text = "Ярлык на рабочем столе", Left = 20, Top = 130, AutoSize = true, Checked = true };
            bar = new ProgressBar { Left = 20, Top = 160, Width = 460, Height = 14, Visible = false };
            installBtn = new Button { Text = "Установить", Left = 330, Top = 196, Width = 150, Height = 36, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            removeBtn = new Button { Text = "Удалить программу", Left = 20, Top = 196, Width = 160, Height = 36, Visible = File.Exists(Path.Combine(target, "EpsPrint.exe")) };
            if (removeBtn.Visible) installBtn.Text = "Переустановить";
            installBtn.Click += (s, e) => Install();
            removeBtn.Click += (s, e) => Uninstall();
            Controls.AddRange(new Control[] { title, status, desktopCb, bar, installBtn, removeBtn });
            AcceptButton = installBtn;
        }

        // ───────── Установка ─────────

        async void Install()
        {
            SetBusy(true);
            try
            {
                status.Text = "Закрываю старую версию, если открыта…";
                await Task.Run(() => StopRunning());

                status.Text = "Копирую файлы…";
                await Task.Run(() => ExtractPayload());

                status.Text = "Создаю ярлыки…";
                string exe = Path.Combine(target, "EpsPrint.exe");
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName), exe);
                string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);
                if (desktopCb.Checked) CreateShortcut(desk, exe);
                else if (File.Exists(desk)) File.Delete(desk);

                status.Text = "Отключаю программе доступ в интернет (Windows спросит разрешение)…";
                bool fw = await Task.Run(() => RunElevated(FirewallAddCommand()));

                bar.Value = 100;
                string msg = "Готово! Программа установлена.";
                if (!fw) msg += "\n\nДоступ в интернет не отключён: не было разрешения администратора. " +
                                "Это можно сделать позже: запустите установщик ещё раз.";
                MessageBox.Show(this, msg, "EPS Печать", MessageBoxButtons.OK, fw ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

                var psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = target;
                psi.UseShellExecute = false;
                Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                status.Text = "Ошибка установки.";
                MessageBox.Show(this, ex.Message, "Ошибка установки", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
            }
        }

        void ExtractPayload()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var res = asm.GetManifestResourceStream("payload.zip"))
            {
                if (res == null) throw new Exception("Установщик повреждён: нет данных программы.");
                Directory.CreateDirectory(target);
                string root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
                using (var za = new ZipArchive(res, ZipArchiveMode.Read))
                {
                    int done = 0;
                    foreach (var e in za.Entries)
                    {
                        string rel = e.FullName.Replace('/', '\\');
                        string dest = Path.GetFullPath(Path.Combine(target, rel));
                        if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new Exception("Недопустимый путь в установщике: " + e.FullName);
                        if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(dest); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                        done++;
                        int pct = (int)(90.0 * done / za.Entries.Count);
                        BeginInvoke(new Action(() => bar.Value = Math.Min(90, pct)));
                    }
                }
            }
        }

        string FirewallAddCommand()
        {
            string cmd = "netsh advfirewall firewall delete rule name=\"" + RuleName + "\"";
            foreach (string rel in new[] { "EpsPrint.exe", @"gs\bin\gswin64c.exe" })
            {
                string p = Path.Combine(target, rel);
                foreach (string dir in new[] { "out", "in" })
                    cmd += " & netsh advfirewall firewall add rule name=\"" + RuleName + "\" dir=" + dir +
                           " action=block enable=yes program=\"" + p + "\"";
            }
            return cmd;
        }

        // ───────── Удаление ─────────

        async void Uninstall()
        {
            if (MessageBox.Show(this, "Удалить EPS Печать с этого компьютера?", "Удаление",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SetBusy(true);
            try
            {
                await Task.Run(() => StopRunning());
                foreach (string lnk in new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName) })
                    if (File.Exists(lnk)) File.Delete(lnk);
                await Task.Run(() => RunElevated("netsh advfirewall firewall delete rule name=\"" + RuleName + "\""));
                await Task.Run(() => Directory.Delete(target, true));
                try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpsPrint"), true); } catch { }
                MessageBox.Show(this, "Программа удалена.", "Удаление", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Ошибка удаления", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
            }
        }

        // ───────── Вспомогательное ─────────

        void SetBusy(bool busy)
        {
            installBtn.Enabled = !busy;
            removeBtn.Enabled = !busy;
            desktopCb.Enabled = !busy;
            bar.Visible = busy;
            if (busy) bar.Value = 0;
            UseWaitCursor = busy;
        }

        void StopRunning()
        {
            foreach (string name in new[] { "EpsPrint", "Updater" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        if (!path.StartsWith(target, StringComparison.OrdinalIgnoreCase)) continue;
                        p.CloseMainWindow();
                        if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(5000); }
                    }
                    catch { }
                }
            }
        }

        /// <summary>Запуск команды с правами администратора (один запрос UAC). false — пользователь отказал.</summary>
        static bool RunElevated(string command)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(60000);
                    return true;
                }
            }
            catch (Win32Exception)
            {
                return false;   // нажали «Нет» в окне UAC
            }
        }

        static void CreateShortcut(string lnkPath, string targetExe)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                try
                {
                    var st = sc.GetType();
                    st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { targetExe });
                    st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(targetExe) });
                    st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Печать EPS-файлов" });
                    st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                }
                finally { Marshal.FinalReleaseComObject(sc); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
    }
}
