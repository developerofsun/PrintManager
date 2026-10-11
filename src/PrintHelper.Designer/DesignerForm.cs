using System;
using System.Windows.Forms;

namespace PrintHelper.Designer
{
    /// <summary>
    /// Host apps embed the designer with:  ReportDesigner.Edit(engine)  or  new DesignerForm(engine).ShowDialog()
    /// </summary>
    public static class ReportDesigner
    {
        /// <summary>Opens the designer modally. Pass an engine that already has your data registered to see its fields.</summary>
        public static void Edit(ReportEngine engine, IWin32Window owner = null)
        {
            using (var f = new DesignerForm(engine)) f.ShowDialog(owner);
        }

        public static void Preview(ReportEngine engine, IWin32Window owner = null)
        {
            using (var f = new PreviewForm(engine)) f.ShowDialog(owner);
        }
    }

    /// <summary>A window around <see cref="DesignerControl"/>.</summary>
    public sealed class DesignerForm : Form
    {
        readonly DesignerControl _designer;

        public DesignerControl Designer { get { return _designer; } }

        public DesignerForm(ReportEngine engine, string filePath = null)
        {
            Width = 1400; Height = 860;
            StartPosition = FormStartPosition.CenterScreen;
            _designer = new DesignerControl(engine, filePath) { Dock = DockStyle.Fill };
            Controls.Add(_designer);
            _designer.StateChanged += (s, e) => UpdateTitle();
            UpdateTitle();
        }

        void UpdateTitle()
        {
            Text = "PrintHelper Designer - " + (_designer.FilePath == null ? "(unsaved)" : System.IO.Path.GetFileName(_designer.FilePath)) + (_designer.IsDirty ? " *" : "");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!e.Cancel && !_designer.ConfirmDiscard(this)) e.Cancel = true;
        }
    }
}
