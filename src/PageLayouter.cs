using System;
using System.Collections.Generic;
using System.Drawing;

namespace EpsPrint
{
    /// <summary>Раскладывает рисунки по листам. Одна и та же раскладка идёт и в превью, и на печать.</summary>
    static class PageLayouter
    {
        public static List<PageLayout> Build(List<EpsItem> items, LayoutOptions o, out string warning)
        {
            warning = null;
            var pages = new List<PageLayout>();

            // порядок с учётом копий
            var seq = new List<EpsItem>();
            int copies = Math.Max(1, o.Copies);
            if (o.Collate)
            {
                for (int c = 0; c < copies; c++)
                    foreach (var it in items) seq.Add(it);
            }
            else
            {
                foreach (var it in items)
                    for (int c = 0; c < copies; c++) seq.Add(it);
            }

            float availW = o.PaperWmm - 2 * o.MarginMm;
            float availH = o.PaperHmm - 2 * o.MarginMm;
            if (availW <= 1 || availH <= 1)
            {
                warning = "Поля слишком большие для такой бумаги.";
                return pages;
            }

            bool tooBig = false;
            bool oneEach = !o.MultiPerSheet || o.Scale == ScaleMode.Fit;
            PageLayout cur = null;
            float x = 0, y = 0, rowH = 0;
            const float eps = 0.05f;

            foreach (var it in seq)
            {
                float w = o.Rotate90 ? it.HeightMm : it.WidthMm;
                float h = o.Rotate90 ? it.WidthMm : it.HeightMm;
                if (w <= 0 || h <= 0) continue;

                float s;
                if (o.Scale == ScaleMode.Fit) s = Math.Min(availW / w, availH / h);
                else if (o.Scale == ScaleMode.Actual) s = 1f;
                else if (o.Scale == ScaleMode.Box) s = Math.Min(Math.Max(1f, o.BoxWmm) / w, Math.Max(1f, o.BoxHmm) / h);
                else s = o.Percent / 100f;
                w *= s;
                h *= s;
                if (w > availW + eps || h > availH + eps) tooBig = true;

                if (oneEach)
                {
                    var pg = new PageLayout();
                    pg.Items.Add(new Placement
                    {
                        Item = it,
                        Rect = new RectangleF((o.PaperWmm - w) / 2, (o.PaperHmm - h) / 2, w, h),
                        Rotated = o.Rotate90
                    });
                    pages.Add(pg);
                    continue;
                }

                // несколько на листе: заполняем рядами слева направо, сверху вниз
                if (cur == null) { cur = new PageLayout(); pages.Add(cur); x = 0; y = 0; rowH = 0; }
                if (x > 0 && x + w > availW + eps) { x = 0; y += rowH + o.GapMm; rowH = 0; }
                if (y > 0 && y + h > availH + eps)
                {
                    cur = new PageLayout(); pages.Add(cur);
                    x = 0; y = 0; rowH = 0;
                }
                cur.Items.Add(new Placement
                {
                    Item = it,
                    Rect = new RectangleF(o.MarginMm + x, o.MarginMm + y, w, h),
                    Rotated = o.Rotate90
                });
                x += w + o.GapMm;
                rowH = Math.Max(rowH, h);
            }

            if (tooBig)
                warning = "Рисунок больше листа и обрежется. Выберите «Вписать в лист», другой масштаб или бумагу.";
            return pages;
        }

        /// <summary>Этикеточный режим: каждая этикетка — отдельный «лист» ровно по размеру рисунка.</summary>
        public static List<PageLayout> BuildLabels(List<EpsItem> items, int copies, bool collate, bool rotate)
        {
            var pages = new List<PageLayout>();
            copies = Math.Max(1, copies);
            var seq = new List<EpsItem>();
            if (collate) { for (int c = 0; c < copies; c++) seq.AddRange(items); }
            else foreach (var it in items) for (int c = 0; c < copies; c++) seq.Add(it);
            foreach (var it in seq)
            {
                float w = rotate ? it.HeightMm : it.WidthMm;
                float h = rotate ? it.WidthMm : it.HeightMm;
                if (w <= 0 || h <= 0) continue;
                var pg = new PageLayout { PaperWmm = w, PaperHmm = h };
                pg.Items.Add(new Placement { Item = it, Rect = new RectangleF(0, 0, w, h), Rotated = rotate });
                pages.Add(pg);
            }
            return pages;
        }

        /// <summary>Рисует картинку в прямоугольник размещения (с поворотом на 90° по часовой, если нужно).</summary>
        public static void DrawPlacement(Graphics g, Bitmap bmp, Placement p)
        {
            var state = g.Save();
            if (p.Rotated)
            {
                g.TranslateTransform(p.Rect.Right, p.Rect.Top);
                g.RotateTransform(90);
                g.DrawImage(bmp, new RectangleF(0, 0, p.Rect.Height, p.Rect.Width));
            }
            else
            {
                g.DrawImage(bmp, p.Rect);
            }
            g.Restore(state);
        }
    }
}
