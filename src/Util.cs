using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EpsPrint
{
    /// <summary>Настройки программы в %APPDATA%\EpsPrint\settings.ini</summary>
    class AppSettings
    {
        readonly Dictionary<string, string> d = new Dictionary<string, string>();
        readonly string path;

        public AppSettings()
        {
            path = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpsPrint"), "settings.ini");
            try
            {
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                    }
                }
            }
            catch { }
        }

        public string Get(string key, string def)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : def;
        }

        public decimal GetNum(string key, decimal def)
        {
            decimal v;
            return decimal.TryParse(Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public bool GetBool(string key, bool def)
        {
            string v = Get(key, null);
            return v == null ? def : v == "1";
        }

        public void Set(string key, string value) { d[key] = value ?? ""; }
        public void Set(string key, decimal value) { d[key] = value.ToString(CultureInfo.InvariantCulture); }
        public void Set(string key, bool value) { d[key] = value ? "1" : "0"; }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }
    }

    static class TextUtil
    {
        /// <summary>Простое форматирование JSON с отступами (без внешних библиотек).</summary>
        public static string PrettyJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inStr = false, esc = false;
            foreach (char c in json)
            {
                if (inStr)
                {
                    sb.Append(c);
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{':
                    case '[': sb.Append(c); indent++; NewLine(sb, indent); break;
                    case '}':
                    case ']': indent = Math.Max(0, indent - 1); NewLine(sb, indent); sb.Append(c); break;
                    case ',': sb.Append(c); NewLine(sb, indent); break;
                    case ':': sb.Append(": "); break;
                    default: if (!char.IsWhiteSpace(c)) sb.Append(c); break;
                }
            }
            // А -> А
            return Regex.Replace(sb.ToString(), @"\\u([0-9a-fA-F]{4})",
                m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }

        static void NewLine(StringBuilder sb, int indent)
        {
            sb.Append("\r\n");
            sb.Append(' ', indent * 2);
        }

        /// <summary>«…_00850027846491_0.eps» → GTIN «00850027846491» и номер кода 0.</summary>
        public static bool TryParseGtin(string name, out string gtin, out int index)
        {
            gtin = null;
            index = 0;
            var m = Regex.Match(Path.GetFileName(name), @"(?:^|_)(\d{8,14})_(\d+)\.eps[fi]?$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            gtin = m.Groups[1].Value;
            return int.TryParse(m.Groups[2].Value, out index);
        }

        /// <summary>«318d3a02-…-eb954bd40432_00850027846491_0.eps» → «00850027846491_0.eps»</summary>
        public static string DisplayName(string name)
        {
            string n = Path.GetFileName(name);
            var m = Regex.Match(n, @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}_(.+)$");
            return m.Success ? m.Groups[1].Value : n;
        }
    }
}
