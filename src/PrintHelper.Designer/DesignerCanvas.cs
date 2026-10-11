using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PrintHelper.Designer
{
    /// <summary>
    /// The visual design surface. Bands are stacked vertically; elements are dragged/resized inside them
    /// and can be dragged from one band to another. Everything is drawn and hit-tested in millimetres.
    /// </summary>
    public sealed class DesignerCanvas : Panel
    {
        const double Pad = 8, HeaderH = 5, SnapMm = 0.5, EdgeGrab = 1.5;

        sealed class BandBox { public Band Band; public double Top; }
        enum Mode { None, Move, Resize, BandResize }

        ReportDefinition _report;
        object _selected;
        float _zoom = 1f;
        double _contentEnd;
        readonly List<BandBox> _boxes = new List<BandBox>();
        readonly Dictionary<ReportElement, KeyValuePair<string, Image>> _images = new Dictionary<ReportElement, KeyValuePair<string, Image>>();

        Mode _mode;
        int _handle;
        PointF _start;
        RectangleF _orig;
        double _origBandH;
        bool _moved;

        public event EventHandler SelectionChanged;
        /// <summary>Raised continuously while dragging (refresh views; do not record undo here).</summary>
        public event EventHandler Changing;
        /// <summary>Raised once when an edit is complete (record undo here).</summary>
        public event EventHandler Changed;

        /// <summary>Lets the canvas fill a dropped data source's columns into a table.</summary>
        public Func<string, IList<string>> FieldLookup;

        public DesignerCanvas()
        {
            DoubleBuffered = true;
            AutoScroll = true;
            AllowDrop = true;
            TabStop = true;
            BackColor = Color.FromArgb(110, 114, 122);
        }

        float S { get { return _zoom * DeviceDpi / 25.4f; } }

        public ReportDefinition Report
        {
            get { return _report; }
            set { _report = value; _selected = _report; Relayout(); RaiseSelection(); }
        }

        public object Selected
        {
            get { return _selected; }
            set { _selected = value; Invalidate(); RaiseSelection(); }
        }

        public float Zoom
        {
            get { return _zoom; }
            set { _zoom = Math.Max(0.25f, Math.Min(4f, value)); Relayout(); }
        }

        void RaiseSelection() { var h = SelectionChanged; if (h != null) h(this, EventArgs.Empty); }
        void RaiseChanging() { var h = Changing; if (h != null) h(this, EventArgs.Empty); }
        void RaiseChanged() { var h = Changed; if (h != null) h(this, EventArgs.Empty); }

        // ------------------------------------------------------------ selection persistence (used by undo/redo)

        /// <summary>Encodes the current selection as indexes so it survives a report reload.</summary>
        public int[] GetSelectionPath()
        {
            var b = _selected as Band;
            if (b != null) return new[] { 1, _report.Bands.IndexOf(b) };
            var el = _selected as ReportElement;
            if (el != null)
                for (int i = 0; i < _report.Bands.Count; i++)
                {
                    int j = _report.Bands[i].Elements.IndexOf(el);
                    if (j >= 0) return new[] { 2, i, j };
                }
            return new[] { 0 };
        }

        /// <summary>Re-lays out after the report content was replaced, restoring the selection.</summary>
        public void ReloadKeepingSelection(int[] path)
        {
            object sel = _report;
            if (path != null && path.Length >= 2 && path[0] == 1 && path[1] >= 0 && path[1] < _report.Bands.Count) sel = _report.Bands[path[1]];
            else if (path != null && path.Length >= 3 && path[0] == 2 && path[1] < _report.Bands.Count && path[2] < _report.Bands[path[1]].Elements.Count)
                sel = _report.Bands[path[1]].Elements[path[2]];
            _selected = sel;
            Relayout();
            RaiseSelection();
        }

        static int Rank(BandKind k)
        {
            switch (k)
            {
                case BandKind.PageHeader: return 0;
                case BandKind.ReportHeader: return 1;
                case BandKind.Body: case BandKind.Detail: case BandKind.GroupHeader: case BandKind.GroupFooter: return 2;
                case BandKind.ReportFooter: return 3;
                default: return 4;
            }
        }

        public void Relayout()
        {
            _boxes.Clear();
            if (_report != null)
            {
                double y = Pad + _report.Page.MarginTop;
                foreach (var b in _report.Bands.OrderBy(b => Rank(b.Kind)))
                {
                    _boxes.Add(new BandBox { Band = b, Top = y + HeaderH });
                    y += HeaderH + b.HeightMm + 1.5;
                }
                _contentEnd = y + _report.Page.MarginBottom;
                AutoScrollMinSize = new Size((int)((_report.Page.EffectiveWidth + 2 * Pad) * S) + 1, (int)((_contentEnd + Pad) * S) + 1);
            }
            Invalidate();
        }

        // ------------------------------------------------------------ painting

        static Font MmFont(string name, double pt, FontStyle st)
        {
            return GdiFonts.Get(name, (float)(Math.Max(1, pt) * 25.4 / 72), st, GraphicsUnit.Millimeter);
        }

        new double Left { get { return Pad + _report.Page.MarginLeft; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_report == null) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            // draw in millimetres: the scroll offset is converted from pixels once, then only zoom is applied
            float pxPerMm = DeviceDpi / 25.4f;
            g.PageUnit = GraphicsUnit.Millimeter;
            g.TranslateTransform(AutoScrollPosition.X / pxPerMm, AutoScrollPosition.Y / pxPerMm);
            g.ScaleTransform(_zoom, _zoom);

            var pg = _report.Page;
            var sheet = new RectangleF((float)Pad, (float)Pad, (float)pg.EffectiveWidth, (float)(_contentEnd - Pad));
            g.FillRectangle(Brushes.White, sheet);
            using (var guide = new Pen(Color.FromArgb(200, 210, 230), 0.15f) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(guide, (float)Left, (float)(Pad + pg.MarginTop), (float)pg.ContentWidth, (float)(_contentEnd - Pad - pg.MarginTop - pg.MarginBottom));
            if (ReferenceEquals(_selected, _report))
                using (var sel = new Pen(Color.DodgerBlue, 0.6f)) g.DrawRectangle(sel, sheet.X, sheet.Y, sheet.Width, sheet.Height);

            foreach (var box in _boxes) DrawBand(g, box);
        }

        static Color BandColor(BandKind k)
        {
            switch (k)
            {
                case BandKind.PageHeader: case BandKind.PageFooter: return Color.FromArgb(120, 150, 200);
                case BandKind.ReportHeader: case BandKind.ReportFooter: return Color.FromArgb(150, 130, 190);
                case BandKind.GroupHeader: case BandKind.GroupFooter: return Color.FromArgb(210, 140, 80);
                case BandKind.Detail: return Color.FromArgb(90, 170, 130);
                default: return Color.FromArgb(150, 150, 150);
            }
        }

        void DrawBand(Graphics g, BandBox box)
        {
            var b = box.Band;
            double w = _report.Page.ContentWidth;
            var head = new RectangleF((float)Left, (float)(box.Top - HeaderH), (float)w, (float)HeaderH);
            var body = new RectangleF((float)Left, (float)box.Top, (float)w, (float)b.HeightMm);
            bool sel = ReferenceEquals(_selected, b);

            using (var hb = new SolidBrush(BandColor(b.Kind))) g.FillRectangle(hb, head);
            using (var f = MmFont("Segoe UI", 7, FontStyle.Bold))
            {
                var label = b.Kind + " - " + b.Name + (string.IsNullOrEmpty(b.DataSource) ? "" : "  [" + b.DataSource + "]") +
                            (string.IsNullOrEmpty(b.GroupBy) ? "" : "  by " + b.GroupBy);
                g.DrawString(label, f, Brushes.White, head.X + 1, head.Y + 0.5f);
            }
            using (var bb = new SolidBrush(Color.FromArgb(252, 252, 250))) g.FillRectangle(bb, body);
            using (var p = new Pen(sel ? Color.DodgerBlue : Color.Silver, sel ? 0.5f : 0.2f)) g.DrawRectangle(p, body.X, body.Y, body.Width, body.Height);

            foreach (var el in b.Elements) DrawElement(g, box, el);
        }

        void DrawElement(Graphics g, BandBox box, ReportElement el)
        {
            var r = AbsRect(box, el);
            var t = el as TextElement;
            var im = el as ImageElement;
            var ln = el as LineElement;
            var rc = el as RectangleElement;
            var tb = el as TableElement;

            if (t != null)
            {
                if (t.BackColor.A > 0) using (var b = new SolidBrush(t.BackColor)) g.FillRectangle(b, r);
                if (t.BorderWidth > 0) using (var p = new Pen(t.BorderColor, (float)(t.BorderWidth * 25.4 / 72))) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                bool rtl = TextUtil.IsRtl(t.Text, t.Direction, _report.DefaultDirection);
                var text = TextUtil.ConvertDigits(t.Text, _report.Digits);
                using (var f = MmFont(t.FontName, t.FontSize, t.Style))
                using (var br = new SolidBrush(t.ForeColor))
                using (var fmt = new StringFormat(StringFormat.GenericTypographic))
                {
                    var ha = t.Align == HAlign.Auto ? (rtl ? HAlign.Right : HAlign.Left) : t.Align;
                    // with DirectionRightToLeft GDI+ mirrors Near/Far, so map physical left/right accordingly
                    if (ha == HAlign.Center) fmt.Alignment = StringAlignment.Center;
                    else if ((ha == HAlign.Left) != rtl) fmt.Alignment = StringAlignment.Near;
                    else fmt.Alignment = StringAlignment.Far;
                    fmt.LineAlignment = t.VAlign == VAlign.Top ? StringAlignment.Near : t.VAlign == VAlign.Middle ? StringAlignment.Center : StringAlignment.Far;
                    if (!t.WordWrap) fmt.FormatFlags |= StringFormatFlags.NoWrap;
                    if (rtl) fmt.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                    var inner = RectangleF.Inflate(r, -1f, -0.5f);
                    var old = g.Clip;
                    g.SetClip(r, CombineMode.Intersect);
                    g.DrawString(text, f, br, inner, fmt);
                    g.Clip = old;
                }
            }
            else if (im != null)
            {
                var img = GetImage(im);
                if (img != null) g.DrawImage(img, r);
                else
                {
                    using (var p = new Pen(Color.Gray, 0.2f) { DashStyle = DashStyle.Dot }) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                    using (var f = MmFont("Segoe UI", 8, FontStyle.Italic))
                        g.DrawString(string.IsNullOrEmpty(im.ImageField) ? "Image" : "Image {" + im.ImageField + "}", f, Brushes.Gray, r.X + 1, r.Y + 1);
                }
            }
            else if (ln != null)
            {
                using (var p = new Pen(ln.Color, Math.Max(0.1f, (float)(ln.Thickness * 25.4 / 72)))) g.DrawLine(p, r.X, r.Y, r.Right, r.Bottom);
            }
            else if (rc != null)
            {
                if (rc.FillColor.A > 0) using (var b = new SolidBrush(rc.FillColor)) g.FillRectangle(b, r);
                if (rc.BorderWidth > 0) using (var p = new Pen(rc.BorderColor, (float)(rc.BorderWidth * 25.4 / 72))) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }
            else if (tb != null) DrawTable(g, r, tb);

            if (ReferenceEquals(_selected, el))
            {
                using (var p = new Pen(Color.DodgerBlue, 0.3f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                foreach (var h in HandleRects(r))
                {
                    g.FillRectangle(Brushes.White, h);
                    using (var p = new Pen(Color.DodgerBlue, 0.2f)) g.DrawRectangle(p, h.X, h.Y, h.Width, h.Height);
                }
            }
        }

        void DrawTable(Graphics g, RectangleF r, TableElement tb)
        {
            bool rtl = tb.Direction == TextDirection.RightToLeft || (tb.Direction == TextDirection.Auto && _report.DefaultDirection == TextDirection.RightToLeft);
            double sum = tb.Columns.Sum(c => c.WidthMm);
            double scale = sum > 0 ? r.Width / sum : 1;
            float rowH = (float)tb.RowHeightMm;
            using (var f = MmFont(tb.FontName, tb.FontSize, FontStyle.Regular))
            using (var fb = MmFont(tb.FontName, tb.FontSize, FontStyle.Bold))
            using (var border = new Pen(tb.BorderColor, 0.15f))
            using (var fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
            {
                if (rtl) fmt.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                if (tb.Columns.Count == 0)
                {
                    g.DrawRectangle(border, r.X, r.Y, r.Width, r.Height);
                    g.DrawString("Table - add columns in the properties grid, or drop a data source here", f, Brushes.Gray, r.X + 1, r.Y + 1);
                    return;
                }
                float off = 0;
                foreach (var c in tb.Columns)
                {
                    float w = (float)(c.WidthMm * scale);
                    float x = rtl ? r.Right - off - w : r.X + off;
                    off += w;
                    var hr = new RectangleF(x, r.Y, w, rowH);
                    using (var hb = new SolidBrush(tb.HeaderBackColor)) g.FillRectangle(hb, hr);
                    g.DrawRectangle(border, hr.X, hr.Y, hr.Width, hr.Height);
                    g.DrawString(c.Header, fb, Brushes.Black, new RectangleF(hr.X + 1, hr.Y, hr.Width - 2, hr.Height), fmt);
                    for (int i = 1; i <= 2; i++)
                    {
                        var cr = new RectangleF(x, r.Y + rowH * i, w, rowH);
                        g.DrawRectangle(border, cr.X, cr.Y, cr.Width, cr.Height);
                        g.DrawString("{" + c.Field + "}", f, Brushes.Gray, new RectangleF(cr.X + 1, cr.Y, cr.Width - 2, cr.Height), fmt);
                    }
                }
            }
        }

        Image GetImage(ImageElement im)
        {
            if (string.IsNullOrEmpty(im.ImageBase64)) return null;
            KeyValuePair<string, Image> cached;
            if (_images.TryGetValue(im, out cached) && ReferenceEquals(cached.Key, im.ImageBase64)) return cached.Value;
            try
            {
                var img = Image.FromStream(new MemoryStream(Convert.FromBase64String(im.ImageBase64)));
                _images[im] = new KeyValuePair<string, Image>(im.ImageBase64, img);
                return img;
            }
            catch (Exception) { return null; }
        }

        RectangleF[] HandleRects(RectangleF r)
        {
            float s = 6f / S, hs = s / 2;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            var pts = new[]
            {
                new PointF(r.X, r.Y), new PointF(cx, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, cy),
                new PointF(r.Right, r.Bottom), new PointF(cx, r.Bottom), new PointF(r.X, r.Bottom), new PointF(r.X, cy)
            };
            return pts.Select(p => new RectangleF(p.X - hs, p.Y - hs, s, s)).ToArray();
        }

        // ------------------------------------------------------------ geometry helpers

        PointF ToMm(Point p) { return new PointF((p.X - AutoScrollPosition.X) / S, (p.Y - AutoScrollPosition.Y) / S); }

        RectangleF AbsRect(BandBox bb, ReportElement e)
        {
            return new RectangleF((float)(Left + e.X), (float)(bb.Top + e.Y), (float)e.Width, (float)e.Height);
        }

        BandBox BoxOfElement(ReportElement e) { return _boxes.FirstOrDefault(b => b.Band.Elements.Contains(e)); }

        BandBox BoxAt(PointF p)
        {
            return _boxes.FirstOrDefault(b => p.X >= Left - 5 && p.X <= Left + _report.Page.ContentWidth + 5 &&
                                              p.Y >= b.Top && p.Y <= b.Top + b.Band.HeightMm);
        }

        static double Sn(double v) { return Math.Round(v / SnapMm) * SnapMm; }

        static RectangleF Local(ReportElement e) { return new RectangleF((float)e.X, (float)e.Y, (float)e.Width, (float)e.Height); }

        static bool TableAllowed(BandKind k)
        {
            return k == BandKind.Body || k == BandKind.ReportHeader || k == BandKind.ReportFooter;
        }

        // ------------------------------------------------------------ mouse

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (_report == null || e.Button != MouseButtons.Left) return;
            var p = ToMm(e.Location);
            _start = p; _moved = false; _mode = Mode.None;

            var selEl = _selected as ReportElement;
            if (selEl != null)
            {
                var bb = BoxOfElement(selEl);
                if (bb != null)
                {
                    var hs = HandleRects(AbsRect(bb, selEl));
                    for (int i = 0; i < hs.Length; i++)
                        if (hs[i].Contains(p)) { _mode = Mode.Resize; _handle = i; _orig = Local(selEl); return; }
                }
            }

            for (int bi = _boxes.Count - 1; bi >= 0; bi--)
            {
                var box = _boxes[bi];
                for (int ei = box.Band.Elements.Count - 1; ei >= 0; ei--)
                {
                    var el = box.Band.Elements[ei];
                    var r = AbsRect(box, el);
                    r.Inflate(el is LineElement ? 1f : 0.3f, el is LineElement ? 1f : 0.3f);
                    if (r.Contains(p))
                    {
                        Selected = el; _mode = Mode.Move; _orig = Local(el);
                        return;
                    }
                }
            }

            foreach (var box in _boxes)
            {
                double bottom = box.Top + box.Band.HeightMm;
                if (p.X >= Left && p.X <= Left + _report.Page.ContentWidth && Math.Abs(p.Y - bottom) <= EdgeGrab)
                {
                    Selected = box.Band; _mode = Mode.BandResize; _origBandH = box.Band.HeightMm;
                    return;
                }
                if (p.X >= Left && p.X <= Left + _report.Page.ContentWidth && p.Y >= box.Top - HeaderH && p.Y <= bottom)
                {
                    Selected = box.Band;
                    return;
                }
            }
            Selected = _report;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_report == null) return;
            var p = ToMm(e.Location);

            if (_mode == Mode.None) { UpdateCursor(p); return; }
            double dx = p.X - _start.X, dy = p.Y - _start.Y;
            var el = _selected as ReportElement;

            if (_mode == Mode.BandResize)
            {
                var band = (Band)_selected;
                double h = Math.Max(1, Sn(_origBandH + dy));
                if (Math.Abs(h - band.HeightMm) > 1e-6) { band.HeightMm = h; _moved = true; Relayout(); }
            }
            else if (el != null && _mode == Mode.Move)
            {
                // dragging over another band moves the element into it
                var cur = BoxOfElement(el);
                var target = BoxAt(p);
                if (cur != null && target != null && target != cur && (!(el is TableElement) || TableAllowed(target.Band.Kind)))
                {
                    cur.Band.Elements.Remove(el);
                    target.Band.Elements.Add(el);
                    el.Y = Math.Max(0, Sn(p.Y - target.Top - (_start.Y - (cur.Top + _orig.Y))));
                    if (el.Y + el.Height > target.Band.HeightMm) target.Band.HeightMm = Math.Ceiling(el.Y + el.Height);
                    _start = p; _orig = Local(el);
                    _moved = true;
                    Relayout();
                }
                else
                {
                    double nx = Math.Max(0, Sn(_orig.X + dx)), ny = Math.Max(0, Sn(_orig.Y + dy));
                    if (Math.Abs(nx - el.X) > 1e-6 || Math.Abs(ny - el.Y) > 1e-6) { el.X = nx; el.Y = ny; _moved = true; Invalidate(); }
                }
            }
            else if (el != null && _mode == Mode.Resize)
            {
                double x = _orig.X, y = _orig.Y, w = _orig.Width, h = _orig.Height;
                bool left = _handle == 0 || _handle == 6 || _handle == 7;
                bool right = _handle == 2 || _handle == 3 || _handle == 4;
                bool top = _handle == 0 || _handle == 1 || _handle == 2;
                bool bottom = _handle == 4 || _handle == 5 || _handle == 6;
                double minW = _orig.Width < 1 ? 0 : 1, minH = _orig.Height < 1 ? 0 : 1;
                if (left) { double nx = Sn(x + dx); w = x + w - nx; x = nx; }
                if (right) w = Sn(w + dx);
                if (top) { double ny = Sn(y + dy); h = y + h - ny; y = ny; }
                if (bottom) h = Sn(h + dy);
                if (w < minW) { if (left) x = _orig.X + _orig.Width - minW; w = minW; }
                if (h < minH) { if (top) y = _orig.Y + _orig.Height - minH; h = minH; }
                el.X = Math.Max(0, x); el.Y = Math.Max(0, y); el.Width = w; el.Height = h;
                _moved = true;
                Invalidate();
            }
            if (_moved) RaiseChanging();
        }

        void UpdateCursor(PointF p)
        {
            Cursor c = Cursors.Default;
            var selEl = _selected as ReportElement;
            if (selEl != null)
            {
                var bb = BoxOfElement(selEl);
                if (bb != null)
                {
                    var hs = HandleRects(AbsRect(bb, selEl));
                    Cursor[] cur = { Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE };
                    for (int i = 0; i < hs.Length; i++) if (hs[i].Contains(p)) { Cursor = cur[i]; return; }
                }
            }
            foreach (var box in _boxes)
            {
                if (box.Band.Elements.Any(el => AbsRect(box, el).Contains(p))) { c = Cursors.SizeAll; break; }
                if (p.X >= Left && p.X <= Left + _report.Page.ContentWidth && Math.Abs(p.Y - (box.Top + box.Band.HeightMm)) <= EdgeGrab) { c = Cursors.SizeNS; break; }
            }
            Cursor = c;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _mode = Mode.None;
            if (_moved) { _moved = false; GrowBandToFit(); RaiseChanged(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var t = _selected as TextElement;
            if (t != null)
            {
                var s = TextEditDialog.Prompt(FindForm(), "Edit text", t.Text);
                if (s != null) { t.Text = s; Invalidate(); RaiseChanged(); }
            }
        }

        void GrowBandToFit()
        {
            var el = _selected as ReportElement;
            if (el == null) return;
            var box = BoxOfElement(el);
            if (box == null) return;
            double need = el.Y + el.Height;
            if (need > box.Band.HeightMm) { box.Band.HeightMm = Math.Ceiling(need); Relayout(); }
        }

        // ------------------------------------------------------------ keyboard

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Delete: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var el = _selected as ReportElement;
            double step = e.Shift ? 5 : SnapMm;
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.D) { Duplicate(); e.Handled = true; return; }
            if (el == null) return;
            switch (e.KeyCode)
            {
                case Keys.Left: el.X = Math.Max(0, el.X - step); break;
                case Keys.Right: el.X += step; break;
                case Keys.Up: el.Y = Math.Max(0, el.Y - step); break;
                case Keys.Down: el.Y += step; break;
                default: return;
            }
            e.Handled = true;
            Invalidate();
            RaiseChanged();
        }

        // ------------------------------------------------------------ commands

        public Band CurrentBand()
        {
            var b = _selected as Band;
            if (b != null) return b;
            var el = _selected as ReportElement;
            if (el != null)
            {
                var box = BoxOfElement(el);
                if (box != null) return box.Band;
            }
            return _report.Bands.FirstOrDefault(x => x.Kind == BandKind.Body) ?? _report.Bands.FirstOrDefault();
        }

        public void AddElement(ElementKind kind)
        {
            if (_report == null) return;
            var band = CurrentBand();
            if (kind == ElementKind.Table && (band == null || !TableAllowed(band.Kind)))
                band = _report.Bands.FirstOrDefault(x => x.Kind == BandKind.Body) ?? AddBand(BandKind.Body, false);
            if (band == null) band = AddBand(BandKind.Body, false);

            ReportElement el;
            switch (kind)
            {
                case ElementKind.Image: el = new ImageElement { Width = 30, Height = 20 }; break;
                case ElementKind.Line: el = new LineElement { Width = 50 }; break;
                case ElementKind.Rectangle: el = new RectangleElement(); break;
                case ElementKind.Table:
                    var tb = new TableElement { Width = _report.Page.ContentWidth, Height = 18 };
                    for (int i = 1; i <= 3; i++) tb.Columns.Add(new TableColumn { Header = "Column" + i, Field = "Field" + i, WidthMm = 40 });
                    el = tb; break;
                default: el = new TextElement { Text = "Text", Width = 40, Height = 6 }; break;
            }
            el.Name = kind + (band.Elements.Count + 1).ToString();
            el.X = 2; el.Y = 2;
            if (el is ImageElement)
            {
                using (var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*" })
                    if (dlg.ShowDialog(FindForm()) == DialogResult.OK) ((ImageElement)el).LoadFromFile(dlg.FileName);
            }
            band.Elements.Add(el);
            if (el.Y + el.Height > band.HeightMm) band.HeightMm = Math.Ceiling(el.Y + el.Height + 1);
            Relayout();
            Selected = el;
            RaiseChanged();
        }

        public Band AddBand(BandKind kind, bool raise = true)
        {
            if (_report == null) return null;
            bool multi = kind == BandKind.Body || kind == BandKind.Detail || kind == BandKind.GroupHeader || kind == BandKind.GroupFooter;
            var existing = _report.Bands.FirstOrDefault(b => b.Kind == kind);
            if (!multi && existing != null) { Selected = existing; return existing; }
            var band = new Band
            {
                Kind = kind, Name = kind.ToString(),
                HeightMm = kind == BandKind.Detail ? 8 : kind == BandKind.PageHeader ? 15 : kind == BandKind.GroupHeader || kind == BandKind.GroupFooter ? 9 : 20
            };
            if (multi) band.Name = kind + (_report.Bands.Count(b => b.Kind == kind) + 1).ToString();
            // give new detail/group bands the data source of an existing detail band
            if (kind == BandKind.Detail || kind == BandKind.GroupHeader || kind == BandKind.GroupFooter)
            {
                var d = _report.Bands.FirstOrDefault(b => b.Kind == BandKind.Detail && !string.IsNullOrEmpty(b.DataSource));
                if (d != null) band.DataSource = d.DataSource;
            }
            _report.Bands.Add(band);
            Relayout();
            Selected = band;
            if (raise) RaiseChanged();
            return band;
        }

        /// <summary>Moves the selected band up (-1) or down (+1) among bands of the same section.</summary>
        public void MoveBand(int direction)
        {
            var band = _selected as Band ?? (_selected is ReportElement ? CurrentBand() : null);
            if (band == null) return;
            var ordered = _report.Bands.OrderBy(b => Rank(b.Kind)).ToList();
            int i = ordered.IndexOf(band), j = i + direction;
            if (j < 0 || j >= ordered.Count || Rank(ordered[j].Kind) != Rank(band.Kind)) return;
            int a = _report.Bands.IndexOf(band), b2 = _report.Bands.IndexOf(ordered[j]);
            _report.Bands[a] = ordered[j];
            _report.Bands[b2] = band;
            Relayout();
            Selected = band;
            RaiseChanged();
        }

        public void DeleteSelected()
        {
            var el = _selected as ReportElement;
            var band = _selected as Band;
            if (el != null)
            {
                var box = BoxOfElement(el);
                if (box != null) box.Band.Elements.Remove(el);
            }
            else if (band != null)
            {
                if (band.Elements.Count > 0 &&
                    MessageBox.Show(FindForm(), "Delete band '" + band.Name + "' and its " + band.Elements.Count + " element(s)?", "Delete band",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _report.Bands.Remove(band);
            }
            else return;
            Relayout();
            Selected = _report;
            RaiseChanged();
        }

        public void Duplicate()
        {
            var el = _selected as ReportElement;
            if (el == null) return;
            var box = BoxOfElement(el);
            if (box == null) return;
            var copy = _report.CloneElement(el);
            copy.X += 3; copy.Y += 3;
            box.Band.Elements.Add(copy);
            Relayout();
            Selected = copy;
            RaiseChanged();
        }

        // ------------------------------------------------------------ drag & drop from the field tree

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            var s = e.Data.GetDataPresent(DataFormats.StringFormat) ? e.Data.GetData(DataFormats.StringFormat) as string : null;
            e.Effect = s != null && s.StartsWith("PHF:") ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var s = e.Data.GetData(DataFormats.StringFormat) as string;
            if (_report == null || s == null || !s.StartsWith("PHF:")) return;
            var parts = s.Substring(4).Split('|');
            string ds = parts[0], field = parts.Length > 1 ? parts[1] : "";
            var p = ToMm(PointToClient(new Point(e.X, e.Y)));

            var hit = _boxes.FirstOrDefault(b => p.Y >= b.Top && p.Y <= b.Top + b.Band.HeightMm + 0.01);
            if (hit == null) return;
            double lx = Math.Max(0, Sn(p.X - Left)), ly = Math.Max(0, Sn(p.Y - hit.Top));

            if (field.Length == 0)
            {
                if (ds.Length == 0 || !TableAllowed(hit.Band.Kind)) return;
                var cols = FieldLookup != null ? FieldLookup(ds) : null;
                var tb = new TableElement { Name = "Table_" + ds, DataSource = ds, X = lx, Y = ly, Width = Math.Max(20, _report.Page.ContentWidth - lx), Height = 18 };
                if (cols != null)
                {
                    double each = tb.Width / Math.Max(1, cols.Count);
                    foreach (var c in cols) tb.Columns.Add(new TableColumn { Header = c, Field = c, WidthMm = each });
                }
                hit.Band.Elements.Add(tb);
                if (ly + tb.Height > hit.Band.HeightMm) hit.Band.HeightMm = Math.Ceiling(ly + tb.Height + 1);
                Relayout(); Selected = tb; RaiseChanged();
                return;
            }

            bool rowBand = hit.Band.Kind == BandKind.Detail || hit.Band.Kind == BandKind.GroupHeader || hit.Band.Kind == BandKind.GroupFooter;
            if (rowBand && string.IsNullOrEmpty(hit.Band.DataSource) && ds.Length > 0) hit.Band.DataSource = ds;
            bool bare = ds.Length == 0 || (rowBand && string.Equals(hit.Band.DataSource, ds, StringComparison.OrdinalIgnoreCase));
            string name = field.IndexOf(' ') >= 0 ? "[" + field + "]" : field;
            var t = new TextElement { Name = field, Text = "{" + (bare ? name : (ds.IndexOf(' ') >= 0 ? "[" + ds + "]." : ds + ".") + name) + "}", X = lx, Y = ly, Width = 40, Height = 6 };
            hit.Band.Elements.Add(t);
            if (ly + t.Height > hit.Band.HeightMm) hit.Band.HeightMm = Math.Ceiling(ly + t.Height + 1);
            Relayout(); Selected = t; RaiseChanged();
        }
    }

    /// <summary>Small multi-line text prompt.</summary>
    public static class TextEditDialog
    {
        public static string Prompt(IWin32Window owner, string title, string text)
        {
            using (var f = new Form { Text = title, Width = 480, Height = 300, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false })
            {
                var tb = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = text ?? "" };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
                bar.Controls.Add(cancel); bar.Controls.Add(ok);
                f.Controls.Add(tb); f.Controls.Add(bar);
                f.AcceptButton = null; f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
            }
        }
    }
}
