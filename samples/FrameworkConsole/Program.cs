using System;
using System.Collections.Generic;
using System.IO;
using PrintHelper;

/// <summary>Runs on plain .NET Framework 4.8: build a report in code, bind a list, export PDF + Excel.</summary>
static class Program
{
    class Item { public string Name { get; set; } public int Qty { get; set; } public decimal Price { get; set; } }

    static int Main(string[] args)
    {
        var report = ReportDefinition.CreateDefault();
        report.Name = "Framework sample";
        ((TextElement)report.Bands[0].Elements[0]).Text = "Order list / لیست سفارش";
        var table = new TableElement { DataSource = "Items", Width = 190, ShowTotals = true };
        table.Columns.Add(new TableColumn { Header = "Name", Field = "Name", WidthMm = 90 });
        table.Columns.Add(new TableColumn { Header = "Qty", Field = "Qty", WidthMm = 30, Align = HAlign.Right, Total = TotalKind.Sum });
        table.Columns.Add(new TableColumn { Header = "Amount", Field = "Qty * Price", WidthMm = 40, Align = HAlign.Right, Format = "N2", Total = TotalKind.Sum });
        report.Bands[1].Elements.Add(table);

        var items = new List<Item> { new Item { Name = "Pen", Qty = 10, Price = 1.5m }, new Item { Name = "قلم", Qty = 3, Price = 4m }, new Item { Name = "Notebook", Qty = 5, Price = 3.25m } };
        var engine = new ReportEngine(report).RegisterData("Items", items);

        string dir = args.Length > 0 ? args[0] : ".";
        engine.ExportPdf(Path.Combine(dir, "framework.pdf"));
        engine.ExportExcel(Path.Combine(dir, "framework.xlsx"));
        Console.WriteLine("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Console.WriteLine("Wrote framework.pdf and framework.xlsx (" + new FileInfo(Path.Combine(dir, "framework.pdf")).Length + " bytes pdf)");
        return 0;
    }
}
