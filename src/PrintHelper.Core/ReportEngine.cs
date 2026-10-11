using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PrintHelper
{
    /// <summary>
    /// The one class a host application needs.
    /// <code>
    /// FontManager.LoadFontFile("Vazirmatn-Regular.ttf");           // optional: your own fonts
    /// var engine = new ReportEngine(ReportDefinition.Load("invoice.prt"))
    ///     .RegisterData("Customers", customerList)
    ///     .SetVariable("Title", "March invoices");
    /// engine.ExportPdf("out.pdf");
    /// engine.ExportExcel("out.xlsx");
    /// </code>
    /// Printing to a Windows printer lives in PrintHelper.Windows (engine.Print()).
    /// </summary>
    public class ReportEngine
    {
        readonly List<string> _sqlLoaded = new List<string>();

        public ReportDefinition Report { get; set; }
        public DataRegistry Data { get; private set; }
        public Dictionary<string, object> Variables { get; private set; }

        public ReportEngine(ReportDefinition report)
        {
            if (report == null) throw new ArgumentNullException("report");
            Report = report;
            Data = new DataRegistry();
            Variables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Supply data from your app: DataTable, List&lt;T&gt;, array, DataView... Overrides a SQL source of the same name.</summary>
        public ReportEngine RegisterData(string name, object data) { Data.Add(name, data); return this; }

        /// <summary>Set a value usable as {Name} in text and as @Name in SQL parameters.</summary>
        public ReportEngine SetVariable(string name, object value) { Variables[name] = value; return this; }

        /// <summary>Loads a font file for this process (same as FontManager.LoadFontFile).</summary>
        public ReportEngine AddFont(string path) { FontManager.LoadFontFile(path); return this; }

        void PrepareData()
        {
            foreach (var n in _sqlLoaded) Data.Remove(n);
            _sqlLoaded.Clear();
            foreach (var d in Report.DataSources)
            {
                if (string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Query) || Data.Contains(d.Name)) continue;
                Data.Add(d.Name, d.Execute(Variables));
                _sqlLoaded.Add(d.Name);
            }
        }

        /// <summary>Runs queries and lays the report out into pages. Every export/print goes through this.</summary>
        public IList<RenderPage> Build()
        {
            FontManager.LoadEmbedded(Report.EmbeddedFonts);
            PrepareData();
            var ctx = new EvalContext { Data = Data, Vars = Variables };
            var first = new LayoutEngine(Report, ctx).Run();      // pass 1: learn the page count
            ctx.Pages = first.Count;
            return new LayoutEngine(Report, ctx).Run();           // pass 2: {Pages} is now correct
        }

        public void ExportPdf(string path)
        {
            var pages = Build();
            using (var fs = File.Create(path)) SkiaRenderer.ToPdf(pages, Report.Name, fs);
        }

        public void ExportPdf(Stream output) { SkiaRenderer.ToPdf(Build(), Report.Name, output); }

        public byte[] ExportPdfBytes()
        {
            using (var ms = new MemoryStream()) { ExportPdf(ms); return ms.ToArray(); }
        }

        public void ExportExcel(string path)
        {
            var pages = Build();
            using (var fs = File.Create(path)) ExcelExporter.Export(pages, Report, fs);
        }

        public void ExportExcel(Stream output) { ExcelExporter.Export(Build(), Report, output); }

        /// <summary>One PNG per page. Handy for previews in any UI framework (WPF Image, web, ...).</summary>
        public IList<byte[]> RenderPng(float dpi = 96)
        {
            return Build().Select(p => SkiaRenderer.ToPng(p, dpi)).ToList();
        }

        /// <summary>Column names of a data source for tooling. Uses registered data first, then the SQL schema.</summary>
        public IList<string> GetFields(string dataSource, bool querySchema)
        {
            var t = Data.Get(dataSource);
            if (t != null) return t.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToList();
            var def = Report.DataSources.FirstOrDefault(d => string.Equals(d.Name, dataSource, StringComparison.OrdinalIgnoreCase));
            if (def == null || !querySchema) return new List<string>();
            return def.GetSchema(Variables).Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToList();
        }
    }
}
