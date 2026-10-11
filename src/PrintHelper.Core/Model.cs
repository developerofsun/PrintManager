using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace PrintHelper
{
    public enum BandKind { PageHeader, ReportHeader, Body, Detail, ReportFooter, PageFooter, GroupHeader, GroupFooter }
    public enum ElementKind { Text, Image, Line, Rectangle, Table }
    /// <summary>Auto = start side of the text direction (left for LTR, right for RTL).</summary>
    public enum HAlign { Auto, Left, Center, Right }
    public enum VAlign { Top, Middle, Bottom }
    public enum ImageFit { Stretch, Fit }
    public enum TotalKind { None, Sum, Count, Avg, Min, Max }
    public enum PaperPreset { A3, A4, A5, Letter, Legal, Receipt80mm, Custom }
    /// <summary>Auto = detected from the text's first strong letter (or the report default).</summary>
    public enum TextDirection { Auto, LeftToRight, RightToLeft }
    /// <summary>Converts digits in all output text: e.g. Persian ۱۲۳, Arabic-Indic (Kurdish/Arabic) ١٢٣ or Latin 123.</summary>
    public enum DigitStyle { AsIs, Latin, ArabicIndic, Persian }

    public class ReportException : Exception
    {
        public ReportException(string message) : base(message) { }
        public ReportException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Colors are stored in the .prt file as "#RRGGBB" / names, but edited with the normal color picker.</summary>
    public class HtmlColorConverter : JsonConverter<Color>
    {
        public override void WriteJson(JsonWriter writer, Color value, JsonSerializer serializer)
        {
            writer.WriteValue(value.IsEmpty || value.A == 0 ? "Transparent" : ColorTranslator.ToHtml(value));
        }

        public override Color ReadJson(JsonReader reader, Type objectType, Color existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var s = reader.Value as string;
            return string.IsNullOrEmpty(s) ? Color.Transparent : ColorTranslator.FromHtml(s);
        }
    }

    /// <summary>Drop-down of installed and loaded fonts, while still allowing free text.</summary>
    public class FontNameConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            return new StandardValuesCollection(FontManager.FamilyNames().ToList());
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter)), JsonObject]
    public class PageSetup
    {
        static readonly Dictionary<PaperPreset, double[]> Presets = new Dictionary<PaperPreset, double[]>
        {
            { PaperPreset.A3, new[] { 297.0, 420.0 } },
            { PaperPreset.A4, new[] { 210.0, 297.0 } },
            { PaperPreset.A5, new[] { 148.0, 210.0 } },
            { PaperPreset.Letter, new[] { 215.9, 279.4 } },
            { PaperPreset.Legal, new[] { 215.9, 355.6 } },
            { PaperPreset.Receipt80mm, new[] { 80.0, 200.0 } },
        };

        PaperPreset _paper = PaperPreset.A4;
        double _w = 210, _h = 297;

        [Description("Pick a standard size, or Custom and type WidthMm/HeightMm.")]
        public PaperPreset Paper
        {
            get { return _paper; }
            set
            {
                _paper = value;
                double[] d;
                if (Presets.TryGetValue(value, out d)) { _w = d[0]; _h = d[1]; }
            }
        }

        [Description("Paper width in millimetres (portrait). Any size is allowed.")]
        public double WidthMm { get { return _w; } set { _w = value; SyncPaper(); } }

        [Description("Paper height in millimetres (portrait). Any size is allowed.")]
        public double HeightMm { get { return _h; } set { _h = value; SyncPaper(); } }

        public bool Landscape { get; set; }
        public double MarginLeft { get; set; } = 10;
        public double MarginTop { get; set; } = 10;
        public double MarginRight { get; set; } = 10;
        public double MarginBottom { get; set; } = 10;

        [JsonIgnore, Browsable(false)]
        public double EffectiveWidth { get { return Landscape ? _h : _w; } }
        [JsonIgnore, Browsable(false)]
        public double EffectiveHeight { get { return Landscape ? _w : _h; } }
        [JsonIgnore, Browsable(false)]
        public double ContentWidth { get { return EffectiveWidth - MarginLeft - MarginRight; } }

        void SyncPaper()
        {
            double[] d;
            if (_paper != PaperPreset.Custom && Presets.TryGetValue(_paper, out d) &&
                (Math.Abs(d[0] - _w) > 0.01 || Math.Abs(d[1] - _h) > 0.01))
                _paper = PaperPreset.Custom;
        }

        public override string ToString() { return _paper + (Landscape ? " landscape" : " portrait"); }
    }

    // ------------------------------------------------------------------ elements

    public abstract class ReportElement
    {
        [Category("Design")] public string Name { get; set; } = "";
        [Category("Layout"), Description("Millimetres from the band's left edge.")] public double X { get; set; }
        [Category("Layout"), Description("Millimetres from the band's top edge.")] public double Y { get; set; }
        [Category("Layout")] public double Width { get; set; } = 30;
        [Category("Layout")] public double Height { get; set; } = 6;
        [Category("Design"), Description("Optional condition, e.g. Balance > 0. The element is skipped when it is false.")]
        public string VisibleWhen { get; set; } = "";
        [Category("Design")] public bool Visible { get; set; } = true;

        [Category("Design"), ReadOnly(true)]
        public abstract ElementKind Kind { get; }

        public override string ToString() { return string.IsNullOrEmpty(Name) ? Kind.ToString() : Name; }
    }

    public class TextElement : ReportElement
    {
        public override ElementKind Kind { get { return ElementKind.Text; } }

        [Category("Content"), Description("Text with {Field}, {Ds.Field}, {Price * Qty}, {IIF(Balance > 0, 'Due', 'Paid')}, {Sum(Ds.Field):N2}, {Page}, {Pages}, {Date:yyyy-MM-dd}. Use {{ for a literal brace.")]
        public string Text { get; set; } = "Text";

        [Category("Font"), Description("Any installed font, or one loaded with FontManager / embedded in the report."), TypeConverter(typeof(FontNameConverter))]
        public string FontName { get; set; } = "Arial";
        [Category("Font")] public float FontSize { get; set; } = 10;
        [Category("Font")] public bool Bold { get; set; }
        [Category("Font")] public bool Italic { get; set; }
        [Category("Font")] public bool Underline { get; set; }

        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color ForeColor { get; set; } = Color.Black;
        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color BackColor { get; set; } = Color.Transparent;
        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color BorderColor { get; set; } = Color.Black;
        [Category("Appearance"), Description("Border thickness in points. 0 = no border.")] public double BorderWidth { get; set; }

        [Category("Layout")] public HAlign Align { get; set; } = HAlign.Auto;
        [Category("Layout")] public VAlign VAlign { get; set; } = VAlign.Top;
        [Category("Layout")] public bool WordWrap { get; set; } = true;
        [Category("Layout"), Description("The element grows downward to fit wrapped text; the band grows with it.")]
        public bool CanGrow { get; set; }
        [Category("Layout"), Description("Reading direction. Auto detects Persian/Kurdish/Arabic/Hebrew text.")]
        public TextDirection Direction { get; set; } = TextDirection.Auto;

        [JsonIgnore, Browsable(false)]
        public FontStyle Style
        {
            get
            {
                var s = FontStyle.Regular;
                if (Bold) s |= FontStyle.Bold;
                if (Italic) s |= FontStyle.Italic;
                if (Underline) s |= FontStyle.Underline;
                return s;
            }
        }
    }

    public class ImageElement : ReportElement
    {
        public override ElementKind Kind { get { return ElementKind.Image; } }

        [Category("Content"), Description("Base64 image data embedded in the report.")]
        public string ImageBase64 { get; set; } = "";

        [Category("Content"), Description("Optional: a data field holding image bytes or a file path, e.g. Products.Photo.")]
        public string ImageField { get; set; } = "";

        [Category("Layout")] public ImageFit Fit { get; set; } = ImageFit.Fit;

        public void LoadFromFile(string path) { ImageBase64 = Convert.ToBase64String(File.ReadAllBytes(path)); }
    }

    public class LineElement : ReportElement
    {
        public override ElementKind Kind { get { return ElementKind.Line; } }
        public LineElement() { Height = 0; Width = 50; }

        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color Color { get; set; } = Color.Black;
        [Category("Appearance"), Description("Points")] public double Thickness { get; set; } = 1;
    }

    public class RectangleElement : ReportElement
    {
        public override ElementKind Kind { get { return ElementKind.Rectangle; } }
        public RectangleElement() { Width = 40; Height = 15; }

        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color BorderColor { get; set; } = Color.Black;
        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color FillColor { get; set; } = Color.Transparent;
        [Category("Appearance")] public double BorderWidth { get; set; } = 1;
    }

    public class TableColumn
    {
        public string Header { get; set; } = "Column";
        [Description("A field name, or a formula such as Price * Qty.")] public string Field { get; set; } = "";
        public double WidthMm { get; set; } = 30;
        public HAlign Align { get; set; } = HAlign.Auto;
        [Description("Format string, e.g. N2, C, yyyy-MM-dd.")] public string Format { get; set; } = "";
        [Description("Adds a totals row cell for this column when the table's ShowTotals is on.")] public TotalKind Total { get; set; } = TotalKind.None;
        public override string ToString() { return string.IsNullOrEmpty(Header) ? Field : Header; }
    }

    /// <summary>A data grid that repeats one row per record, breaks across pages and repeats its header.</summary>
    public class TableElement : ReportElement
    {
        public override ElementKind Kind { get { return ElementKind.Table; } }
        public TableElement() { Width = 0; Height = 20; }

        [Category("Content"), Description("Name of the data source (SQL or host-supplied list).")]
        public string DataSource { get; set; } = "";

        [Category("Content")] public List<TableColumn> Columns { get; set; } = new List<TableColumn>();
        [Category("Content")] public bool ShowHeader { get; set; } = true;
        [Category("Content")] public bool ShowTotals { get; set; }
        [Category("Content")] public string TotalLabel { get; set; } = "Total";
        [Category("Layout"), Description("RightToLeft puts the first column on the right (Persian/Kurdish/Arabic tables).")]
        public TextDirection Direction { get; set; } = TextDirection.Auto;

        [Category("Font"), TypeConverter(typeof(FontNameConverter))] public string FontName { get; set; } = "Arial";
        [Category("Font")] public float FontSize { get; set; } = 9;

        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color HeaderBackColor { get; set; } = Color.FromArgb(225, 228, 235);
        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color HeaderForeColor { get; set; } = Color.Black;
        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color ForeColor { get; set; } = Color.Black;
        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color ZebraColor { get; set; } = Color.Transparent;
        [Category("Appearance"), JsonConverter(typeof(HtmlColorConverter))] public Color BorderColor { get; set; } = Color.Gray;
        [Category("Appearance"), Description("Points")] public double BorderWidth { get; set; } = 0.5;
        [Category("Layout"), Description("Minimum row height in mm. Rows grow when text wraps.")] public double RowHeightMm { get; set; } = 6;
    }

    // ------------------------------------------------------------------ bands & report

    public class Band
    {
        public string Name { get; set; } = "Band";
        public BandKind Kind { get; set; } = BandKind.Body;
        [Description("Height in mm.")] public double HeightMm { get; set; } = 20;
        [Description("Detail and Group bands repeat over the rows of this data source.")] public string DataSource { get; set; } = "";
        [Description("GroupHeader/GroupFooter: field or formula to group by, e.g. City. Order of group bands sets nesting.")] public string GroupBy { get; set; } = "";
        [Description("GroupHeader: start every group on a new page.")] public bool NewPageBefore { get; set; }
        [Description("Detail: extra sort order inside groups, e.g. Name or Balance DESC.")] public string SortBy { get; set; } = "";
        [Browsable(false)] public List<ReportElement> Elements { get; set; } = new List<ReportElement>();
        public override string ToString() { return Name; }
    }

    class ElementConverter : JsonConverter
    {
        public override bool CanWrite { get { return false; } }
        public override bool CanConvert(Type t) { return t == typeof(ReportElement); }
        public override void WriteJson(JsonWriter w, object v, JsonSerializer s) { throw new NotSupportedException(); }

        public override object ReadJson(JsonReader reader, Type t, object existing, JsonSerializer serializer)
        {
            var jo = JObject.Load(reader);
            var kindText = (string)jo["Kind"] ?? "Text";
            ElementKind kind;
            if (!Enum.TryParse(kindText, true, out kind)) throw new ReportException("Unknown element kind '" + kindText + "'.");
            ReportElement el;
            switch (kind)
            {
                case ElementKind.Image: el = new ImageElement(); break;
                case ElementKind.Line: el = new LineElement(); break;
                case ElementKind.Rectangle: el = new RectangleElement(); break;
                case ElementKind.Table: el = new TableElement(); break;
                default: el = new TextElement(); break;
            }
            serializer.Populate(jo.CreateReader(), el);
            return el;
        }
    }

    public class EmbeddedFont
    {
        public string FileName { get; set; } = "";
        public string Base64 { get; set; } = "";
    }

    public class ReportDefinition
    {
        public string Name { get; set; } = "Report";
        public PageSetup Page { get; set; } = new PageSetup();
        [Category("Language"), Description("Default reading direction for the whole report (Auto detects per text).")]
        public TextDirection DefaultDirection { get; set; } = TextDirection.Auto;
        [Category("Language"), Description("Convert all digits to Persian, Arabic-Indic (Kurdish/Arabic) or Latin.")]
        public DigitStyle Digits { get; set; } = DigitStyle.AsIs;
        [Browsable(false)] public List<DataSourceDef> DataSources { get; set; } = new List<DataSourceDef>();
        [Browsable(false)] public List<EmbeddedFont> EmbeddedFonts { get; set; } = new List<EmbeddedFont>();
        [Browsable(false)] public List<Band> Bands { get; set; } = new List<Band>();

        static JsonSerializerSettings Settings()
        {
            var s = new JsonSerializerSettings { Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore };
            s.Converters.Add(new StringEnumConverter());
            s.Converters.Add(new ElementConverter());
            return s;
        }

        public string ToJson() { return JsonConvert.SerializeObject(this, Settings()); }

        public static ReportDefinition FromJson(string json)
        {
            var r = JsonConvert.DeserializeObject<ReportDefinition>(json, Settings());
            if (r == null) throw new ReportException("Empty report file.");
            return r;
        }

        public static ReportDefinition Load(string path) { return FromJson(File.ReadAllText(path)); }
        public void Save(string path) { File.WriteAllText(path, ToJson()); }

        /// <summary>Replaces this report's content with another's, keeping this object's identity (used by undo/redo).</summary>
        public void CopyFrom(ReportDefinition o)
        {
            Name = o.Name; Page = o.Page; DefaultDirection = o.DefaultDirection; Digits = o.Digits;
            DataSources = o.DataSources; EmbeddedFonts = o.EmbeddedFonts; Bands = o.Bands;
        }

        public ReportElement CloneElement(ReportElement e)
        {
            var json = JsonConvert.SerializeObject(new List<ReportElement> { e }, Settings());
            return JsonConvert.DeserializeObject<List<ReportElement>>(json, Settings())[0];
        }

        /// <summary>A minimal starting report: title header, empty body, page-number footer.</summary>
        public static ReportDefinition CreateDefault()
        {
            var r = new ReportDefinition();
            var head = new Band { Name = "PageHeader", Kind = BandKind.PageHeader, HeightMm = 16 };
            head.Elements.Add(new TextElement { Name = "Title", Text = "Report title", FontSize = 16, Bold = true, X = 0, Y = 3, Width = 120, Height = 9 });
            head.Elements.Add(new LineElement { Name = "Rule", X = 0, Y = 14, Width = r.Page.ContentWidth });
            var body = new Band { Name = "Body", Kind = BandKind.Body, HeightMm = 60 };
            var foot = new Band { Name = "PageFooter", Kind = BandKind.PageFooter, HeightMm = 8 };
            foot.Elements.Add(new TextElement { Name = "Date", Text = "{Date:yyyy-MM-dd}", FontSize = 8, X = 0, Y = 2, Width = 40, Height = 5 });
            foot.Elements.Add(new TextElement { Name = "PageNo", Text = "Page {Page} of {Pages}", FontSize = 8, Align = HAlign.Right, X = r.Page.ContentWidth - 40, Y = 2, Width = 40, Height = 5 });
            r.Bands.Add(head); r.Bands.Add(body); r.Bands.Add(foot);
            return r;
        }
    }
}
