using System.Text.Json;
using System.Text.Json.Nodes;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Storage;

namespace CqNl.Application;

/// <summary>Template CQ lưu trong kho chung (dùng cho mọi máy). Template mặc định (cài kèm) chỉ lưu BẢN ĐỒ Ô sửa đổi (nếu có);
/// file Excel gốc cài kèm không bao giờ bị sửa. Template thêm mới: lưu file Excel (theo hash) + bản đồ ô.</summary>
public sealed class TemplateRecord
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool BuiltIn { get; set; }
    public string FileName { get; set; } = "";
    public string? XlsxSha { get; set; }          // null với template mặc định
    public string? MapJson { get; set; }          // null = dùng bản đồ mặc định cài kèm
    public bool Active { get; set; } = true;
    public string? BasedOn { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record TemplateInfo(string Id, string DisplayName, bool BuiltIn, bool MapOverridden, string FileName, string Source, bool Active, string Status)
{
    public string KindText => BuiltIn ? (MapOverridden ? "Mặc định (đã sửa bản đồ)" : "Mặc định") : "Thêm mới";
}

/// <summary>Danh mục template: mặc định (cài kèm, chỉ đọc file) + thêm mới (kho chung). Kiểm tra bản đồ trước khi lưu.</summary>
public sealed class TemplateCatalog(Repo repo, AuditLog audit, AppPaths paths)
{
    public const string T = "template";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private IEnumerable<TemplateRecord> Records() => repo.Ids(T).Select(id => repo.Get<TemplateRecord>(T, id).Value).Where(r => r is not null).Select(r => r!);

    public IReadOnlyList<TemplateInfo> List(bool includeInactive = false)
    {
        var recs = Records().ToDictionary(r => r.Id);
        var list = new List<TemplateInfo>();
        foreach (var id in paths.TemplateIds())
        {
            var m = TemplateMap.Load(paths.TemplateMap(id));
            recs.TryGetValue(id, out var ov);
            var file = paths.Template(m.FileName);
            list.Add(new TemplateInfo(id, string.IsNullOrEmpty(m.DisplayName) ? id : m.DisplayName, true, ov?.MapJson is not null, m.FileName, "Cài kèm ứng dụng (file gốc, chỉ đọc)",
                true, File.Exists(file) ? "Sẵn sàng" : "THIẾU FILE"));
        }
        foreach (var r in recs.Values.Where(r => !r.BuiltIn && (includeInactive || r.Active)).OrderBy(r => r.DisplayName))
            list.Add(new TemplateInfo(r.Id, r.DisplayName, false, true, r.FileName, "Kho chung (" + r.XlsxSha?[..12] + ")", r.Active, r.Active ? "Sẵn sàng" : "Đã ngừng dùng"));
        return list;
    }

    /// <summary>Bản đồ + file Excel để dựng CQ.</summary>
    public (TemplateMap Map, byte[] Xlsx) Resolve(string id)
    {
        var (rec, _) = repo.Get<TemplateRecord>(T, id);
        if (paths.TemplateIds().Contains(id))
        {
            var map = rec?.MapJson is { } j ? TemplateMap.Parse(j) : TemplateMap.Load(paths.TemplateMap(id));
            return (map, File.ReadAllBytes(paths.Template(map.FileName)));
        }
        if (rec is null) throw new BusinessException($"Không tìm thấy template '{id}'");
        if (!rec.Active) throw new BusinessException($"Template '{rec.DisplayName}' đã ngừng dùng");
        return (TemplateMap.Parse(rec.MapJson!), repo.Store.GetBlob(rec.XlsxSha!, "xlsx"));
    }

    public string MapJson(string id) => Resolve(id).Map.Json;
    public byte[] Xlsx(string id) => Resolve(id).Xlsx;

    public static List<TemplateIssue> Check(byte[] xlsx, string mapJson)
    {
        TemplateMap map;
        try { map = TemplateMap.Parse(mapJson); } catch (Exception e) { return new() { new(true, "json", "Bản đồ ô không đọc được: " + e.Message) }; }
        List<TemplateIssue> issues;
        try { issues = new TemplateInspector(xlsx, map.Sheet).Validate(map); }
        catch (Exception e) { return new() { new(true, "xlsx", "File Excel không đọc được: " + e.Message) }; }
        if (issues.Any(i => i.Error)) return issues;
        try { CqWriter.Build(xlsx, map, SampleData(map)); }                       // dựng thử bằng dữ liệu mẫu
        catch (Exception e) { issues.Add(new(true, "build", "Dựng CQ thử bị lỗi: " + e.Message)); }
        return issues;
    }

    /// <summary>Dữ liệu mẫu để dựng thử / xem trước template.</summary>
    public static CqData SampleData(TemplateMap map)
    {
        var d = new CqData();
        foreach (var (k, v) in new Dictionary<string, string>
        {
            ["Customer"] = "CÔNG TY MẪU", ["Material"] = "SKD11", ["Size"] = "100x50x20", ["Dngc"] = "DNGC/MAU-0001", ["ReportNo"] = "000/00",
            ["ReceivedDate"] = "01/01/2026", ["CheckDate"] = "02/01/2026", ["HardnessRequirement"] = "58-62", ["HardnessUnit"] = "(HRC)",
            ["HardnessTolerance"] = "-", ["AppearanceResult"] = "-", ["CurvatureRequirement"] = "-", ["CurvatureResult"] = "-", ["OtherResult"] = "-",
            ["Remark"] = "Nhận xét/Remark: Chất lượng đạt",
        }) d.Values[k] = v;
        d.Numbers["Quantity"] = 1; d.Numbers["WeightKg"] = 1.5m;
        d.Samples.AddRange(new[] { "59", "60", "61" });
        var temps = map.Texts("DrawingTemperatures");
        if (temps.Count > 0) d.QuenchTemps = temps.ToList();
        else if (map.Cell("QuenchTemp1") is not null) d.QuenchTemps = new() { "650℃", "1030℃" };
        var rams = map.Texts("DrawingRamTexts");
        if (rams.Count > 0) d.RamValues = rams.ToList();
        else if (map.Cells("RamCells").Count > 0) d.RamValues = map.Cells("RamCells").Select(_ => "180p").ToList();
        foreach (var mf in map.Manual) d.Manual[mf.Key] = mf.Text ?? (string.IsNullOrWhiteSpace(mf.Default) || mf.Default.Contains('{') ? "(mẫu)" : mf.Default);
        return d;
    }

    /// <summary>Thêm template mới (file Excel + bản đồ). Bắt buộc không có lỗi kiểm tra.</summary>
    public TemplateRecord AddCustom(Session s, string displayName, string fileName, byte[] xlsx, string mapJson, string? basedOn)
    {
        AuthService.Require(s, Role.Admin);
        if (string.IsNullOrWhiteSpace(displayName)) throw new BusinessException("Nhập tên template");
        if (List(true).Any(t => string.Equals(t.DisplayName, displayName.Trim(), StringComparison.OrdinalIgnoreCase) && t.Active))
            throw new BusinessException("Đã có template cùng tên");
        var id = "tpl-" + Guid.NewGuid().ToString("N")[..8];
        var node = JsonNode.Parse(mapJson)!.AsObject(); node["templateId"] = id; node["displayName"] = displayName.Trim(); node["fileName"] = fileName;
        var json = node.ToJsonString(Pretty);
        var errs = Check(xlsx, json).Where(i => i.Error).ToList();
        if (errs.Count > 0) throw new BusinessException("Bản đồ ô còn lỗi:\n• " + string.Join("\n• ", errs.Select(e => e.Message)));
        var rec = new TemplateRecord
        {
            Id = id, DisplayName = displayName.Trim(), FileName = fileName, XlsxSha = repo.Store.PutBlob(xlsx, "xlsx"), MapJson = json,
            BasedOn = basedOn, UpdatedBy = s.User.Username, UpdatedAt = DateTimeOffset.Now,
        };
        repo.Put(T, id, rec, 0, s.User.Username);
        audit.Write(s.User.Username, "template.add", id, new { rec.DisplayName, fileName, rec.XlsxSha, basedOn });
        return rec;
    }

    /// <summary>Sửa bản đồ ô (template mặc định: lưu bản sửa; template thêm mới: thay bản đồ). CQ đã tạo không bị ảnh hưởng (lưu bản đồ riêng).</summary>
    public void SaveMap(Session s, string id, string mapJson)
    {
        AuthService.Require(s, Role.Admin);
        var (_, xlsx) = Resolve(id);
        var builtIn = paths.TemplateIds().Contains(id);
        var node = JsonNode.Parse(mapJson)!.AsObject(); node["templateId"] = id;
        var json = node.ToJsonString(Pretty);
        var errs = Check(xlsx, json).Where(i => i.Error).ToList();
        if (errs.Count > 0) throw new BusinessException("Bản đồ ô còn lỗi:\n• " + string.Join("\n• ", errs.Select(e => e.Message)));
        var (rec, v) = repo.Get<TemplateRecord>(T, id);
        if (rec is null)
        {
            if (!builtIn) throw new BusinessException("Không tìm thấy template");
            var m = TemplateMap.Load(paths.TemplateMap(id));
            rec = new TemplateRecord { Id = id, BuiltIn = true, DisplayName = m.DisplayName, FileName = m.FileName };
        }
        rec.MapJson = json; rec.UpdatedBy = s.User.Username; rec.UpdatedAt = DateTimeOffset.Now;
        repo.Put(T, id, rec, v, s.User.Username);
        audit.Write(s.User.Username, "template.map", id, new { rec.DisplayName });
    }

    public void ResetBuiltIn(Session s, string id)
    {
        AuthService.Require(s, Role.Admin);
        var (rec, v) = repo.Get<TemplateRecord>(T, id);
        if (rec is null || !rec.BuiltIn) return;
        rec.MapJson = null; rec.UpdatedBy = s.User.Username; rec.UpdatedAt = DateTimeOffset.Now;
        repo.Put(T, id, rec, v, s.User.Username);
        audit.Write(s.User.Username, "template.reset", id);
    }

    /// <summary>Ngừng dùng template thêm mới (không xóa file — CQ cũ vẫn mở được).</summary>
    public void Deactivate(Session s, string id)
    {
        AuthService.Require(s, Role.Admin);
        if (paths.TemplateIds().Contains(id)) throw new BusinessException("Không xóa được template mặc định");
        var (rec, v) = repo.Get<TemplateRecord>(T, id);
        if (rec is null) throw new BusinessException("Không tìm thấy template");
        rec.Active = false; rec.UpdatedBy = s.User.Username; rec.UpdatedAt = DateTimeOffset.Now;
        repo.Put(T, id, rec, v, s.User.Username);
        audit.Write(s.User.Username, "template.deactivate", id, new { rec.DisplayName });
    }
}
