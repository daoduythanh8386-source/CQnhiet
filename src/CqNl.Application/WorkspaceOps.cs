using System.Text.Json;
using System.Text.Json.Nodes;
using CqNl.Domain;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Storage;

namespace CqNl.Application;

public sealed record ErpFileInfo(string Path, string Name, string Sha, DateTime Modified, int Lines, string? Error);
public sealed record ErpFolderScan(string Folder, IReadOnlyList<ErpFileInfo> NewFiles, ErpReadResult Combined, ImportPlan Plan);

public sealed class ErpFileRecord
{
    public string Sha { get; set; } = "";
    public string Name { get; set; } = "";
    public string By { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
}

public sealed partial class Workspace
{
    // ---------------- Đồng bộ ERP từ thư mục theo dõi ----------------
    public const string ErpFileType = "erpfile";

    private string ErpFolder() => Settings!.ErpFolderPath() is { Length: > 0 } f ? f
        : throw new BusinessException("Chưa cài \"Thư mục ERP theo dõi\" (Cài đặt → Nguồn dữ liệu ERP)");

    private static IEnumerable<string> ErpCandidates(string folder) =>
        Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("~$"));

    /// <summary>Số file ERP mới (chưa nhập) trong thư mục theo dõi — dùng cho thông báo "Có file ERP mới".</summary>
    public int CountNewErpFiles()
    {
        if (!Online) return 0;
        var folder = Settings!.ErpFolderPath();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
        var done = Repo!.Ids(ErpFileType).ToHashSet();
        return ErpCandidates(folder).Count(f => { try { return !done.Contains(ErpSha(f)); } catch { return false; } });
    }

    /// <summary>Quét thư mục ERP: đọc các file CHƯA nhập (theo mã hash, đổi tên file không nhập lại), gộp theo thứ tự thời gian
    /// (file mới hơn ghi đè dòng trùng), lập kế hoạch import để xem trước. Không ghi gì.</summary>
    public ErpFolderScan ScanErpFolder()
    {
        RequireOnline("đồng bộ ERP");
        var folder = ErpFolder();
        if (!Directory.Exists(folder)) throw new BusinessException("Không truy cập được thư mục ERP: " + folder);
        var done = Repo!.Ids(ErpFileType).ToHashSet();
        var files = new List<ErpFileInfo>();
        var lines = new Dictionary<string, ErpLine>(); var rejected = new List<(int, string?, string)>();
        foreach (var f in ErpCandidates(folder).OrderBy(File.GetLastWriteTime))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(f); } catch (IOException e) { files.Add(new(f, Path.GetFileName(f), "", File.GetLastWriteTime(f), 0, "Đang mở/khóa: " + e.Message)); continue; }
            var sha = FileStore.Sha256Hex(bytes);
            if (done.Contains(sha)) continue;
            try
            {
                var r = ErpReader.Read(f);
                foreach (var l in r.Lines) lines[l.Key] = l;
                rejected.AddRange(r.Rejected.Select(x => (x.Row, x.Dngc, $"{Path.GetFileName(f)}: {x.Reason}")));
                files.Add(new(f, Path.GetFileName(f), sha, File.GetLastWriteTime(f), r.Lines.Count, null));
            }
            catch (Exception e) { files.Add(new(f, Path.GetFileName(f), sha, File.GetLastWriteTime(f), 0, "Không phải file MF0015 hợp lệ: " + e.Message)); }
        }
        var combined = new ErpReadResult(lines.Values.ToList(), rejected);
        return new ErpFolderScan(folder, files, combined, Orders!.Preview(combined));
    }

    public (int Added, int Updated, int Conflicts) ApplyErpScan(ErpFolderScan scan)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        RequireOnline("đồng bộ ERP");
        var ok = scan.NewFiles.Where(f => f.Error is null).ToList();
        var r = Orders!.Apply(s, scan.Plan, $"Đồng bộ ERP ({ok.Count} file): {string.Join(", ", ok.Select(f => f.Name))}", Cq!.HasActiveCq);
        foreach (var f in ok)
            try { Repo!.Put(ErpFileType, f.Sha, new ErpFileRecord { Sha = f.Sha, Name = f.Name, By = s.User.Username, At = DateTimeOffset.Now, Added = r.Added, Updated = r.Updated }, 0, s.User.Username); }
            catch (ConflictException) { /* máy khác vừa ghi nhận cùng file */ }
        return r;
    }

    // ---------------- Gộp / tách dòng đơn ----------------
    /// <summary>Gộp nhiều dòng cùng ĐNGC + cùng mác + cùng yêu cầu độ cứng, chưa có CQ → một CQ ở dòng đầu (SL, KL cộng dồn).</summary>
    public string MergeOrders(IReadOnlyList<string> keys)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        RequireOnline("gộp đơn");
        var rows = keys.Distinct().Select(k => GetOrder(k) ?? throw new BusinessException("Không tìm thấy đơn " + k)).ToList();
        var cands = rows.Select(r =>
        {
            var p = r.Order.Key.Split('|');
            return new MergeCandidate(p[0], int.TryParse(p.ElementAtOrDefault(1), out var n) ? n : 0, r.Order.Qc.SteelGrade ?? r.Order.Erp.GetValueOrDefault("GradeFromErp"),
                r.Order.Qc.HardnessRequirement ?? r.Order.Erp.GetValueOrDefault("HardnessRequirementErp"), Cq!.HasActiveCq(r.Order.Key), r.Order.Qc.MergeGroup);
        }).ToList();
        var (ok, reason) = MergeRules.CanMerge(cands);
        if (!ok) throw new BusinessException("Không gộp được: " + reason);
        var ordered = rows.OrderBy(r => int.TryParse(r.Order.Key.Split('|').ElementAtOrDefault(1), out var n) ? n : 0).ToList();
        var parent = ordered[0].Order.Key; var children = ordered.Skip(1).Select(r => r.Order.Key).ToList();
        foreach (var c in ordered.Skip(1)) Orders!.EditQc(s, c.Order.Key, c.Version, q => q.MergeGroup = parent, "merge.child " + parent);
        Orders!.EditQc(s, parent, ordered[0].Version, q => { q.MergeGroup = parent; q.MergedChildren = children; }, "merge.parent");
        Audit!.Write(s.User.Username, "order.merge", parent, new { children });
        foreach (var k in keys) GetOrder(k);
        return parent;
    }

    public void SplitOrders(string key)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        RequireOnline("tách đơn gộp");
        var row = GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn");
        var parent = row.Order.Qc.MergeGroup ?? throw new BusinessException("Dòng này không thuộc nhóm gộp nào");
        if (Cq!.HasActiveCq(parent)) throw new BusinessException("Nhóm gộp đã có CQ — xóa CQ (có lý do) trước khi tách");
        var p = GetOrder(parent) ?? throw new BusinessException("Không tìm thấy dòng chính " + parent);
        foreach (var c in p.Order.Qc.MergedChildren ?? new())
            if (GetOrder(c) is { } cr && cr.Order.Qc.MergeGroup == parent) Orders!.EditQc(s, c, cr.Version, q => q.MergeGroup = null, "split.child");
        Orders!.EditQc(s, parent, p.Version, q => { q.MergeGroup = null; q.MergedChildren = null; }, "split.parent");
        Audit!.Write(s.User.Username, "order.split", parent, new { children = p.Order.Qc.MergedChildren });
        GetOrder(key);
    }

    // ---------------- Danh mục template (có bản sao để chọn khi offline) ----------------
    public IReadOnlyList<TemplateInfo> TemplateList()
    {
        if (Online)
        {
            var l = Cq!.Templates.List().ToList();
            Cache.Put("meta", "templates", l, 0, false);
            return l;
        }
        return Cache.Get<List<TemplateInfo>>("meta", "templates")?.Value ?? new List<TemplateInfo>();
    }
}

/// <summary>Diễn giải nhật ký thao tác sang tiếng Việt dễ đọc (thay cho JSON thô).</summary>
public static class AuditText
{
    private static readonly Dictionary<string, string> Actions = new()
    {
        ["login.ok"] = "Đăng nhập", ["login.fail"] = "Đăng nhập sai mật khẩu", ["login.unknown"] = "Đăng nhập bằng tài khoản không tồn tại", ["login.locked"] = "Đăng nhập bằng tài khoản bị khóa",
        ["login.denied_other_session"] = "Từ chối đăng nhập (đang mở ở máy khác)", ["login.offline_resumed"] = "Kết nối lại sau khi đăng nhập offline",
        ["login.offline_overlap"] = "Cảnh báo trùng phiên sau offline", ["logout"] = "Đăng xuất", ["password.change"] = "Đổi mật khẩu",
        ["user.first_admin"] = "Tạo quản trị đầu tiên", ["user.create"] = "Tạo tài khoản", ["user.update"] = "Sửa tài khoản", ["image.export"] = "Tải ảnh xuống", ["user.delete"] = "Xoá tài khoản", ["data.reset"] = "Reset dữ liệu", ["store.move"] = "Chuyển kho", ["signer.rename"] = "Đổi tên người ký", ["signer.remove"] = "Bỏ chữ ký",
        ["erp.import"] = "Import ERP", ["order.edit"] = "Sửa đơn", ["order.merge"] = "Gộp dòng đơn", ["order.split"] = "Tách nhóm gộp",
        ["hardness.confirm"] = "Xác nhận kết quả đo", ["cq.create"] = "Tạo CQ", ["cq.approve"] = "Duyệt CQ", ["cq.printed"] = "Đánh dấu đã in",
        ["cq.unprinted"] = "Bỏ đánh dấu in", ["cq.delete"] = "Xóa CQ", ["note.add"] = "Ghi lưu ý", ["note.hide"] = "Ẩn lưu ý",
        ["signer.add"] = "Thêm/thay chữ ký", ["signer.defaults"] = "Đặt người ký mặc định", ["stamp.set"] = "Cập nhật con dấu",
        ["steel.set"] = "Cập nhật bảng mác thép", ["placement.set"] = "Lưu vị trí dấu/chữ ký mặc định", ["sync"] = "Đồng bộ nháp offline",
        ["conflict.keep_mine"] = "Xử lý xung đột: giữ bản của tôi", ["conflict.keep_store"] = "Xử lý xung đột: giữ bản trên kho",
        ["lock.takeover"] = "Thu hồi khóa đơn quá hạn", ["backup"] = "Sao lưu", ["restore"] = "Khôi phục", ["report.export"] = "Xuất báo cáo Excel",
        ["template.add"] = "Thêm template", ["template.map"] = "Sửa bản đồ ô template", ["template.reset"] = "Khôi phục bản đồ mặc định",
        ["template.deactivate"] = "Ngừng dùng template", ["erp.folder"] = "Đặt thư mục ERP theo dõi",
        ["user.recovery"] = "Khôi phục mật khẩu quản trị (trên máy chứa kho)", ["login.lockout"] = "Tạm khoá do nhập sai mật khẩu", ["login.temp_locked"] = "Đăng nhập khi đang tạm khoá", ["cq.issue"] = "Xuất bản in CQ", ["cq.excel"] = "Xuất file Excel CQ để chỉnh sửa",
        ["customer.alias"] = "Sửa tên viết tắt khách hàng", ["customer.alias.import"] = "Nhập tên viết tắt khách hàng", ["release.publish"] = "Phát hành bản cập nhật", ["release.install"] = "Cài bản cập nhật",
    };

    public static string Action(string a) => Actions.TryGetValue(a, out var v) ? v : a;

    private static string? S(JsonNode? d, string k) => d is JsonObject o && o.TryGetPropertyValue(k, out var v) && v is not null ? v.ToString() : null;

    /// <summary>Mô tả thao tác sửa đơn (mã nội bộ như "image.confirm &lt;id&gt; = 47.4") thành câu dễ đọc.</summary>
    public static string EditWhat(string w)
    {
        static string Kind(string k) => k switch { "Hardness" => "ảnh đo độ cứng", "Sample" => "ảnh sản phẩm", "Other" => "ảnh kim tương / khác", _ => "ảnh " + k };
        var p = w.Split(' ', 2); var rest = p.Length > 1 ? p[1] : "";
        return p[0] switch
        {
            "image.add" => "Thêm " + Kind(rest.Split(' ')[0]),
            "image.confirm" => "Xác nhận số đọc từ ảnh" + (rest.Contains('=') ? ": " + rest[(rest.IndexOf('=') + 1)..].Trim() : ""),
            "image.crop" => "Cắt/chỉnh ảnh",
            "image.reprocess" => "Xử lý lại ảnh (từ ảnh gốc)",
            "image.kind" => rest.EndsWith("Hardness") ? "Đổi loại ảnh → ảnh đo độ cứng" : rest.EndsWith("Other") ? "Đổi loại ảnh → ảnh kim tương / khác" : "Đổi loại ảnh → ảnh sản phẩm",
            "hardness.import" => "Nhập điểm đo từ file",
            "image.oncq" => rest.EndsWith("=True", StringComparison.OrdinalIgnoreCase) ? "Đưa ảnh lên CQ" : "Bỏ ảnh khỏi CQ",
            "image.remove" => "Gỡ ảnh (vẫn lưu trong kho)",
            "image.restore" => "Khôi phục ảnh đã gỡ",
            "hardness.save" => "Lưu kết quả đo độ cứng",
            "hardness.confirm" => "Xác nhận kết quả đo" + (rest switch { "Pass" => " · ĐẠT", "Fail" => " · KHÔNG ĐẠT", "" => "", _ => " · " + rest }),
            "merge.parent" => "Thành dòng chính của nhóm gộp",
            "merge.child" => "Gộp vào dòng " + (rest.Split('|').LastOrDefault() ?? rest),
            "split.parent" or "split.child" => "Tách khỏi nhóm gộp",
            "auto.fill" => "Tự điền thông tin từ ERP / bảng mác",
            "image.confirm_all" => $"Xác nhận tất cả số đọc từ ảnh ({rest})",
            "info.save" => "Lưu thông tin QC",
            "lock.takeover" => "Thu hồi khóa đơn",
            _ => w,
        };
    }

    public static string Detail(string action, JsonNode? d)
    {
        if (d is null) return "";
        string RoleVi(string? r) => r switch { "Admin" => "Quản trị", "Qc" => "QC", "Sales" => "Kinh doanh", "Viewer" => "Chỉ xem", _ => r ?? "" };
        return action switch
        {
            "erp.import" => $"Thêm {S(d, "added")} · cập nhật {S(d, "updated")} · không đổi {S(d, "same")} · xung đột {S(d, "conflicts")}",
            "user.create" or "user.update" => $"Vai trò {RoleVi(S(d, "role"))} · {(S(d, "active") == "true" ? "đang hoạt động" : "ĐÃ KHÓA")}" + (S(d, "resetPassword") == "true" ? " · đặt mật khẩu mặc định" : ""),
            "steel.set" => $"File {S(d, "fileName")} · {S(d, "grades")} dòng mác" + (S(d, "issues") is { } i && i != "0" ? $" · {i} cảnh báo" : ""),
            "stamp.set" => "Đã thay ảnh con dấu",
            "sync" => $"Đẩy {S(d, "Pushed")} nháp · {S(d, "Conflicts")} xung đột · còn {S(d, "Pending")}",
            "hardness.confirm" => (S(d, "status") == "Pass" ? "ĐẠT" : S(d, "status") == "Fail" ? "KHÔNG ĐẠT" : S(d, "status") ?? "") + (S(d, "Low") is { } lo ? $" · giới hạn [{lo}; {S(d, "High")}]" : ""),
            "cq.create" => $"Số {S(d, "reportNo")} · {(S(d, "result") == "Pass" ? "ĐẠT" : "KHÔNG ĐẠT")}",
            "cq.approve" => $"Người duyệt {S(d, "approver")} · kiểm tra {S(d, "checker")}",
            "note.add" => (S(d, "ForCustomer") == "true" ? "Nhắc lại cho các đơn sau của khách" : "Lưu ý của đơn") + (S(d, "images") is { } n && n != "0" ? $" · {n} ảnh" : ""),
            "signer.add" => $"Người ký {S(d, "Name")}",
            "template.add" or "template.map" or "template.deactivate" => S(d, "DisplayName") ?? "",
            "order.merge" or "order.split" => d["children"] is JsonArray a ? $"Các dòng: {string.Join(", ", a.Select(x => x?.ToString().Split('|').LastOrDefault()))}" : "",
            "cq.excel" => S(d, "file") ?? "",
            "cq.issue" => S(d, "kind") switch { "StampOnly" => "Bản có dấu, chưa ký", "Blank" => "Bản trắng (in ký tay)", _ => "Bản có dấu + chữ ký" },
            "customer.alias" or "customer.alias.import" => $"{S(d, "count")} khách hàng",
            "user.recovery" => $"Đặt lại trên máy {S(d, "machine")}",
            "release.publish" => $"{S(d, "FileName")} · {(long.TryParse(S(d, "Size"), out var sz) ? $"{sz / 1048576.0:0.0} MB" : "")}",
            "release.install" => $"Từ {S(d, "from")} lên {S(d, "to")} trên máy {S(d, "machine")}",
            "report.export" => $"{S(d, "lines")} dòng đơn",
            "order.edit" when d is JsonValue ev => EditWhat(ev.ToString()),
            _ when d is JsonValue v => v.ToString(),
            _ => d.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        };
    }
}
