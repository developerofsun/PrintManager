using System;
using System.Windows;
using System.Windows.Forms.Integration;
using PrintHelper.Designer;

namespace PrintHelper.Wpf
{
    /// <summary>The full visual designer as a WPF element (WinForms designer inside a WindowsFormsHost).</summary>
    public class ReportDesignerHost : WindowsFormsHost
    {
        readonly DesignerControl _designer;

        public ReportDesignerHost(ReportEngine engine, string filePath = null)
        {
            _designer = new DesignerControl(engine, filePath);
            Child = _designer;
        }

        public DesignerControl Designer { get { return _designer; } }
        public ReportEngine Engine { get { return _designer.Engine; } }
        public bool IsDirty { get { return _designer.IsDirty; } }
        public event EventHandler StateChanged
        {
            add { _designer.StateChanged += value; }
            remove { _designer.StateChanged -= value; }
        }
    }

    /// <summary>One-liners for WPF apps.</summary>
    public static class ReportWindows
    {
        /// <summary>Opens the designer in its own window (modal when an owner is given, otherwise a normal window).</summary>
        public static void ShowDesigner(ReportEngine engine, Window owner = null, string filePath = null)
        {
            var host = new ReportDesignerHost(engine, filePath);
            var win = new Window { Title = "PrintHelper Designer", Width = 1400, Height = 850, Content = host, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            win.Closing += (s, e) =>
            {
                if (host.IsDirty && !host.Designer.ConfirmDiscard(null)) e.Cancel = true;
            };
            if (owner != null) { win.Owner = owner; win.ShowDialog(); } else win.Show();
        }

        /// <summary>Opens a preview window with PDF / Excel / Print buttons.</summary>
        public static void ShowPreview(ReportEngine engine, Window owner = null)
        {
            var win = new Window { Title = "Preview - " + engine.Report.Name, Width = 1000, Height = 800, Content = new ReportPreviewControl { Engine = engine }, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            if (owner != null) { win.Owner = owner; win.ShowDialog(); } else win.Show();
        }
    }
}
