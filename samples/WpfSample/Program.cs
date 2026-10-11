using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PrintHelper;
using PrintHelper.Designer;
using PrintHelper.Wpf;

/// <summary>Code-only WPF host (no XAML needed): design, preview and export a report from a normal WPF app.</summary>
static class Program
{
    class Sale
    {
        public string City { get; set; }
        public string Customer { get; set; }
        public int Qty { get; set; }
        public decimal Price { get; set; }
    }

    [STAThread]
    static void Main()
    {
        // your own data: any List<T>, array or DataTable
        var sales = new List<Sale>();
        var rnd = new Random(1);
        string[] cities = { "هەولێر", "تهران", "London" };
        string[] names = { "ئاکۆ", "علی", "John" };
        for (int i = 0; i < 40; i++)
            sales.Add(new Sale { City = cities[rnd.Next(3)], Customer = names[rnd.Next(3)], Qty = rnd.Next(1, 9), Price = rnd.Next(10, 99) });

        var engine = new ReportEngine(DemoData.Report()).RegisterData("Customers", DemoData.Customers()).RegisterData("Sales", sales);

        var panel = new StackPanel { Margin = new Thickness(16) };
        Button Add(string text, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 6, 12, 6) };
            b.Click += (s, e) => click();
            panel.Children.Add(b);
            return b;
        }
        var window = new Window { Title = "PrintHelper - WPF sample", Width = 320, Height = 260, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        Add("Design report...", () => ReportWindows.ShowDesigner(engine, window));
        Add("Preview...", () => ReportWindows.ShowPreview(engine, window));
        Add("Export PDF (report.pdf)", () => { engine.ExportPdf("report.pdf"); MessageBox.Show("Saved report.pdf"); });

        new Application().Run(window);
    }
}
