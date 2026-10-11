using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PrintHelper.Designer
{
    /// <summary>Load TTF/OTF fonts, and optionally embed them in the report so it looks the same on any machine.</summary>
    public sealed class FontsDialog : Form
    {
        readonly ReportDefinition _report;
        readonly ListBox _embedded = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        readonly ListBox _loaded = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        public bool Changed { get; private set; }

        public FontsDialog(ReportDefinition report)
        {
            _report = report;
            Text = "Fonts";
            Width = 640; Height = 460;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;

            var info = new Label
            {
                Dock = DockStyle.Top, Height = 56, Padding = new Padding(8),
                Text = "Embedded fonts travel inside the .prt file (recommended for Persian/Kurdish/Arabic fonts). " +
                       "Session fonts are only loaded for this run. Both are then available in every font list."
            };

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8) };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            split.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            split.Controls.Add(new Label { Text = "Embedded in this report", Dock = DockStyle.Fill }, 0, 0);
            split.Controls.Add(new Label { Text = "Loaded fonts (families)", Dock = DockStyle.Fill }, 1, 0);
            split.Controls.Add(_embedded, 0, 1);
            split.Controls.Add(_loaded, 1, 1);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
            var embed = new Button { Text = "Embed font file...", Width = 130 };
            var session = new Button { Text = "Load for session...", Width = 130 };
            var folder = new Button { Text = "Load folder...", Width = 110 };
            var remove = new Button { Text = "Remove embedded", Width = 130 };
            var close = new Button { Text = "Close", Width = 80, DialogResult = DialogResult.OK };
            bar.Controls.AddRange(new Control[] { embed, session, folder, remove, close });

            Controls.Add(split);
            Controls.Add(bar);
            Controls.Add(info);

            embed.Click += (s, e) => Add(true);
            session.Click += (s, e) => Add(false);
            folder.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog())
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        int n = FontManager.LoadFontDirectory(dlg.SelectedPath);
                        MessageBox.Show(this, n + " font(s) loaded.", "Fonts");
                        Refresh_();
                    }
            };
            remove.Click += (s, e) =>
            {
                int i = _embedded.SelectedIndex;
                if (i < 0) return;
                _report.EmbeddedFonts.RemoveAt(i);
                Changed = true;
                Refresh_();
            };
            Refresh_();
        }

        void Add(bool embed)
        {
            using (var dlg = new OpenFileDialog { Filter = "Fonts|*.ttf;*.otf|All files|*.*", Multiselect = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                foreach (var f in dlg.FileNames)
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(f);
                        if (embed)
                        {
                            var ef = new EmbeddedFont { FileName = Path.GetFileName(f), Base64 = Convert.ToBase64String(bytes) };
                            _report.EmbeddedFonts.Add(ef);
                            FontManager.LoadEmbedded(new[] { ef });
                            Changed = true;
                        }
                        else FontManager.LoadFontBytes(bytes);
                    }
                    catch (Exception ex) { MessageBox.Show(this, f + ": " + ex.Message, "Cannot load font"); }
                }
                Refresh_();
            }
        }

        void Refresh_()
        {
            _embedded.Items.Clear();
            foreach (var f in _report.EmbeddedFonts) _embedded.Items.Add(f.FileName + "  (" + (f.Base64.Length * 3 / 4 / 1024) + " KB)");
            _loaded.Items.Clear();
            foreach (var n in FontManager.LoadedFamilyNames()) _loaded.Items.Add(n);
        }
    }
}
