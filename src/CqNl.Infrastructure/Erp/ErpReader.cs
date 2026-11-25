using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CqNl.Domain;
using CqNl.Infrastructure.Excel;

namespace CqNl.Infrastructure.Erp;

/// <summary>Một dòng ERP MF0015 — CHỈ các trường thuộc ERP. Khóa = Dngc + LineNo.</summary>
public sealed record ErpLine
{
    public required string Dngc { get; init; }
    public required int LineNo { get; init; }
    public string? ReceivedAt { get; init; }
    public string? SalesOrderNo { get; init; }
    public string? Customer { get; init; }
    public string? ProductCode { get; init; }
    public string? ProductName { get; init; }
    public string? HardnessRequirementErp { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? WeightKg { get; init; }
    public string? ProcessMethod { get; init; }
    public string? Stage { get; init; }
    public string? GradeFromErp { get; init; }
    /// <summary>Cột "Ghi chú" (đầu tiên) của MF0015.</summary>
    public string? Note { get; init; }
    public int SourceRow { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string Key => $"{Dngc}|{LineNo}";

    /// <summary>Các trường ERP để so sánh khi cập nhật (không gồm khóa, dòng nguồn, cảnh báo).</summary>
    public IReadOnlyDictionary<string, string?> ErpFields() => new Dictionary<string, string?>
    {
        ["ReceivedAt"] = ReceivedAt, ["SalesOrderNo"] = SalesOrderNo, ["Customer"] = Customer, ["ProductCode"] = ProductCode,
        ["ProductName"] = ProductName, ["HardnessRequirementErp"] = HardnessRequirementErp,
        ["Quantity"] = Quantity?.ToString(CultureInfo.InvariantCulture), ["WeightKg"] = WeightKg?.ToString(CultureInfo.InvariantCulture),
        ["ProcessMethod"] = ProcessMethod, ["Stage"] = Stage, ["GradeFromErp"] = GradeFromErp, ["Note"] = Note,
    };
}

public sealed record ErpReadResult(IReadOnlyList<ErpLine> Lines, IReadOnlyList<(int Row, string? Dngc, string Reason)> Rejected);

public static class ErpReader
{
    private static readonly string[] Required = { "Số ĐNGC", "Dòng", "Khách hàng", "Sản phẩm", "Độ cứng", "Số lượng", "Trọng lượng" };

    public static ErpReadResult Read(string path)
    {
        var head = new byte[512];
        using (var fs = File.OpenRead(path)) fs.Read(head, 0, head.Length);
        var s = Encoding.UTF8.GetString(head).ToLowerInvariant();
        var rows = s.Contains("<html") || s.Contains("<table") ? ReadHtml(path) : ReadXlsx(path);
        return Parse(rows);
    }

    // Lưới chung: mỗi ô là (text, number?, date?)
    private sealed record G(string? Text, decimal? Num, DateTime? Date);

    private static List<G?[]> ReadXlsx(string path) => XlsxReader.ReadSheet(path).Select(r => r.Select(c => c is null ? null :
        new G(c.Text, c.Number is { } n && !c.IsDate ? (decimal?)Convert.ToDecimal(n) : null, c.Date)).ToArray()).ToList();

    private static List<G?[]> ReadHtml(string path)
    {
        var txt = File.ReadAllText(path, Encoding.UTF8);
        var rows = new List<G?[]>();
        foreach (Match tr in Regex.Matches(txt, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var cells = new List<G?>();
            foreach (Match td in Regex.Matches(tr.Groups[1].Value, "<t([dh])([^>]*)>(.*?)</t\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var val = WebUtility.HtmlDecode(Regex.Replace(td.Groups[3].Value, "<[^>]+>", "")).Replace(' ', ' ').Trim();
                cells.Add(val.Length == 0 ? null : new G(val, null, null));
                var span = Regex.Match(td.Groups[2].Value, "colspan=\"?(\\d+)");
                if (span.Success) for (var k = 1; k < int.Parse(span.Groups[1].Value); k++) cells.Add(null);
            }
            rows.Add(cells.ToArray());
        }
        return rows;
    }

    private static string? Str(G? g) => g?.Text?.Trim() ?? g?.Num?.ToString(CultureInfo.InvariantCulture);

    private static string? NormDate(G? g)
    {
        if (g?.Date is { } d0)
        {   // số ngày Excel (OADate) có sai số mili-giây → làm tròn về giây để so khớp với bản HTML
            var d = new DateTime((d0.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
            return d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        var t = g?.Text?.Trim().TrimStart('\'');
        if (string.IsNullOrEmpty(t)) return null;
        var m = Regex.Match(t, @"^(\d{2})/(\d{2})/(\d{4})(?: (\d{2}):(\d{2}):(\d{2}))?");
        return m.Success ? $"{m.Groups[3].Value}-{m.Groups[2].Value}-{m.Groups[1].Value} {(m.Groups[4].Success ? m.Groups[4].Value : "00")}:{(m.Groups[5].Success ? m.Groups[5].Value : "00")}:{(m.Groups[6].Success ? m.Groups[6].Value : "00")}" : null;
    }

    private static (decimal? v, bool err) Dec(G? g)
    {
        if (g?.Num is { } n) return (n, false);
        var t = g?.Text?.Trim();
        if (string.IsNullOrEmpty(t) || t == "'") return (null, false);
        return decimal.TryParse(t.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (d, false) : (null, true);
    }

    private static ErpReadResult Parse(List<G?[]> rows)
    {
        int hi = rows.FindIndex(r => r.Any(c => Str(c) == "Số ĐNGC") && r.Any(c => Str(c) == "Dòng"));
        if (hi < 0) throw new InvalidDataException("Không phải báo cáo MF0015 (không thấy cột Số ĐNGC/Dòng)");
        var header = rows[hi].Select(Str).ToList();
        int Col(string n) => header.IndexOf(n);                       // lần xuất hiện đầu ('Ghi chú' có 2 cột)
        var missing = Required.Where(n => Col(n) < 0).ToList();
        if (missing.Count > 0) throw new InvalidDataException("Thiếu cột bắt buộc: " + string.Join(", ", missing));
        G? Get(G?[] r, string n) { var i = Col(n); return i >= 0 && i < r.Length ? r[i] : null; }

        var lines = new List<ErpLine>(); var rejected = new List<(int, string?, string)>();
        for (var i = hi + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            var dngc = Str(Get(r, "Số ĐNGC"));
            if (string.IsNullOrEmpty(dngc) || !dngc.StartsWith("DNGC", StringComparison.Ordinal)) continue;
            var (line, lerr) = Dec(Get(r, "Dòng"));
            if (line is null || lerr) { rejected.Add((i + 1, dngc, "Dòng không hợp lệ")); continue; }
            var w = new List<string>();
            var hg = Get(r, "Độ cứng"); string? hard = null;
            if (hg?.Date is { } hd) w.Add($"Độ cứng bị Excel đổi thành ngày ({hd:dd/MM/yyyy}) → nhập tay");
            else
            {
                hard = Str(hg);
                if (hard is not null && HardnessRequirement.TryParse(hard) is null) w.Add($"Độ cứng \"{hard}\" không hợp lệ → nhập tay");
            }
            var (q, qe) = Dec(Get(r, "Số lượng")); if (qe) w.Add("Số lượng không phải số");
            var (kg, ke) = Dec(Get(r, "Trọng lượng")); if (ke) w.Add("Trọng lượng không phải số");
            var product = Str(Get(r, "Sản phẩm"));
            var gm = Regex.Match(product ?? "", @"^[A-Z]+_(.+?)\s+SP$");
            var grade = gm.Success ? gm.Groups[1].Value.Trim() : null;
            if (grade is null) w.Add("Không lấy được mác từ mã sản phẩm");
            lines.Add(new ErpLine
            {
                Dngc = dngc.Trim(), LineNo = (int)line.Value, SourceRow = i + 1, ReceivedAt = NormDate(Get(r, "Ngày nhận ĐNGC")),
                SalesOrderNo = Str(Get(r, "Số đơn hàng")), Customer = Str(Get(r, "Khách hàng")), ProductCode = product,
                ProductName = Str(Get(r, "Tên sản phẩm")), HardnessRequirementErp = hard, Quantity = q, WeightKg = kg,
                ProcessMethod = Str(Get(r, "Phương thức gia công")), Stage = Str(Get(r, "Công đoạn")), GradeFromErp = grade, Note = Str(Get(r, "Ghi chú")), Warnings = w,
            });
        }
        var dup = lines.GroupBy(l => l.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var l in lines.Where(l => dup.Contains(l.Key))) rejected.Add((l.SourceRow, l.Dngc, "Trùng khóa ĐNGC+Dòng trong cùng file"));
        return new ErpReadResult(lines.Where(l => !dup.Contains(l.Key)).ToList(), rejected);
    }
}

public sealed record ImportPlan(IReadOnlyList<ErpLine> Add, IReadOnlyList<(ErpLine Line, IReadOnlyDictionary<string, (string? Old, string? New)> Changes)> Update, IReadOnlyList<ErpLine> Same);

public static class ErpImportPlanner
{
    /// <summary>So sánh với trường ERP đang có (current: key → trường ERP). Không đụng dữ liệu QC.</summary>
    public static ImportPlan Plan(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> current, IEnumerable<ErpLine> lines)
    {
        var add = new List<ErpLine>(); var upd = new List<(ErpLine, IReadOnlyDictionary<string, (string?, string?)>)>(); var same = new List<ErpLine>();
        foreach (var l in lines)
        {
            if (!current.TryGetValue(l.Key, out var cur)) { add.Add(l); continue; }
            var ch = l.ErpFields().Where(kv => !Equals(cur.GetValueOrDefault(kv.Key), kv.Value))
                .ToDictionary(kv => kv.Key, kv => (cur.GetValueOrDefault(kv.Key), kv.Value));
            if (ch.Count > 0) upd.Add((l, ch)); else same.Add(l);
        }
        return new ImportPlan(add, upd, same);
    }
}
