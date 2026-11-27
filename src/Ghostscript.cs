using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace EpsPrint
{
    /// <summary>Рендер EPS в картинку через Ghostscript (gswin64c.exe).</summary>
    static class Ghostscript
    {
        static string cachedExe;

        public static string FindExe()
        {
            if (cachedExe != null && File.Exists(cachedExe)) return cachedExe;

            // 1) рядом с программой: gs\bin\gswin64c.exe, gs\gswin64c.exe, gswin64c.exe
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string n in new[] { "gswin64c.exe", "gswin32c.exe" })
            {
                foreach (string c in new[] {
                    Path.Combine(Path.Combine(Path.Combine(baseDir, "gs"), "bin"), n),
                    Path.Combine(Path.Combine(baseDir, "gs"), n),
                    Path.Combine(baseDir, n) })
                {
                    if (File.Exists(c)) return cachedExe = c;
                }
            }

            // 2) установленный: C:\Program Files\gs\gs10.xx.x\bin\gswin64c.exe (берём самую новую версию)
            var roots = new List<string>();
            roots.Add(Environment.GetEnvironmentVariable("ProgramW6432"));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            string best = null;
            Version bestVer = null;
            foreach (string pf in roots)
            {
                if (string.IsNullOrEmpty(pf)) continue;
                string gsRoot = Path.Combine(pf, "gs");
                if (!Directory.Exists(gsRoot)) continue;
                foreach (string dir in Directory.GetDirectories(gsRoot))
                {
                    string bin = Path.Combine(dir, "bin");
                    string exe = Path.Combine(bin, "gswin64c.exe");
                    if (!File.Exists(exe)) exe = Path.Combine(bin, "gswin32c.exe");
                    if (!File.Exists(exe)) continue;
                    Version v;
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("gs", StringComparison.OrdinalIgnoreCase)) name = name.Substring(2);
                    if (!Version.TryParse(name, out v)) v = new Version(0, 0);
                    if (bestVer == null || v > bestVer) { bestVer = v; best = exe; }
                }
            }
            return cachedExe = best;
        }

        /// <summary>Читает %%BoundingBox из EPS и возвращает размер в мм.</summary>
        public static bool TryReadBoundingBox(string path, out float wMm, out float hMm)
        {
            wMm = 0; hMm = 0;
            byte[] buf;
            using (var fs = File.OpenRead(path))
            {
                // EPS с бинарным заголовком (DOS EPS): C5 D0 D3 C6, дальше смещение PostScript-части
                var head = new byte[8];
                int hr = fs.Read(head, 0, 8);
                long start = 0;
                if (hr == 8 && head[0] == 0xC5 && head[1] == 0xD0 && head[2] == 0xD3 && head[3] == 0xC6)
                    start = BitConverter.ToUInt32(head, 4);
                fs.Seek(start, SeekOrigin.Begin);
                int n = (int)Math.Min(fs.Length - start, 512 * 1024);
                if (n <= 0) return false;
                buf = new byte[n];
                int read = 0;
                while (read < n)
                {
                    int r = fs.Read(buf, read, n - read);
                    if (r <= 0) break;
                    read += r;
                }
            }
            string text = Encoding.GetEncoding(28591).GetString(buf);
            // Ghostscript с -dEPSCrop обрезает по %%HiResBoundingBox, если он есть, — берём его же
            const string nums = @":[ \t]*(-?[\d.]+)[ \t]+(-?[\d.]+)[ \t]+(-?[\d.]+)[ \t]+(-?[\d.]+)";
            var m = Regex.Match(text, @"%%HiResBoundingBox" + nums);
            if (!m.Success) m = Regex.Match(text, @"%%BoundingBox" + nums);
            if (!m.Success) return false;
            float x1, y1, x2, y2;
            var ci = CultureInfo.InvariantCulture;
            if (!float.TryParse(m.Groups[1].Value, NumberStyles.Float, ci, out x1) ||
                !float.TryParse(m.Groups[2].Value, NumberStyles.Float, ci, out y1) ||
                !float.TryParse(m.Groups[3].Value, NumberStyles.Float, ci, out x2) ||
                !float.TryParse(m.Groups[4].Value, NumberStyles.Float, ci, out y2)) return false;
            float w = x2 - x1, h = y2 - y1;
            if (w <= 0 || h <= 0) return false;
            wMm = w / 72f * 25.4f;
            hMm = h / 72f * 25.4f;
            return true;
        }

        /// <summary>Рендерит EPS (обрезанный по BoundingBox) в Bitmap с заданным разрешением.</summary>
        public static Bitmap Render(string epsPath, float dpi, bool antialias, string tempDir)
        {
            return Render(epsPath, dpi, dpi, antialias, tempDir);
        }

        /// <summary>Рендер с отдельным разрешением по X и Y (под точную сетку принтера).</summary>
        public static Bitmap Render(string epsPath, float dpiX, float dpiY, bool antialias, string tempDir)
        {
            string exe = FindExe();
            if (exe == null)
                throw new Exception("Не найден Ghostscript. Установите его (см. инструкцию) и перезапустите программу.");

            string outFile = Path.Combine(tempDir, "r_" + Guid.NewGuid().ToString("N") + ".png");
            var args = new StringBuilder();
            args.Append("-dSAFER -dBATCH -dNOPAUSE -dQUIET -dEPSCrop -sDEVICE=png16m ");
            args.Append("-r" + dpiX.ToString("0.###", CultureInfo.InvariantCulture) + "x" + dpiY.ToString("0.###", CultureInfo.InvariantCulture) + " ");
            if (antialias) args.Append("-dTextAlphaBits=4 -dGraphicsAlphaBits=4 ");
            args.Append(Quote("-sOutputFile=" + outFile) + " " + Quote(epsPath));

            var psi = new ProcessStartInfo(exe, args.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;

            string output;
            int exitCode;
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(); } catch { }
                    throw new Exception("Ghostscript не ответил за 2 минуты.");
                }
                output = (stdout + "\n" + errTask.Result).Trim();
                exitCode = p.ExitCode;
            }

            if (exitCode != 0 || !File.Exists(outFile))
            {
                try { File.Delete(outFile); } catch { }
                if (output.Length > 600) output = output.Substring(0, 600) + "…";
                throw new Exception("Ghostscript не смог обработать файл (код " + exitCode + ").\n" + output);
            }

            Bitmap bmp;
            using (var fs = new FileStream(outFile, FileMode.Open, FileAccess.Read))
            using (var img = new Bitmap(fs))
            {
                bmp = CopyAs24bpp(img);
            }
            try { File.Delete(outFile); } catch { }
            bmp.SetResolution(dpiX, dpiY);
            return bmp;
        }

        /// <summary>Точная попиксельная копия в 24 bpp (без альфа-канала и без пересчёта).</summary>
        static Bitmap CopyAs24bpp(Bitmap src)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            var sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var dd = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int rowBytes = src.Width * 3;
                    var row = new byte[rowBytes];
                    for (int y = 0; y < src.Height; y++)
                    {
                        Marshal.Copy(new IntPtr(sd.Scan0.ToInt64() + (long)y * sd.Stride), row, 0, rowBytes);
                        Marshal.Copy(row, 0, new IntPtr(dd.Scan0.ToInt64() + (long)y * dd.Stride), rowBytes);
                    }
                }
                finally { dst.UnlockBits(dd); }
            }
            finally { src.UnlockBits(sd); }
            return dst;
        }

        static string Quote(string s)
        {
            return "\"" + s + "\"";
        }
    }
}
