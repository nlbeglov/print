using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace EpsPrintUpdater
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            int pid = 0;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--pid") int.TryParse(args[i + 1], out pid);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UpdaterForm(pid));
        }
    }

    /// <summary>
    /// Единственная часть программы, которая ходит в интернет — только по HTTPS и только на GitHub
    /// (релизы публичного репозитория). Пакет принимается, только если его версия новее установленной
    /// и совпадает с тегом релиза, а внутри лишь разрешённые файлы.
    /// </summary>
    class UpdaterForm : Form
    {
        const string AssetZip = "EpsPrint-update.zip";
        const string DefaultRepo = "nlbeglov/print";
        const long MaxDownload = 80L * 1024 * 1024;
        static readonly string[] AllowedFiles = { "EpsPrint.exe", "Updater.exe", "README.txt" };

        readonly string appDir = AppDomain.CurrentDomain.BaseDirectory;
        readonly int parentPid;
        string repo, token;
        Version current, latest;
        string zipUrl;
        bool installed, busy;

        Label status;
        TextBox notes;
        ProgressBar bar;
        Button installBtn, closeBtn;

        public UpdaterForm(int parentPid)
        {
            this.parentPid = parentPid;
            Text = "Обновление EPS Печать";
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(520, 340);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            status = new Label { Left = 16, Top = 14, Width = 488, Height = 44, Text = "Проверяю обновления…" };
            bar = new ProgressBar { Left = 16, Top = 62, Width = 488, Height = 14, Style = ProgressBarStyle.Marquee };
            notes = new TextBox { Left = 16, Top = 86, Width = 488, Height = 196, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false };
            installBtn = new Button { Text = "Установить", Left = 284, Top = 294, Width = 110, Height = 32, Visible = false };
            closeBtn = new Button { Text = "Закрыть", Left = 400, Top = 294, Width = 104, Height = 32 };
            installBtn.Click += (s, e) => Install();
            closeBtn.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { status, bar, notes, installBtn, closeBtn });

            FormClosing += (s, e) => { if (busy) e.Cancel = true; };
            FormClosed += (s, e) => { if (!installed) RestartMain(); };
            Shown += (s, e) => Check();
        }

        // ───────── Проверка ─────────

        async void Check()
        {
            busy = true;
            try
            {
                CleanupOld();
                LoadConfig();
                current = Normalize(AssemblyName.GetAssemblyName(Path.Combine(appDir, "EpsPrint.exe")).Version);

                // 1) Обычные страницы github.com (как в браузере) — без лимита API в 60 запросов/час
                string tag = null, body = null;
                Exception webError = null;
                try
                {
                    tag = await Task.Run(() => LatestTagViaWeb());
                    zipUrl = "https://github.com/" + repo + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + AssetZip;
                }
                catch (Exception ex) { webError = ex; }

                // 2) Запасной путь — API GitHub (там же берём описание «что нового», если получится)
                Dictionary<string, object> rel = null;
                try
                {
                    string json = await Task.Run(() => GetText("https://api.github.com/repos/" + repo + "/releases/latest"));
                    rel = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                }
                catch (Exception apiError)
                {
                    if (tag == null) throw webError ?? apiError;
                }
                if (rel != null)
                {
                    string apiTag = rel.ContainsKey("tag_name") ? rel["tag_name"] as string : null;
                    if (tag == null || tag == apiTag)
                    {
                        body = rel.ContainsKey("body") ? rel["body"] as string : null;
                        if (tag == null)
                        {
                            tag = apiTag;
                            var assets = rel.ContainsKey("assets") ? rel["assets"] as IEnumerable : null;
                            if (assets != null)
                                foreach (var a in assets)
                                {
                                    var d = a as Dictionary<string, object>;
                                    if (d != null && d["name"] as string == AssetZip) zipUrl = d["url"] as string;
                                }
                        }
                    }
                }

                Version v;
                if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out v))
                    throw new Exception("Не удалось понять версию последнего релиза: " + tag);
                latest = Normalize(v);

                bar.Style = ProgressBarStyle.Continuous;
                if (latest <= current)
                {
                    status.Text = "У вас последняя версия (" + Short(current) + ").";
                    return;
                }
                if (zipUrl == null)
                    throw new Exception("В релизе " + tag + " нет файла " + AssetZip + ".");

                status.Text = "Доступна новая версия " + Short(latest) + " (у вас " + Short(current) + ").";
                if (!string.IsNullOrEmpty(body))
                {
                    notes.Text = body.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    notes.Visible = true;
                }
                installBtn.Visible = true;
                AcceptButton = installBtn;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally { busy = false; }
        }

        // ───────── Установка ─────────

        async void Install()
        {
            installBtn.Enabled = false;
            closeBtn.Enabled = false;
            busy = true;
            bar.Style = ProgressBarStyle.Marquee;
            string staging = Path.Combine(Path.GetTempPath(), "EpsPrintUpdate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                status.Text = "Скачиваю версию " + Short(latest) + "…";
                byte[] zip = await Task.Run(() => Download(zipUrl));

                status.Text = "Проверяю пакет…";
                Directory.CreateDirectory(staging);
                var files = ExtractChecked(zip, staging);

                // версия внутри пакета должна совпадать с тегом релиза (защита от отката на старую версию)
                var pkgVer = Normalize(AssemblyName.GetAssemblyName(Path.Combine(staging, "EpsPrint.exe")).Version);
                if (pkgVer != latest || pkgVer <= current)
                    throw new Exception("Версия в пакете (" + Short(pkgVer) + ") не совпадает с релизом (" + Short(latest) + ").");

                status.Text = "Жду закрытия программы…";
                await Task.Run(() => WaitMainClosed());

                status.Text = "Заменяю файлы…";
                ReplaceFiles(files, staging);
                installed = true;

                bar.Style = ProgressBarStyle.Continuous;
                bar.Value = 100;
                status.Text = "Готово! Установлена версия " + Short(latest) + ".";
                RestartMain();
                busy = false;
                Close();
            }
            catch (Exception ex)
            {
                busy = false;
                closeBtn.Enabled = true;
                Fail(ex);
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch { }
                busy = false;
            }
        }

        List<string> ExtractChecked(byte[] zip, string staging)
        {
            var result = new List<string>();
            using (var ms = new MemoryStream(zip))
            using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var e in za.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    string allowed = AllowedFiles.FirstOrDefault(a => string.Equals(a, e.FullName, StringComparison.OrdinalIgnoreCase));
                    if (allowed == null) throw new Exception("Недопустимый файл в пакете: " + e.FullName);
                    if (e.Length > MaxDownload) throw new Exception("Слишком большой файл в пакете: " + e.FullName);
                    if (result.Contains(allowed)) throw new Exception("Повтор файла в пакете: " + e.FullName);
                    e.ExtractToFile(Path.Combine(staging, allowed));
                    result.Add(allowed);
                }
            }
            if (!result.Contains("EpsPrint.exe")) throw new Exception("В пакете нет EpsPrint.exe.");
            return result;
        }

        void ReplaceFiles(List<string> files, string staging)
        {
            string stamp = DateTime.Now.Ticks.ToString();
            var done = new List<Tuple<string, string>>();   // (файл, резервная копия)
            try
            {
                foreach (string name in files)
                {
                    string target = Path.Combine(appDir, name);
                    string backup = null;
                    if (File.Exists(target))
                    {
                        backup = target + "." + stamp + ".old";   // работающий Updater.exe тоже можно переименовать
                        File.Move(target, backup);
                    }
                    done.Add(Tuple.Create(target, backup));
                    File.Move(Path.Combine(staging, name), target);
                }
            }
            catch (UnauthorizedAccessException)
            {
                Rollback(done);
                throw new Exception("Нет прав на запись в папку программы:\n" + appDir +
                    "\nПоложите программу в папку пользователя (например C:\\EpsPrint), а не в Program Files.");
            }
            catch
            {
                Rollback(done);
                throw;
            }
        }

        static void Rollback(List<Tuple<string, string>> done)
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (File.Exists(done[i].Item1)) File.Delete(done[i].Item1);
                    if (done[i].Item2 != null) File.Move(done[i].Item2, done[i].Item1);
                }
                catch { }
            }
        }

        void WaitMainClosed()
        {
            if (parentPid > 0)
            {
                try { Process.GetProcessById(parentPid).WaitForExit(30000); } catch { }
            }
            string mainExe = Path.GetFullPath(Path.Combine(appDir, "EpsPrint.exe"));
            for (int attempt = 0; attempt < 60; attempt++)
            {
                bool running = false;
                foreach (var p in Process.GetProcessesByName("EpsPrint"))
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(p.MainModule.FileName), mainExe, StringComparison.OrdinalIgnoreCase))
                            running = true;
                    }
                    catch { running = true; }
                }
                if (!running) return;
                System.Threading.Thread.Sleep(500);
            }
            throw new Exception("EPS Печать всё ещё открыта. Закройте все её окна и попробуйте снова.");
        }

        // ───────── Сеть (только HTTPS, только GitHub) ─────────

        /// <summary>update.ini необязателен: по умолчанию берётся публичный репозиторий DefaultRepo.</summary>
        void LoadConfig()
        {
            repo = DefaultRepo;
            token = null;
            string ini = Path.Combine(appDir, "update.ini");
            if (File.Exists(ini))
            {
                foreach (string line in File.ReadAllLines(ini))
                {
                    string l = line.Trim();
                    if (l.StartsWith("#") || l.StartsWith(";")) continue;
                    int i = l.IndexOf('=');
                    if (i <= 0) continue;
                    string k = l.Substring(0, i).Trim().ToLowerInvariant(), v = l.Substring(i + 1).Trim();
                    if (k == "repo" && v.Length > 0) repo = v;
                    if (k == "token" && v.Length > 0) token = v;
                }
            }
            if (!Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
                throw new Exception("В update.ini неверно указан repo (нужно в виде владелец/репозиторий).");
            if (token != null && !Regex.IsMatch(token, @"^[A-Za-z0-9_]+$"))
                throw new Exception("В update.ini неверный token.");
        }

        static void EnsureTls()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        HttpWebRequest Request(string url, bool withToken, string accept)
        {
            var uri = new Uri(url);
            bool github = uri.Host == "api.github.com";
            bool web = uri.Host == "github.com";
            bool assetHost = uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
            if (uri.Scheme != Uri.UriSchemeHttps || !(github || web || assetHost))
                throw new Exception("Отклонён запрос к неразрешённому адресу: " + uri.Host);

            var r = (HttpWebRequest)WebRequest.Create(uri);
            r.UserAgent = "EpsPrint-Updater";
            r.Accept = accept;
            r.AllowAutoRedirect = false;
            r.Timeout = 30000;
            r.ReadWriteTimeout = 60000;
            r.Proxy = WebRequest.GetSystemWebProxy();
            if (github) r.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            if (withToken && github && token != null)   // токен (если задан) отправляется только на api.github.com
                r.Headers["Authorization"] = "Bearer " + token;
            return r;
        }

        /// <summary>github.com/…/releases/latest перенаправляет на …/releases/tag/vX.Y.Z — берём тег из адреса.</summary>
        string LatestTagViaWeb()
        {
            EnsureTls();
            using (var resp = Send(Request("https://github.com/" + repo + "/releases/latest", false, "text/html")))
            {
                int code = (int)resp.StatusCode;
                string loc = resp.Headers["Location"];
                if (code < 300 || code >= 400 || string.IsNullOrEmpty(loc))
                    throw new Exception("На GitHub ещё нет ни одного релиза.");
                const string marker = "/releases/tag/";
                int i = loc.IndexOf(marker, StringComparison.Ordinal);
                if (i < 0) throw new Exception("На GitHub ещё нет ни одного релиза.");
                string tag = Uri.UnescapeDataString(loc.Substring(i + marker.Length).Split('?', '#', '/')[0]);
                if (!Regex.IsMatch(tag, @"^[vV]?\d+\.\d+\.\d+$")) throw new Exception("Странный тег релиза: " + tag);
                return tag;
            }
        }

        string GetText(string url)
        {
            EnsureTls();
            using (var resp = Send(Request(url, true, "application/vnd.github+json")))
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        byte[] Download(string apiUrl)
        {
            EnsureTls();
            string url = apiUrl;
            bool first = true;
            for (int hop = 0; hop < 5; hop++)
            {
                using (var resp = Send(Request(url, first, "application/octet-stream")))
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 300 && code < 400)
                    {
                        string loc = resp.Headers["Location"];
                        if (string.IsNullOrEmpty(loc)) throw new Exception("GitHub вернул перенаправление без адреса.");
                        url = new Uri(new Uri(url), loc).ToString();
                        first = false;   // на хранилище файлов токен не отправляем
                        continue;
                    }
                    if (resp.ContentLength > MaxDownload) throw new Exception("Файл обновления слишком большой.");
                    using (var s = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ms.Length > MaxDownload) throw new Exception("Файл обновления слишком большой.");
                        }
                        return ms.ToArray();
                    }
                }
            }
            throw new Exception("Слишком много перенаправлений.");
        }

        static HttpWebResponse Send(HttpWebRequest r)
        {
            try
            {
                return (HttpWebResponse)r.GetResponse();
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new Exception("Нет связи с GitHub: " + ex.Message);
                int code = (int)resp.StatusCode;
                string text = "";
                try
                {
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        var buf = new char[400];
                        text = new string(buf, 0, sr.Read(buf, 0, buf.Length)).Trim();
                    }
                }
                catch { }
                resp.Close();
                if (code == 401) throw new Exception("GitHub не принял токен из update.ini (401).");
                if ((code == 403 || code == 429) && text.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new Exception("GitHub ограничил число запросов с вашего интернет-адреса (" + code + "). Попробуйте позже.\n\n" + text);
                if (code == 403) throw new Exception("GitHub отказал в доступе (403).\n\n" + text);
                if (code == 404) throw new Exception("Не найден репозиторий или релиз (404). Возможно, репозиторий закрыт или ещё нет ни одного релиза.");
                throw new Exception("Ошибка GitHub: HTTP " + code);
            }
        }

        // ───────── Прочее ─────────

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        static string Short(Version v)
        {
            return v.Major + "." + v.Minor + "." + v.Build;
        }

        void Fail(Exception ex)
        {
            bar.Style = ProgressBarStyle.Continuous;
            bar.Value = 0;
            status.Text = "Не получилось обновиться.";
            notes.Text = ex.Message;
            notes.Visible = true;
            installBtn.Visible = false;
        }

        void RestartMain()
        {
            if (parentPid <= 0) return;   // запускали не из программы — ничего не открываем
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(appDir, "EpsPrint.exe"));
                psi.WorkingDirectory = appDir;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }

        void CleanupOld()
        {
            try
            {
                foreach (string f in Directory.GetFiles(appDir, "*.old"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
