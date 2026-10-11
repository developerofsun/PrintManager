using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace PrintHelper
{
    public enum ItemKind { Text, Rect, Line, Image }

    /// <summary>One drawing primitive on a finished page. All coordinates are millimetres from the page's top-left corner.
    /// PDF, images/preview/print and Excel all consume this same list, so they always agree.</summary>
    public class RenderItem
    {
        public ItemKind Kind;
        public double X, Y, W, H;             // Line: (X,Y) to (X+W,Y+H)
        public string Text;                   // one wrapped line
        public string FontName;
        public float FontSize;                // points
        public FontStyle Style;
        public Color Color = Color.Black;     // text / line / border colour
        public Color Fill = Color.Transparent;
        public double Thickness;              // points
        public byte[] Image;
        public bool Rtl;

        // Excel export metadata
        public int Page;
        public bool Repeat;                   // page header/footer/table header repeated on pages 2+
        public bool Cont;                     // 2nd+ wrapped line of the same cell
        public string Full;                   // whole cell text (first line only)
        public object Raw;                    // raw data value for table cells
        public double RowY, ColX, ColW;       // cell box
    }

    public class RenderPage
    {
        public double Width, Height;
        public List<RenderItem> Items = new List<RenderItem>();
    }

    internal enum RowKind { Header, Normal, Zebra, Total }

    /// <summary>Turns a report definition + data into paginated pages of drawing primitives.</summary>
    internal sealed class LayoutEngine
    {
        const double PadX = 1.0, PadY = 0.5;

        readonly ReportDefinition _r;
        readonly EvalContext _c;
        readonly List<RenderPage> _pages = new List<RenderPage>();
        RenderPage _cur;
        double _y, _bottom, _contentTop, _pw, _ph, _ml, _mt, _cw;
        bool _markRepeat;
        Band _pageHeader, _pageFooter;
        TableElement _activeTable;
        double _tX0, _tScale, _tWidth;
        bool _tRtl;

        public LayoutEngine(ReportDefinition r, EvalContext c) { _r = r; _c = c; }

        public List<RenderPage> Run()
        {
            var pg = _r.Page;
            _pw = pg.EffectiveWidth; _ph = pg.EffectiveHeight;
            _ml = pg.MarginLeft; _mt = pg.MarginTop; _cw = pg.ContentWidth;
            _pageHeader = _r.Bands.FirstOrDefault(b => b.Kind == BandKind.PageHeader);
            _pageFooter = _r.Bands.FirstOrDefault(b => b.Kind == BandKind.PageFooter);

            _c.Rows.Clear();
            _c.DefaultSource = null; _c.GroupSource = null; _c.GroupRows = null; _c.RowNumber = 0;
            ResetRows();

            NewPage();
            foreach (var b in _r.Bands.Where(b => b.Kind == BandKind.ReportHeader)) FlowBody(b);
            foreach (var b in _r.Bands.Where(b => b.Kind == BandKind.Body || b.Kind == BandKind.Detail))
            {
                if (b.Kind == BandKind.Detail) FlowDetail(b); else FlowBody(b);
            }
            foreach (var b in _r.Bands.Where(b => b.Kind == BandKind.ReportFooter)) FlowBody(b);
            return _pages;
        }

        void ResetRows()
        {
            foreach (var n in _c.Data.Names)
            {
                var t = _c.Data.Get(n);
                if (t.Rows.Count > 0) _c.Rows[n] = t.Rows[0]; else _c.Rows.Remove(n);
            }
        }

        // ------------------------------------------------------------ paging

        void NewPage()
        {
            _cur = new RenderPage { Width = _pw, Height = _ph };
            _pages.Add(_cur);
            _c.Page = _pages.Count;
            _y = _mt;
            bool rep = _pages.Count > 1;
            double footerH = _pageFooter != null ? _pageFooter.HeightMm : 0;
            _bottom = _ph - _r.Page.MarginBottom - footerH;

            // page bands are not tied to a data row: show the group/detail context they were called in
            string savedGroup = _c.GroupSource; var savedRows = _c.GroupRows;
            _c.GroupSource = null; _c.GroupRows = null;
            _markRepeat = rep;
            if (_pageHeader != null)
            {
                EmitElements(Statics(_pageHeader), _y, 0);
                _y += _pageHeader.HeightMm;
            }
            if (_pageFooter != null) EmitElements(Statics(_pageFooter), _bottom, 0);
            _c.GroupSource = savedGroup; _c.GroupRows = savedRows;
            _contentTop = _y;
            if (rep && _activeTable != null && _activeTable.ShowHeader)
                TableRow(_activeTable, null, RowKind.Header, false);
            _markRepeat = false;
        }

        void Ensure(double h)
        {
            if (_y + h > _bottom + 1e-6 && _y > _contentTop + 1e-6) NewPage();
        }

        void Place(double h, Action<double> emit)
        {
            Ensure(h);
            emit(_y);
            _y += h;
        }

        static List<ReportElement> Statics(Band b)
        {
            return b.Elements.Where(e => e.Visible && !(e is TableElement)).ToList();
        }

        // ------------------------------------------------------------ growing text

        double GrownHeight(TextElement t)
        {
            var text = Bidi.ConvertDigits(_c.Expand(t.Text), _r.Digits);
            if (string.IsNullOrEmpty(text)) return t.Height;
            bool rtl = TextEngine.ResolveRtl(text, t.Direction, _r.DefaultDirection);
            var spec = FontSpec.From(t.FontName, t.FontSize, t.Style);
            int lines = TextEngine.Wrap(text, spec, t.Width - 2 * PadX, t.WordWrap, rtl).Count;
            return Math.Max(t.Height, lines * TextEngine.LineHeight(spec) + 2 * PadY);
        }

        double BandHeight(Band b, List<ReportElement> els)
        {
            double h = b.HeightMm;
            foreach (var e in els)
            {
                var t = e as TextElement;
                if (t != null && t.CanGrow && _c.EvalBool(t.VisibleWhen)) h = Math.Max(h, t.Y + GrownHeight(t) + 0.5);
            }
            return h;
        }

        // ------------------------------------------------------------ bands

        void PlaceBand(Band b, double keepWith = 0)
        {
            var els = Statics(b);
            double h = BandHeight(b, els);
            Ensure(h + keepWith);
            EmitElements(els, _y, 0);
            _y += h;
        }

        void FlowBody(Band b)
        {
            var tables = b.Elements.OfType<TableElement>().Where(t => t.Visible).OrderBy(t => t.Y).ToList();
            var others = Statics(b);
            if (tables.Count == 0) { PlaceBand(b); return; }

            // elements above the first table, then the table(s), then elements below
            var ft = tables[0];
            double split = ft.Y + ft.Height;
            var above = others.Where(e => e.Y < split).ToList();
            var below = others.Where(e => e.Y >= split).ToList();
            if (ft.Y > 0.01) Place(ft.Y, top => EmitElements(above, top, 0));
            foreach (var t in tables) { EmitTable(t); _y += 1.5; }
            double bh = b.HeightMm - split;
            if (below.Count > 0 && bh > 0) Place(bh, top => EmitElements(below, top, split));
        }

        // ------------------------------------------------------------ detail + groups

        sealed class SortKey { public string Expr; public bool Desc; }

        static List<SortKey> ParseSort(string s)
        {
            var list = new List<SortKey>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            foreach (var part in s.Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                bool desc = false;
                if (p.EndsWith(" desc", StringComparison.OrdinalIgnoreCase)) { desc = true; p = p.Substring(0, p.Length - 5).Trim(); }
                else if (p.EndsWith(" asc", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4).Trim();
                list.Add(new SortKey { Expr = p, Desc = desc });
            }
            return list;
        }

        void FlowDetail(Band b)
        {
            var ds = b.DataSource;
            var dt = _c.Data.Get(ds);
            if (dt == null) throw new ReportException("Detail band '" + b.Name + "' needs a valid DataSource (got '" + ds + "').");

            var groupBands = _r.Bands.Where(g => (g.Kind == BandKind.GroupHeader || g.Kind == BandKind.GroupFooter) &&
                                                 string.Equals(g.DataSource, ds, StringComparison.OrdinalIgnoreCase) &&
                                                 !string.IsNullOrWhiteSpace(g.GroupBy)).ToList();
            var levels = groupBands.Select(g => g.GroupBy.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var headers = levels.Select(l => groupBands.FirstOrDefault(g => g.Kind == BandKind.GroupHeader && string.Equals(g.GroupBy.Trim(), l, StringComparison.OrdinalIgnoreCase))).ToList();
            var footers = levels.Select(l => groupBands.FirstOrDefault(g => g.Kind == BandKind.GroupFooter && string.Equals(g.GroupBy.Trim(), l, StringComparison.OrdinalIgnoreCase))).ToList();
            var sort = ParseSort(b.SortBy);

            // evaluate group + sort keys once per row
            var all = dt.Rows.Cast<DataRow>().ToList();
            _c.DefaultSource = ds;
            var keyOf = new Dictionary<DataRow, object[]>();
            int nKeys = levels.Count + sort.Count;
            foreach (var row in all)
            {
                _c.Rows[ds] = row;
                var k = new object[nKeys];
                for (int i = 0; i < levels.Count; i++) k[i] = SafeEval(levels[i]);
                for (int i = 0; i < sort.Count; i++) k[levels.Count + i] = SafeEval(sort[i].Expr);
                keyOf[row] = k;
            }
            var index = Enumerable.Range(0, all.Count).ToList();
            if (nKeys > 0)
                index.Sort((x, y) =>
                {
                    var kx = keyOf[all[x]]; var ky = keyOf[all[y]];
                    for (int i = 0; i < nKeys; i++)
                    {
                        int c = Formula.CompareValues(kx[i], ky[i]);
                        if (i >= levels.Count && sort[i - levels.Count].Desc) c = -c;
                        if (c != 0) return c;
                    }
                    return x.CompareTo(y);
                });
            var rows = index.Select(i => all[i]).ToList();
            var keys = rows.Select(r => keyOf[r]).ToList();

            Func<int, int, int, bool> same = (a, c, upTo) =>
            {
                for (int k = 0; k <= upTo; k++) if (Formula.CompareValues(keys[a][k], keys[c][k]) != 0) return false;
                return true;
            };

            var els = Statics(b);
            for (int i = 0; i < rows.Count; i++)
            {
                int changed = 0;
                if (i > 0)
                {
                    changed = levels.Count;
                    for (int L = 0; L < levels.Count; L++)
                        if (Formula.CompareValues(keys[i][L], keys[i - 1][L]) != 0) { changed = L; break; }
                    for (int L = levels.Count - 1; L >= changed; L--) EmitGroup(footers[L], ds, rows, i - 1, false, L, same);
                }
                for (int L = changed; L < levels.Count; L++)
                {
                    var h = headers[L];
                    if (h != null && h.NewPageBefore && i > 0 && _y > _contentTop + 1e-6) NewPage();
                    EmitGroup(h, ds, rows, i, true, L, same, b.HeightMm);
                }

                _c.DefaultSource = ds; _c.GroupSource = null; _c.GroupRows = null;
                _c.Rows[ds] = rows[i];
                _c.RowNumber = i + 1;
                double rowH = BandHeight(b, els);
                Ensure(rowH);
                EmitElements(els, _y, 0);
                _y += rowH;
            }
            if (rows.Count > 0)
                for (int L = levels.Count - 1; L >= 0; L--) EmitGroup(footers[L], ds, rows, rows.Count - 1, false, L, same);

            _c.DefaultSource = null; _c.GroupSource = null; _c.GroupRows = null; _c.RowNumber = 0;
            ResetRows();
        }

        object SafeEval(string expr)
        {
            object v;
            return _c.TryEval(expr, out v) ? v : null;
        }

        void EmitGroup(Band band, string ds, List<DataRow> rows, int at, bool header, int level, Func<int, int, int, bool> same, double keepWith = 0)
        {
            if (band == null) return;
            int from = at, to = at;
            if (header) { while (to + 1 < rows.Count && same(at, to + 1, level)) to++; }
            else { while (from - 1 >= 0 && same(at, from - 1, level)) from--; }
            var range = rows.GetRange(from, to - from + 1);

            _c.DefaultSource = ds;
            _c.GroupSource = ds;
            _c.GroupRows = range;
            _c.Rows[ds] = header ? rows[from] : rows[to];
            _c.RowNumber = from + 1;
            PlaceBand(band, header ? keepWith : 0);
            _c.GroupSource = null; _c.GroupRows = null;
        }

        // ------------------------------------------------------------ elements

        void EmitElements(IEnumerable<ReportElement> els, double top, double shift)
        {
            foreach (var e in els)
            {
                if (!_c.EvalBool(e.VisibleWhen)) continue;
                double x = _ml + e.X, y = top + e.Y - shift;
                var t = e as TextElement;
                var im = e as ImageElement;
                var ln = e as LineElement;
                var rc = e as RectangleElement;
                if (t != null)
                {
                    double h = t.CanGrow ? GrownHeight(t) : e.Height;
                    if (t.BackColor.A > 0 || (t.BorderWidth > 0 && t.BorderColor.A > 0))
                        Add(new RenderItem { Kind = ItemKind.Rect, X = x, Y = y, W = e.Width, H = h, Fill = t.BackColor, Color = t.BorderColor, Thickness = t.BorderWidth });
                    EmitText(x, y, e.Width, h, _c.Expand(t.Text), t.FontName, t.FontSize, t.Style, t.ForeColor, t.Align, t.VAlign, t.WordWrap, t.Direction, null);
                }
                else if (im != null) EmitImage(im, x, y);
                else if (ln != null)
                    Add(new RenderItem { Kind = ItemKind.Line, X = x, Y = y, W = e.Width, H = e.Height, Color = ln.Color, Thickness = ln.Thickness });
                else if (rc != null)
                    Add(new RenderItem { Kind = ItemKind.Rect, X = x, Y = y, W = e.Width, H = e.Height, Fill = rc.FillColor, Color = rc.BorderColor, Thickness = rc.BorderWidth });
            }
        }

        void Add(RenderItem it)
        {
            it.Page = _pages.Count;
            it.Repeat = _markRepeat;
            _cur.Items.Add(it);
        }

        double EmitText(double x, double y, double w, double h, string text, string fontName, float size, FontStyle style,
                        Color fore, HAlign ha, VAlign va, bool wrap, TextDirection dir, object raw)
        {
            text = Bidi.ConvertDigits(text, _r.Digits);
            if (string.IsNullOrEmpty(text)) return 0;
            bool rtl = TextEngine.ResolveRtl(text, dir, _r.DefaultDirection);
            if (ha == HAlign.Auto) ha = rtl ? HAlign.Right : HAlign.Left;

            var spec = FontSpec.From(fontName, size, style);
            var lines = TextEngine.Wrap(text, spec, w - 2 * PadX, wrap, rtl);
            double lh = TextEngine.LineHeight(spec), total = lines.Count * lh;
            double ty = va == VAlign.Top ? y + PadY : va == VAlign.Middle ? y + (h - total) / 2 : y + h - PadY - total;
            for (int i = 0; i < lines.Count; i++)
            {
                double lw = TextEngine.Width(lines[i], spec, rtl);
                double tx = ha == HAlign.Left ? x + PadX : ha == HAlign.Center ? x + (w - lw) / 2 : x + w - PadX - lw;
                Add(new RenderItem
                {
                    Kind = ItemKind.Text, X = tx, Y = ty + i * lh, W = lw + 0.3, H = lh, Text = lines[i],
                    FontName = fontName, FontSize = size, Style = style, Color = fore, Rtl = rtl,
                    Full = i == 0 ? text : null, Cont = i > 0, Raw = raw, RowY = y, ColX = x, ColW = w
                });
            }
            return total + 2 * PadY;
        }

        void EmitImage(ImageElement im, double x, double y)
        {
            byte[] bytes = null;
            try
            {
                if (!string.IsNullOrEmpty(im.ImageField))
                {
                    object v;
                    if (_c.TryEval(im.ImageField, out v))
                    {
                        bytes = v as byte[];
                        var path = v as string;
                        if (bytes == null && path != null && File.Exists(path)) bytes = File.ReadAllBytes(path);
                    }
                }
                if (bytes == null && !string.IsNullOrEmpty(im.ImageBase64)) bytes = Convert.FromBase64String(im.ImageBase64);
                if (bytes == null) return;

                double dx = x, dy = y, dw = im.Width, dh = im.Height;
                if (im.Fit == ImageFit.Fit)
                {
                    using (var codec = SKCodec.Create(new SKMemoryStream(bytes)))
                    {
                        if (codec == null) return;
                        double sc = Math.Min(im.Width / codec.Info.Width, im.Height / codec.Info.Height);
                        dw = codec.Info.Width * sc; dh = codec.Info.Height * sc;
                        dx = x + (im.Width - dw) / 2; dy = y + (im.Height - dh) / 2;
                    }
                }
                Add(new RenderItem { Kind = ItemKind.Image, X = dx, Y = dy, W = dw, H = dh, Image = bytes });
            }
            catch (Exception) { /* unreadable image: leave the space empty rather than failing the whole report */ }
        }

        // ------------------------------------------------------------ tables

        void EmitTable(TableElement t)
        {
            var dt = _c.Data.Get(t.DataSource);
            if (dt == null) throw new ReportException("Table '" + t.Name + "' uses unknown data source '" + t.DataSource + "'.");
            if (t.Columns.Count == 0) return;

            double sum = t.Columns.Sum(c => c.WidthMm);
            _tWidth = t.Width > 0 ? t.Width : _cw - t.X;
            _tScale = sum > 0 ? _tWidth / sum : 1;
            _tX0 = _ml + t.X;
            _tRtl = t.Direction == TextDirection.RightToLeft || (t.Direction == TextDirection.Auto && _r.DefaultDirection == TextDirection.RightToLeft);

            string savedDefault = _c.DefaultSource;
            _c.DefaultSource = t.DataSource;
            _activeTable = null;
            if (t.ShowHeader) TableRow(t, null, RowKind.Header, true);
            _activeTable = t;
            int idx = 0;
            foreach (DataRow row in dt.Rows)
            {
                _c.Rows[t.DataSource] = row;
                _c.RowNumber = idx + 1;
                TableRow(t, row, idx % 2 == 1 ? RowKind.Zebra : RowKind.Normal, true);
                idx++;
            }
            ResetRows();
            _c.RowNumber = 0;
            if (t.ShowTotals) TableRow(t, null, RowKind.Total, true);
            _activeTable = null;
            _c.DefaultSource = savedDefault;
        }

        void TableRow(TableElement t, DataRow row, RowKind kind, bool ensure)
        {
            var dt = _c.Data.Get(t.DataSource);
            var style = kind == RowKind.Header || kind == RowKind.Total ? FontStyle.Bold : FontStyle.Regular;
            var spec = FontSpec.From(t.FontName, t.FontSize, style);
            double lh = TextEngine.LineHeight(spec);
            var dir = _tRtl ? TextDirection.RightToLeft : TextDirection.Auto;

            int n = t.Columns.Count;
            var texts = new string[n];
            var raws = new object[n];
            double h = t.RowHeightMm;
            for (int i = 0; i < n; i++)
            {
                var col = t.Columns[i];
                object v;
                if (kind == RowKind.Header) texts[i] = col.Header;
                else if (kind == RowKind.Total)
                {
                    if (col.Total != TotalKind.None && !string.IsNullOrWhiteSpace(col.Field) &&
                        _c.TryEval(col.Total + "All(" + col.Field + ")", out v))
                    { texts[i] = EvalContext.Format(v, col.Format); raws[i] = v; }
                    else texts[i] = i == 0 ? t.TotalLabel : "";
                }
                else if (row != null && !string.IsNullOrWhiteSpace(col.Field))
                {
                    if (dt.Columns.Contains(col.Field)) v = row[col.Field];
                    else if (!_c.TryEval(col.Field, out v)) v = null;
                    v = Formula.Norm(v);
                    texts[i] = EvalContext.Format(v, col.Format);
                    raws[i] = v;
                }
                else texts[i] = "";

                string conv = Bidi.ConvertDigits(texts[i], _r.Digits);
                bool rtl = TextEngine.ResolveRtl(conv, dir, _r.DefaultDirection);
                int lines = TextEngine.Wrap(conv, spec, col.WidthMm * _tScale - 2 * PadX, true, rtl).Count;
                h = Math.Max(h, lines * lh + 2 * PadY + 0.4);
            }

            if (ensure) Ensure(h);

            double off = 0;
            for (int i = 0; i < n; i++)
            {
                var col = t.Columns[i];
                double cw = col.WidthMm * _tScale;
                double x = _tRtl ? _tX0 + _tWidth - off - cw : _tX0 + off;
                off += cw;
                Color back = kind == RowKind.Header || kind == RowKind.Total ? t.HeaderBackColor
                           : kind == RowKind.Zebra ? t.ZebraColor : Color.Transparent;
                Color fore = kind == RowKind.Header ? t.HeaderForeColor : t.ForeColor;
                if (back.A > 0 || t.BorderWidth > 0)
                    Add(new RenderItem { Kind = ItemKind.Rect, X = x, Y = _y, W = cw, H = h, Fill = back, Color = t.BorderColor, Thickness = t.BorderWidth });
                EmitText(x, _y, cw, h, texts[i], t.FontName, t.FontSize, style, fore, col.Align, VAlign.Middle, true, dir, raws[i]);
            }
            _y += h;
        }
    }
}
