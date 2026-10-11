using System;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using PrintHelper;

static class Program
{
    static int failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) failures++;
    }

    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "out";
        Directory.CreateDirectory(outDir);

        var sales = new DataTable("Sales");
        sales.Columns.Add("City", typeof(string));
        sales.Columns.Add("Customer", typeof(string));
        sales.Columns.Add("Qty", typeof(int));
        sales.Columns.Add("Price", typeof(decimal));
        string[] cities = { "هەولێر", "تهران", "القاهرة", "London" };
        string[] names = { "ئاکۆ محەمەد", "علی رضایی", "محمد علي", "John Smith", "ڕێبوار کەریم", "سارا احمدی" };
        var rnd = new Random(3);
        for (int i = 0; i < 70; i++)
            sales.Rows.Add(cities[rnd.Next(cities.Length)], names[rnd.Next(names.Length)], rnd.Next(1, 9), Math.Round((decimal)(rnd.NextDouble() * 90 + 10), 2));

        // ---- report 1: grouping + formulas, LTR page with mixed-language text
        var r = ReportDefinition.CreateDefault();
        r.Name = "Sales by city";
        r.Digits = DigitStyle.AsIs;
        ((TextElement)r.Bands[0].Elements[0]).Text = "Sales by city / فروش بر اساس شهر / فرۆشتن بە پێی شار";
        ((TextElement)r.Bands[0].Elements[0]).FontSize = 13;
        ((TextElement)r.Bands[0].Elements[0]).Width = 170;
        r.Bands.RemoveAll(b => b.Kind == BandKind.Body);
        var gh = new Band { Name = "CityHeader", Kind = BandKind.GroupHeader, DataSource = "Sales", GroupBy = "City", HeightMm = 8 };
        gh.Elements.Add(new TextElement { Text = "{City}  ({Count()} rows)", Bold = true, FontSize = 11, BackColor = Color.FromArgb(230, 236, 250), X = 0, Y = 1, Width = 180, Height = 6 });
        var det = new Band { Name = "Detail", Kind = BandKind.Detail, DataSource = "Sales", HeightMm = 6, SortBy = "Customer" };
        det.Elements.Add(new TextElement { Text = "{RowNo()}", X = 0, Y = 0, Width = 10, Height = 6 });
        det.Elements.Add(new TextElement { Text = "{Customer}", X = 12, Y = 0, Width = 60, Height = 6 });
        det.Elements.Add(new TextElement { Text = "{Qty} x {Price:N2}", X = 75, Y = 0, Width = 40, Height = 6 });
        det.Elements.Add(new TextElement { Text = "{Qty * Price:N2}", X = 120, Y = 0, Width = 30, Height = 6, Align = HAlign.Right });
        det.Elements.Add(new TextElement { Text = "{IIF(Qty * Price > 400, 'BIG', '')}", X = 155, Y = 0, Width = 20, Height = 6, Bold = true, ForeColor = Color.Firebrick });
        var gf = new Band { Name = "CityFooter", Kind = BandKind.GroupFooter, DataSource = "Sales", GroupBy = "City", HeightMm = 9 };
        gf.Elements.Add(new TextElement { Text = "Subtotal {City}: {Sum(Qty * Price):N2}", Bold = true, X = 60, Y = 1, Width = 90, Height = 6, Align = HAlign.Right });
        var rf = new Band { Name = "ReportFooter", Kind = BandKind.ReportFooter, HeightMm = 10 };
        rf.Elements.Add(new TextElement { Text = "Grand total: {SumAll(Sales.Qty * Sales.Price):N2}", Bold = true, X = 60, Y = 2, Width = 90, Height = 6, Align = HAlign.Right });
        r.Bands.Insert(1, gh); r.Bands.Insert(2, det); r.Bands.Insert(3, gf); r.Bands.Insert(4, rf);

        var engine = new ReportEngine(r).RegisterData("Sales", sales);
        var pages = engine.Build();
        Check(pages.Count >= 2, "report 1 paginates (" + pages.Count + " pages)");
        var text1 = string.Join("\n", pages.SelectMany(p => p.Items).Where(i => i.Kind == ItemKind.Text).Select(i => i.Text));
        Check(text1.Contains("Subtotal"), "group footers rendered");
        Check(text1.Contains("Grand total"), "report footer rendered");
        Check(text1.Contains("BIG"), "IIF formula evaluated");
        Check(!text1.Contains("{"), "no unresolved placeholders"); foreach (var l in text1.Split((char)10).Where(s => s.Contains("{")).Distinct().Take(5)) Console.WriteLine("   unresolved: " + l);
        engine.ExportPdf(Path.Combine(outDir, "report1.pdf"));
        engine.ExportExcel(Path.Combine(outDir, "report1.xlsx"));
        File.WriteAllBytes(Path.Combine(outDir, "report1.png"), engine.RenderPng(110)[0]);

        // ---- report 2: RTL table, Persian digits
        var r2 = ReportDefinition.CreateDefault();
        r2.Name = "گزارش فروش";
        r2.DefaultDirection = TextDirection.RightToLeft;
        r2.Digits = DigitStyle.Persian;
        ((TextElement)r2.Bands[0].Elements[0]).Text = "گزارش فروش — فرۆشتن — مبيعات";
        ((TextElement)r2.Bands[0].Elements[0]).Width = 170;
        var body = r2.Bands.First(b => b.Kind == BandKind.Body);
        var t = new TableElement { DataSource = "Sales", X = 0, Y = 0, Width = 190, Height = 18, ShowTotals = true, Direction = TextDirection.RightToLeft };
        t.Columns.Add(new TableColumn { Header = "شهر", Field = "City", WidthMm = 35 });
        t.Columns.Add(new TableColumn { Header = "مشتری", Field = "Customer", WidthMm = 55 });
        t.Columns.Add(new TableColumn { Header = "تعداد", Field = "Qty", WidthMm = 25, Align = HAlign.Left, Total = TotalKind.Sum });
        t.Columns.Add(new TableColumn { Header = "مبلغ", Field = "Qty * Price", WidthMm = 40, Align = HAlign.Left, Format = "N2", Total = TotalKind.Sum });
        body.Elements.Add(t);
        var eng2 = new ReportEngine(r2).RegisterData("Sales", sales);
        var pages2 = eng2.Build();
        var first = pages2[0].Items.First(i => i.Kind == ItemKind.Text && i.Text == "شهر");
        var last = pages2[0].Items.First(i => i.Kind == ItemKind.Text && i.Text == "مبلغ");
        Check(first.X > last.X, "RTL table: first column is rightmost");
        var digits = string.Join("", pages2.SelectMany(p => p.Items).Where(i => i.Kind == ItemKind.Text).Select(i => i.Text));
        Check(!digits.Any(ch => ch >= '0' && ch <= '9'), "Persian digit conversion leaves no Latin digits");
        eng2.ExportPdf(Path.Combine(outDir, "report2.pdf"));
        eng2.ExportExcel(Path.Combine(outDir, "report2.xlsx"));
        File.WriteAllBytes(Path.Combine(outDir, "report2.png"), eng2.RenderPng(110)[0]);

        // ---- formulas
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var cases = new[] {
            new[] { "{1+2*3}", "7" }, new[] { "{IIF(2>1,'yes','no')}", "yes" }, new[] { "{Upper('abc')}", "ABC" },
            new[] { "{Round(10/3,2)}", "3.33" }, new[] { "{'a' + 'b'}", "ab" }, new[] { "{Left('hello',2)}", "he" },
            new[] { "{Format(1234.5,'N1')}", "1,234.5" }, new[] { "{5 % 3}", "2" }, new[] { "{1 = 1 and 2 < 3}", "True" },
            new[] { "{Len('hello')}", "5" }, new[] { "{{x}} and {1+1}", "{x} and 2" }, new[] { "{-(2+3)}", "-5" },
            new[] { "{10/0}", "0" }, new[] { "{nothing_here}", "{nothing_here}" }, new[] { "{1234.5:N2}", "1,234.50" },
            new[] { "{Date:yyyy}", DateTime.Today.Year.ToString() }, new[] { "{Mid('abcdef',2,3)}", "bcd" }, new[] { "{IsNull(null,'d')}", "d" },
            new[] { "{Contains('Hello','ell')}", "True" }, new[] { "{Replace('a-b','-','+')}", "a+b" },
            new[] { "{[Sales].[Qty] * 2 > 0}", "True" },
        };
        var rf2 = new ReportDefinition { Name = "formulas" };
        var fb = new Band { Kind = BandKind.Body, HeightMm = 200 };
        for (int i = 0; i < cases.Length; i++) fb.Elements.Add(new TextElement { Text = cases[i][0], X = 0, Y = i * 8, Width = 150, Height = 7 });
        rf2.Bands.Add(fb);
        var fp = new ReportEngine(rf2).RegisterData("Sales", sales).Build();
        var got = fp.SelectMany(pg => pg.Items).Where(it => it.Kind == ItemKind.Text).Select(it => it.Text).ToList();
        for (int i = 0; i < cases.Length; i++) Check(i < got.Count && got[i] == cases[i][1], "formula " + cases[i][0] + " => " + (i < got.Count ? got[i] : "(missing)") + " (expected " + cases[i][1] + ")");

        // ---- auto-grow text pushes the band taller
        var rg = new ReportDefinition { Name = "grow" };
        var gb = new Band { Kind = BandKind.Detail, DataSource = "Sales", HeightMm = 6 };
        gb.Elements.Add(new TextElement { Text = "{Customer} long long long long long long long long long long long long long long long long text", Width = 40, Height = 6, CanGrow = true });
        rg.Bands.Add(gb);
        var gp = new ReportEngine(rg).RegisterData("Sales", sales.AsEnumerable().Take(3).CopyToDataTable()).Build();
        var ys = gp[0].Items.Where(it => it.Kind == ItemKind.Text && !it.Cont).Select(it => it.RowY).ToList();
        Check(ys.Count == 3 && ys[1] - ys[0] > 12, "CanGrow makes rows taller (row pitch " + (ys.Count == 3 ? (ys[1] - ys[0]).ToString("0.0") : "?") + " mm)");

        // ---- shaping sample at large size
        var r3 = new ReportDefinition { Name = "shaping" };
        r3.Page.Paper = PaperPreset.Custom; r3.Page.WidthMm = 150; r3.Page.HeightMm = 110;
        var b3 = new Band { Kind = BandKind.Body, HeightMm = 100 };
        string[] samples = {
            "کوردی: ڕێبوار ئاکۆ محەمەد هەولێر",
            "فارسی: علی رضایی — تهران ۱۲۳",
            "عربي: القاهرة محمد علي ١٢٣",
            "Mixed: Order #12 (علی) total 45.50",
            "RTL par: سلام (Hello) 2026-09-26",
        };
        for (int i = 0; i < samples.Length; i++)
            b3.Elements.Add(new TextElement { Text = samples[i], FontSize = 16, X = 0, Y = i * 12, Width = 130, Height = 10, FontName = "Arial" });
        r3.Bands.Add(b3);
        File.WriteAllBytes(Path.Combine(outDir, "shaping.png"), new ReportEngine(r3).RenderPng(150)[0]);

        // ---- json round trip + custom page size
        r2.Page.Paper = PaperPreset.Custom; r2.Page.WidthMm = 100; r2.Page.HeightMm = 150;
        var back = ReportDefinition.FromJson(r2.ToJson());
        Check(back.Page.WidthMm == 100 && back.Bands.Any(b => b.Elements.OfType<TableElement>().Any()), "JSON round trip keeps custom page size and table");
        Check(back.Digits == DigitStyle.Persian && back.DefaultDirection == TextDirection.RightToLeft, "JSON keeps language settings");

        // ---- formulas
        var ctx = new PrintHelperTestsAccess().Ctx();
        Console.WriteLine(failures == 0 ? "\nALL PASSED" : "\n" + failures + " FAILED");
        return failures;
    }
}

class PrintHelperTestsAccess { public object Ctx() { return null; } }
