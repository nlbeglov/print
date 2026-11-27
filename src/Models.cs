using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;

namespace EpsPrint
{
    /// <summary>Один EPS-файл из архива.</summary>
    class EpsItem
    {
        public string Path;          // копия во временной папке
        public string Name;          // исходное имя файла
        public string DisplayName;   // имя для списка
        public string Gtin;          // код товара из имени файла (…_00850027846491_0.eps)
        public int CodeIndex;        // номер кода этого товара (0, 1, 2…)
        public Bitmap Preview;       // картинка для превью
        public float WidthMm;        // реальный размер рисунка
        public float HeightMm;
        public string Error;

        public override string ToString()
        {
            if (Error != null) return DisplayName + "  — ошибка";
            if (Preview == null) return DisplayName + "  — загрузка…";
            return DisplayName + string.Format("  ({0:0.#}×{1:0.#} мм)", WidthMm, HeightMm);
        }
    }

    /// <summary>Где на листе стоит рисунок (всё в миллиметрах от левого верхнего угла бумаги).</summary>
    class Placement
    {
        public EpsItem Item;
        public RectangleF Rect;
        public bool Rotated;
    }

    class PageLayout
    {
        public List<Placement> Items = new List<Placement>();
        public float PaperWmm, PaperHmm;   // свой размер листа (этикетки); 0 — общий размер бумаги
    }

    enum ScaleMode { Fit, Actual, Custom, Box }

    class LayoutOptions
    {
        public float PaperWmm, PaperHmm;
        public float MarginMm, GapMm;
        public ScaleMode Scale;
        public float Percent;
        public float BoxWmm, BoxHmm;   // ScaleMode.Box: рисунок вписывается в этот размер (пропорции сохраняются)
        public bool MultiPerSheet, Rotate90, Collate;
        public int Copies;
    }

    /// <summary>
    /// Размер наклейки в рулоне. W — поперёк принтера, H — вдоль (по ходу печати).
    /// Показывается так, как привыкли пользователи: «вдоль × поперёк» (наклейка 30 на 40 = 30 вдоль, 40 поперёк).
    /// </summary>
    class StickerItem
    {
        public float W, H;
        public bool IsCustom;

        public string Key
        {
            get { return IsCustom ? "custom" : W.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" + H.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        }

        public override string ToString()
        {
            return IsCustom ? "Свой размер…" : string.Format("{0:0.#} × {1:0.#} мм", H, W);
        }
    }

    class PaperItem
    {
        public PaperSize Size;
        public bool IsCustom;

        public override string ToString()
        {
            if (IsCustom) return "Свой размер…";
            return string.Format("{0}  ({1:0}×{2:0} мм)", Size.PaperName, Size.Width * 0.254, Size.Height * 0.254);
        }
    }
}
