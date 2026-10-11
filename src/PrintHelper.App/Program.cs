using System;
using System.Windows.Forms;
using PrintHelper;
using PrintHelper.Designer;

static class Program
{
    /// <summary>Standalone designer. Optional argument: path of a .prt file to open.</summary>
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var engine = new ReportEngine(ReportDefinition.CreateDefault());
        Application.Run(new DesignerForm(engine, args.Length > 0 ? args[0] : null));
    }
}
