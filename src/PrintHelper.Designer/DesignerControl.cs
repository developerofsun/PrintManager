using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PrintHelper.Designer
{
    /// <summary>
    /// The complete designer as a reusable control: toolbar, field tree, design surface, property grid,
    /// undo/redo. Put it on a WinForms form, or host it in WPF with WindowsFormsHost (see PrintHelper.Wpf).
    /// </summary>
    public sealed class DesignerControl : UserControl
    {
        readonly ReportEngine _engine;
        readonly DesignerCanvas _canvas = new DesignerCanvas { Dock = DockStyle.Fill };
        readonly PropertyGrid _grid = new PropertyGrid { Dock = DockStyle.Right, Width = 300, HelpVisible = true };
        readonly TreeView _tree = new TreeView { Dock = DockStyle.Left, Width = 230, HideSelection = false };
        readonly Dictionary<string, IList<string>> _schemaCache = new Dictionary<string, IList<string>>(StringComparer.OrdinalIgnoreCase);
        readonly ToolStripLabel _status = new ToolStripLabel();
        readonly ToolStripButton _undoBtn = new ToolStripButton("Undo");
        readonly ToolStripButton _redoBtn = new ToolStripButton("Redo");

        // undo/redo: whole-report JSON snapshots (reports are small; this keeps every kind of edit undoable)
        readonly Stack<string> _undo = new Stack<string>();
        readonly Stack<string> _redo = new Stack<string>();
        string _current;
        const int MaxHistory = 200;

        string _path;
        bool _dirty;

        /// <summary>Raised when the file name or the unsaved-changes flag changes.</summary>
        public event EventHandler StateChanged;

        public ReportEngine Engine { get { return _engine; } }
        /// <summary>The design surface, for hosts that want to script it.</summary>
        public DesignerCanvas Canvas { get { return _canvas; } }
        public string FilePath { get { return _path; } }
        public bool IsDirty { get { return _dirty; } }

        public DesignerControl(ReportEngine engine, string filePath = null)
        {
            _engine = engine ?? new ReportEngine(ReportDefinition.CreateDefault());
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                _engine.Report = ReportDefinition.Load(filePath);
                _path = filePath;
            }

            var tb = BuildToolbar();
            Controls.Add(_canvas);
            Controls.Add(_grid);
            Controls.Add(_tree);
            Controls.Add(tb);

            _canvas.FieldLookup = ds => SafeFields(ds, false);
            _canvas.SelectionChanged += (s, e) => _grid.SelectedObject = _canvas.Selected;
            _canvas.Changing += (s, e) => _grid.Refresh();
            _canvas.Changed += (s, e) => { Commit(); _grid.Refresh(); };
            _grid.PropertyValueChanged += (s, e) => { _canvas.Relayout(); Commit(); };
            _tree.ItemDrag += (s, e) =>
            {
                var n = e.Item as TreeNode;
                var tag = n == null ? null : n.Tag as string;
                if (tag != null) DoDragDrop("PHF:" + tag, DragDropEffects.Copy);
            };

            LoadIntoUi();
        }

        // ------------------------------------------------------------ toolbar

        ToolStrip BuildToolbar()
        {
            var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), CanOverflow = true };
            Action<string, EventHandler> btn = (text, click) => { var b = new ToolStripButton(text); b.Click += click; tb.Items.Add(b); };
            Action sep = () => tb.Items.Add(new ToolStripSeparator());

            btn("New", (s, e) => { if (ConfirmDiscard(this)) { _engine.Report.CopyFrom(ReportDefinition.CreateDefault()); _path = null; LoadIntoUi(); } });
            btn("Open", (s, e) => Open());
            btn("Save", (s, e) => Save(false));
            btn("Save As", (s, e) => Save(true));
            sep();
            _undoBtn.Click += (s, e) => Undo();
            _redoBtn.Click += (s, e) => Redo();
            tb.Items.Add(_undoBtn); tb.Items.Add(_redoBtn);
            sep();
            btn("Text", (s, e) => _canvas.AddElement(ElementKind.Text));
            btn("Image", (s, e) => _canvas.AddElement(ElementKind.Image));
            btn("Line", (s, e) => _canvas.AddElement(ElementKind.Line));
            btn("Rectangle", (s, e) => _canvas.AddElement(ElementKind.Rectangle));
            btn("Table", (s, e) => _canvas.AddElement(ElementKind.Table));

            var bands = new ToolStripDropDownButton("Add band");
            foreach (BandKind k in Enum.GetValues(typeof(BandKind)))
            {
                var kind = k;
                bands.DropDownItems.Add(kind.ToString(), null, (s, e) => _canvas.AddBand(kind));
            }
            tb.Items.Add(bands);
            btn("Band ▲", (s, e) => _canvas.MoveBand(-1));
            btn("Band ▼", (s, e) => _canvas.MoveBand(1));
            btn("Duplicate", (s, e) => _canvas.Duplicate());
            btn("Delete", (s, e) => _canvas.DeleteSelected());
            sep();
            btn("Zoom -", (s, e) => _canvas.Zoom /= 1.25f);
            btn("Zoom +", (s, e) => _canvas.Zoom *= 1.25f);
            sep();
            btn("Data sources...", (s, e) =>
            {
                using (var d = new DataSourceDialog(_engine)) d.ShowDialog(this);
                _schemaCache.Clear(); BuildTree(true); Commit();
            });
            btn("Fonts...", (s, e) =>
            {
                using (var d = new FontsDialog(_engine.Report)) d.ShowDialog(this);
                _canvas.Relayout(); Commit();
            });
            btn("Refresh fields", (s, e) => { _schemaCache.Clear(); BuildTree(true); });
            sep();
            btn("Preview", (s, e) => ReportDesigner.Preview(_engine, this));
            btn("PDF", (s, e) => QuickExport("PDF|*.pdf", ".pdf", p => _engine.ExportPdf(p)));
            btn("Excel", (s, e) => QuickExport("Excel|*.xlsx", ".xlsx", p => _engine.ExportExcel(p)));
            btn("Print", (s, e) => DoPrint());
            sep();
            btn("Demo", (s, e) => LoadDemo(false));
            btn("Demo فارسی/کوردی", (s, e) => LoadDemo(true));
            tb.Items.Add(_status);
            return tb;
        }

        void LoadDemo(bool persian)
        {
            if (!ConfirmDiscard(this)) return;
            _engine.RegisterData("Customers", DemoData.Customers(persian));
            _engine.Report.CopyFrom(persian ? DemoData.PersianReport() : DemoData.Report());
            _path = null;
            LoadIntoUi();
        }

        // ------------------------------------------------------------ state + undo/redo

        void LoadIntoUi()
        {
            _canvas.Report = _engine.Report;
            _undo.Clear(); _redo.Clear();
            _current = _engine.Report.ToJson();
            _dirty = false;
            UpdateUndoButtons();
            RaiseState();
            BuildTree(false);
        }

        /// <summary>Records the current report as an undo step if it changed since the last commit.</summary>
        void Commit()
        {
            var json = _engine.Report.ToJson();
            if (json == _current) return;
            _undo.Push(_current);
            if (_undo.Count > MaxHistory) { var keep = _undo.Reverse().Skip(_undo.Count - MaxHistory).ToArray(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
            _redo.Clear();
            _current = json;
            _dirty = true;
            UpdateUndoButtons();
            RaiseState();
        }

        void Restore(string json)
        {
            var sel = _canvas.GetSelectionPath();
            _engine.Report.CopyFrom(ReportDefinition.FromJson(json));
            _current = json;
            _canvas.ReloadKeepingSelection(sel);
            _grid.SelectedObject = _canvas.Selected;
            _dirty = true;
            UpdateUndoButtons();
            RaiseState();
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Push(_current);
            Restore(_undo.Pop());
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Push(_current);
            Restore(_redo.Pop());
        }

        void UpdateUndoButtons()
        {
            _undoBtn.Enabled = _undo.Count > 0;
            _redoBtn.Enabled = _redo.Count > 0;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // only when the design surface / tree has focus, so text boxes keep their own Ctrl+Z
            if (_canvas.Focused || _tree.Focused)
            {
                if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
                if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
                if (keyData == (Keys.Control | Keys.S)) { Save(false); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void RaiseState() { var h = StateChanged; if (h != null) h(this, EventArgs.Empty); }

        /// <summary>Asks to save unsaved changes. Returns false if the user cancels.</summary>
        public bool ConfirmDiscard(IWin32Window owner)
        {
            if (!_dirty) return true;
            var r = MessageBox.Show(owner, "Save changes to the current report?", "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return Save(false);
            return true;
        }

        // ------------------------------------------------------------ files

        public void Open()
        {
            if (!ConfirmDiscard(this)) return;
            using (var dlg = new OpenFileDialog { Filter = "PrintHelper report|*.prt|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                OpenFile(dlg.FileName);
            }
        }

        public void OpenFile(string path)
        {
            try
            {
                _engine.Report.CopyFrom(ReportDefinition.Load(path));
                FontManager.LoadEmbedded(_engine.Report.EmbeddedFonts);
                _path = path;
                LoadIntoUi();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        public bool Save(bool saveAs)
        {
            if (saveAs || _path == null)
            {
                using (var dlg = new SaveFileDialog { Filter = "PrintHelper report|*.prt", DefaultExt = ".prt", FileName = _engine.Report.Name })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                    _path = dlg.FileName;
                }
            }
            try
            {
                _engine.Report.Save(_path);
                _dirty = false;
                RaiseState();
                return true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot save", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
        }

        // ------------------------------------------------------------ field tree

        IList<string> SafeFields(string ds, bool query)
        {
            IList<string> cached;
            if (_schemaCache.TryGetValue(ds, out cached)) return cached;
            var fields = _engine.GetFields(ds, query);
            if (fields.Count > 0) _schemaCache[ds] = fields;
            return fields;
        }

        void BuildTree(bool querySql)
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            var vars = _tree.Nodes.Add("Variables");
            foreach (var v in new[] { "Page", "Pages", "Date", "Now" }) vars.Nodes.Add(new TreeNode(v) { Tag = "|" + v });
            foreach (var k in _engine.Variables.Keys) vars.Nodes.Add(new TreeNode(k) { Tag = "|" + k });

            var names = _engine.Data.Names.Concat(_engine.Report.DataSources.Select(d => d.Name))
                               .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var name in names)
            {
                var node = new TreeNode(name) { Tag = name + "|" };
                try
                {
                    foreach (var f in SafeFields(name, querySql)) node.Nodes.Add(new TreeNode(f) { Tag = name + "|" + f });
                    if (node.Nodes.Count == 0) node.Nodes.Add(new TreeNode("(click 'Refresh fields')") { ForeColor = Color.Gray });
                }
                catch (Exception ex) { node.Nodes.Add(new TreeNode("(failed: " + ex.Message + ")") { ForeColor = Color.Firebrick }); }
                _tree.Nodes.Add(node);
            }
            _tree.ExpandAll();
            _tree.EndUpdate();
            _status.Text = "   Drag a field onto a band, or a data source onto a Body band to create a table.";
        }

        // ------------------------------------------------------------ export / print

        void QuickExport(string filter, string ext, Action<string> export)
        {
            using (var dlg = new SaveFileDialog { Filter = filter, DefaultExt = ext, FileName = _engine.Report.Name + ext })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    export(dlg.FileName);
                    _status.Text = "   Saved " + dlg.FileName;
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { Cursor = Cursors.Default; }
            }
        }

        void DoPrint()
        {
            try
            {
                using (var dlg = new PrintDialog { PrinterSettings = new PrinterSettings(), UseEXDialog = true })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _engine.Print(dlg.PrinterSettings.PrinterName);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Print failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
