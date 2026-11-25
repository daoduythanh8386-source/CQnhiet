using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace CqNl.Infrastructure.Excel;

/// <summary>Bản đồ ô một template (config/template-maps/*.json).</summary>
public sealed class TemplateMap
{
    public required string TemplateId { get; init; }
    public required string FileName { get; init; }
    public required string Sheet { get; init; }
    public required Dictionary<string, JsonElement> Fields { get; init; }

    /// <summary>JSON gốc của bản đồ (để lưu kèm bản CQ — bản CQ luôn dựng/duyệt theo đúng bản đồ lúc tạo).</summary>
    public string Json { get; init; } = "";
    public string DisplayName { get; init; } = "";
    /// <summary>Thông số NHẬP TAY riêng của template (Vinfast, Thấm Nitơ…): ô (có thể có tiền tố nhãn) hoặc chữ trong hình vẽ.</summary>
    public IReadOnlyList<ManualField> Manual { get; init; } = Array.Empty<ManualField>();
    /// <summary>Cột ĐÁNH DẤU của bảng mác ứng với template (1 khuôn, 2 gió, 3 carbon); null = template không phụ thuộc mác.</summary>
    public int? SteelGradeMark { get; init; }
    /// <summary>Tên khách (không dấu, viết hoa) chứa từ này → app tự chọn template (vd "VINFAST").</summary>
    public string? CustomerKeyword { get; init; }
    /// <summary>Phương thức gia công ERP (không dấu) chứa từ này → app tự chọn template (vd "THAM NITO").</summary>
    public string? ProcessKeyword { get; init; }

    public static TemplateMap Load(string path) => Parse(File.ReadAllText(path));

    public static TemplateMap Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new TemplateMap
        {
            Json = json,
            DisplayName = r.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
            TemplateId = r.GetProperty("templateId").GetString()!, FileName = r.GetProperty("fileName").GetString()!,
            Sheet = r.GetProperty("sheet").GetString()!,
            Fields = r.GetProperty("fields").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
            Manual = r.TryGetProperty("manual", out var mf) && mf.ValueKind == JsonValueKind.Array ? mf.EnumerateArray().Select(ManualField.Parse).ToList() : Array.Empty<ManualField>(),
            SteelGradeMark = r.TryGetProperty("steelGradeMark", out var sm) && sm.ValueKind == JsonValueKind.Number ? sm.GetInt32() : null,
            CustomerKeyword = r.TryGetProperty("customerKeyword", out var ck) ? ck.GetString() : null,
            ProcessKeyword = r.TryGetProperty("processKeyword", out var pk) ? pk.GetString() : null,
        };
    }

    /// <summary>Thứ tự gán giá trị cho các chữ hình vẽ (vd hình Vinfast: chữ 1030℃ đứng trước 650℃ → order [2,0,1]); mặc định theo thứ tự.</summary>
    public IReadOnlyList<int>? Order(string field) => Fields.TryGetValue(field, out var f) && f.TryGetProperty("order", out var o) ? o.EnumerateArray().Select(x => x.GetInt32()).ToList() : null;
    /// <summary>Chừa khoảng trống dưới đáy khung ảnh (mm) — cho chú thích in sẵn trên template (vd "Độ phóng đại 300X").</summary>
    public double PadBottomMm(string field) => Fields.TryGetValue(field, out var f) && f.TryGetProperty("padBottomMm", out var p) ? p.GetDouble() : 0;

    public string? Cell(string field) => Fields.TryGetValue(field, out var f) && f.TryGetProperty("cell", out var c) ? c.GetString() : null;
    public IReadOnlyList<string> Cells(string field) => Fields.TryGetValue(field, out var f) && f.TryGetProperty("cells", out var c) ? c.EnumerateArray().Select(x => x.GetString()!).ToList() : Array.Empty<string>();
    public IReadOnlyList<string> Ranges(string field) => !Fields.TryGetValue(field, out var f) ? Array.Empty<string>()
        : f.TryGetProperty("range", out var r) ? new[] { r.GetString()! } : f.TryGetProperty("ranges", out var rs) ? rs.EnumerateArray().Select(x => x.GetString()!).ToList() : Array.Empty<string>();
    public IReadOnlyList<string> Texts(string field) => Fields.TryGetValue(field, out var f) && f.TryGetProperty("texts", out var t) ? t.EnumerateArray().Select(x => x.GetString()!).ToList() : Array.Empty<string>();
    public string? Type(string field) => Fields.TryGetValue(field, out var f) && f.TryGetProperty("type", out var t) ? t.GetString() : null;
    public int? UnhideRow(string field) => Fields.TryGetValue(field, out var f) && f.TryGetProperty("unhideRowWhenFilled", out var u) ? u.GetInt32() : null;
    public int MaxImages(string field, int dflt) => Fields.TryGetValue(field, out var f) && f.TryGetProperty("maxImages", out var m) ? m.GetInt32() : dflt;
}

/// <summary>Một thông số nhập tay của template: ghi vào ô <see cref="Cell"/> (kèm tiền tố nhãn) hoặc thay chữ <see cref="Text"/>
/// (lần xuất hiện thứ <see cref="Occurrence"/>) trong hình vẽ. Default có thể chứa {HardnessRequirement} {Unit} {Min} {Max}.</summary>
public sealed record ManualField(string Key, string Label, string Group, string? Cell, string? Text, int Occurrence, string Prefix, string Default, bool Required)
{
    public static ManualField Parse(JsonElement e)
    {
        string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var text = S("text");
        return new ManualField(S("key") ?? throw new FormatException("manual: thiếu key"), S("label") ?? S("key")!, S("group") ?? "", S("cell"), text,
            e.TryGetProperty("occurrence", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 1, S("prefix") ?? "",
            S("default") ?? text ?? "", e.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True);
    }
}

/// <summary>Ảnh đã sẵn sàng đặt vào CQ (PNG/JPEG bytes + kích thước px).</summary>
public sealed record CqImage(byte[] Bytes, int Width, int Height, string Name);

/// <summary>Dấu/chữ ký đặt tự do: neo ô + lệch mm + rộng mm (người làm chỉnh được), hoặc giữa một vùng ô.</summary>
public sealed record CqOverlay(CqImage Image, string? Frame = null, string? Cell = null, double DxMm = 0, double DyMm = 0, double WidthMm = 40);

public sealed class CqData
{
    public Dictionary<string, string?> Values { get; } = new();          // Customer, Material, Size, PartNo, PartName, ReportNo, Dngc…
    public Dictionary<string, decimal?> Numbers { get; } = new();        // Quantity, WeightKg
    public List<string> Samples { get; } = new();                        // ô kết quả mẫu 1..6 (đã định dạng)
    public List<string>? QuenchTemps { get; set; }                       // theo mác / sửa tay
    public List<string>? RamValues { get; set; }
    public List<CqImage> SampleImages { get; } = new();                  // ảnh xuất hàng (đã ghi chú số cân nếu có)
    public List<CqImage> HardnessImages { get; } = new();                // ảnh độ cứng (đã sắp xếp + dựng thẻ)
    public List<CqImage> OtherImages { get; } = new();                   // ảnh khác (vd ảnh kim tương 300X — mẫu Thấm Nitơ)
    /// <summary>Giá trị thông số nhập tay theo key của <see cref="TemplateMap.Manual"/> (đã thay {…}).</summary>
    public Dictionary<string, string?> Manual { get; } = new();
    public List<CqOverlay> Overlays { get; } = new();
    /// <summary>Ô kết quả Mẫu 1–6 in đậm đồng nhất (mặc định bật — người dùng chốt 13/11/2026).</summary>
    public bool BoldSampleCells { get; set; } = true;
}

public sealed class CqBuildException(string message) : Exception(message);

/// <summary>
/// Sinh CQ từ BẢN SAO template: chỉ sửa phần tử &lt;c&gt; của ô đã map (giữ style), bỏ ẩn dòng khi cần, thay chữ nhiệt độ/ram
/// trong hình vẽ khi khớp đúng bản gốc, và THÊM ảnh vào drawing có sẵn. Mọi part khác giữ nguyên byte.
/// </summary>
public static class CqWriter
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const int Mdw = 7;   // bề rộng chữ số (px) font mặc định Arial Narrow 11 — đã kiểm bằng Excel 16 trên ADMIN-PC (ảnh nằm đúng khung)

    public static byte[] Build(byte[] templateBytes, TemplateMap map, CqData d)
    {
        var ms = new MemoryStream(); ms.Write(templateBytes); ms.Position = 0;
        // AutoSave=false: SDK KHÔNG tự ghi lại các part đã nạp DOM; ta chỉ ghi qua stream những part cần đổi → giữ nguyên byte phần còn lại.
        using (var pkg = SpreadsheetDocument.Open(ms, true, new OpenSettings { AutoSave = false }))
        {
            var wb = pkg.WorkbookPart!;
            var wbXml = LoadXml(wb);
            var sheetEl = wbXml.Root!.Element(S + "sheets")?.Elements(S + "sheet").FirstOrDefault(x => (string?)x.Attribute("name") == map.Sheet)
                          ?? throw new CqBuildException($"Template thiếu sheet '{map.Sheet}'");
            var wsPart = (WorksheetPart)wb.GetPartById((string)sheetEl.Attribute(R + "id")!);
            var sheet = LoadXml(wsPart);

            foreach (var (key, val) in d.Values)
            {
                var cell = map.Cell(key); if (cell is null) continue;
                SetCell(sheet, cell, val, number: false);
                if (!string.IsNullOrWhiteSpace(val) && map.UnhideRow(key) is { } row) Unhide(sheet, row);
            }
            foreach (var (key, val) in d.Numbers) { var cell = map.Cell(key); if (cell is not null) SetCell(sheet, cell, val?.ToString(CultureInfo.InvariantCulture), number: true); }
            var sampleCells = map.Cells("Samples");
            // template không có ô Mẫu 1–6 (vd Thấm Nitơ: độ cứng bề mặt ghi ở thông số nhập tay) → không ghi mẫu
            if (sampleCells.Count > 0 && d.Samples.Count > sampleCells.Count) throw new CqBuildException($"Template chỉ có {sampleCells.Count} ô mẫu");
            if (sampleCells.Count > 0) for (var i = 0; i < d.Samples.Count; i++) SetCell(sheet, sampleCells[i], d.Samples[i], number: false);
            // Người dùng chốt 13/11/2026: ô kết quả Mẫu 1–6 đồng nhất CHỮ ĐẬM. Template gốc (cả 3) để lẫn đậm/thường →
            // KHÔNG sửa template; trong BẢN SAO CQ thêm kiểu chữ đậm mới (nhân bản font + xf, giữ nguyên viền/căn lề/định dạng số).
            if (d.BoldSampleCells && wb.WorkbookStylesPart is { } stylesPart)
            {
                var styles = LoadXml(stylesPart);
                MakeBold(sheet, styles, sampleCells);
                SaveXml(stylesPart, styles);
            }

            var drawingsPart = wsPart.DrawingsPart ?? throw new CqBuildException("Template không có drawing (hình vẽ/logo) — sai template?");
            var drawing = LoadXml(drawingsPart);
            // chữ trong hình vẽ: tính TẤT CẢ vị trí trên hình GỐC trước rồi mới thay (giá trị mới trùng chữ gốc khác không bị thay nhầm)
            var tNodes = drawing.Descendants(A + "t").ToList();
            var orig = tNodes.Select(t => t.Value).ToList();
            var assign = new Dictionary<int, string>();
            PlanDrawingTexts(orig, map.Texts("DrawingTemperatures"), map.Order("DrawingTemperatures"), d.QuenchTemps, "tôi", assign);
            PlanDrawingTexts(orig, map.Texts("DrawingRamTexts"), map.Order("DrawingRamTexts"), d.RamValues, "ram", assign);
            foreach (var mf in map.Manual)
            {
                var val = d.Manual.GetValueOrDefault(mf.Key);
                if (mf.Required && string.IsNullOrWhiteSpace(val)) throw new CqBuildException($"Chưa nhập thông số \"{mf.Label}\"");
                if (mf.Cell is not null) { SetCell(sheet, mf.Cell, string.IsNullOrWhiteSpace(val) ? (mf.Prefix.Length > 0 ? mf.Prefix : null) : mf.Prefix + val, number: false); continue; }
                if (mf.Text is null) continue;
                var hits = orig.Select((t, i) => (t, i)).Where(x => x.t == mf.Text).Select(x => x.i).ToList();
                if (hits.Count < mf.Occurrence) throw new CqBuildException($"Hình vẽ không có chữ \"{mf.Text}\" (lần {mf.Occurrence}) cho \"{mf.Label}\" → template đã bị đổi?");
                var at = hits[mf.Occurrence - 1];
                if (assign.ContainsKey(at)) throw new CqBuildException($"Chữ \"{mf.Text}\" trong hình vẽ được gán 2 lần (bản đồ template sai)");
                assign[at] = string.IsNullOrWhiteSpace(val) ? mf.Text : val.Trim();
            }
            foreach (var (i, v) in assign) tNodes[i].Value = v;
            if (map.Cell("QuenchTemp1") is not null && d.QuenchTemps is not null)
            {
                if (d.QuenchTemps.Count != 2 || d.QuenchTemps.Any(string.IsNullOrWhiteSpace))
                    throw new CqBuildException($"Mác có {d.QuenchTemps.Count(t => !string.IsNullOrWhiteSpace(t))} giai đoạn tôi, template carbon cần 2 → không tạo CQ");
                SetCell(sheet, map.Cell("QuenchTemp1")!, d.QuenchTemps[0], false); SetCell(sheet, map.Cell("QuenchTemp2")!, d.QuenchTemps[1], false);
            }
            var ramCells = map.Cells("RamCells");
            if (ramCells.Count > 0 && d.RamValues is not null)
            {
                if (d.RamValues.Count != ramCells.Count || d.RamValues.Any(string.IsNullOrWhiteSpace)) throw new CqBuildException("Số giai đoạn RAM không khớp template → không tạo CQ");
                for (var i = 0; i < ramCells.Count; i++) SetCell(sheet, ramCells[i], d.RamValues[i], false);
            }

            var geo = new Geo(sheet);
            var idx = MaxPicId(drawing);
            void PlaceAll(string frameField, List<CqImage> imgs, int maxDefault)
            {
                if (imgs.Count == 0) return;
                var max = map.MaxImages(frameField, maxDefault);
                if (imgs.Count > max) throw new CqBuildException($"{imgs.Count} ảnh vượt giới hạn {max} của khung");
                var pad = map.PadBottomMm(frameField) * 36000;
                var rects = map.Ranges(frameField).Select(geo.Rect).Select(r => (r.X, r.Y, r.W, Math.Max(r.H * 0.3, r.H - pad))).ToList();
                var boxes = Layout.Table(rects, imgs.Select(i => (i.Width, i.Height)).ToList());
                for (var i = 0; i < imgs.Count; i++) Place(drawingsPart, drawing, geo, imgs[i], ++idx, boxes[i]);
            }
            PlaceAll("SampleImageFrame", d.SampleImages, 4);
            PlaceAll("HardnessImageFrames", d.HardnessImages, 8);
            if (d.OtherImages.Count > 0 && map.Ranges("OtherImageFrame").Count == 0) throw new CqBuildException("Template này không có khung ảnh khác (ảnh kim tương) — bỏ \"in trên CQ\" ở ảnh đó");
            PlaceAll("OtherImageFrame", d.OtherImages, 2);
            foreach (var ov in d.Overlays) Place(drawingsPart, drawing, geo, ov.Image, ++idx, OverlayBox(geo, ov));
            SaveXml(wsPart, sheet); SaveXml(drawingsPart, drawing);
        }
        return ms.ToArray();
    }

    private static int MaxPicId(XDocument drawing) =>
        drawing.Descendants().Where(e => e.Name.LocalName == "cNvPr").Select(e => int.TryParse((string?)e.Attribute("id"), out var v) ? v : 0).DefaultIfEmpty(0).Max();

    /// <summary>Chèn dấu/chữ ký lên một CQ ĐÃ DỰNG (bản nháp đã cấp số) — không dựng lại dữ liệu, nên bản duyệt đúng bằng bản nháp đã xem
    /// cộng thêm dấu và chữ ký. Các part khác giữ nguyên.</summary>
    public static byte[] AddOverlays(byte[] cqBytes, TemplateMap map, IReadOnlyList<CqOverlay> overlays)
    {
        var ms = new MemoryStream(); ms.Write(cqBytes); ms.Position = 0;
        using (var pkg = SpreadsheetDocument.Open(ms, true, new OpenSettings { AutoSave = false }))
        {
            var wb = pkg.WorkbookPart!;
            var sheetEl = LoadXml(wb).Root!.Element(S + "sheets")?.Elements(S + "sheet").FirstOrDefault(x => (string?)x.Attribute("name") == map.Sheet)
                          ?? throw new CqBuildException($"CQ thiếu sheet '{map.Sheet}'");
            var wsPart = (WorksheetPart)wb.GetPartById((string)sheetEl.Attribute(R + "id")!);
            var sheet = LoadXml(wsPart);
            var drawingsPart = wsPart.DrawingsPart ?? throw new CqBuildException("CQ không có drawing");
            var drawing = LoadXml(drawingsPart);
            var geo = new Geo(sheet); var idx = MaxPicId(drawing);
            foreach (var ov in overlays) Place(drawingsPart, drawing, geo, ov.Image, ++idx, OverlayBox(geo, ov));
            SaveXml(drawingsPart, drawing);
        }
        return ms.ToArray();
    }

    private static (double X, double Y, double W, double H) OverlayBox(Geo geo, CqOverlay ov)
    {
        if (ov.Frame is not null) return Layout.Table(new[] { geo.Rect(ov.Frame) }, new[] { (ov.Image.Width, ov.Image.Height) }, pad: 0.06)[0];
        var (col, row) = Geo.Split(ov.Cell ?? throw new CqBuildException("Dấu/chữ ký thiếu vị trí"));
        var w = ov.WidthMm * 36000;
        return (geo.ColX(col) + ov.DxMm * 36000, geo.RowY(row) + ov.DyMm * 36000, w, w * ov.Image.Height / ov.Image.Width);
    }

    /// <summary>Hình học in của sheet: vùng in (EMU, theo toạ độ sheet), lề, tỉ lệ, căn giữa ngang. Dùng để đặt dấu/chữ ký bằng kéo-thả
    /// trên ảnh trang PDF (tỉ lệ thực tế được hiệu chỉnh theo ảnh trang khi hiển thị).</summary>
    public sealed record SheetGeometry(double AreaX, double AreaY, double AreaW, double AreaH, double TopMarginMm, double LeftMarginMm, double Scale, bool HCenter);

    public static SheetGeometry Geometry(byte[] xlsx, TemplateMap map)
    {
        using var ms = new MemoryStream(xlsx);
        using var pkg = SpreadsheetDocument.Open(ms, false);
        var wb = pkg.WorkbookPart!; var wbXml = LoadXml(wb);
        var sheets = wbXml.Root!.Element(S + "sheets")!.Elements(S + "sheet").ToList();
        var idx = sheets.FindIndex(x => (string?)x.Attribute("name") == map.Sheet);
        if (idx < 0) throw new CqBuildException($"Thiếu sheet '{map.Sheet}'");
        var sheet = LoadXml((WorksheetPart)wb.GetPartById((string)sheets[idx].Attribute(R + "id")!));
        var geo = new Geo(sheet);
        var pa = wbXml.Root.Element(S + "definedNames")?.Elements(S + "definedName")
            .FirstOrDefault(d => (string?)d.Attribute("name") == "_xlnm.Print_Area" && (int?)d.Attribute("localSheetId") == idx)?.Value;
        (double X, double Y, double W, double H) area;
        if (pa is not null && pa.Contains('!'))
        {
            var rng = pa[(pa.LastIndexOf('!') + 1)..].Replace("$", "").Split(',')[0];
            area = geo.Rect(rng.Contains(':') ? rng : rng + ":" + rng);
        }
        else throw new CqBuildException("Template không khai báo vùng in (Print_Area)");
        var pm = sheet.Root!.Element(S + "pageMargins");
        double In(string a, double d) => pm?.Attribute(a) is { } v ? double.Parse(v.Value, CultureInfo.InvariantCulture) : d;
        var scale = ((double?)sheet.Root.Element(S + "pageSetup")?.Attribute("scale") ?? 100) / 100.0;
        var hc = (string?)sheet.Root.Element(S + "printOptions")?.Attribute("horizontalCentered") is "1" or "true";
        return new SheetGeometry(area.X, area.Y, area.W, area.H, In("top", 0.75) * 25.4, In("left", 0.7) * 25.4, scale, hc);
    }

    /// <summary>Hình chữ nhật (EMU, toạ độ sheet) mà dấu/chữ ký sẽ chiếm — cùng công thức với lúc chèn thật.</summary>
    public static (double X, double Y, double W, double H) OverlayRectEmu(byte[] xlsx, TemplateMap map, CqOverlay ov) => OverlayBox(SheetGeo(xlsx, map), ov);

    /// <summary>Gốc toạ độ (EMU) của một ô — để đổi vị trí kéo-thả thành "lệch mm" so với ô neo.</summary>
    public static (double X, double Y) CellOriginEmu(byte[] xlsx, TemplateMap map, string cell)
    {
        var g = SheetGeo(xlsx, map); var (c, r) = Geo.Split(cell);
        return (g.ColX(c), g.RowY(r));
    }

    /// <summary>Các DÒNG HIỆN trong vùng in (bỏ dòng ẩn): số dòng, toạ độ đầu dòng và chiều cao (EMU, toạ độ sheet), kèm ô đầu vùng in.
    /// Dùng đặt ô đánh dấu hiệu chỉnh TỪNG DÒNG: Excel in chiều cao dòng theo tỉ lệ khác bề rộng cột (đo trên CQ khuôn: dọc 0.4935, ngang 0.4707
    /// mm in / mm sheet) nên không được suy toạ độ dọc từ tỉ lệ ngang.</summary>
    public static (string FirstCol, List<(int Row, double Y, double H)> Rows) AreaRows(byte[] xlsx, TemplateMap map)
    {
        var g = Geometry(xlsx, map); var geo = SheetGeo(xlsx, map);
        var (col, _, row, _) = geo.ToCell(g.AreaX + 1, g.AreaY + 1);
        var rows = new List<(int, double, double)>();
        for (var r = row; r < 2000; r++)
        {
            var y = geo.RowY(r); if (y >= g.AreaY + g.AreaH - 1) break;
            var h = geo.RowY(r + 1) - y; if (h > 0) rows.Add((r, y, h));
        }
        return (ColName(col), rows);
    }

    /// <summary>Các CỘT có bề rộng trong vùng in: số cột, toạ độ đầu cột, bề rộng (EMU, toạ độ sheet).</summary>
    public static List<(int Col, double X, double W)> AreaCols(byte[] xlsx, TemplateMap map)
    {
        var g = Geometry(xlsx, map); var geo = SheetGeo(xlsx, map);
        var (col, _, _, _) = geo.ToCell(g.AreaX + 1, g.AreaY + 1);
        var cols = new List<(int, double, double)>();
        for (var c = col; c < 500; c++)
        {
            var x = geo.ColX(c); if (x >= g.AreaX + g.AreaW - 1) break;
            var w = geo.ColX(c + 1) - x; if (w > 0) cols.Add((c, x, w));
        }
        return cols;
    }

    /// <summary>Ô chứa một điểm (EMU, toạ độ sheet) + phần lệch trong ô — đúng cách Excel neo ảnh (oneCellAnchor).</summary>
    public static (string Cell, double OffX, double OffY) CellAt(byte[] xlsx, TemplateMap map, double x, double y)
    {
        var geo = SheetGeo(xlsx, map); var (c, cx, r, cy) = geo.ToCell(Math.Max(0, x), Math.Max(0, y));
        return (ColName(c) + r, cx, cy);
    }

    /// <summary>File Excel có chứa đoạn chữ (trong ô / chuỗi dùng chung) — kiểm file sửa tay đúng là CQ cần duyệt.</summary>
    public static bool ContainsText(byte[] xlsx, string text)
    {
        using var ms = new MemoryStream(xlsx);
        using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var r = new StreamReader(e.Open());
            if (System.Net.WebUtility.HtmlDecode(r.ReadToEnd()).Contains(text, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    internal static string ColName(int c) { var s = ""; while (c > 0) { var m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; } return s; }

    private static Geo SheetGeo(byte[] xlsx, TemplateMap map)
    {
        using var ms = new MemoryStream(xlsx);
        using var pkg = SpreadsheetDocument.Open(ms, false);
        var wb = pkg.WorkbookPart!; var wbXml = LoadXml(wb);
        var sheetEl = wbXml.Root!.Element(S + "sheets")!.Elements(S + "sheet").First(x => (string?)x.Attribute("name") == map.Sheet);
        return new Geo(LoadXml((WorksheetPart)wb.GetPartById((string)sheetEl.Attribute(R + "id")!)));
    }

    /// <summary>Chiều cao (mm) mà một ảnh (w×h px) sẽ có KHI IN, nếu là ảnh duy nhất trong khung — dùng để tính cỡ chữ ghi chú
    /// (vd số cân cao ≈ 4 mm trên giấy A4 ở mọi template, vì mỗi template có tỉ lệ in khác nhau: 50% / 56% / 39%).</summary>
    public static double PrintedHeightMm(byte[] templateBytes, TemplateMap map, string frameField, int w, int h)
    {
        using var ms = new MemoryStream(templateBytes);
        using var pkg = SpreadsheetDocument.Open(ms, false);
        var wb = pkg.WorkbookPart!; var wbXml = LoadXml(wb);
        var sheetEl = wbXml.Root!.Element(S + "sheets")!.Elements(S + "sheet").First(x => (string?)x.Attribute("name") == map.Sheet);
        var sheet = LoadXml((WorksheetPart)wb.GetPartById((string)sheetEl.Attribute(R + "id")!));
        var scale = ((double?)sheet.Root!.Element(S + "pageSetup")?.Attribute("scale") ?? 100) / 100.0;
        var geo = new Geo(sheet);
        var box = Layout.Table(map.Ranges(frameField).Take(1).Select(geo.Rect).ToList(), new[] { (w, h) })[0];
        return box.H / 36000.0 * scale;
    }

    // ---------- XML ----------
    private static XDocument LoadXml(OpenXmlPart part) { using var s = part.GetStream(FileMode.Open, FileAccess.Read); return XDocument.Load(s, LoadOptions.PreserveWhitespace); }
    private static void SaveXml(OpenXmlPart part, XDocument x) { using var s = part.GetStream(FileMode.Create, FileAccess.Write); x.Save(s, SaveOptions.DisableFormatting); }

    private static void SetCell(XDocument sheet, string reference, string? value, bool number)
    {
        var (colLetters, row) = SplitRef(reference);
        var sd = sheet.Root!.Element(S + "sheetData")!;
        var rowEl = sd.Elements(S + "row").FirstOrDefault(r => (int?)r.Attribute("r") == row);
        if (rowEl is null)
        {
            rowEl = new XElement(S + "row", new XAttribute("r", row));
            var before = sd.Elements(S + "row").LastOrDefault(r => (int)r.Attribute("r")! < row);
            if (before is null) sd.AddFirst(rowEl); else before.AddAfterSelf(rowEl);
        }
        var c = rowEl.Elements(S + "c").FirstOrDefault(x => (string?)x.Attribute("r") == reference);
        if (c is null)
        {
            c = new XElement(S + "c", new XAttribute("r", reference));
            var before = rowEl.Elements(S + "c").LastOrDefault(x => ColNum(SplitRef((string)x.Attribute("r")!).col) < ColNum(colLetters));
            if (before is null) rowEl.AddFirst(c); else before.AddAfterSelf(c);
        }
        var style = c.Attribute("s");
        c.RemoveNodes();
        foreach (var a in c.Attributes().Where(a => a.Name != "r" && a.Name != "s").ToList()) a.Remove();
        _ = style;
        if (string.IsNullOrEmpty(value)) return;
        if (number) c.Add(new XElement(S + "v", value));
        else
        {
            c.SetAttributeValue("t", "inlineStr");
            c.Add(new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value)));
        }
    }

    private static void MakeBold(XDocument sheet, XDocument styles, IReadOnlyList<string> cells)
    {
        var fonts = styles.Root!.Element(S + "fonts")!; var xfs = styles.Root.Element(S + "cellXfs")!;
        var fontList = fonts.Elements(S + "font").ToList(); var xfList = xfs.Elements(S + "xf").ToList();
        var cache = new Dictionary<int, int>();   // xf cũ → xf đậm mới
        foreach (var r in cells)
        {
            var c = sheet.Descendants(S + "c").FirstOrDefault(x => (string?)x.Attribute("r") == r);
            if (c is null) continue;
            var sIdx = (int?)c.Attribute("s") ?? 0;
            if (!cache.TryGetValue(sIdx, out var newIdx))
            {
                var xf = xfList[sIdx]; var fontId = (int?)xf.Attribute("fontId") ?? 0;
                if (fontList[fontId].Element(S + "b") is not null) { cache[sIdx] = sIdx; continue; }   // đã đậm
                var bold = new XElement(fontList[fontId]);
                bold.AddFirst(new XElement(S + "b"));                                                    // <b/> đứng đầu theo thứ tự lược đồ CT_Font
                fonts.Add(bold); fontList.Add(bold); fonts.SetAttributeValue("count", fontList.Count);
                var nx = new XElement(xf); nx.SetAttributeValue("fontId", fontList.Count - 1); nx.SetAttributeValue("applyFont", 1);
                xfs.Add(nx); xfList.Add(nx); xfs.SetAttributeValue("count", xfList.Count);
                newIdx = xfList.Count - 1; cache[sIdx] = newIdx;
            }
            c.SetAttributeValue("s", newIdx);
        }
    }

    private static void Unhide(XDocument sheet, int row)
    {
        var r = sheet.Root!.Element(S + "sheetData")!.Elements(S + "row").FirstOrDefault(x => (int?)x.Attribute("r") == row);
        r?.Attribute("hidden")?.Remove();
    }

    /// <summary>Chữ nhiệt độ/ram trong hình vẽ: các chữ gốc (đúng thứ tự xuất hiện) nhận giá trị values[order[i]].</summary>
    private static void PlanDrawingTexts(List<string> orig, IReadOnlyList<string> original, IReadOnlyList<int>? order, List<string>? values, string what, Dictionary<int, string> assign)
    {
        if (original.Count == 0 || values is null) return;
        var idx = orig.Select((t, i) => (t, i)).Where(x => original.Contains(x.t)).Select(x => x.i).ToList();
        if (!idx.Select(i => orig[i]).SequenceEqual(original))
            throw new CqBuildException($"Hình vẽ {what} không khớp template map ({string.Join(",", idx.Select(i => orig[i]))}) → template đã bị đổi?");
        if (values.Count != original.Count || values.Any(string.IsNullOrWhiteSpace))
            throw new CqBuildException($"Mác có {values.Count(v => !string.IsNullOrWhiteSpace(v))} giai đoạn {what}, template cần {original.Count} → không tạo CQ");
        if (order is not null && (order.Count != original.Count || order.OrderBy(x => x).Where((x, i) => x != i).Any()))
            throw new CqBuildException($"Bản đồ template: \"order\" của chữ {what} không hợp lệ");
        for (var i = 0; i < idx.Count; i++) assign[idx[i]] = values[order?[i] ?? i];
    }

    private static void Place(DrawingsPart part, XDocument drawing, Geo geo, CqImage img, int idx, (double X, double Y, double W, double H) box)
    {
        var isPng = img.Bytes.Length > 4 && img.Bytes[0] == 0x89 && img.Bytes[1] == 0x50;
        var ip = part.AddImagePart(isPng ? ImagePartType.Png : ImagePartType.Jpeg);
        using (var s = new MemoryStream(img.Bytes)) ip.FeedData(s);
        var rid = part.GetIdOfPart(ip);
        var (col, cx, row, cy) = geo.ToCell(box.X, box.Y);
        var anc = new XElement(Xdr + "oneCellAnchor",
            new XElement(Xdr + "from", new XElement(Xdr + "col", col - 1), new XElement(Xdr + "colOff", (long)cx), new XElement(Xdr + "row", row - 1), new XElement(Xdr + "rowOff", (long)cy)),
            new XElement(Xdr + "ext", new XAttribute("cx", (long)box.W), new XAttribute("cy", (long)box.H)),
            new XElement(Xdr + "pic",
                new XElement(Xdr + "nvPicPr", new XElement(Xdr + "cNvPr", new XAttribute("id", 1000 + idx), new XAttribute("name", img.Name)),
                    new XElement(Xdr + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", "1")))),
                new XElement(Xdr + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", rid)), new XElement(A + "stretch", new XElement(A + "fillRect"))),
                new XElement(Xdr + "spPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", (long)box.W), new XAttribute("cy", (long)box.H))),
                    new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))),
            new XElement(Xdr + "clientData"));
        drawing.Root!.Add(anc);
    }

    // ---------- toạ độ ----------
    internal static (string col, int row) SplitRef(string r) { var m = Regex.Match(r, "^([A-Z]+)(\\d+)$"); return (m.Groups[1].Value, int.Parse(m.Groups[2].Value)); }
    internal static int ColNum(string letters) { var n = 0; foreach (var ch in letters) n = n * 26 + (ch - 'A' + 1); return n; }

    private sealed class Geo
    {
        private readonly Dictionary<int, double> _colPx = new(); private readonly double _defColPx;
        private readonly Dictionary<int, double> _rowPt = new(); private readonly HashSet<int> _hidden = new(); private readonly double _defRowPt;
        public Geo(XDocument sheet)
        {
            var fmt = sheet.Root!.Element(S + "sheetFormatPr");
            double Conv(double w) => Math.Truncate((256 * w + Math.Truncate(128.0 / Mdw)) / 256 * Mdw);
            _defColPx = Conv((double?)fmt?.Attribute("defaultColWidth") ?? (double?)fmt?.Attribute("baseColWidth") ?? 8.43);
            _defRowPt = (double?)fmt?.Attribute("defaultRowHeight") ?? 15;
            foreach (var c in sheet.Root.Element(S + "cols")?.Elements(S + "col") ?? Enumerable.Empty<XElement>())
                for (var i = (int)c.Attribute("min")!; i <= Math.Min((int)c.Attribute("max")!, 200); i++) _colPx[i] = Conv((double)c.Attribute("width")!);
            foreach (var r in sheet.Root.Element(S + "sheetData")!.Elements(S + "row"))
            {
                var n = (int)r.Attribute("r")!;
                if (r.Attribute("ht") is { } ht) _rowPt[n] = double.Parse(ht.Value, CultureInfo.InvariantCulture);
                if ((string?)r.Attribute("hidden") == "1") _hidden.Add(n);
            }
        }
        double ColEmu(int c) => (_colPx.TryGetValue(c, out var p) ? p : _defColPx) * 9525;
        double RowEmu(int r) => _hidden.Contains(r) ? 0 : (_rowPt.TryGetValue(r, out var p) ? p : _defRowPt) * 12700;
        public double ColX(int c) { double x = 0; for (var i = 1; i < c; i++) x += ColEmu(i); return x; }
        public double RowY(int r) { double y = 0; for (var i = 1; i < r; i++) y += RowEmu(i); return y; }
        public static (int col, int row) Split(string cell) { var (l, r) = SplitRef(cell); return (ColNum(l), r); }
        public (double X, double Y, double W, double H) Rect(string range)
        {
            var p = range.Split(':'); var (c1, r1) = Split(p[0]); var (c2, r2) = Split(p[1]);
            double x0 = ColX(c1), y0 = RowY(r1);
            return (x0, y0, ColX(c2 + 1) - x0, RowY(r2 + 1) - y0);
        }
        public (int col, double cx, int row, double cy) ToCell(double x, double y)
        {
            int col = 1; while (x >= ColEmu(col)) { x -= ColEmu(col); col++; }
            int row = 1; while (RowEmu(row) == 0 || y >= RowEmu(row)) { y -= RowEmu(row); row++; }
            return (col, x, row, y);
        }
    }
}

/// <summary>Bảng ô đều nhau trải qua các khung đặt cạnh nhau; đọc trái→phải (hết khung 1 sang khung 2), trên→dưới.</summary>
public static class Layout
{
    public static IReadOnlyList<(double X, double Y, double W, double H)> Table(IReadOnlyList<(double X, double Y, double W, double H)> rects,
        IReadOnlyList<(int W, int H)> sizes, double pad = 0.04, double gap = 0.04, bool cells = false)
    {
        var n = sizes.Count; var F = rects.Count;
        if (n == 0) return Array.Empty<(double, double, double, double)>();
        (double sc, int cpf, int rows, double cw, double ch) best = (-1, 1, 1, 0, 0);
        for (var cpf = 1; cpf <= n; cpf++)
        {
            var rows = (int)Math.Ceiling(n / (double)(cpf * F));
            var cw = rects.Min(r => (r.W * (1 - 2 * pad) - (cpf - 1) * r.W * gap) / cpf);
            var ch = rects.Min(r => (r.H * (1 - 2 * pad) - (rows - 1) * r.H * gap) / rows);
            var sc = sizes.Min(s => Math.Min(cw / s.W, ch / s.H));
            if (sc > best.sc + 1e-9) best = (sc, cpf, rows, cw, ch);
        }
        var outList = new List<(double, double, double, double)>();
        for (var i = 0; i < n; i++)
        {
            var r = i / (best.cpf * F); var c = i % (best.cpf * F); var fi = c / best.cpf; var cc = c % best.cpf;
            var (X, Y, W, H) = rects[fi]; double gx = W * gap, gy = H * gap;
            var bx = X + (W - (best.cpf * best.cw + (best.cpf - 1) * gx)) / 2 + cc * (best.cw + gx);
            var by = Y + (H - (best.rows * best.ch + (best.rows - 1) * gy)) / 2 + r * (best.ch + gy);
            if (cells) { outList.Add((bx, by, best.cw, best.ch)); continue; }
            var s = Math.Min(best.cw / sizes[i].W, best.ch / sizes[i].H); double w = sizes[i].W * s, h = sizes[i].H * s;
            outList.Add((bx + (best.cw - w) / 2, by + (best.ch - h) / 2, w, h));
        }
        return outList;
    }
}
