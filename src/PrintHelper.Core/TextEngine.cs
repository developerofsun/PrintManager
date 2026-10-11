using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace PrintHelper
{
    internal struct FontSpec
    {
        public string Name;
        public float SizePt;
        public bool Bold, Italic;

        public static FontSpec From(string name, float size, FontStyle style)
        {
            return new FontSpec { Name = name, SizePt = size <= 0 ? 10 : size, Bold = (style & FontStyle.Bold) != 0, Italic = (style & FontStyle.Italic) != 0 };
        }

        public float SizeMm { get { return SizePt * 25.4f / 72f; } }
    }

    internal sealed class Piece
    {
        public string Text;
        public SKTypeface Face;
        public double Width; // mm
    }

    internal sealed class LineLayout
    {
        public List<Piece> Pieces = new List<Piece>();
        public double Width;
    }

    /// <summary>Shapes (HarfBuzz), measures, wraps and draws text. Persian/Kurdish/Arabic get correct joined letter forms.</summary>
    internal static class TextEngine
    {
        static readonly object Gate = new object();
        static readonly Dictionary<SKTypeface, SKShaper> Shapers = new Dictionary<SKTypeface, SKShaper>();
        static readonly Dictionary<string, LineLayout> Cache = new Dictionary<string, LineLayout>();

        public static bool ResolveRtl(string text, TextDirection element, TextDirection reportDefault)
        {
            var d = element != TextDirection.Auto ? element : reportDefault;
            if (d == TextDirection.RightToLeft) return true;
            if (d == TextDirection.LeftToRight) return false;
            return Bidi.DetectRtl(text);
        }

        static SKShaper ShaperFor(SKTypeface face)
        {
            SKShaper s;
            if (!Shapers.TryGetValue(face, out s)) { s = new SKShaper(face); Shapers[face] = s; }
            return s;
        }

        static SKPaint MakePaint(SKTypeface face, FontSpec f)
        {
            var p = new SKPaint { Typeface = face, TextSize = f.SizeMm, IsAntialias = true, SubpixelText = true, LcdRenderText = false };
            p.FakeBoldText = f.Bold && face.FontWeight < 600;
            if (f.Italic && face.FontSlant == SKFontStyleSlant.Upright) p.TextSkewX = -0.25f;
            return p;
        }

        public static double LineHeight(FontSpec f)
        {
            lock (Gate)
                using (var p = MakePaint(FontManager.Get(f.Name, f.Bold, f.Italic), f))
                {
                    var m = p.FontMetrics;
                    return m.Descent - m.Ascent + m.Leading;
                }
        }

        public static double Ascent(FontSpec f)
        {
            lock (Gate)
                using (var p = MakePaint(FontManager.Get(f.Name, f.Bold, f.Italic), f))
                    return -p.FontMetrics.Ascent;
        }

        static bool KeepsFace(int cp, string s, int index)
        {
            if (cp == 0x200C || cp == 0x200D || cp == 0x200E || cp == 0x200F || cp == ' ') return true;
            var cat = CharUnicodeInfo.GetUnicodeCategory(s, index);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.Format;
        }

        static int FirstMissing(SKTypeface face, string t)
        {
            for (int i = 0; i < t.Length; i++)
            {
                int len = char.IsHighSurrogate(t[i]) && i + 1 < t.Length ? 2 : 1;
                int cp = len == 2 ? char.ConvertToUtf32(t, i) : t[i];
                i += len - 1;
                if (cp <= 0x20 || (cp >= 0x200B && cp <= 0x200F) || cp == 0xA0) continue;
                if (!face.ContainsGlyph(cp)) return cp;
            }
            return -1;
        }

        static void AddPieces(LineLayout ll, BidiRun run, FontSpec f, SKTypeface primary)
        {
            var parts = new List<KeyValuePair<string, SKTypeface>>();
            var sb = new StringBuilder();
            SKTypeface cur = null;
            string t = run.Text;
            // HarfBuzz picks direction from the script; feed Arabic-script digits reversed so they display left-to-right
            if (run.ArabicDigits) { var arr = t.ToCharArray(); Array.Reverse(arr); t = new string(arr); }

            // If the primary font lacks glyphs, prefer ONE font for the whole run so Arabic-script letters still join.
            int missing = FirstMissing(primary, t);
            if (missing >= 0)
            {
                var alt = FontManager.FindFor(primary, missing, f.Bold, f.Italic);
                if (!ReferenceEquals(alt, primary) && FirstMissing(alt, t) < 0) primary = alt;
            }
            for (int i = 0; i < t.Length; i++)
            {
                int len = char.IsHighSurrogate(t[i]) && i + 1 < t.Length ? 2 : 1;
                int cp = len == 2 ? char.ConvertToUtf32(t, i) : t[i];
                SKTypeface face;
                if (KeepsFace(cp, t, i) && cur != null) face = cur;
                else if (primary.ContainsGlyph(cp)) face = primary;
                else face = FontManager.FindFor(primary, cp, f.Bold, f.Italic);
                if (cur != null && !ReferenceEquals(face, cur) && sb.Length > 0)
                {
                    parts.Add(new KeyValuePair<string, SKTypeface>(sb.ToString(), cur));
                    sb.Clear();
                }
                cur = face;
                sb.Append(t, i, len);
                i += len - 1;
            }
            if (sb.Length > 0) parts.Add(new KeyValuePair<string, SKTypeface>(sb.ToString(), cur ?? primary));
            if (run.Rtl) parts.Reverse();

            foreach (var kv in parts)
            {
                using (var p = MakePaint(kv.Value, f))
                {
                    var r = ShaperFor(kv.Value).Shape(kv.Key, p);
                    ll.Pieces.Add(new Piece { Text = kv.Key, Face = kv.Value, Width = r.Width });
                    ll.Width += r.Width;
                }
            }
        }

        public static LineLayout LayoutLine(string text, FontSpec f, bool rtl)
        {
            var key = f.Name + "|" + f.SizePt + "|" + f.Bold + "|" + f.Italic + "|" + rtl + "|" + text;
            lock (Gate)
            {
                LineLayout ll;
                if (Cache.TryGetValue(key, out ll)) return ll;
                ll = new LineLayout();
                if (!string.IsNullOrEmpty(text))
                {
                    var primary = FontManager.Get(f.Name, f.Bold, f.Italic);
                    foreach (var run in Bidi.Reorder(text, rtl)) AddPieces(ll, run, f, primary);
                }
                if (Cache.Count > 4000) Cache.Clear();
                Cache[key] = ll;
                return ll;
            }
        }

        public static double Width(string text, FontSpec f, bool rtl) { return LayoutLine(text, f, rtl).Width; }

        public static List<string> Wrap(string text, FontSpec f, double maxW, bool wrap, bool rtl)
        {
            var lines = new List<string>();
            foreach (var para in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (!wrap || maxW <= 0) { lines.Add(para); continue; }
                string cur = "";
                foreach (var word in para.Split(' '))
                {
                    var cand = cur.Length == 0 ? word : cur + " " + word;
                    if (cur.Length > 0 && Width(cand, f, rtl) > maxW) { lines.Add(cur); cur = word; }
                    else cur = cand;
                    // one word wider than the box: break it by characters
                    while (cur.Length > 1 && Width(cur, f, rtl) > maxW)
                    {
                        int n = cur.Length - 1;
                        while (n > 1 && Width(cur.Substring(0, n), f, rtl) > maxW) n--;
                        if (char.IsLowSurrogate(cur[n]) && n > 1) n--;
                        lines.Add(cur.Substring(0, n));
                        cur = cur.Substring(n);
                    }
                }
                lines.Add(cur);
            }
            return lines;
        }

        /// <summary>Draws one already-positioned line. (X,Y) is the top-left of the line box, in the canvas's units (mm).</summary>
        public static void DrawLine(SKCanvas canvas, RenderItem it)
        {
            var f = FontSpec.From(it.FontName, it.FontSize, it.Style);
            LineLayout ll;
            double ascent;
            lock (Gate) { ll = LayoutLine(it.Text, f, it.Rtl); ascent = Ascent(f); }
            float x = (float)it.X, baseline = (float)(it.Y + ascent);
            var color = new SKColor(it.Color.R, it.Color.G, it.Color.B, it.Color.A);
            lock (Gate)
            {
                foreach (var piece in ll.Pieces)
                {
                    using (var p = MakePaint(piece.Face, f))
                    {
                        p.Color = color;
                        canvas.DrawShapedText(ShaperFor(piece.Face), piece.Text, x, baseline, p);
                    }
                    x += (float)piece.Width;
                }
                if ((it.Style & FontStyle.Underline) != 0 && ll.Width > 0)
                    using (var p = new SKPaint { Color = color, IsAntialias = true, StrokeWidth = Math.Max(0.05f, f.SizeMm / 16f), Style = SKPaintStyle.Stroke })
                        canvas.DrawLine((float)it.X, baseline + f.SizeMm * 0.12f, (float)(it.X + ll.Width), baseline + f.SizeMm * 0.12f, p);
            }
        }
    }
}
