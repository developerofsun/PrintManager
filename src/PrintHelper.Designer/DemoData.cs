using System;
using System.Data;
using System.Drawing;

namespace PrintHelper.Designer
{
    /// <summary>Sample reports + data so the designer can be tried without a database.</summary>
    public static class DemoData
    {
        public static DataTable Customers(bool persian = false)
        {
            var dt = new DataTable("Customers");
            dt.Columns.Add("Id", typeof(int));
            dt.Columns.Add("Name", typeof(string));
            dt.Columns.Add("City", typeof(string));
            dt.Columns.Add("Orders", typeof(int));
            dt.Columns.Add("Balance", typeof(decimal));
            var rnd = new Random(7);
            string[] cities = persian ? new[] { "هەولێر", "تهران", "القاهرة", "سلێمانی" } : new[] { "Dubai", "Cairo", "Istanbul", "Berlin", "Toronto", "Lahore" };
            string[] names = persian ? new[] { "ئاکۆ محەمەد", "علی رضایی", "محمد علي", "ڕێبوار کەریم", "سارا احمدی", "دانا عەبدوڵڵا" } : null;
            for (int i = 1; i <= 60; i++)
                dt.Rows.Add(i, persian ? names[rnd.Next(names.Length)] : "Customer " + i, cities[rnd.Next(cities.Length)], rnd.Next(1, 40), Math.Round((decimal)(rnd.NextDouble() * 5000), 2));
            return dt;
        }

        /// <summary>English report showing a table with totals and a data-bound summary.</summary>
        public static ReportDefinition Report()
        {
            var r = ReportDefinition.CreateDefault();
            r.Name = "Customer report";
            ((TextElement)r.Bands[0].Elements[0]).Text = "Customer report";
            var body = r.Bands[1];
            body.HeightMm = 24;
            body.Elements.Add(new TextElement { Name = "Summary", Text = "{Count(Customers)} customers, total balance {SumAll(Customers.Balance):N2}", FontSize = 10, X = 0, Y = 1, Width = 150, Height = 6 });
            var t = new TableElement { Name = "CustomerTable", DataSource = "Customers", X = 0, Y = 9, Width = r.Page.ContentWidth, Height = 18, ShowTotals = true, ZebraColor = Color.FromArgb(246, 248, 252) };
            t.Columns.Add(new TableColumn { Header = "#", Field = "Id", WidthMm = 14 });
            t.Columns.Add(new TableColumn { Header = "Name", Field = "Name", WidthMm = 60 });
            t.Columns.Add(new TableColumn { Header = "City", Field = "City", WidthMm = 40 });
            t.Columns.Add(new TableColumn { Header = "Orders", Field = "Orders", WidthMm = 25, Align = HAlign.Right, Total = TotalKind.Sum });
            t.Columns.Add(new TableColumn { Header = "Balance", Field = "Balance", WidthMm = 35, Align = HAlign.Right, Format = "N2", Total = TotalKind.Sum });
            body.Elements.Add(t);
            return r;
        }

        /// <summary>Persian/Kurdish/Arabic report: right-to-left page, grouped by city with subtotals, Persian digits.</summary>
        public static ReportDefinition PersianReport()
        {
            var r = ReportDefinition.CreateDefault();
            r.Name = "گزارش مشتریان";
            r.DefaultDirection = TextDirection.RightToLeft;
            r.Digits = DigitStyle.Persian;
            var title = (TextElement)r.Bands[0].Elements[0];
            title.Text = "گزارش مشتریان — ڕاپۆرتی کڕیاران";
            title.Width = r.Page.ContentWidth;
            r.Bands.RemoveAll(b => b.Kind == BandKind.Body);

            var gh = new Band { Name = "CityHeader", Kind = BandKind.GroupHeader, DataSource = "Customers", GroupBy = "City", HeightMm = 9 };
            gh.Elements.Add(new TextElement { Text = "{City}  ({Count()})", Bold = true, FontSize = 11, BackColor = Color.FromArgb(230, 236, 250), X = 0, Y = 1, Width = r.Page.ContentWidth, Height = 6 });
            var det = new Band { Name = "Detail", Kind = BandKind.Detail, DataSource = "Customers", HeightMm = 6, SortBy = "Name" };
            det.Elements.Add(new TextElement { Text = "{RowNo()}", X = 0, Y = 0, Width = 12, Height = 6 });
            det.Elements.Add(new TextElement { Text = "{Name}", X = 14, Y = 0, Width = 80, Height = 6 });
            det.Elements.Add(new TextElement { Text = "{Orders}", X = 100, Y = 0, Width = 25, Height = 6, Align = HAlign.Center });
            det.Elements.Add(new TextElement { Text = "{Balance:N2}", X = 130, Y = 0, Width = 40, Height = 6, Align = HAlign.Left });
            var gf = new Band { Name = "CityFooter", Kind = BandKind.GroupFooter, DataSource = "Customers", GroupBy = "City", HeightMm = 9 };
            gf.Elements.Add(new TextElement { Text = "جمع {City}: {Sum(Balance):N2}", Bold = true, X = 0, Y = 1, Width = r.Page.ContentWidth, Height = 6 });
            var rf = new Band { Name = "ReportFooter", Kind = BandKind.ReportFooter, HeightMm = 10 };
            rf.Elements.Add(new TextElement { Text = "جمع کل: {SumAll(Customers.Balance):N2}", Bold = true, FontSize = 11, X = 0, Y = 2, Width = r.Page.ContentWidth, Height = 6 });
            r.Bands.Insert(1, gh); r.Bands.Insert(2, det); r.Bands.Insert(3, gf); r.Bands.Insert(4, rf);
            return r;
        }
    }
}
