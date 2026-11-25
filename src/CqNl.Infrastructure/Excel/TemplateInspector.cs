using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace CqNl.Infrastructure.Excel;

public enum FieldKind { Cell, Cells, Range, Ranges, Texts }

/// <summary>Các trường của bản đồ ô CQ (mô tả tiếng Việt cho trình sửa bản đồ).</summary>
public sealed record TemplateField(string Key, string Label, FieldKind Kind, bool Required, string Hint = "");

public sealed record TemplateIssue(bool Error, string Field, string Message);

public static class TemplateFields
{
    public static readonly IReadOnlyList<TemplateField> All = new TemplateField[]
    {
        new("Customer", "Khách hàng", FieldKind.Cell, true), new("Material", "Vật liệu (mác)", FieldKind.Cell, true),
        new("Size", "Kích thước", FieldKind.Cell, false), new("PartNo", "Mã quản lý", FieldKind.Cell, false), new("PartName", "Tên chi tiết", FieldKind.Cell, false),
        new("Quantity", "Số lượng", FieldKind.Cell, true), new("WeightKg", "Trọng lượng (kg)", FieldKind.Cell, true),
        new("ReceivedDate", "Ngày nhận hàng", FieldKind.Cell, true), new("CheckDate", "Ngày kiểm tra", FieldKind.Cell, true),
        new("Dngc", "Số ĐNGC", FieldKind.Cell, true), new("ReportNo", "Số báo cáo", FieldKind.Cell, true),
        new("HardnessRequirement", "Yêu cầu độ cứng", FieldKind.Cell, false), new("HardnessUnit", "Đơn vị độ cứng", FieldKind.Cell, false),
        new("HardnessTolerance", "Dung sai độ cứng", FieldKind.Cell, false),
        new("Samples", "Kết quả Mẫu 1–6", FieldKind.Cells, false, "6 ô, cách nhau bởi dấu phẩy (mẫu nhập tay như Thấm Nitơ: bỏ trống)"),
        new("AppearanceResult", "Kết quả ngoại quan", FieldKind.Cell, false), new("CurvatureRequirement", "Độ cong — yêu cầu", FieldKind.Cell, false),
        new("CurvatureResult", "Độ cong — kết quả", FieldKind.Cell, false), new("OtherResult", "Yêu cầu khác — kết quả", FieldKind.Cell, false),
        new("Remark", "Nhận xét", FieldKind.Cell, true),
        new("QuenchTemp1", "Nhiệt độ tôi 1 (ô)", FieldKind.Cell, false), new("QuenchTemp2", "Nhiệt độ tôi 2 (ô)", FieldKind.Cell, false),
        new("RamCells", "Ô nhiệt độ ram", FieldKind.Cells, false, "các ô, cách nhau bởi dấu phẩy"),
        new("SampleImageFrame", "Khung ảnh xuất hàng", FieldKind.Range, true, "vùng ô, vd B19:K23"),
        new("HardnessImageFrames", "Khung ảnh độ cứng", FieldKind.Ranges, true, "1–2 vùng ô, cách nhau bởi dấu phẩy"),
        new("OtherImageFrame", "Khung ảnh khác (kim tương)", FieldKind.Range, false, "vùng ô, vd A23:H23"),
        new("ApproverSignatureFrame", "Khung chữ ký người duyệt (+ dấu)", FieldKind.Range, true),
        new("CheckerSignatureFrame", "Khung chữ ký người kiểm tra", FieldKind.Range, true),
        new("DrawingTemperatures", "Chữ nhiệt độ tôi trong hình vẽ", FieldKind.Texts, false, "đúng thứ tự trong hình, vd 650℃, 850℃, 1030℃"),
        new("DrawingRamTexts", "Chữ ram trong hình vẽ", FieldKind.Texts, false, "vd 180p, 180p, 180p"),
    };
}

/// <summary>Đọc thông tin template Excel (sheet, ô, vùng gộp, vùng in, chữ trong hình vẽ), kiểm tra bản đồ ô,
/// và tự dò bản đồ cho template mới dựa trên một template có sẵn (dò theo nhãn, tính độ lệch hàng/cột).</summary>
public sealed class TemplateInspector
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public string Sheet { get; }
    public IReadOnlyList<string> Sheets { get; }
    public Dictionary<(int Col, int Row), XCell> Cells { get; } = new();
    public List<(int C1, int R1, int C2, int R2)> Merges { get; } = new();
    public (int C1, int R1, int C2, int R2)? PrintArea { get; }
    public bool HasDrawing { get; }
    public List<string> DrawingTexts { get; } = new();

    public TemplateInspector(byte[] xlsx, string? sheet = null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cqnl_tpl_" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.WriteAllBytes(tmp, xlsx);
        try
        {
            using (var doc = SpreadsheetDocument.Open(tmp, false))
            {
                var wb = doc.WorkbookPart!;
                var sheets = wb.Workbook.Sheets!.Elements<Sheet>().ToList();
                Sheets = sheets.Select(s => s.Name!.Value!).ToList();
                var sh = (sheet is null ? sheets.FirstOrDefault() : sheets.FirstOrDefault(s => s.Name == sheet))
                         ?? throw new InvalidDataException($"Không có sheet '{sheet}'");
                Sheet = sh.Name!.Value!;
                var idx = sheets.IndexOf(sh);
                var ws = (WorksheetPart)wb.GetPartById(sh.Id!);
                foreach (var mc in ws.Worksheet.Descendants<MergeCell>())
                    if (mc.Reference?.Value is { } r && TryRange(r, out var m)) Merges.Add(m);
                var pa = wb.Workbook.DefinedNames?.Elements<DefinedName>()
                    .FirstOrDefault(d => d.Name == "_xlnm.Print_Area" && d.LocalSheetId?.Value == (uint)idx)?.Text;
                if (pa is not null && pa.Contains('!') && TryRange(pa[(pa.LastIndexOf('!') + 1)..].Replace("$", "").Split(',')[0], out var p)) PrintArea = p;
                if (ws.DrawingsPart is { } dp)
                {
                    HasDrawing = true;
                    using var st = dp.GetStream();
                    DrawingTexts.AddRange(XDocument.Load(st).Descendants(A + "t").Select(t => t.Value));
                }
            }
            var rows = XlsxReader.ReadSheet(tmp, Sheet, 3000);
            for (var r = 0; r < rows.Count; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    if (rows[r][c] is { } x && (x.Text is not null || x.Number is not null)) Cells[(c + 1, r + 1)] = x;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ---------- toạ độ ----------
    public static bool TryCell(string s, out (int Col, int Row) cell)
    {
        cell = default;
        var m = Regex.Match((s ?? "").Trim().ToUpperInvariant().Replace("$", ""), "^([A-Z]{1,3})([0-9]{1,5})$");
        if (!m.Success) return false;
        var col = 0; foreach (var ch in m.Groups[1].Value) col = col * 26 + (ch - 'A' + 1);
        cell = (col, int.Parse(m.Groups[2].Value)); return true;
    }
    public static bool TryRange(string s, out (int C1, int R1, int C2, int R2) r)
    {
        r = default; var p = (s ?? "").Split(':');
        if (!TryCell(p[0], out var a)) return false;
        var b = a; if (p.Length > 1 && !TryCell(p[1], out b)) return false;
        r = (Math.Min(a.Col, b.Col), Math.Min(a.Row, b.Row), Math.Max(a.Col, b.Col), Math.Max(a.Row, b.Row)); return true;
    }
    public static string Ref(int col, int row) { var s = ""; while (col > 0) { var m = (col - 1) % 26; s = (char)('A' + m) + s; col = (col - 1) / 26; } return s + row; }
    public static string RefRange((int C1, int R1, int C2, int R2) r) => Ref(r.C1, r.R1) + ":" + Ref(r.C2, r.R2);

    public string? Text((int Col, int Row) c) => Cells.TryGetValue(c, out var x) ? x.Text ?? x.Number?.ToString(CultureInfo.InvariantCulture) : null;
    public string? TextAt(string cell) => TryCell(cell, out var c) ? Text(c) : null;

    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"\s+", " ").Trim();

    /// <summary>Ô có nhãn gần vị trí gợi ý nhất (số: so khớp giá trị; chữ: chứa chuỗi).</summary>
    public (int Col, int Row)? FindLabel(JsonElement label, (int Col, int Row) near)
    {
        IEnumerable<(int Col, int Row)> hits;
        if (label.ValueKind == JsonValueKind.Number)
        {
            var v = label.GetDouble();
            hits = Cells.Where(kv => kv.Value.Number is { } n && Math.Abs(n - v) < 1e-9).Select(kv => kv.Key);
        }
        else
        {
            var t = Norm(label.GetString());
            if (t.Length == 0) return null;
            hits = Cells.Where(kv => kv.Value.Text is { } s && Norm(s).Contains(t)).Select(kv => kv.Key);
        }
        var best = hits.OrderBy(h => Math.Abs(h.Row - near.Row) * 3 + Math.Abs(h.Col - near.Col)).Take(1).ToList();
        return best.Count == 0 ? null : best[0];
    }

    // ---------- kiểm tra ----------
    public List<TemplateIssue> Validate(TemplateMap map)
    {
        var issues = new List<TemplateIssue>();
        if (!Sheets.Contains(map.Sheet)) { issues.Add(new(true, "sheet", $"Không có sheet '{map.Sheet}'")); return issues; }
        if (PrintArea is null) issues.Add(new(true, "PrintArea", "Template chưa đặt vùng in (Page Layout → Print Area)"));
        if (!HasDrawing) issues.Add(new(true, "Drawing", "Template phải có ít nhất một hình/logo (để chèn ảnh, dấu, chữ ký)"));
        bool InPrint((int C1, int R1, int C2, int R2) r) => PrintArea is not { } p || (r.C1 >= p.C1 && r.C2 <= p.C2 && r.R1 >= p.R1 && r.R2 <= p.R2);
        foreach (var f in TemplateFields.All)
        {
            var has = map.Fields.ContainsKey(f.Key);
            var cells = map.Cell(f.Key) is { } c ? new[] { c } : map.Cells(f.Key).ToArray();
            var ranges = map.Ranges(f.Key);
            var texts = map.Texts(f.Key);
            if (f.Required && (!has || (cells.Length == 0 && ranges.Count == 0)))
            { issues.Add(new(true, f.Key, $"{f.Label}: chưa khai báo")); continue; }
            foreach (var cell in cells)
            {
                if (!TryCell(cell, out var cc)) { issues.Add(new(true, f.Key, $"{f.Label}: ô '{cell}' không hợp lệ")); continue; }
                var mg = Merges.FirstOrDefault(m => cc.Col >= m.C1 && cc.Col <= m.C2 && cc.Row >= m.R1 && cc.Row <= m.R2);
                if (mg != default && (mg.C1, mg.R1) != cc) issues.Add(new(true, f.Key, $"{f.Label}: {cell} nằm giữa vùng gộp {RefRange(mg)} — phải ghi vào ô {Ref(mg.C1, mg.R1)}"));
                if (!InPrint((cc.Col, cc.Row, cc.Col, cc.Row))) issues.Add(new(true, f.Key, $"{f.Label}: {cell} ngoài vùng in"));
            }
            foreach (var rg in ranges)
            {
                if (!TryRange(rg, out var r)) { issues.Add(new(true, f.Key, $"{f.Label}: vùng '{rg}' không hợp lệ")); continue; }
                if (!InPrint(r)) issues.Add(new(true, f.Key, $"{f.Label}: vùng {rg} ngoài vùng in"));
            }
            if (f.Key == "Samples" && cells.Length is > 0 and < 6) issues.Add(new(false, f.Key, $"{f.Label}: chỉ có {cells.Length} ô (tối đa 6 mẫu)"));
            if (f.Kind == FieldKind.Texts && texts.Count > 0)
            {
                var found = DrawingTexts.Where(t => texts.Contains(t)).ToList();
                if (!found.SequenceEqual(texts)) issues.Add(new(true, f.Key, $"{f.Label}: hình vẽ không có đúng các chữ {string.Join(", ", texts)} (thấy: {string.Join(", ", found)})"));
            }
            if (has && map.Fields[f.Key].TryGetProperty("label", out var lab) && lab.ValueKind == JsonValueKind.Array && lab.GetArrayLength() == 2
                && lab[0].GetString() is { } lc && lab[1].ValueKind == JsonValueKind.String)
            {
                var actual = TextAt(lc);
                if (actual is null || !Norm(actual).Contains(Norm(lab[1].GetString()))) issues.Add(new(false, f.Key, $"{f.Label}: ô nhãn {lc} = \"{actual}\" (mong đợi chứa \"{lab[1].GetString()}\")"));
            }
        }
        // mẫu độ cứng: phải có ô Mẫu 1–6 + ô yêu cầu, HOẶC (mẫu nhập tay) có thông số nhập tay
        if (map.Cells("Samples").Count == 0 && map.Manual.Count == 0) issues.Add(new(true, "Samples", "Kết quả Mẫu 1–6: chưa khai báo (hoặc khai báo thông số nhập tay \"manual\")"));
        if (map.Cells("Samples").Count > 0 && map.Cell("HardnessRequirement") is null) issues.Add(new(true, "HardnessRequirement", "Yêu cầu độ cứng: chưa khai báo"));
        foreach (var mf in map.Manual)
        {
            if (mf.Cell is not null)
            {
                if (!TryCell(mf.Cell, out var cc)) { issues.Add(new(true, "manual." + mf.Key, $"{mf.Label}: ô '{mf.Cell}' không hợp lệ")); continue; }
                var mg = Merges.FirstOrDefault(m => cc.Col >= m.C1 && cc.Col <= m.C2 && cc.Row >= m.R1 && cc.Row <= m.R2);
                if (mg != default && (mg.C1, mg.R1) != cc) issues.Add(new(true, "manual." + mf.Key, $"{mf.Label}: {mf.Cell} nằm giữa vùng gộp {RefRange(mg)}"));
                if (!InPrint((cc.Col, cc.Row, cc.Col, cc.Row))) issues.Add(new(true, "manual." + mf.Key, $"{mf.Label}: {mf.Cell} ngoài vùng in"));
            }
            else if (mf.Text is not null)
            {
                if (DrawingTexts.Count(t => t == mf.Text) < mf.Occurrence) issues.Add(new(true, "manual." + mf.Key, $"{mf.Label}: hình vẽ không có chữ \"{mf.Text}\" (lần {mf.Occurrence})"));
            }
            else issues.Add(new(true, "manual." + mf.Key, $"{mf.Label}: thiếu \"cell\" hoặc \"text\""));
        }
        if (map.Manual.GroupBy(m => m.Key).Any(g => g.Count() > 1)) issues.Add(new(true, "manual", "Thông số nhập tay trùng key"));
        return issues;
    }

    // ---------- tự dò bản đồ cho template mới ----------
    /// <summary>Dò bản đồ cho template mới dựa trên template có sẵn: mỗi trường tìm lại ô NHÃN của nó trong file mới, lấy độ lệch
    /// hàng/cột đem áp cho ô/vùng ghi; trường không có nhãn dùng độ lệch của trường gần nhất. Chữ trong hình vẽ dò theo mẫu "650℃", "180p".</summary>
    public (string Json, List<string> Report) AutoMap(TemplateMap baseMap, string newId, string displayName, string fileName)
    {
        var report = new List<string>();
        var root = JsonNode.Parse(baseMap.Json)!.AsObject();
        root["templateId"] = newId; root["displayName"] = displayName; root["fileName"] = fileName; root["sheet"] = Sheet;
        root["status"] = $"tự dò từ mẫu {baseMap.TemplateId} — cần kiểm tra";
        var fields = root["fields"]!.AsObject();
        var offsets = new Dictionary<string, (int dc, int dr, int row)>();
        foreach (var (key, node) in fields.ToList())
        {
            var f = baseMap.Fields[key];
            if (!f.TryGetProperty("label", out var lab) || lab.ValueKind != JsonValueKind.Array || lab.GetArrayLength() != 2) continue;
            if (!TryCell(lab[0].GetString() ?? "", out var orig)) continue;
            var hit = FindLabel(lab[1], orig);
            if (hit is { } h)
            {
                offsets[key] = (h.Col - orig.Col, h.Row - orig.Row, orig.Row);
                node!["label"] = new JsonArray(Ref(h.Col, h.Row), JsonNode.Parse(lab[1].GetRawText()));
            }
        }
        // nhãn tìm được nhưng lệch quá xa so với đa số trường (vd. chữ "Khác" xuất hiện ở chỗ khác) → coi như không tìm thấy
        if (offsets.Count >= 3)
        {
            var mdr = offsets.Values.Select(v => v.dr).OrderBy(x => x).ElementAt(offsets.Count / 2);
            var mdc = offsets.Values.Select(v => v.dc).OrderBy(x => x).ElementAt(offsets.Count / 2);
            foreach (var k in offsets.Where(kv => Math.Abs(kv.Value.dr - mdr) > 3 || Math.Abs(kv.Value.dc - mdc) > 3).Select(kv => kv.Key).ToList())
            {
                report.Add($"{k}: nhãn tìm thấy ở chỗ lệch xa ({offsets[k].dc:+0;-0;0} cột, {offsets[k].dr:+0;-0;0} dòng) — bỏ qua, dùng độ lệch chung");
                offsets.Remove(k);
                if (baseMap.Fields[k].TryGetProperty("label", out var bl)) fields[k]!["label"] = JsonNode.Parse(bl.GetRawText());
            }
        }
        (int dc, int dr) OffsetFor(string key, int row)
        {
            if (offsets.TryGetValue(key, out var o)) return (o.dc, o.dr);
            if (offsets.Count == 0) return (0, 0);
            var n = offsets.Values.OrderBy(v => Math.Abs(v.row - row)).First();
            return (n.dc, n.dr);
        }
        string Shift(string cell, (int dc, int dr) o) => TryCell(cell, out var c) ? Ref(c.Col + o.dc, c.Row + o.dr) : cell;
        string ShiftR(string rg, (int dc, int dr) o) => TryRange(rg, out var r) ? Ref(r.C1 + o.dc, r.R1 + o.dr) + ":" + Ref(r.C2 + o.dc, r.R2 + o.dr) : rg;
        foreach (var (key, node) in fields.ToList())
        {
            var obj = node!.AsObject();
            int RowOf(string s) => TryCell(s.Split(':')[0], out var c) ? c.Row : 0;
            if (obj["cell"] is JsonValue cv && cv.ToString() is { } cell)
            { var o = OffsetFor(key, RowOf(cell)); obj["cell"] = Shift(cell, o); if (o != (0, 0)) report.Add($"{key}: {cell} → {obj["cell"]}"); }
            if (obj["cells"] is JsonArray ca)
            { var cs = ca.Select(x => x!.ToString()).ToList(); var o = OffsetFor(key, RowOf(cs.FirstOrDefault() ?? "")); obj["cells"] = new JsonArray(cs.Select(x => (JsonNode)Shift(x, o)).ToArray()); }
            if (obj["range"] is JsonValue rv && rv.ToString() is { } rg)
            { var o = OffsetFor(key, RowOf(rg)); obj["range"] = ShiftR(rg, o); if (o != (0, 0)) report.Add($"{key}: {rg} → {obj["range"]}"); }
            if (obj["ranges"] is JsonArray ra)
            { var rs = ra.Select(x => x!.ToString()).ToList(); var o = OffsetFor(key, RowOf(rs.FirstOrDefault() ?? "")); obj["ranges"] = new JsonArray(rs.Select(x => (JsonNode)ShiftR(x, o)).ToArray()); }
            if (obj["unhideRowWhenFilled"] is JsonValue uv && obj["cell"] is JsonValue c2 && TryCell(c2.ToString(), out var cc2)) obj["unhideRowWhenFilled"] = cc2.Row;
        }
        // trường KHÔNG bắt buộc mà không thấy nhãn: nếu ô suy ra rơi giữa vùng gộp, ngoài vùng in hoặc trùng ô của trường khác → bỏ trống (mẫu mới không có mục này)
        var required = TemplateFields.All.Where(f => f.Required).Select(f => f.Key).ToHashSet();
        var used = new Dictionary<string, string>();
        foreach (var (key, node) in fields) if (offsets.ContainsKey(key) && node?["cell"] is JsonValue uc) used[uc.ToString()] = key;
        foreach (var (key, node) in fields.ToList())
        {
            if (required.Contains(key) || offsets.ContainsKey(key) || node?["cell"] is not JsonValue cv2 || !TryCell(cv2.ToString(), out var c3)) continue;
            var mg = Merges.FirstOrDefault(m => c3.Col >= m.C1 && c3.Col <= m.C2 && c3.Row >= m.R1 && c3.Row <= m.R2);
            var bad = (mg != default && (mg.C1, mg.R1) != c3) || (PrintArea is { } pa && (c3.Col < pa.C1 || c3.Col > pa.C2 || c3.Row < pa.R1 || c3.Row > pa.R2))
                      || used.ContainsKey(cv2.ToString());
            if (bad) { fields.Remove(key); report.Add($"{key}: mẫu mới không có mục này (không thấy nhãn) — để trống, thêm tay nếu cần"); }
            else used[cv2.ToString()] = key;
        }
        var temps = DrawingTexts.Where(t => Regex.IsMatch(t.Trim(), @"^\d{3,4}\s*℃$")).ToList();
        var rams = DrawingTexts.Where(t => Regex.IsMatch(t.Trim(), @"^\d{2,4}\s*(p|min|phút)$", RegexOptions.IgnoreCase)).ToList();
        if (fields["DrawingTemperatures"] is JsonObject dt)
        {
            var baseTexts = baseMap.Texts("DrawingTemperatures");
            dt["texts"] = new JsonArray((baseTexts.Count == 0 ? new List<string>() : temps).Select(x => (JsonNode)x).ToArray());
            report.Add($"Chữ nhiệt độ trong hình: {(temps.Count == 0 ? "(không có)" : string.Join(", ", temps))}");
        }
        if (fields["DrawingRamTexts"] is JsonObject dr2)
        {
            dr2["texts"] = new JsonArray(rams.Select(x => (JsonNode)x).ToArray());
            report.Add($"Chữ ram trong hình: {(rams.Count == 0 ? "(không có)" : string.Join(", ", rams))}");
        }
        report.Insert(0, $"Dò được nhãn của {offsets.Count}/{fields.Count(kv => baseMap.Fields[kv.Key].TryGetProperty("label", out _))} trường.");
        return (root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), report);
    }
}
