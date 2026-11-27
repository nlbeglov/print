using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EpsPrint
{
    /// <summary>Чёрно-белая картинка: 1 бит на точку, 1 = чёрный, строки выровнены по байту (как в PBM).</summary>
    class MonoImage
    {
        public int Width, Height, Stride;
        public byte[] Bits;

        public bool Get(int x, int y)
        {
            return (Bits[y * Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
        }

        public static MonoImage Blank(int w, int h)
        {
            var r = new MonoImage { Width = w, Height = h, Stride = (w + 7) / 8 };
            r.Bits = new byte[r.Stride * h];
            return r;
        }

        /// <summary>Накладывает чёрные точки src в позицию (ox, oy); всё, что за краем, обрезается.</summary>
        public void Blit(MonoImage src, int ox, int oy)
        {
            for (int y = 0; y < src.Height; y++)
            {
                int ty = oy + y;
                if (ty < 0 || ty >= Height) continue;
                for (int x = 0; x < src.Width; x++)
                {
                    int tx = ox + x;
                    if (tx < 0 || tx >= Width || !src.Get(x, y)) continue;
                    Bits[ty * Stride + (tx >> 3)] |= (byte)(0x80 >> (tx & 7));
                }
            }
        }

        /// <summary>Поворот на 90° по часовой (как в превью).</summary>
        public MonoImage Rotate90()
        {
            var r = new MonoImage { Width = Height, Height = Width };
            r.Stride = (r.Width + 7) / 8;
            r.Bits = new byte[r.Stride * r.Height];
            for (int y2 = 0; y2 < r.Height; y2++)
                for (int x2 = 0; x2 < r.Width; x2++)
                    if (Get(y2, Height - 1 - x2))
                        r.Bits[y2 * r.Stride + (x2 >> 3)] |= (byte)(0x80 >> (x2 & 7));
            return r;
        }
    }

    enum LabelLanguage { Epl, Zpl }

    /// <summary>
    /// Прямая печать на этикеточный принтер (Zebra и совместимые) без драйвера Windows:
    /// размер этикетки и зазор передаются принтеру в каждой задаче, поэтому печать не «съезжает»
    /// на два стикера при смене размера.
    /// </summary>
    static class LabelPrinter
    {
        /// <summary>Рендер EPS в 1-битную картинку точно под разрешение головки.</summary>
        public static MonoImage RenderMono(string epsPath, float dpi, string tempDir)
        {
            string exe = Ghostscript.FindExe();
            if (exe == null) throw new Exception("Не найден Ghostscript.");
            string outFile = Path.Combine(tempDir, "m_" + Guid.NewGuid().ToString("N") + ".pbm");
            string r = dpi.ToString("0.###", CultureInfo.InvariantCulture);
            var psi = new ProcessStartInfo(exe,
                "-dSAFER -dBATCH -dNOPAUSE -dQUIET -dEPSCrop -sDEVICE=pbmraw -r" + r +
                " \"-sOutputFile=" + outFile + "\" \"" + epsPath + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            string output;
            int code;
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                string so = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } throw new Exception("Ghostscript не ответил."); }
                output = (so + "\n" + errTask.Result).Trim();
                code = p.ExitCode;
            }
            if (code != 0 || !File.Exists(outFile))
                throw new Exception("Ghostscript не смог обработать файл (код " + code + ").\n" + output);
            byte[] data = File.ReadAllBytes(outFile);
            try { File.Delete(outFile); } catch { }
            return ParsePbm(data);
        }

        static MonoImage ParsePbm(byte[] d)
        {
            int pos = 0;
            Func<string> token = () =>
            {
                while (pos < d.Length)
                {
                    if (d[pos] == '#') { while (pos < d.Length && d[pos] != '\n') pos++; }
                    else if (char.IsWhiteSpace((char)d[pos])) pos++;
                    else break;
                }
                int start = pos;
                while (pos < d.Length && !char.IsWhiteSpace((char)d[pos])) pos++;
                return Encoding.ASCII.GetString(d, start, pos - start);
            };
            if (token() != "P4") throw new Exception("Неожиданный формат картинки от Ghostscript.");
            int w = int.Parse(token(), CultureInfo.InvariantCulture);
            int h = int.Parse(token(), CultureInfo.InvariantCulture);
            pos++; // один пробельный символ перед данными
            var img = new MonoImage { Width = w, Height = h, Stride = (w + 7) / 8 };
            img.Bits = new byte[img.Stride * h];
            if (d.Length - pos < img.Bits.Length) throw new Exception("Картинка от Ghostscript обрезана.");
            Buffer.BlockCopy(d, pos, img.Bits, 0, img.Bits.Length);
            return img;
        }

        // ───────── Команды принтера ─────────

        /// <summary>
        /// Одна наклейка (copies штук) в EPL2 — TLP 2844, LP 2844 и т.п.
        /// Наклейка лежит по центру головки, а отсчёт X идёт от левого края головки,
        /// поэтому картинку размером с наклейку ставим в X = (ширина головки − ширина наклейки) / 2.
        /// </summary>
        public static void AppendEpl(Stream s, MonoImage img, int copies, int gapDots, int headDots)
        {
            int x = Math.Max(0, (headDots - img.Width) / 2);
            Ascii(s, "\r\nN\r\n");
            Ascii(s, "q" + Math.Max(headDots, img.Width) + "\r\n");   // вся ширина головки
            Ascii(s, "Q" + img.Height + "," + gapDots + "\r\n");       // длина наклейки и зазор
            Ascii(s, "R0,0\r\nZT\r\n");
            Ascii(s, "GW" + x + ",0," + img.Stride + "," + img.Height + ",");
            // В EPL бит 0 = чёрная точка, поэтому инвертируем
            var row = new byte[img.Stride];
            for (int y = 0; y < img.Height; y++)
            {
                for (int i = 0; i < img.Stride; i++) row[i] = (byte)~img.Bits[y * img.Stride + i];
                s.Write(row, 0, row.Length);
            }
            Ascii(s, "\r\nP" + copies + "\r\n");
        }

        /// <summary>Одна этикетка (copies штук) в ZPL — модели с «-Z», GK/GX/ZD и т.п.</summary>
        public static void AppendZpl(Stream s, MonoImage img, int copies, int headDots)
        {
            int total = img.Stride * img.Height;
            int x = Math.Max(0, (headDots - img.Width) / 2);
            var sb = new StringBuilder();
            sb.Append("^XA^MNY^LH0,0^PW").Append(Math.Max(headDots, img.Width)).Append("^LL").Append(img.Height);
            sb.Append("^FO").Append(x).Append(",0^GFA,").Append(total).Append(',').Append(total).Append(',').Append(img.Stride).Append(',');
            const string hex = "0123456789ABCDEF";
            foreach (byte b in img.Bits) { sb.Append(hex[b >> 4]); sb.Append(hex[b & 15]); }
            sb.Append("^FS^PQ").Append(copies).Append("^XZ\r\n");
            Ascii(s, sb.ToString());
        }

        /// <summary>Автокалибровка датчика этикеток (принтер протянет несколько этикеток).</summary>
        public static byte[] CalibrateCommand(LabelLanguage lang)
        {
            return Encoding.ASCII.GetBytes(lang == LabelLanguage.Epl ? "\r\nxa\r\n" : "~JC\r\n");
        }

        static void Ascii(Stream s, string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        /// <summary>Похоже ли имя принтера на этикеточный Zebra.</summary>
        public static bool LooksLikeLabelPrinter(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToUpperInvariant();
            return n.Contains("ZEBRA") || n.Contains("ZDESIGNER") || n.Contains("TLP") ||
                   n.Contains("LP 2844") || n.Contains("LP2844") || n.Contains("EPL") || n.Contains("ZPL");
        }

        /// <summary>TLP/LP 2844 без «-Z» — EPL, остальные Zebra — ZPL.</summary>
        public static LabelLanguage GuessLanguage(string name)
        {
            string n = (name ?? "").ToUpperInvariant();
            if (n.Contains("-Z") || n.Contains("ZPL")) return LabelLanguage.Zpl;
            if (n.Contains("2844") || n.Contains("2824") || n.Contains("3842") || n.Contains("3844") || n.Contains("EPL")) return LabelLanguage.Epl;
            return LabelLanguage.Zpl;
        }
    }

    /// <summary>Отправка «сырых» данных на принтер через очередь печати Windows (без обработки драйвером).</summary>
    static class RawPrinter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class DOCINFOW
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool ClosePrinter(IntPtr hPrinter);
        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOW di);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool EndDocPrinter(IntPtr hPrinter);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool StartPagePrinter(IntPtr hPrinter);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool EndPagePrinter(IntPtr hPrinter);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

        public static void Send(string printerName, string docName, byte[] data)
        {
            IntPtr h;
            if (!OpenPrinter(printerName, out h, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось открыть принтер «" + printerName + "»");
            try
            {
                // RAW — классические драйверы; XPS_PASS — новые драйверы на основе XPS
                int job = 0;
                foreach (string type in new[] { "RAW", "XPS_PASS" })
                {
                    job = StartDocPrinter(h, 1, new DOCINFOW { pDocName = docName, pDataType = type });
                    if (job != 0) break;
                }
                if (job == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Принтер не принимает прямую печать");
                try
                {
                    if (!StartPagePrinter(h)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    int written;
                    if (!WritePrinter(h, data, data.Length, out written) || written != data.Length)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось передать данные принтеру");
                    EndPagePrinter(h);
                }
                finally { EndDocPrinter(h); }
            }
            finally { ClosePrinter(h); }
        }
    }
}
