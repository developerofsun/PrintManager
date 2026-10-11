using System;
using System.Linq;
using System.Windows.Forms;

namespace PrintHelper.Designer
{
    /// <summary>Add / edit / test the report's SQL data sources.</summary>
    public sealed class DataSourceDialog : Form
    {
        readonly ReportEngine _engine;
        readonly ListBox _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        readonly TextBox _name = new TextBox { Dock = DockStyle.Fill };
        readonly TextBox _provider = new TextBox { Dock = DockStyle.Fill, Text = "SqlServer" };
        readonly TextBox _conn = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 54, ScrollBars = ScrollBars.Vertical };
        readonly TextBox _query = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Both, Font = new System.Drawing.Font("Consolas", 10) };
        readonly TextBox _params = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, Height = 54, ScrollBars = ScrollBars.Vertical };
        DataSourceDef _current;
        bool _loading;

        public DataSourceDialog(ReportEngine engine)
        {
            _engine = engine;
            Text = "Data sources";
            Width = 860; Height = 640;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            var left = new Panel { Dock = DockStyle.Left, Width = 190 };
            var add = new Button { Text = "Add", Dock = DockStyle.Bottom };
            var remove = new Button { Text = "Remove", Dock = DockStyle.Bottom };
            left.Controls.Add(_list); left.Controls.Add(remove); left.Controls.Add(add);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Row(grid, 0, "Name", _name);
            Row(grid, 1, "Provider", _provider);
            Row(grid, 2, "Connection string", _conn);
            Row(grid, 3, "SQL query", _query);
            Row(grid, 4, "Parameters\r\n(Name=Value per line)", _params);

            var test = new Button { Text = "Test / load fields", Width = 140 };
            var close = new Button { Text = "Close", Width = 80, DialogResult = DialogResult.OK };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            bar.Controls.Add(test); bar.Controls.Add(close);
            grid.Controls.Add(bar, 1, 5);

            Controls.Add(grid);
            Controls.Add(left);

            foreach (var d in _engine.Report.DataSources) _list.Items.Add(d);
            _list.SelectedIndexChanged += (s, e) => { Commit(); Show((DataSourceDef)_list.SelectedItem); };
            add.Click += (s, e) =>
            {
                Commit();
                var d = new DataSourceDef { Name = "DataSource" + (_engine.Report.DataSources.Count + 1) };
                _engine.Report.DataSources.Add(d);
                _list.Items.Add(d);
                _list.SelectedItem = d;
            };
            remove.Click += (s, e) =>
            {
                if (_current == null) return;
                _engine.Report.DataSources.Remove(_current);
                var c = _current; _current = null;
                _list.Items.Remove(c);
                Show(null);
            };
            test.Click += (s, e) => Test();
            FormClosing += (s, e) => Commit();
            AcceptButton = null;

            if (_list.Items.Count > 0) _list.SelectedIndex = 0; else Show(null);
        }

        static void Row(TableLayoutPanel g, int row, string label, Control c)
        {
            g.Controls.Add(new Label { Text = label, AutoSize = false, Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, row);
            g.Controls.Add(c, 1, row);
        }

        void Show(DataSourceDef d)
        {
            _loading = true;
            _current = d;
            bool on = d != null;
            foreach (var c in new Control[] { _name, _provider, _conn, _query, _params }) c.Enabled = on;
            _name.Text = on ? d.Name : "";
            _provider.Text = on ? d.Provider : "";
            _conn.Text = on ? d.ConnectionString : "";
            _query.Text = on ? d.Query : "";
            _params.Text = on ? string.Join("\r\n", d.Parameters.Select(p => p.Name + "=" + p.Value)) : "";
            _loading = false;
        }

        void Commit()
        {
            if (_loading || _current == null) return;
            _current.Name = _name.Text.Trim();
            _current.Provider = _provider.Text.Trim();
            _current.ConnectionString = _conn.Text;
            _current.Query = _query.Text;
            _current.Parameters = _params.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l =>
                {
                    int i = l.IndexOf('=');
                    return i < 0 ? new ParamDef { Name = l.Trim() } : new ParamDef { Name = l.Substring(0, i).Trim(), Value = l.Substring(i + 1) };
                }).ToList();
            int idx = _list.Items.IndexOf(_current);
            if (idx >= 0) { _loading = true; _list.Items[idx] = _current; _loading = false; }
        }

        void Test()
        {
            Commit();
            if (_current == null) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                var schema = _current.GetSchema(_engine.Variables);
                var cols = string.Join(", ", schema.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
                MessageBox.Show(this, "Connected. " + schema.Columns.Count + " column(s):\r\n" + cols, "Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Test failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Cursor = Cursors.Default; }
        }
    }
}
