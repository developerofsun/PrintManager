using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using PrintHelper;
using PrintHelper.Designer;

static class Program
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failures++; }

    static void Mouse(DesignerCanvas c, string method, double mmX, double mmY)
    {
        float S = c.DeviceDpi / 25.4f;   // zoom 1
        var args = new MouseEventArgs(MouseButtons.Left, 1, (int)(mmX * S), (int)(mmY * S), 0);
        typeof(DesignerCanvas).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, new object[] { args });
    }

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "out";
        Directory.CreateDirectory(outDir);

        var engine = new ReportEngine(ReportDefinition.CreateDefault());
        var designer = new DesignerControl(engine) { Width = 1200, Height = 800 };
        var form = new Form { Width = 1200, Height = 800 };
        form.Controls.Add(designer);
        form.Show();                       // creates handles so layout/paint work
        Application.DoEvents();
        var canvas = designer.Canvas;
        var report = engine.Report;

        // ---- add element + undo/redo
        int before = report.Bands.SelectMany(b => b.Elements).Count();
        canvas.AddElement(ElementKind.Text);
        int after = report.Bands.SelectMany(b => b.Elements).Count();
        Check(after == before + 1, "AddElement adds an element");
        designer.Undo();
        Check(report.Bands.SelectMany(b => b.Elements).Count() == before, "Undo removes it");
        designer.Redo();
        Check(report.Bands.SelectMany(b => b.Elements).Count() == after, "Redo brings it back");
        Check(ReferenceEquals(designer.Engine.Report, report), "report object identity survives undo/redo");

        // ---- cross-band drag: move the title from PageHeader into Body with the mouse
        var header = report.Bands.First(b => b.Kind == BandKind.PageHeader);
        var body = report.Bands.First(b => b.Kind == BandKind.Body);
        var title = header.Elements.OfType<TextElement>().First();
        // layout: page top 8 + margin 10 = 18; header strip 5 -> header body top 23; title at X=0,Y=3 => abs x=18, y=26
        Mouse(canvas, "OnMouseDown", 30, 28);
        Check(ReferenceEquals(canvas.Selected, title), "mouse-down selects the title element");
        Mouse(canvas, "OnMouseMove", 30, 45);    // still in header/gap region
        Mouse(canvas, "OnMouseMove", 30, 60);    // inside the Body band (starts at 45.5)
        Mouse(canvas, "OnMouseUp", 30, 60);
        Check(body.Elements.Contains(title) && !header.Elements.Contains(title), "dragging across bands moves the element to the other band");
        designer.Undo();
        header = report.Bands.First(b => b.Kind == BandKind.PageHeader);   // undo rebuilds band objects
        body = report.Bands.First(b => b.Kind == BandKind.Body);
        Check(header.Elements.Any(e => e.Name == "Title") && !body.Elements.Any(e => e.Name == "Title"), "Undo puts it back in the original band");

        // ---- property-grid style edit, then undo
        var text = header.Elements.OfType<TextElement>().First();
        string original = text.Text;
        text.Text = "changed";
        canvas.Selected = text;
        typeof(DesignerCanvas).GetMethod("RaiseChanged", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(canvas, null);
        designer.Undo();
        header = report.Bands.First(b => b.Kind == BandKind.PageHeader);
        Check(header.Elements.OfType<TextElement>().First().Text == original, "undo reverts a text edit");

        // ---- group bands
        canvas.AddBand(BandKind.GroupHeader);
        canvas.AddBand(BandKind.Detail);
        canvas.AddBand(BandKind.GroupFooter);
        Check(report.Bands.Count(b => b.Kind == BandKind.GroupHeader) == 1 && report.Bands.Count(b => b.Kind == BandKind.GroupFooter) == 1, "group bands can be added");
        var gh = report.Bands.First(b => b.Kind == BandKind.GroupHeader);
        canvas.Selected = gh;
        int i0 = report.Bands.IndexOf(gh);
        canvas.MoveBand(1);
        Check(report.Bands.IndexOf(gh) != i0, "MoveBand reorders bands within a section");

        // ---- custom font loading and rendering
        string tahoma = @"C:\Windows\Fonts\tahoma.ttf";
        if (File.Exists(tahoma))
        {
            string family = FontManager.LoadFontFile(tahoma);
            Check(FontManager.LoadedFamilyNames().Contains(family), "FontManager loads a font file (" + family + ")");
            var r = new ReportDefinition { Name = "fontcheck" };
            r.Page.Paper = PaperPreset.Custom; r.Page.WidthMm = 120; r.Page.HeightMm = 30;
            var b = new Band { Kind = BandKind.Body, HeightMm = 20 };
            b.Elements.Add(new TextElement { Text = "Tahoma: سلام ڕێبوار ۱۲۳ Hello", FontName = family, FontSize = 16, Width = 110, Height = 10 });
            r.Bands.Add(b);
            File.WriteAllBytes(Path.Combine(outDir, "font.png"), new ReportEngine(r).RenderPng(150)[0]);
        }

        // ---- embedded font round trip
        var rep2 = new ReportDefinition();
        rep2.EmbeddedFonts.Add(new EmbeddedFont { FileName = "x.ttf", Base64 = "AAAA" });
        Check(ReportDefinition.FromJson(rep2.ToJson()).EmbeddedFonts.Count == 1, "embedded fonts survive JSON");

        // ---- paint the canvas to a bitmap to make sure drawing does not throw
        canvas.Selected = report;
        using (var bmp = new Bitmap(canvas.Width, canvas.Height))
        {
            canvas.DrawToBitmap(bmp, new Rectangle(0, 0, canvas.Width, canvas.Height));
            bmp.Save(Path.Combine(outDir, "designer.png"));
        }
        Check(true, "canvas paints");

        form.Close();
        Console.WriteLine(failures == 0 ? "\nALL PASSED" : "\n" + failures + " FAILED");
        return failures;
    }
}
