using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace CqNl.Infrastructure.Excel;

/// <summary>Một sheet báo cáo: tiêu đề cột + các dòng (string/số/null) + độ rộng cột (ký tự, tùy chọn).</summary>
public sealed record ReportSheet(string Name, IReadOnlyList<string> Header, IReadOnlyList<object?[]> Rows, IReadOnlyList<double>? Widths);

/// <summary>Ghi file Excel báo cáo đơn giản (không dùng template CQ): tiêu đề in đậm có nền, cố định dòng tiêu đề, lọc tự động.</summary>
public static class XlsxReportWriter
{
    public static byte[] Build(IReadOnlyList<ReportSheet> sheets)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook();
            var styles = wb.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(
                new Fonts(new Font(new FontSize { Val = 11D }, new FontName { Val = "Calibri" }),
                          new Font(new Bold(), new FontSize { Val = 11D }, new FontName { Val = "Calibri" })),
                new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                          new Fill(new PatternFill(new ForegroundColor { Rgb = "FFDCE6F1" }) { PatternType = PatternValues.Solid })),
                new Borders(new Border(), new Border(new LeftBorder { Style = BorderStyleValues.Thin }, new RightBorder { Style = BorderStyleValues.Thin },
                    new TopBorder { Style = BorderStyleValues.Thin }, new BottomBorder { Style = BorderStyleValues.Thin }, new DiagonalBorder())),
                new CellFormats(
                    new CellFormat(),                                                                                   // 0 thường
                    new CellFormat { FontId = 1U, FillId = 2U, BorderId = 1U, ApplyFont = true, ApplyFill = true, ApplyBorder = true,
                                     Alignment = new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Center } }, // 1 tiêu đề
                    new CellFormat { BorderId = 1U, ApplyBorder = true },                                                // 2 ô có viền
                    new CellFormat { BorderId = 1U, NumberFormatId = 4U, ApplyBorder = true, ApplyNumberFormat = true }));  // 3 số #,##0.00
            var sheetsEl = wb.Workbook.AppendChild(new Sheets());
            uint id = 1;
            foreach (var s in sheets)
            {
                var ws = wb.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                var cols = s.Header.Count;
                var head = new Row { RowIndex = 1U };
                for (var c = 0; c < cols; c++) head.Append(TextCell(Ref(c, 1), s.Header[c], 1U));
                data.Append(head);
                uint r = 2U;
                foreach (var row in s.Rows)
                {
                    var xr = new Row { RowIndex = r };
                    for (var c = 0; c < Math.Min(cols, row.Length); c++)
                    {
                        var v = row[c];
                        xr.Append(v switch
                        {
                            null => new Cell { CellReference = Ref(c, r), StyleIndex = 2U },
                            int i => NumCell(Ref(c, r), i.ToString(CultureInfo.InvariantCulture), 2U),
                            long l => NumCell(Ref(c, r), l.ToString(CultureInfo.InvariantCulture), 2U),
                            decimal d => NumCell(Ref(c, r), d.ToString(CultureInfo.InvariantCulture), d == Math.Truncate(d) ? 2u : 3u),
                            double d => NumCell(Ref(c, r), d.ToString("R", CultureInfo.InvariantCulture), 3U),
                            _ => TextCell(Ref(c, r), Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", 2U),
                        });
                    }
                    data.Append(xr); r++;
                }
                var worksheet = new Worksheet();
                worksheet.Append(new SheetViews(new SheetView(new Pane { VerticalSplit = 1D, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen })
                    { WorkbookViewId = 0U }));
                var columns = new Columns();
                for (var c = 0; c < cols; c++)
                {
                    var w = s.Widths is not null && c < s.Widths.Count ? s.Widths[c] : Math.Clamp(s.Header[c].Length + 4, 10, 30);
                    columns.Append(new Column { Min = (uint)c + 1, Max = (uint)c + 1, Width = w, CustomWidth = true });
                }
                worksheet.Append(columns);
                worksheet.Append(data);
                if (s.Rows.Count > 0) worksheet.Append(new AutoFilter { Reference = $"A1:{Ref(cols - 1, r - 1)}" });
                ws.Worksheet = worksheet;
                sheetsEl.Append(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = id++, Name = SafeName(s.Name) });
            }
            wb.Workbook.Save();
        }
        return ms.ToArray();
    }

    private static string SafeName(string n)
    {
        foreach (var ch in new[] { '\\', '/', '?', '*', '[', ']', ':' }) n = n.Replace(ch, '-');
        return n.Length > 31 ? n[..31] : n;
    }

    private static Cell TextCell(string r, string v, uint style) => new() { CellReference = r, DataType = CellValues.InlineString, StyleIndex = style, InlineString = new InlineString(new Text(v) { Space = SpaceProcessingModeValues.Preserve }) };
    private static Cell NumCell(string r, string v, uint style) => new() { CellReference = r, StyleIndex = style, CellValue = new CellValue(v) };

    public static string Ref(int col0, uint row)
    {
        var s = ""; var n = col0 + 1;
        while (n > 0) { var m = (n - 1) % 26; s = (char)('A' + m) + s; n = (n - 1) / 26; }
        return s + row;
    }
}
