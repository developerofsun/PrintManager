using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PrintHelper.Wpf
{
    /// <summary>
    /// A WPF preview control: shows the report's pages and offers Export PDF / Excel / Print.
    /// <code>
    /// var preview = new ReportPreviewControl { Engine = engine };   // put it in any WPF layout
    /// </code>
    /// </summary>
    public class ReportPreviewControl : UserControl
    {
        public static readonly DependencyProperty EngineProperty =
            DependencyProperty.Register("Engine", typeof(ReportEngine), typeof(ReportPreviewControl),
                new PropertyMetadata(null, (d, e) => ((ReportPreviewControl)d).Refresh()));

        readonly StackPanel _pages = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
        readonly TextBlock _info = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        double _zoom = 1.0;

        public ReportEngine Engine
        {
            get { return (ReportEngine)GetValue(EngineProperty); }
            set { SetValue(EngineProperty, value); }
        }

        public ReportPreviewControl()
        {
            var bar = new ToolBar();
            bar.Items.Add(Btn("Refresh", (s, e) => Refresh()));
            bar.Items.Add(new Separator());
            bar.Items.Add(Btn("Zoom -", (s, e) => { _zoom = Math.Max(0.3, _zoom / 1.25); Refresh(); }));
            bar.Items.Add(Btn("Zoom +", (s, e) => { _zoom = Math.Min(3, _zoom * 1.25); Refresh(); }));
            bar.Items.Add(new Separator());
            bar.Items.Add(Btn("Export PDF", (s, e) => Export("PDF|*.pdf", ".pdf", p => Engine.ExportPdf(p))));
            bar.Items.Add(Btn("Export Excel", (s, e) => Export("Excel|*.xlsx", ".xlsx", p => Engine.ExportExcel(p))));
            bar.Items.Add(Btn("Print...", (s, e) => DoPrint()));
            bar.Items.Add(_info);

            var scroll = new ScrollViewer
            {
                Content = _pages,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(110, 114, 122))
            };

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(scroll);
            Content = root;
        }

        static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2) };
            b.Click += click;
            return b;
        }

        /// <summary>Rebuilds the pages from the engine (re-runs queries).</summary>
        public void Refresh()
        {
            _pages.Children.Clear();
            var engine = Engine;
            if (engine == null) return;
            try
            {
                IList<byte[]> pngs = engine.RenderPng((float)(96 * _zoom));
                foreach (var png in pngs)
                {
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(png))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                    }
                    bmp.Freeze();
                    _pages.Children.Add(new Border
                    {
                        Margin = new Thickness(0, 6, 0, 6),
                        Background = Brushes.White,
                        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, Opacity = 0.4, ShadowDepth = 2 },
                        Child = new Image { Source = bmp, Width = bmp.PixelWidth * 96.0 / bmp.DpiX, Stretch = Stretch.None }
                    });
                }
                _info.Text = pngs.Count + " page(s), " + (int)(_zoom * 100) + "%";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Cannot build report", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void Export(string filter, string ext, Action<string> export)
        {
            if (Engine == null) return;
            var dlg = new SaveFileDialog { Filter = filter, DefaultExt = ext, FileName = Engine.Report.Name + ext };
            if (dlg.ShowDialog() != true) return;
            try { export(dlg.FileName); MessageBox.Show("Saved " + dlg.FileName, "Export"); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        void DoPrint()
        {
            if (Engine == null) return;
            var dlg = new PrintDialog();
            if (dlg.ShowDialog() != true) return;
            try { Engine.Print(dlg.PrintQueue.Name); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Print failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
