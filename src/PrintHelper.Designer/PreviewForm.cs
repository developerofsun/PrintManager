using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Windows.Forms;

namespace PrintHelper.Designer
{
    /// <summary>Shows the finished pages and offers PDF / Excel / Print.</summary>
    public sealed class PreviewForm : Form
    {
        readonly ReportEngine _engine;
        readonly FlowLayoutPanel _pagesPanel = new FlowLayoutPanel();
        readonly Label _info = new Label();
        readonly List<Bitmap> _bitmaps = new List<Bitmap>();
        float _zoom = 1f;
        IList<RenderPage> _pages;

        public PreviewForm(ReportEngine engine)
        {
            _engine = engine;
            Text = "Preview - " + engine.Report.Name;
            Width = 1000; Height = 800;
            StartPosition = FormStartPosition.CenterParent;

            _pagesPanel.Dock = DockStyle.Fill;
            _pagesPanel.AutoScroll = true;
            _pagesPanel.FlowDirection = FlowDirection.TopDown;
            _pagesPanel.WrapContents = false;
            _pagesPanel.BackColor = Color.FromArgb(110, 114, 122);

            var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            tb.Items.Add(Button("Refresh", (s, e) => Rebuild()));
            tb.Items.Add(new ToolStripSeparator());
            tb.Items.Add(Button("Zoom -", (s, e) => { _zoom = Math.Max(0.3f, _zoom / 1.25f); Render(); }));
            tb.Items.Add(Button("Zoom +", (s, e) => { _zoom = Math.Min(3f, _zoom * 1.25f); Render(); }));
            tb.Items.Add(new ToolStripSeparator());
            tb.Items.Add(Button("Export PDF", (s, e) => Export("PDF|*.pdf", ".pdf", p => _engine.ExportPdf(p))));
            tb.Items.Add(Button("Export Excel", (s, e) => Export("Excel|*.xlsx", ".xlsx", p => _engine.ExportExcel(p))));
            tb.Items.Add(Button("Print...", (s, e) => DoPrint()));
            _info.AutoSize = true;
            tb.Items.Add(new ToolStripControlHost(_info));

            Controls.Add(_pagesPanel);
            Controls.Add(tb);
            Shown += (s, e) => Rebuild();
            FormClosed += (s, e) => DisposeBitmaps();
        }

        static ToolStripButton Button(string text, EventHandler click)
        {
            var b = new ToolStripButton(text);
            b.Click += click;
            return b;
        }

        void Rebuild()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                _pages = _engine.Build();
                Render();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot build report", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Cursor = Cursors.Default; }
        }

        void DisposeBitmaps()
        {
            foreach (var b in _bitmaps) b.Dispose();
            _bitmaps.Clear();
        }

        void Render()
        {
            if (_pages == null) return;
            _pagesPanel.SuspendLayout();
            foreach (Control c in _pagesPanel.Controls) c.Dispose();
            _pagesPanel.Controls.Clear();
            DisposeBitmaps();
            foreach (var p in _pages)
            {
                Bitmap bmp;
                using (var ms = new System.IO.MemoryStream(SkiaRenderer.ToPng(p, 96f * _zoom))) bmp = new Bitmap(ms);
                _bitmaps.Add(bmp);
                _pagesPanel.Controls.Add(new PictureBox { Image = bmp, SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(20, 12, 20, 12) });
            }
            _pagesPanel.ResumeLayout();
            _info.Text = "   " + _pages.Count + " page(s), zoom " + (int)(_zoom * 100) + "%";
        }

        void Export(string filter, string ext, Action<string> export)
        {
            using (var dlg = new SaveFileDialog { Filter = filter, DefaultExt = ext, FileName = _engine.Report.Name + ext })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    export(dlg.FileName);
                    if (MessageBox.Show(this, "Saved. Open the file now?", "Export", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void DoPrint()
        {
            try
            {
                using (var dlg = new PrintDialog { PrinterSettings = new PrinterSettings(), UseEXDialog = true })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    _engine.Print(dlg.PrinterSettings.PrinterName);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Print failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
