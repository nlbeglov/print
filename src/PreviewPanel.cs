using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EpsPrint
{
    /// <summary>Показывает лист бумаги так, как он будет напечатан.</summary>
    class PreviewPanel : Panel
    {
        public List<PageLayout> Pages;
        public int PageIndex;
        public float PaperW = 210, PaperH = 297, MarginMm = 5;
        public string EmptyText = "Перетащите сюда zip-архив с EPS-файлами\nили нажмите «Открыть архив или файлы…»";

        public PreviewPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(222, 226, 231);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            if (PaperW <= 0 || PaperH <= 0) return;

            const float pad = 24;
            float zoom = Math.Min((Width - 2 * pad) / PaperW, (Height - 2 * pad) / PaperH);
            if (zoom <= 0) return;
            float pw = PaperW * zoom, ph = PaperH * zoom;
            float ox = (Width - pw) / 2, oy = (Height - ph) / 2;

            using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillRectangle(shadow, ox + 4, oy + 4, pw, ph);
            g.FillRectangle(Brushes.White, ox, oy, pw, ph);
            g.DrawRectangle(Pens.Gray, ox, oy, pw, ph);

            float m = MarginMm * zoom;
            if (m > 1 && pw > 2 * m && ph > 2 * m)
            {
                using (var pen = new Pen(Color.FromArgb(170, 190, 225)))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(pen, ox + m, oy + m, pw - 2 * m, ph - 2 * m);
                }
            }

            if (Pages == null || Pages.Count == 0)
            {
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(EmptyText, Font, Brushes.Gray, new RectangleF(ox, oy, pw, ph), sf);
                return;
            }

            var page = Pages[Math.Max(0, Math.Min(PageIndex, Pages.Count - 1))];
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var state = g.Save();
            g.TranslateTransform(ox, oy);
            g.ScaleTransform(zoom, zoom);
            g.SetClip(new RectangleF(0, 0, PaperW, PaperH));
            foreach (var p in page.Items)
            {
                if (p.Item.Preview != null)
                    PageLayouter.DrawPlacement(g, p.Item.Preview, p);
            }
            g.Restore(state);
        }
    }
}
