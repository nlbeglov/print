using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EpsPrint
{
    class MainForm : Form
    {
        // Лимиты для защиты от «битых» и слишком больших архивов
        const long MaxEntryBytes = 100L * 1024 * 1024;   // один файл в архиве
        const long MaxTotalBytes = 500L * 1024 * 1024;   // всего распаковано за раз
        const int MaxFiles = 2000;

        readonly string tempDir;
        readonly AppSettings settings = new AppSettings();
        PrinterSettings printerSettings = new PrinterSettings();
        List<PageLayout> pages = new List<PageLayout>();
        string layoutWarning;
        bool initializing = true;
        bool suppressPrinterChange;
        bool suppressCheckEvents;
        bool rendering, renderAgain, printing;
        int fileCounter;
        long extractedBytes;

        CheckedListBox fileList;
        TextBox infoBox;
        PreviewPanel preview;
        Label pageLabel;
        Button prevBtn, nextBtn;
        ComboBox printerCombo, paperCombo;
        NumericUpDown customW, customH, percentNum, marginNum, gapNum, copiesNum;
        RadioButton portraitRb, landscapeRb, fitRb, actualRb, customRb, oneRb, multiRb;
        CheckBox rotateCb, collateCb;
        RadioButton allPagesRb, currentPageRb, rangeRb;
        TextBox rangeBox;
        Button printBtn;
        Label summaryLabel;
        ToolStripStatusLabel statusLabel;

        public MainForm(string[] args)
        {
            tempDir = Path.Combine(Path.GetTempPath(), "EpsPrint_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            CleanupOldFiles();

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            Text = "EPS Печать  v" + ver.Major + "." + ver.Minor + "." + ver.Build;
            Font = new Font("Segoe UI", 9f);
            Size = new Size(1280, 820);
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            LoadPrinters();
            LoadSettingsToUi();
            initializing = false;
            RebuildPages();
            EnableDrop(this);

            Shown += (s, e) =>
            {
                CheckGhostscript();
                if (args != null && args.Length > 0) AddPaths(args);
            };
            FormClosing += (s, e) => SaveSettings();
            FormClosed += (s, e) => { try { Directory.Delete(tempDir, true); } catch { } };
        }

        // ───────────────────────── Интерфейс ─────────────────────────

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // Верхняя панель
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6), WrapContents = false };
            var openBtn = new Button { Text = "Открыть архив или файлы…", AutoSize = true, Height = 32 };
            openBtn.Click += (s, e) => OpenFiles();
            var clearBtn = new Button { Text = "Очистить список", AutoSize = true, Height = 32 };
            clearBtn.Click += (s, e) => ClearAll();
            var hint = new Label { Text = "…или просто перетащите zip-архив в это окно", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 9, 0, 0) };
            var updateBtn = new Button { Text = "Проверить обновления", AutoSize = true, Height = 32, Margin = new Padding(40, 3, 3, 3) };
            updateBtn.Click += (s, e) => StartUpdater();
            bar.Controls.AddRange(new Control[] { openBtn, clearBtn, hint, updateBtn });
            root.Controls.Add(bar, 0, 0);
            root.SetColumnSpan(bar, 3);

            // Левая колонка: файлы + информация
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(6, 0, 0, 6) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            left.Controls.Add(Header("Файлы для печати"), 0, 0);
            fileList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
            fileList.ItemCheck += (s, e) => { if (!suppressCheckEvents) BeginInvoke(new Action(RebuildPages)); };
            fileList.SelectedIndexChanged += (s, e) => JumpToSelected();
            left.Controls.Add(fileList, 0, 1);
            var selBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            var allBtn = new Button { Text = "Выбрать все", AutoSize = true };
            allBtn.Click += (s, e) => SetAllChecked(true);
            var noneBtn = new Button { Text = "Снять все", AutoSize = true };
            noneBtn.Click += (s, e) => SetAllChecked(false);
            selBar.Controls.Add(allBtn);
            selBar.Controls.Add(noneBtn);
            left.Controls.Add(selBar, 0, 2);
            left.Controls.Add(Header("Информация из архива (на печать не идёт)"), 0, 3);
            infoBox = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                WordWrap = false, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window
            };
            left.Controls.Add(infoBox, 0, 4);
            root.Controls.Add(left, 0, 1);

            // Центр: превью
            var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(6, 0, 6, 6) };
            center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            preview = new PreviewPanel { Dock = DockStyle.Fill };
            center.Controls.Add(preview, 0, 0);
            var nav = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false };
            prevBtn = new Button { Text = "◀", Width = 48, Height = 30 };
            nextBtn = new Button { Text = "▶", Width = 48, Height = 30 };
            pageLabel = new Label { AutoSize = true, Margin = new Padding(12, 8, 12, 0) };
            prevBtn.Click += (s, e) => GoPage(-1);
            nextBtn.Click += (s, e) => GoPage(1);
            nav.Controls.AddRange(new Control[] { prevBtn, pageLabel, nextBtn });
            center.Controls.Add(nav, 0, 1);
            root.Controls.Add(center, 1, 1);

            // Правая колонка: настройки
            const int W = 290;
            var right = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(4, 0, 6, 6)
            };

            right.Controls.Add(Header("Принтер"));
            printerCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W };
            printerCombo.SelectedIndexChanged += (s, e) => OnPrinterChanged();
            right.Controls.Add(printerCombo);
            var propsBtn = new Button { Text = "Дополнительные настройки принтера…", Width = W, Height = 28 };
            propsBtn.Click += (s, e) => ShowPrinterDialog();
            right.Controls.Add(propsBtn);

            right.Controls.Add(Header("Бумага"));
            paperCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W, DropDownHeight = 400 };
            paperCombo.SelectedIndexChanged += (s, e) => RebuildPages();
            right.Controls.Add(paperCombo);
            customW = Num(10, 2000, 1, 100);
            customH = Num(10, 2000, 1, 150);
            right.Controls.Add(Row("Ширина, мм:", customW));
            right.Controls.Add(Row("Высота, мм:", customH));
            portraitRb = Radio("Книжная");
            landscapeRb = Radio("Альбомная");
            portraitRb.Checked = true;
            right.Controls.Add(Group(FlowDirection.LeftToRight, portraitRb, landscapeRb));

            right.Controls.Add(Header("Масштаб"));
            fitRb = Radio("Вписать в лист");
            actualRb = Radio("Реальный размер (100%)");
            customRb = Radio("Свой масштаб, %:");
            percentNum = Num(5, 1000, 0, 100);
            percentNum.Enter += (s, e) => customRb.Checked = true;
            Exclusive(fitRb, actualRb, customRb);
            right.Controls.Add(fitRb);
            right.Controls.Add(actualRb);
            right.Controls.Add(Group(FlowDirection.LeftToRight, customRb, percentNum));
            rotateCb = new CheckBox { Text = "Повернуть на 90°", AutoSize = true };
            rotateCb.CheckedChanged += (s, e) => RebuildPages();
            right.Controls.Add(rotateCb);

            right.Controls.Add(Header("Размещение"));
            oneRb = Radio("Один рисунок на лист");
            multiRb = Radio("Несколько на листе (экономия бумаги)");
            oneRb.Checked = true;
            right.Controls.Add(Group(FlowDirection.TopDown, oneRb, multiRb));
            marginNum = Num(0, 50, 1, 5);
            gapNum = Num(0, 50, 1, 3);
            right.Controls.Add(Row("Поля, мм:", marginNum));
            right.Controls.Add(Row("Промежуток, мм:", gapNum));

            right.Controls.Add(Header("Копии"));
            copiesNum = Num(1, 999, 0, 1);
            right.Controls.Add(Row("Копий каждого:", copiesNum));
            collateCb = new CheckBox { Text = "Комплектами (1,2,3, 1,2,3…)", AutoSize = true, Checked = true };
            collateCb.CheckedChanged += (s, e) => RebuildPages();
            right.Controls.Add(collateCb);

            right.Controls.Add(Header("Что печатать"));
            allPagesRb = Radio("Все листы");
            currentPageRb = Radio("Только текущий лист (пробная печать)");
            rangeRb = Radio("Листы:");
            rangeBox = new TextBox { Width = 130 };
            rangeBox.TextChanged += (s, e) =>
            {
                if (!rangeRb.Checked && rangeBox.Focused) rangeRb.Checked = true;
                else RebuildPages();
            };
            rangeBox.Enter += (s, e) => rangeRb.Checked = true;
            Exclusive(allPagesRb, currentPageRb, rangeRb);
            allPagesRb.Checked = true;
            right.Controls.Add(allPagesRb);
            right.Controls.Add(currentPageRb);
            right.Controls.Add(Group(FlowDirection.LeftToRight, rangeRb, rangeBox));
            right.Controls.Add(new Label { Text = "например: 1-3, 5", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(24, 0, 3, 3) });

            printBtn = new Button
            {
                Text = "ПЕЧАТЬ", Width = W, Height = 50,
                Font = new Font(this.Font.FontFamily, 13f, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 16, 3, 6)
            };
            printBtn.FlatAppearance.BorderSize = 0;
            printBtn.Click += (s, e) => DoPrint();
            right.Controls.Add(printBtn);
            summaryLabel = new Label { AutoSize = true, MaximumSize = new Size(W, 0) };
            right.Controls.Add(summaryLabel);
            root.Controls.Add(right, 2, 1);

            var status = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("Готово");
            status.Items.Add(statusLabel);

            Controls.Add(root);
            Controls.Add(status);
        }

        Label Header(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(this.Font, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };
        }

        NumericUpDown Num(decimal min, decimal max, int decimals, decimal value)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Value = value, Width = 80 };
            n.ValueChanged += (s, e) => RebuildPages();
            return n;
        }

        RadioButton Radio(string text)
        {
            var r = new RadioButton { Text = text, AutoSize = true };
            r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) RebuildPages(); };
            return r;
        }

        static void Exclusive(params RadioButton[] group)
        {
            foreach (var r in group)
            {
                var me = r;
                me.CheckedChanged += (s, e) =>
                {
                    if (!me.Checked) return;
                    foreach (var o in group) if (o != me) o.Checked = false;
                };
            }
        }

        static Control Group(FlowDirection dir, params Control[] controls)
        {
            var f = new FlowLayoutPanel { FlowDirection = dir, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        static Control Row(string label, Control c)
        {
            var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            t.Controls.Add(c, 1, 0);
            return t;
        }

        void SetStatus(string text)
        {
            statusLabel.Text = text;
        }

        // ───────────────────────── Принтер и бумага ─────────────────────────

        void LoadPrinters()
        {
            printerCombo.Items.Clear();
            try
            {
                foreach (string p in PrinterSettings.InstalledPrinters) printerCombo.Items.Add(p);
            }
            catch { }
        }

        void SelectInitialPrinter(string saved)
        {
            string pick = null;
            if (!string.IsNullOrEmpty(saved) && printerCombo.Items.Contains(saved)) pick = saved;
            if (pick == null)
                foreach (string p in printerCombo.Items)
                    if (p.IndexOf("M132", StringComparison.OrdinalIgnoreCase) >= 0) { pick = p; break; }
            if (pick == null)
            {
                string def = new PrinterSettings().PrinterName;
                if (printerCombo.Items.Contains(def)) pick = def;
            }
            if (pick == null && printerCombo.Items.Count > 0) pick = (string)printerCombo.Items[0];
            if (pick != null) printerCombo.SelectedItem = pick;
        }

        void OnPrinterChanged()
        {
            if (suppressPrinterChange) return;
            var name = printerCombo.SelectedItem as string;
            if (name == null) return;
            string prevPaper = CurrentPaperName();
            printerSettings = new PrinterSettings();
            printerSettings.PrinterName = name;
            LoadPapers(prevPaper ?? settings.Get("paper", "A4"));
            RebuildPages();
        }

        void LoadPapers(string preferName)
        {
            paperCombo.BeginUpdate();
            paperCombo.Items.Clear();
            try
            {
                foreach (PaperSize ps in printerSettings.PaperSizes)
                    if (ps.Width > 0 && ps.Height > 0) paperCombo.Items.Add(new PaperItem { Size = ps });
            }
            catch { }
            paperCombo.Items.Add(new PaperItem { IsCustom = true });
            paperCombo.EndUpdate();

            int idx = -1;
            for (int i = 0; i < paperCombo.Items.Count; i++)
            {
                var pi = (PaperItem)paperCombo.Items[i];
                if (preferName == "__custom" ? pi.IsCustom : (!pi.IsCustom && pi.Size.PaperName == preferName)) { idx = i; break; }
            }
            if (idx < 0)
                for (int i = 0; i < paperCombo.Items.Count; i++)
                {
                    var pi = (PaperItem)paperCombo.Items[i];
                    if (!pi.IsCustom && pi.Size.Kind == PaperKind.A4) { idx = i; break; }
                }
            paperCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }

        string CurrentPaperName()
        {
            var pi = paperCombo.SelectedItem as PaperItem;
            if (pi == null) return null;
            return pi.IsCustom ? "__custom" : pi.Size.PaperName;
        }

        void ShowPrinterDialog()
        {
            using (var dlg = new PrintDialog())
            {
                dlg.UseEXDialog = true;
                dlg.AllowSomePages = false;
                dlg.AllowSelection = false;
                dlg.AllowCurrentPage = false;
                printerSettings.Copies = (short)Math.Min(copiesNum.Value, 999);
                printerSettings.Collate = collateCb.Checked;
                var pi = paperCombo.SelectedItem as PaperItem;
                if (pi != null && !pi.IsCustom) printerSettings.DefaultPageSettings.PaperSize = pi.Size;
                printerSettings.DefaultPageSettings.Landscape = landscapeRb.Checked;
                dlg.PrinterSettings = printerSettings;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                printerSettings = dlg.PrinterSettings;
                initializing = true;
                try
                {
                    suppressPrinterChange = true;
                    if (printerCombo.Items.Contains(printerSettings.PrinterName))
                        printerCombo.SelectedItem = printerSettings.PrinterName;
                    suppressPrinterChange = false;
                    try { LoadPapers(printerSettings.DefaultPageSettings.PaperSize.PaperName); } catch { LoadPapers("A4"); }
                    try
                    {
                        bool land = printerSettings.DefaultPageSettings.Landscape;
                        landscapeRb.Checked = land;
                        portraitRb.Checked = !land;
                    }
                    catch { }
                    SetNum(copiesNum, printerSettings.Copies);
                    collateCb.Checked = printerSettings.Collate;
                }
                finally
                {
                    suppressPrinterChange = false;
                    initializing = false;
                }
                RebuildPages();
            }
        }

        // ───────────────────────── Настройки ─────────────────────────

        static void SetNum(NumericUpDown n, decimal v)
        {
            n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        }

        void LoadSettingsToUi()
        {
            SelectInitialPrinter(settings.Get("printer", ""));
            LoadPapers(settings.Get("paper", "A4"));
            SetNum(customW, settings.GetNum("customW", 100));
            SetNum(customH, settings.GetNum("customH", 150));
            bool land = settings.Get("orientation", "p") == "l";
            landscapeRb.Checked = land;
            portraitRb.Checked = !land;
            string sc = settings.Get("scale", "fit");
            fitRb.Checked = sc == "fit";
            actualRb.Checked = sc == "actual";
            customRb.Checked = sc == "custom";
            SetNum(percentNum, settings.GetNum("percent", 100));
            rotateCb.Checked = settings.GetBool("rotate", false);
            bool multi = settings.Get("layout", "one") == "multi";
            multiRb.Checked = multi;
            oneRb.Checked = !multi;
            SetNum(marginNum, settings.GetNum("margin", 5));
            SetNum(gapNum, settings.GetNum("gap", 3));
            collateCb.Checked = settings.GetBool("collate", true);
        }

        void SaveSettings()
        {
            settings.Set("printer", printerCombo.SelectedItem as string);
            settings.Set("paper", CurrentPaperName());
            settings.Set("customW", customW.Value);
            settings.Set("customH", customH.Value);
            settings.Set("orientation", landscapeRb.Checked ? "l" : "p");
            settings.Set("scale", fitRb.Checked ? "fit" : actualRb.Checked ? "actual" : "custom");
            settings.Set("percent", percentNum.Value);
            settings.Set("rotate", rotateCb.Checked);
            settings.Set("layout", multiRb.Checked ? "multi" : "one");
            settings.Set("margin", marginNum.Value);
            settings.Set("gap", gapNum.Value);
            settings.Set("collate", collateCb.Checked);
            settings.Save();
        }

        // ───────────────────────── Загрузка файлов ─────────────────────────

        void EnableDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            c.DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) BeginInvoke(new Action(() => AddPaths(files)));
            };
            foreach (Control child in c.Controls) EnableDrop(child);
        }

        void OpenFiles()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Выберите zip-архив или EPS-файлы";
                dlg.Filter = "Архивы и EPS (*.zip;*.eps)|*.zip;*.eps;*.epsf|Все файлы (*.*)|*.*";
                dlg.Multiselect = true;
                string last = settings.Get("lastDir", "");
                if (Directory.Exists(last)) dlg.InitialDirectory = last;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { settings.Set("lastDir", Path.GetDirectoryName(dlg.FileNames[0])); } catch { }
                AddPaths(dlg.FileNames);
            }
        }

        void AddPaths(IEnumerable<string> paths)
        {
            var added = new List<EpsItem>();
            var errors = new List<string>();
            extractedBytes = 0;
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (string p in paths)
                {
                    try
                    {
                        if (Directory.Exists(p))
                        {
                            var files = Directory.GetFiles(p, "*.*", SearchOption.AllDirectories);
                            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                            foreach (string f in files) AddFile(f, added);
                        }
                        else AddFile(p, added);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(Path.GetFileName(p) + ": " + ex.Message);
                    }
                }
            }
            finally { Cursor = Cursors.Default; }

            suppressCheckEvents = true;
            foreach (var it in added) fileList.Items.Add(it, true);
            suppressCheckEvents = false;

            if (errors.Count > 0)
                MessageBox.Show(this, string.Join("\n\n", errors.ToArray()), "Не всё удалось открыть", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (added.Count == 0)
                MessageBox.Show(this, "В выбранных файлах не найдено EPS-файлов.", "Нечего печатать", MessageBoxButtons.OK, MessageBoxIcon.Information);

            RebuildPages();
            RenderPending();
        }

        static bool IsEps(string ext)
        {
            return ext == ".eps" || ext == ".epsf" || ext == ".epsi";
        }

        string NewTempFile(string ext)
        {
            fileCounter++;
            return Path.Combine(tempDir, fileCounter.ToString("D5") + ext);
        }

        void CheckLimits(long size)
        {
            if (size > MaxEntryBytes) throw new Exception("Слишком большой файл внутри архива (больше 100 МБ).");
            extractedBytes += size;
            if (extractedBytes > MaxTotalBytes) throw new Exception("Слишком много данных за один раз (больше 500 МБ).");
            if (fileList.Items.Count + fileCounter > MaxFiles * 2) throw new Exception("Слишком много файлов.");
        }

        void AddFile(string f, List<EpsItem> added)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext == ".zip")
            {
                ExtractZip(f, Path.GetFileName(f), added, 0);
            }
            else if (IsEps(ext))
            {
                CheckLimits(new FileInfo(f).Length);
                // копируем во временную папку под безопасным именем (только латиница и цифры)
                string dest = NewTempFile(".eps");
                File.Copy(f, dest);
                added.Add(new EpsItem { Path = dest, Name = Path.GetFileName(f), DisplayName = TextUtil.DisplayName(f) });
            }
            else if (ext == ".json" || ext == ".txt")
            {
                if (new FileInfo(f).Length < 1024 * 1024)
                    AppendInfo(Path.GetFileName(f), File.ReadAllText(f, Encoding.UTF8));
            }
        }

        void ExtractZip(string zipPath, string title, List<EpsItem> added, int depth)
        {
            using (var za = ZipFile.OpenRead(zipPath))
            {
                var entries = za.Entries.Where(e => !string.IsNullOrEmpty(e.Name))
                                        .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).ToList();
                if (entries.Count > MaxFiles) throw new Exception("В архиве слишком много файлов.");
                foreach (var entry in entries)
                {
                    string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (IsEps(ext))
                    {
                        CheckLimits(entry.Length);
                        // Имя на диске генерируем сами — пути из архива никогда не используются
                        string dest = NewTempFile(".eps");
                        entry.ExtractToFile(dest);
                        added.Add(new EpsItem { Path = dest, Name = entry.Name, DisplayName = TextUtil.DisplayName(entry.Name) });
                    }
                    else if ((ext == ".json" || ext == ".txt") && entry.Length < 1024 * 1024)
                    {
                        using (var r = new StreamReader(entry.Open(), Encoding.UTF8))
                            AppendInfo(title + " / " + entry.FullName, r.ReadToEnd());
                    }
                    else if (ext == ".zip" && depth < 2)
                    {
                        CheckLimits(entry.Length);
                        string nested = NewTempFile(".zip");
                        entry.ExtractToFile(nested);
                        ExtractZip(nested, title + " / " + entry.FullName, added, depth + 1);
                    }
                }
            }
        }

        void AppendInfo(string title, string text)
        {
            string t = text.TrimStart();
            string body = (t.StartsWith("{") || t.StartsWith("[")) ? TextUtil.PrettyJson(t) : text;
            body = body.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var sb = new StringBuilder(infoBox.Text);
            if (sb.Length > 0) sb.Append("\r\n\r\n");
            sb.Append("── " + title + " ──\r\n");
            sb.Append(body);
            infoBox.Text = sb.ToString();
        }

        void ClearAll()
        {
            foreach (EpsItem it in fileList.Items)
                if (it.Preview != null) { it.Preview.Dispose(); it.Preview = null; }
            fileList.Items.Clear();
            infoBox.Clear();
            try
            {
                foreach (string f in Directory.GetFiles(tempDir)) { try { File.Delete(f); } catch { } }
            }
            catch { }
            RebuildPages();
            SetStatus("Список очищен");
        }

        void SetAllChecked(bool value)
        {
            suppressCheckEvents = true;
            for (int i = 0; i < fileList.Items.Count; i++) fileList.SetItemChecked(i, value);
            suppressCheckEvents = false;
            RebuildPages();
        }

        // ───────────────────────── Превью ─────────────────────────

        async void RenderPending()
        {
            if (rendering) { renderAgain = true; return; }
            rendering = true;
            try
            {
                do
                {
                    renderAgain = false;
                    var pending = fileList.Items.Cast<EpsItem>().Where(i => i.Preview == null && i.Error == null).ToList();
                    int n = 0;
                    foreach (var item in pending)
                    {
                        n++;
                        SetStatus(string.Format("Обработка {0} из {1}: {2}", n, pending.Count, item.DisplayName));
                        var cur = item;
                        try
                        {
                            Bitmap bmp = await Task.Run(() => RenderPreview(cur));
                            if (fileList.Items.Contains(cur)) cur.Preview = bmp;
                            else bmp.Dispose();
                        }
                        catch (Exception ex)
                        {
                            cur.Error = ex.Message;
                            SetStatus("Ошибка: " + cur.DisplayName);
                            MessageBox.Show(this, cur.DisplayName + "\n\n" + ex.Message, "Не удалось открыть файл", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        fileList.Invalidate();
                        RebuildPages();
                    }
                } while (renderAgain);
                SetStatus(fileList.Items.Count > 0 ? "Готово к печати" : "Готово");
            }
            finally { rendering = false; }
        }

        Bitmap RenderPreview(EpsItem it)
        {
            float w, h;
            if (!Ghostscript.TryReadBoundingBox(it.Path, out w, out h)) { w = 0; h = 0; }
            float dpi = 150;
            if (w > 0 && h > 0)
            {
                double px = (w / 25.4 * dpi) * (h / 25.4 * dpi);
                if (px > 6e6) dpi = (float)Math.Max(30, dpi * Math.Sqrt(6e6 / px));
            }
            var bmp = Ghostscript.Render(it.Path, dpi, true, tempDir);
            if (w <= 0 || h <= 0)
            {
                w = bmp.Width / dpi * 25.4f;
                h = bmp.Height / dpi * 25.4f;
            }
            it.WidthMm = w;
            it.HeightMm = h;
            return bmp;
        }

        LayoutOptions ReadOptions()
        {
            var o = new LayoutOptions();
            float w = 210, h = 297;
            var pi = paperCombo.SelectedItem as PaperItem;
            if (pi != null && pi.IsCustom) { w = (float)customW.Value; h = (float)customH.Value; }
            else if (pi != null) { w = pi.Size.Width * 0.254f; h = pi.Size.Height * 0.254f; }
            if (landscapeRb.Checked) { float t = w; w = h; h = t; }
            o.PaperWmm = w;
            o.PaperHmm = h;
            o.MarginMm = (float)marginNum.Value;
            o.GapMm = (float)gapNum.Value;
            o.Scale = fitRb.Checked ? ScaleMode.Fit : actualRb.Checked ? ScaleMode.Actual : ScaleMode.Custom;
            o.Percent = (float)percentNum.Value;
            o.MultiPerSheet = multiRb.Checked;
            o.Rotate90 = rotateCb.Checked;
            o.Collate = collateCb.Checked;
            o.Copies = (int)copiesNum.Value;
            return o;
        }

        List<EpsItem> SelectedReadyItems()
        {
            var list = new List<EpsItem>();
            for (int i = 0; i < fileList.Items.Count; i++)
            {
                if (!fileList.GetItemChecked(i)) continue;
                var it = (EpsItem)fileList.Items[i];
                if (it.Preview != null) list.Add(it);
            }
            return list;
        }

        void RebuildPages()
        {
            if (initializing) return;
            var o = ReadOptions();
            var sel = SelectedReadyItems();
            string warn;
            pages = PageLayouter.Build(sel, o, out warn);
            layoutWarning = warn;

            preview.PaperW = o.PaperWmm;
            preview.PaperH = o.PaperHmm;
            preview.MarginMm = o.MarginMm;
            preview.Pages = pages;
            if (preview.PageIndex >= pages.Count) preview.PageIndex = Math.Max(0, pages.Count - 1);
            preview.Invalidate();
            UpdatePageLabel();

            var pi = paperCombo.SelectedItem as PaperItem;
            bool custom = pi != null && pi.IsCustom;
            customW.Parent.Visible = custom;
            customH.Parent.Visible = custom;
            bool multiPossible = !fitRb.Checked;
            multiRb.Enabled = multiPossible;
            gapNum.Enabled = multiPossible && multiRb.Checked;

            int labels = 0;
            foreach (var p in pages) labels += p.Items.Count;
            var sb = new StringBuilder();
            sb.AppendFormat("Выбрано файлов: {0}\nВсего рисунков с копиями: {1}\nЛистов бумаги: {2}", sel.Count, labels, pages.Count);
            if (fitRb.Checked && multiRb.Checked) sb.Append("\n\n«Несколько на листе» работает только с реальным или своим масштабом.");
            if (pages.Count > 0 && !allPagesRb.Checked)
            {
                string rangeErr;
                var chosen = PagesToPrint(out rangeErr);
                if (chosen == null) sb.Append("\n\n⚠ " + rangeErr);
                else sb.AppendFormat("\nБудет напечатано листов: {0}", chosen.Count);
            }
            if (layoutWarning != null) sb.Append("\n\n⚠ " + layoutWarning);
            summaryLabel.Text = sb.ToString();
            summaryLabel.ForeColor = layoutWarning != null ? Color.DarkRed : SystemColors.ControlText;
            printBtn.Enabled = pages.Count > 0 && !printing;
        }

        void UpdatePageLabel()
        {
            int count = pages.Count;
            int idx = preview.PageIndex;
            pageLabel.Text = count == 0 ? "Нет листов" : string.Format("Лист {0} из {1}", idx + 1, count);
            currentPageRb.Text = count == 0 ? "Только текущий лист (пробная печать)"
                                            : "Только текущий лист — №" + (idx + 1) + " (пробная печать)";
            prevBtn.Enabled = idx > 0;
            nextBtn.Enabled = idx < count - 1;
        }

        /// <summary>Какие листы печатать: все, текущий или диапазон «1-3, 5». null — ошибка в диапазоне.</summary>
        List<PageLayout> PagesToPrint(out string error)
        {
            error = null;
            if (pages.Count == 0) return new List<PageLayout>();
            if (currentPageRb.Checked)
                return new List<PageLayout> { pages[Math.Max(0, Math.Min(preview.PageIndex, pages.Count - 1))] };
            if (!rangeRb.Checked) return pages;

            string text = System.Text.RegularExpressions.Regex.Replace(rangeBox.Text, @"\s*[-–—]\s*", "-");
            var idx = new SortedSet<int>();
            foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] ab = part.Split('-');
                int a, b;
                if (ab.Length == 1 && int.TryParse(ab[0], out a)) b = a;
                else if (ab.Length == 2 && int.TryParse(ab[0], out a) && int.TryParse(ab[1], out b)) { }
                else { error = "Не понял «" + part + "». Пример: 1-3, 5"; return null; }
                if (a < 1 || b > pages.Count || a > b)
                {
                    error = "Номера листов должны быть от 1 до " + pages.Count + ".";
                    return null;
                }
                for (int i = a; i <= b; i++) idx.Add(i - 1);
            }
            if (idx.Count == 0) { error = "Укажите номера листов, например: 1-3, 5"; return null; }
            return idx.Select(i => pages[i]).ToList();
        }

        void GoPage(int delta)
        {
            int idx = Math.Max(0, Math.Min(pages.Count - 1, preview.PageIndex + delta));
            preview.PageIndex = idx;
            preview.Invalidate();
            UpdatePageLabel();
        }

        void JumpToSelected()
        {
            var it = fileList.SelectedItem as EpsItem;
            if (it == null) return;
            if (it.Error != null) SetStatus("Ошибка: " + it.Error.Split('\n')[0]);
            for (int i = 0; i < pages.Count; i++)
            {
                if (pages[i].Items.Any(p => p.Item == it))
                {
                    preview.PageIndex = i;
                    preview.Invalidate();
                    UpdatePageLabel();
                    return;
                }
            }
        }

        // ───────────────────────── Печать ─────────────────────────

        // Печать «точка в точку»: Ghostscript рисует EPS (вектор) сразу с разрешением принтера
        // с учётом масштаба, а готовая картинка кладётся на лист без пересчёта (1 пиксель = 1 точка принтера).
        // Никакого PDF и сглаживания: штрихи штрихкода остаются ровными.

        static float ScaleOf(Placement p)
        {
            float nativeW = p.Rotated ? p.Item.HeightMm : p.Item.WidthMm;
            return nativeW > 0 ? p.Rect.Width / nativeW : 1f;
        }

        static string Key(Placement p)
        {
            return p.Item.Path + "|" + ScaleOf(p).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + "|" + p.Rotated;
        }

        PaperSize PrintPaperSize()
        {
            var pi = paperCombo.SelectedItem as PaperItem;
            if (pi == null || pi.IsCustom)
                return new PaperSize("Custom", (int)Math.Round((float)customW.Value / 0.254f), (int)Math.Round((float)customH.Value / 0.254f));
            return pi.Size;
        }

        async void DoPrint()
        {
            if (printing) return;
            if (rendering)
            {
                MessageBox.Show(this, "Подождите, файлы ещё обрабатываются.", "Печать", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string rangeError;
            var toPrint = PagesToPrint(out rangeError);
            if (toPrint == null)
            {
                MessageBox.Show(this, rangeError, "Печать", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (toPrint.Count == 0) return;
            if (!printerSettings.IsValid)
            {
                MessageBox.Show(this, "Принтер «" + printerSettings.PrinterName + "» недоступен.", "Печать", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (layoutWarning != null &&
                MessageBox.Show(this, layoutWarning + "\n\nВсё равно печатать?", "Печать", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            printing = true;
            printBtn.Enabled = false;
            var bitmaps = new Dictionary<string, Bitmap>();
            try
            {
                using (var doc = new PrintDocument())
                {
                    doc.DocumentName = "EPS печать (" + toPrint.Count + " л.)";
                    doc.PrinterSettings = printerSettings;
                    doc.PrinterSettings.Copies = 1;        // копии уже разложены по листам
                    doc.PrinterSettings.Collate = false;
                    doc.DefaultPageSettings.PaperSize = PrintPaperSize();
                    doc.DefaultPageSettings.Landscape = landscapeRb.Checked;
                    doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

                    // Настоящее разрешение принтера с текущими настройками драйвера (у M132 — 600×600)
                    float devX = 600, devY = 600;
                    try
                    {
                        using (var mg = doc.PrinterSettings.CreateMeasurementGraphics(doc.DefaultPageSettings))
                        {
                            if (mg.DpiX >= 150 && mg.DpiY >= 150) { devX = mg.DpiX; devY = mg.DpiY; }
                        }
                    }
                    catch { }

                    var jobs = new Dictionary<string, Placement>();
                    foreach (var page in toPrint)
                        foreach (var p in page.Items)
                        {
                            string k = Key(p);
                            if (!jobs.ContainsKey(k)) jobs[k] = p;
                        }

                    SetStatus(string.Format("Подготовка к печати ({0:0}×{1:0} dpi)…", devX, devY));
                    string tmp = tempDir;
                    await Task.Run(() =>
                    {
                        foreach (var kv in jobs)
                            bitmaps[kv.Key] = RenderForPrint(kv.Value, devX, devY, tmp);
                    });

                    SetStatus("Отправка на принтер…");
                    int pageNo = 0;
                    doc.PrintPage += (s, e) =>
                    {
                        var g = e.Graphics;
                        g.PageUnit = GraphicsUnit.Pixel;               // 1 единица = 1 точка принтера
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.SmoothingMode = SmoothingMode.None;
                        float dx = g.DpiX, dy = g.DpiY;
                        // начало координат — физический край бумаги, а не край печатаемой области
                        float hx = e.PageSettings.HardMarginX / 100f * dx;
                        float hy = e.PageSettings.HardMarginY / 100f * dy;
                        foreach (var p in toPrint[pageNo].Items)
                        {
                            Bitmap b;
                            if (!bitmaps.TryGetValue(Key(p), out b)) continue;
                            int x = (int)Math.Round(p.Rect.X / 25.4f * dx - hx);
                            int y = (int)Math.Round(p.Rect.Y / 25.4f * dy - hy);
                            int w = (int)Math.Round(p.Rect.Width / 25.4f * dx);
                            int h = (int)Math.Round(p.Rect.Height / 25.4f * dy);
                            // если размер совпадает с точностью до пары точек — кладём 1:1 без пересчёта
                            if (Math.Abs(b.Width - w) <= 3 && Math.Abs(b.Height - h) <= 3) { w = b.Width; h = b.Height; }
                            g.DrawImage(b, new Rectangle(x, y, w, h), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel);
                        }
                        pageNo++;
                        e.HasMorePages = pageNo < toPrint.Count;
                    };
                    doc.Print();
                }
                SetStatus("Отправлено на печать: листов " + toPrint.Count);
                SaveSettings();
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка печати");
                MessageBox.Show(this, ex.Message, "Ошибка печати", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                foreach (var b in bitmaps.Values) b.Dispose();
                printing = false;
                RebuildPages();
            }
        }

        /// <summary>Рендер одного размещения с разрешением принтера × масштаб, с поворотом без потерь.</summary>
        static Bitmap RenderForPrint(Placement p, float devX, float devY, string tmp)
        {
            double s = ScaleOf(p);
            // при повороте ось X рисунка ляжет вдоль оси Y принтера
            double rx = (p.Rotated ? devY : devX) * s;
            double ry = (p.Rotated ? devX : devY) * s;
            // защита памяти: не больше ~120 Мпикс на рисунок (A4 при 1200 dpi помещается)
            double px = (p.Item.WidthMm / 25.4 * rx) * (p.Item.HeightMm / 25.4 * ry);
            if (px > 120e6)
            {
                double f = Math.Sqrt(120e6 / px);
                rx *= f;
                ry *= f;
            }
            var bmp = Ghostscript.Render(p.Item.Path, (float)rx, (float)ry, false, tmp);
            if (p.Rotated) bmp.RotateFlip(RotateFlipType.Rotate90FlipNone);
            return bmp;
        }

        // ───────────────────────── Ghostscript и обновление ─────────────────────────

        void CheckGhostscript()
        {
            if (Ghostscript.FindExe() != null) return;
            MessageBox.Show(this,
                "Не найден Ghostscript — бесплатная программа, которая нужна, чтобы открывать EPS-файлы.\n\n" +
                "Скачайте «Ghostscript AGPL Release» для Windows 64 bit на сайте ghostscript.com " +
                "(раздел Releases), установите с настройками по умолчанию и перезапустите эту программу.",
                "Нужен Ghostscript", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// В этой программе нет сетевого кода. Обновлением занимается отдельный Updater.exe —
        /// единственный файл, которому брандмауэр разрешает выход в интернет.
        /// </summary>
        void StartUpdater()
        {
            string updater = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Updater.exe");
            if (!File.Exists(updater))
            {
                MessageBox.Show(this, "Не найден Updater.exe рядом с программой.", "Обновление", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (MessageBox.Show(this, "Программа закроется и проверит наличие новой версии.\nПродолжить?", "Обновление",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            try
            {
                var psi = new ProcessStartInfo(updater, "--pid " + Process.GetCurrentProcess().Id);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Обновление", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static void CleanupOldFiles()
        {
            try
            {
                foreach (string f in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.old"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
