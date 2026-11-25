using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace CqNl.Infrastructure.Excel;

/// <summary>Giá trị ô đã giải mã: Text (chuỗi), Number (số), IsDate (ô số có định dạng ngày → Date).</summary>
public sealed record XCell(string? Text, double? Number, bool IsDate)
{
    public DateTime? Date => IsDate && Number is { } n ? DateTime.FromOADate(n) : null;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && Number is null;
    public override string ToString() => Text ?? (Number?.ToString(CultureInfo.InvariantCulture) ?? "");
}

/// <summary>Đọc CHỈ ĐỌC sheet đầu (hoặc theo tên) của file .xlsx thành lưới [hàng][cột] (0-based).</summary>
public static class XlsxReader
{
    private static readonly HashSet<uint> BuiltinDateFormats = new() { 14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47 };

    public static IReadOnlyList<string> SheetNames(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = SpreadsheetDocument.Open(fs, false);
        return doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Select(s => s.Name?.Value ?? "").ToList();
    }

    public static List<XCell?[]> ReadSheet(string path, string? sheetName = null, int maxRows = 100000)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = SpreadsheetDocument.Open(fs, false);
        var wb = doc.WorkbookPart!;
        var sheet = wb.Workbook.Sheets!.Elements<Sheet>().FirstOrDefault(s => sheetName is null || s.Name == sheetName)
                    ?? throw new InvalidDataException($"Không thấy sheet '{sheetName}'");
        var wsPart = (WorksheetPart)wb.GetPartById(sheet.Id!);
        var sst = wb.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? Array.Empty<string>();
        var dateStyles = DateStyleIndexes(wb);
        var rows = new List<XCell?[]>();
        using var reader = OpenXmlReader.Create(wsPart);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
            var row = (Row)reader.LoadCurrentElement()!;
            var r = (int)(row.RowIndex?.Value ?? (uint)(rows.Count + 1)) - 1;
            if (r >= maxRows) break;
            var cells = new List<(int col, XCell cell)>();
            foreach (var c in row.Elements<Cell>())
            {
                var col = ColIndex(c.CellReference?.Value);
                cells.Add((col, Decode(c, sst, dateStyles)));
            }
            while (rows.Count <= r) rows.Add(Array.Empty<XCell?>());
            var arr = new XCell?[cells.Count == 0 ? 0 : cells.Max(x => x.col) + 1];
            foreach (var (col, cell) in cells) arr[col] = cell;
            rows[r] = arr;
        }
        return rows;
    }

    private static XCell Decode(Cell c, string[] sst, HashSet<uint> dateStyles)
    {
        var raw = c.CellValue?.Text;
        var t = c.DataType?.Value;
        if (t == CellValues.SharedString && int.TryParse(raw, out var i)) return new XCell(i < sst.Length ? sst[i] : null, null, false);
        if (t == CellValues.InlineString) return new XCell(c.InlineString?.InnerText, null, false);
        if (t == CellValues.String || t == CellValues.Error) return new XCell(raw, null, false);
        if (t == CellValues.Boolean) return new XCell(raw == "1" ? "TRUE" : "FALSE", null, false);
        if (raw is null) return new XCell(null, null, false);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return new XCell(null, d, c.StyleIndex is not null && dateStyles.Contains(c.StyleIndex.Value));
        return new XCell(raw, null, false);
    }

    private static HashSet<uint> DateStyleIndexes(WorkbookPart wb)
    {
        var result = new HashSet<uint>();
        var ss = wb.WorkbookStylesPart?.Stylesheet;
        if (ss?.CellFormats is null) return result;
        var custom = ss.NumberingFormats?.Elements<NumberingFormat>()
            .Where(n => n.NumberFormatId is not null && n.FormatCode?.Value is { } f && IsDateFormat(f))
            .Select(n => n.NumberFormatId!.Value).ToHashSet() ?? new HashSet<uint>();
        uint idx = 0;
        foreach (var xf in ss.CellFormats.Elements<CellFormat>())
        {
            var id = xf.NumberFormatId?.Value ?? 0;
            if (BuiltinDateFormats.Contains(id) || custom.Contains(id)) result.Add(idx);
            idx++;
        }
        return result;
    }

    private static bool IsDateFormat(string f)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(f, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "").ToLowerInvariant();
        return s.Contains('d') || s.Contains('y') || (s.Contains('m') && (s.Contains('h') || s.Contains('s')) ) || s.Contains("mmm");
    }

    public static int ColIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return 0;
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (ch < 'A' || ch > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n - 1;
    }
}
