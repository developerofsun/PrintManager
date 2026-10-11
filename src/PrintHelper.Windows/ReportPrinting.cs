using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;

namespace PrintHelper
{
    /// <summary>Printing to Windows printers. Pages are rendered by the same engine as PDF, at 300 dpi.</summary>
    public static class ReportPrinting
    {
        /// <summary>Prints the report. printerName = null uses the default printer.</summary>
        public static void Print(this ReportEngine engine, string printerName = null)
        {
            using (var doc = engine.CreatePrintDocument(printerName)) doc.Print();
        }

        /// <summary>Creates a PrintDocument you can hand to PrintDialog / PrintPreviewDialog / your own code.</summary>
        public static PrintDocument CreatePrintDocument(this ReportEngine engine, string printerName = null, float dpi = 300)
        {
            IList<RenderPage> pages = engine.Build();
            var report = engine.Report;
            var doc = new PrintDocument { DocumentName = report.Name };
            if (!string.IsNullOrEmpty(printerName)) doc.PrinterSettings.PrinterName = printerName;

            var pg = report.Page;
            doc.DefaultPageSettings.PaperSize = new PaperSize("Custom",
                (int)Math.Round(pg.WidthMm / 25.4 * 100), (int)Math.Round(pg.HeightMm / 25.4 * 100));
            doc.DefaultPageSettings.Landscape = pg.Landscape;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

            int index = 0;
            doc.BeginPrint += (s, e) => index = 0;
            doc.PrintPage += (s, e) =>
            {
                var page = pages[index];
                using (var ms = new MemoryStream(SkiaRenderer.ToPng(page, dpi)))
                using (var bmp = new Bitmap(ms))
                {
                    var g = e.Graphics;
                    g.PageUnit = GraphicsUnit.Millimeter;
                    // printers have unprintable hard margins; shift so (0,0) is the physical paper corner
                    g.TranslateTransform(-e.PageSettings.HardMarginX * 0.254f, -e.PageSettings.HardMarginY * 0.254f);
                    g.DrawImage(bmp, 0f, 0f, (float)page.Width, (float)page.Height);
                }
                index++;
                e.HasMorePages = index < pages.Count;
            };
            return doc;
        }
    }
}
