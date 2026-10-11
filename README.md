# PrintHelper

A small, simple report designer and engine for .NET. Design reports visually, bind them to SQL Server or to lists from your own app, and export to **PDF**, **Excel** or **print**. Built to be dropped into **WPF, WinForms, .NET Framework, .NET 8 and server apps**.

- Visual designer: text, images, lines, rectangles, tables, headers, footers, groups, any page size
- **Persian, Kurdish (Sorani), Arabic, Hebrew and English** with correct letter joining, mixed-direction text and RTL tables
- Load your own fonts, or embed them in the report file
- Formulas and grouping with subtotals
- Undo / redo, drag elements between bands

> License: not chosen yet. Add a `LICENSE` file before publishing.

## Projects

| Project | What it is | Targets |
|---|---|---|
| `PrintHelper.Core` | Engine: model, data, layout, formulas, PDF, Excel, PNG | netstandard2.0 (works on .NET Framework 4.6.1+, .NET Core, .NET 5+) |
| `PrintHelper.Windows` | `engine.Print()` for Windows printers | net462, net8.0-windows |
| `PrintHelper.Designer` | WinForms designer + preview (`DesignerControl`) | net48, net8.0-windows |
| `PrintHelper.Wpf` | WPF preview control + designer host | net48, net8.0-windows |
| `PrintHelper.App` | Standalone designer exe | net48, net8.0-windows |
| `samples/` | `WpfSample` (code-only WPF app), `FrameworkConsole` (.NET 4.8, engine only) | |
| `tests/` | `SmokeTest` (engine), `DesignerTest` (undo/redo, drag, fonts) | |

## Build and run

Needs the .NET 8 SDK (it builds the .NET Framework targets too).

```
dotnet build PrintHelper.sln -c Release
dotnet run --project src/PrintHelper.App -f net8.0-windows
```

In the designer click **Demo** (English) or **Demo فارسی/کوردی** to see a full report at once.

## Use it from your app

```csharp
using PrintHelper;

FontManager.LoadFontFile("Vazirmatn-Regular.ttf");            // optional: your own fonts

var report = ReportDefinition.Load("invoice.prt");
var engine = new ReportEngine(report)
    .RegisterData("Customers", customerList)                  // List<T>, array, DataTable, DataView...
    .SetVariable("Title", "March statement");

engine.ExportPdf("out.pdf");
engine.ExportExcel("out.xlsx");
engine.Print();                                               // needs PrintHelper.Windows
byte[] pdf = engine.ExportPdfBytes();                         // e.g. return from a web API
IList<byte[]> pngs = engine.RenderPng(96);                    // page images for any UI
```

### WPF

```csharp
using PrintHelper.Wpf;

ReportWindows.ShowDesigner(engine, this);   // full visual designer (WinForms designer inside WindowsFormsHost)
ReportWindows.ShowPreview(engine, this);    // preview with PDF / Excel / Print buttons

// or place controls in your own layout:
var preview  = new ReportPreviewControl { Engine = engine };
var designer = new ReportDesignerHost(engine);
```

### WinForms

```csharp
using PrintHelper.Designer;

ReportDesigner.Edit(engine);       // modal designer
ReportDesigner.Preview(engine);    // preview window
// or drop a DesignerControl on your own form
```

### .NET Framework

Reference `PrintHelper.Core` (and the others as needed) from a .NET Framework 4.6.2+ project. `samples/FrameworkConsole` runs on 4.8. Build with `Prefer32Bit` off (AnyCPU) so the native Skia library resolves.

## Designing

- **Bands** stack vertically:
  `PageHeader`, `ReportHeader`, `Body`, `GroupHeader`, `Detail`, `GroupFooter`, `ReportFooter`, `PageFooter`. Use **Band ▲ / ▼** to reorder within a section.
  - PageHeader/PageFooter repeat on every page.
  - A **Body** band holding a **Table** flows across pages and repeats the table header.
  - A **Detail** band repeats once per row of its `DataSource`.
- **Elements**: drag to move (also into other bands), drag handles to resize, arrows nudge (Shift = 5 mm), Ctrl+D duplicate, Delete removes, double-click text to edit. Ctrl+Z / Ctrl+Y undo and redo.
- **Any page size**: click the empty page area and set `Page.Paper`, or choose Custom and set width/height in mm; margins and landscape are there too.
- **Data sources**: toolbar > Data sources. Give a name, a SQL query and `@Param` values. Drag fields from the left tree onto a band, or drop a data source onto a Body band to build a table.

### Text placeholders and formulas

Anything in `{ }` is an expression. `{{` gives a literal brace. Unknown text stays visible so mistakes are easy to spot.

| Example | Meaning |
|---|---|
| `{Name}` / `{Customers.Name}` / `{[First Name]}` | a field (bare names use the band's data source) |
| `{Qty * Price:N2}` | arithmetic with an optional format |
| `{IIF(Balance > 0, 'Due', 'Paid')}` | conditions (`IIF`, `IsNull`, `and`/`or`/`not`, `== != < > <= >=`) |
| `{Upper(Name)}` `{Left(Name, 3)}` `{Mid(..)}` `{Replace(..)}` `{Contains(..)}` `{Len(..)}` `{Trim(..)}` | text |
| `{Round(x, 2)}` `{Abs(x)}` `{Floor(x)}` `{Ceil(x)}` | numbers |
| `{Format(Date, 'yyyy-MM-dd')}` `{Year(d)}` `{Month(d)}` `{Day(d)}` | dates |
| `{Sum(Qty * Price)}` `{Count()}` `{Avg(x)}` `{Min(x)}` `{Max(x)}` | aggregates: inside group bands they cover the group, elsewhere the whole source |
| `{SumAll(Sales.Qty)}` `{CountAll(..)}` … | always the whole data source (grand totals) |
| `{Page}` `{Pages}` `{Date}` `{Now}` `{RowNo()}` `{MyVariable}` | page info, row number, your own variables |

`VisibleWhen` on any element takes a condition such as `Balance > 0`. Table columns accept a field name **or a formula** (`Qty * Price`) and can show totals.

### Grouping

Add a `GroupHeader` and/or `GroupFooter` band for the same data source with the same `GroupBy` (a field or formula, e.g. `City`). Add more group bands for nesting (outermost first). Rows are sorted by the group fields automatically; `Detail.SortBy` adds a sort inside groups (`Name`, `Balance DESC`). `GroupHeader.NewPageBefore` starts each group on a new page.

### Persian, Kurdish, Arabic

- Text direction is detected per text (`Direction = Auto`), or forced per element/table, or set for the whole report with `DefaultDirection`.
- `Align = Auto` means "start side": right for RTL text, left for LTR.
- Tables can run right-to-left (first column on the right).
- `Digits` converts all digits to Persian (۱۲۳), Arabic-Indic (١٢٣, used for Kurdish/Arabic) or Latin.
- Missing glyphs fall back to another installed font, preferring fonts you loaded.

### Fonts

Any installed font works. To use your own (recommended for Persian/Kurdish so output is identical everywhere): **Fonts...** in the designer, or in code `FontManager.LoadFontFile(path)` / `LoadFontDirectory(dir)`. **Embed font file...** stores the font inside the `.prt` so the report carries its fonts with it; check the font's license allows that.

### SQL Server connection strings

`Microsoft.Data.SqlClient` encrypts by default, so a local server with a self-signed certificate needs `TrustServerCertificate=True`. For another ADO.NET provider register it once at startup, then put its name in the data source's Provider box:

```csharp
DbProviders.Register("PostgreSQL", Npgsql.NpgsqlFactory.Instance);
```

## Known limits

- The **designer and printing are Windows-only** (WinForms/GDI+). The engine (`PrintHelper.Core`) is plain netstandard2.0 + SkiaSharp and is intended to run elsewhere too, but has only been tested on Windows. On Linux/macOS you would add the matching `SkiaSharp.NativeAssets.*` package.
- **PDF size**: fonts are embedded whole, not subset, so a PDF is roughly 0.5–1 MB per font face used.
- **Printing** sends 300 dpi page images to the printer (accurate, but not vector).
- **Excel export** is a positional grid of the report's text (good for tables); it does not reproduce images, lines or borders.
- Bidirectional text handles the common cases (Persian/Kurdish/Arabic mixed with English and numbers). It is a compact implementation, not the full Unicode algorithm, so unusual embeddings may order differently.
- No multi-select, snapping guides or copy/paste between reports yet.
