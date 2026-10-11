using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace PrintHelper
{
    /// <summary>
    /// Exports the report's text cells to one worksheet. Cells are placed by their position on the page:
    /// items on the same visual row share an Excel row, distinct left edges become columns.
    /// Numbers and dates from data stay real Excel numbers/dates. Repeated page headers/footers are skipped.
    /// Right-to-left reports get a right-to-left sheet with the first column on the right.
    /// </summary>
    public static class ExcelExporter
    {
        const double Tol = 1.5; // mm

        public static void Export(IList<RenderPage> pages, ReportDefinition report, Stream output)
        {
            bool rtl = report.DefaultDirection == TextDirection.RightToLeft ||
                       report.Bands.SelectMany(b => b.Elements).OfType<TableElement>().Any(t => t.Direction == TextDirection.RightToLeft);

            var cells = pages.SelectMany(p => p.Items)
                             .Where(i => i.Kind == ItemKind.Text && !i.Cont && !i.Repeat && !string.IsNullOrEmpty(i.Full))
                             .OrderBy(i => i.Page).ThenBy(i => i.RowY).ThenBy(i => i.ColX)
                             .ToList();

            // rows: cluster RowY within a page
            var rowOf = new Dictionary<RenderItem, int>();
            int row = 0, lastPage = -1;
            double clusterY = double.MinValue;
            foreach (var c in cells)
            {
                if (c.Page != lastPage || c.RowY - clusterY > Tol) { row++; clusterY = c.RowY; lastPage = c.Page; }
                rowOf[c] = row;
            }

            // columns: cluster left edges
            var colStarts = new List<double>();
            foreach (var x in cells.Select(c => c.ColX).OrderBy(v => v))
                if (colStarts.Count == 0 || x - colStarts[colStarts.Count - 1] > Tol) colStarts.Add(x);
            Func<double, int> colOf = x =>
            {
                int best = 0;
                for (int i = 0; i < colStarts.Count; i++) if (x >= colStarts[i] - Tol) best = i;
                return rtl ? colStarts.Count - best : best + 1;
            };

            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add(SafeSheetName(report.Name));
                if (rtl) ws.RightToLeft = true;
                var widths = new Dictionary<int, double>();
                foreach (var c in cells)
                {
                    int col = colOf(c.ColX);
                    var cell = ws.Cell(rowOf[c], col);
                    bool numeric = c.Raw is decimal || c.Raw is double || c.Raw is float || c.Raw is int || c.Raw is long ||
                                   c.Raw is short || c.Raw is byte || c.Raw is DateTime;
                    if (numeric)
                    {
                        try { cell.Value = XLCellValue.FromObject(c.Raw); }
                        catch (Exception) { cell.Value = c.Full; numeric = false; }
                    }
                    else cell.Value = c.Full;

                    if ((c.Style & FontStyle.Bold) != 0) cell.Style.Font.Bold = true;
                    if (!string.IsNullOrEmpty(c.FontName)) cell.Style.Font.FontName = c.FontName;
                    if (c.FontSize > 0) cell.Style.Font.FontSize = c.FontSize;
                    if (numeric && !(c.Raw is DateTime)) cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                    double w;
                    widths.TryGetValue(col, out w);
                    widths[col] = Math.Max(w, Math.Min(80, Math.Max(6, c.ColW / 1.9)));
                }
                foreach (var kv in widths) ws.Column(kv.Key).Width = kv.Value;
                wb.SaveAs(output);
            }
        }

        static string SafeSheetName(string name)
        {
            var s = new string((name ?? "Report").Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray());
            if (s.Length == 0) s = "Report";
            return s.Length > 31 ? s.Substring(0, 31) : s;
        }
    }
}
