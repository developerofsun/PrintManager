using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using SkiaSharp;

namespace PrintHelper
{
    /// <summary>Draws finished pages with Skia: the same code path produces PDF, PNG previews and print bitmaps.</summary>
    public static class SkiaRenderer
    {
        const float PtToMm = 25.4f / 72f;

        static SKColor Col(Color c) { return new SKColor(c.R, c.G, c.B, c.A); }

        /// <summary>Draws a page onto a canvas whose units are millimetres.</summary>
        public static void DrawPage(SKCanvas canvas, RenderPage page)
        {
            var images = new Dictionary<byte[], SKBitmap>();
            try
            {
                foreach (var it in page.Items)
                {
                    float x = (float)it.X, y = (float)it.Y, w = (float)it.W, h = (float)it.H;
                    switch (it.Kind)
                    {
                        case ItemKind.Rect:
                            if (it.Fill.A > 0)
                                using (var p = new SKPaint { Color = Col(it.Fill), Style = SKPaintStyle.Fill, IsAntialias = true }) canvas.DrawRect(x, y, w, h, p);
                            if (it.Thickness > 0 && it.Color.A > 0)
                                using (var p = new SKPaint { Color = Col(it.Color), Style = SKPaintStyle.Stroke, StrokeWidth = (float)it.Thickness * PtToMm, IsAntialias = true })
                                    canvas.DrawRect(x, y, w, h, p);
                            break;

                        case ItemKind.Line:
                            using (var p = new SKPaint { Color = Col(it.Color), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.05f, (float)it.Thickness * PtToMm), IsAntialias = true })
                                canvas.DrawLine(x, y, x + w, y + h, p);
                            break;

                        case ItemKind.Image:
                            SKBitmap bmp;
                            if (!images.TryGetValue(it.Image, out bmp)) { bmp = SKBitmap.Decode(it.Image); images[it.Image] = bmp; }
                            if (bmp != null)
                                using (var p = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                                    canvas.DrawBitmap(bmp, new SKRect(x, y, x + w, y + h), p);
                            break;

                        case ItemKind.Text:
                            if (!string.IsNullOrEmpty(it.Text)) TextEngine.DrawLine(canvas, it);
                            break;
                    }
                }
            }
            finally
            {
                foreach (var b in images.Values) if (b != null) b.Dispose();
            }
        }

        /// <summary>Renders a page to PNG bytes at the given resolution.</summary>
        public static byte[] ToPng(RenderPage page, float dpi)
        {
            int wpx = (int)Math.Ceiling(page.Width / 25.4 * dpi), hpx = (int)Math.Ceiling(page.Height / 25.4 * dpi);
            using (var bmp = new SKBitmap(wpx, hpx))
            {
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.White);
                    canvas.Scale(dpi / 25.4f);
                    DrawPage(canvas, page);
                }
                using (var image = SKImage.FromBitmap(bmp))
                using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                    return data.ToArray();
            }
        }

        /// <summary>Writes all pages to a PDF. Text is vector, fonts are embedded (subset), Arabic-script text is shaped.</summary>
        public static void ToPdf(IList<RenderPage> pages, string title, Stream output)
        {
            var meta = new SKDocumentPdfMetadata { Title = title, Creator = "PrintHelper", RasterDpi = 300, EncodingQuality = 90 };
            using (var doc = SKDocument.CreatePdf(output, meta))
            {
                foreach (var p in pages)
                {
                    var canvas = doc.BeginPage((float)(p.Width * 72 / 25.4), (float)(p.Height * 72 / 25.4));
                    canvas.Scale(72f / 25.4f);
                    DrawPage(canvas, p);
                    doc.EndPage();
                }
                doc.Close();
            }
        }
    }
}
