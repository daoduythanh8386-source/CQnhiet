using System.Globalization;
using CqNl.Domain;
using CqNl.Infrastructure.Storage;

namespace CqNl.Application;

/// <summary>Kết quả tự nhập ERP từ thư mục theo dõi (null = không có gì mới).</summary>
public sealed record ErpAutoResult(IReadOnlyList<string> Files, int Added, int Updated, int Conflicts, IReadOnlyList<string> Errors);

/// <summary>Kết quả tự điền thông tin QC cho một đơn.</summary>
public sealed record AutoFillResult(Workspace.OrderRow Row, IReadOnlyList<string> Filled, IReadOnlyList<string> NeedsQc);

/// <summary>Kết quả "Xác nhận tất cả" số đọc từ ảnh.</summary>
public sealed record ConfirmAllResult(Workspace.OrderRow Row, IReadOnlyList<string> Confirmed, IReadOnlyList<string> Skipped, bool SamplesFilled);

/// <summary>Quy trình tự động: ERP vào thư mục MF0015 → tự nhập; mở đơn → tự điền mác/yêu cầu/nhiệt độ/ram/template;
/// ảnh đo → số đọc tự điền, QC xác nhận một lần cho tất cả.</summary>
public sealed partial class Workspace
{
    // ---------------- 1. Tự nhập ERP ----------------
    private readonly Dictionary<string, (long Len, DateTime Mtime, string Sha)> _erpShaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _erpErrorsReported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mã hash file ERP, nhớ theo (kích thước, giờ sửa) để không đọc lại toàn bộ thư mục mỗi lần quét.</summary>
    internal string ErpSha(string path)
    {
        var fi = new FileInfo(path);
        lock (_erpShaCache)
            if (_erpShaCache.TryGetValue(path, out var c) && c.Len == fi.Length && c.Mtime == fi.LastWriteTimeUtc) return c.Sha;
        var sha = FileStore.Sha256Hex(File.ReadAllBytes(path));
        lock (_erpShaCache) _erpShaCache[path] = (fi.Length, fi.LastWriteTimeUtc, sha);
        return sha;
    }

    /// <summary>Gọi định kỳ trên máy QC/Quản trị đang mở ứng dụng: có file MF0015 mới trong thư mục theo dõi → tự nhập vào Đơn gia công
    /// (chỉ ghi các cột ERP; số đo/CQ không bị đụng; ERP đổi sau khi đã có CQ → gắn cảnh báo). Lỗi file chỉ báo một lần.</summary>
    public ErpAutoResult? AutoImportErp()
    {
        var s = Session;
        if (s is null || !Online || s.MustChangePassword || s.User.Role is not (Role.Admin or Role.Qc)) return null;
        var folder = Settings!.ErpFolderPath();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        if (CountNewErpFiles() == 0) return null;
        var scan = ScanErpFolder();
        var ok = scan.NewFiles.Where(f => f.Error is null).ToList();
        var errs = scan.NewFiles.Where(f => f.Error is not null && _erpErrorsReported.Add(f.Path + "|" + f.Sha)).Select(f => $"{f.Name}: {f.Error}").ToList();
        if (ok.Count == 0) return errs.Count > 0 ? new ErpAutoResult(Array.Empty<string>(), 0, 0, 0, errs) : null;
        var (a, u, c) = ApplyErpScan(scan);
        return new ErpAutoResult(ok.Select(f => f.Name).ToList(), a, u, c, errs);
    }

    // ---------------- 2. Tự điền thông tin QC khi mở đơn ----------------

    /// <summary>Tính các giá trị tự điền (không ghi). Chỉ điền ô đang TRỐNG — không ghi đè thứ QC đã nhập.</summary>
    public static (Action<QcData>? Apply, List<string> Filled, List<string> NeedsQc) PlanAutoFill(OrderRecord o, CqNl.Infrastructure.Excel.SteelGradeTable? steel,
        IEnumerable<OrderRecord> customerHistory, IReadOnlyList<TemplateInfo>? templates = null)
    {
        var q = o.Qc; var e = o.Erp;
        var filled = new List<string>(); var needs = new List<string>();
        string? grade = q.SteelGrade, req = q.HardnessRequirement, tol = q.Tolerance, tpl = q.TemplateId; int? row = q.SteelGradeRow;
        List<string>? heats = null, rams = null;

        if (string.IsNullOrWhiteSpace(grade) && e.GetValueOrDefault("GradeFromErp") is { Length: > 0 } eg) { grade = eg.Trim(); filled.Add($"Mác thép {grade} (ERP)"); }
        // ERP không có cột mác nhưng ghi mác ở "Ghi chú" (vd "H13ESR") → lấy nếu tra được trong bảng mác
        if (string.IsNullOrWhiteSpace(grade) && GradeFromNote(e.GetValueOrDefault("Note"), steel) is { } ng) { grade = ng; filled.Add($"Mác thép {grade} (ghi chú ERP)"); }
        if (string.IsNullOrWhiteSpace(req) && HardnessRequirement.TryParse(e.GetValueOrDefault("HardnessRequirementErp")) is not null)
        { req = e.GetValueOrDefault("HardnessRequirementErp")!.Trim(); filled.Add($"Yêu cầu độ cứng {req} (ERP)"); }
        else if (string.IsNullOrWhiteSpace(req)) needs.Add("Yêu cầu độ cứng (ERP không có hoặc không đọc được)");

        // khách Vinfast → mẫu CQ Vinfast (thông số lò theo bảng mác + thông số nhập tay riêng)
        var custPlain = Workspace.Plain(q.CustomerOverride ?? e.GetValueOrDefault("Customer"));
        if (string.IsNullOrWhiteSpace(tpl) && custPlain.Contains("VINFAST") && templates?.FirstOrDefault(t => t.Active && Workspace.Plain(t.DisplayName).Contains("VINFAST")) is { } vf)
        { tpl = vf.Id; filled.Add($"Template {vf.DisplayName} (khách Vinfast)"); }
        // phương thức gia công ERP "Thấm Nitơ" → template thấm nitơ (nếu đã thêm vào mục Template CQ)
        var method = Workspace.Plain(e.GetValueOrDefault("ProcessMethod"));
        if (string.IsNullOrWhiteSpace(tpl) && method.Contains("THAM NITO") && templates?.FirstOrDefault(t => t.Active && Workspace.Plain(t.DisplayName).Contains("NITO")) is { } nt)
        { tpl = nt.Id; filled.Add($"Template {nt.DisplayName} (phương thức ERP: thấm nitơ)"); }

        var needSteel = q.QuenchTemps is not { Count: > 0 } || q.RamValues is not { Count: > 0 } || string.IsNullOrWhiteSpace(tpl) || string.IsNullOrWhiteSpace(tol);
        if (needSteel && !string.IsNullOrWhiteSpace(grade))
        {
            IReadOnlyList<SteelGrade> found = FindGrade(steel, grade);
            if (row is { } r0 && found.Where(g => g.SourceRow == r0).ToList() is { Count: > 0 } pick) found = pick;   // QC đã chọn dòng trước đó
            // nhiều dòng cùng mác nhưng KHÁC thông số → không tự chọn (QC chọn bằng "Tra bảng mác")
            var distinct = found.GroupBy(g => (string.Join("|", Workspace.HeatTexts(g)), string.Join("|", Workspace.RamTexts(g)), g.Tolerance, g.TemplateMark)).ToList();
            if (steel is null) needs.Add("Bảng mác thép chưa có — nhập tay nhiệt độ tôi/ram");
            else if (found.Count == 0) needs.Add($"Mác \"{grade}\" không có trong bảng mác — nhập tay nhiệt độ tôi/ram, chọn template");
            else if (distinct.Count > 1) needs.Add($"Mác \"{grade}\" có {distinct.Count} dòng khác thông số trong bảng mác — bấm \"Tra bảng mác\" để chọn");
            else
            {
                var g = found[0];
                if (q.QuenchTemps is not { Count: > 0 } && Workspace.HeatTexts(g) is { Count: > 0 } h) { heats = h; filled.Add("Nhiệt độ tôi " + string.Join("; ", h)); }
                if (q.RamValues is not { Count: > 0 } && Workspace.RamTexts(g) is { Count: > 0 } rr) { rams = rr; filled.Add("Ram " + string.Join("; ", rr)); }
                if (string.IsNullOrWhiteSpace(tol) && !string.IsNullOrWhiteSpace(g.Tolerance)) { tol = g.Tolerance!.Trim(); filled.Add("Dung sai " + tol); }
                if (string.IsNullOrWhiteSpace(tpl) && Workspace.TemplateFromMark(g.TemplateMark) is { } t) { tpl = t; filled.Add("Template theo bảng mác"); }
                row ??= g.SourceRow;
            }
        }
        // template theo thông tin sản phẩm ERP (khi bảng mác chưa có / chưa đánh dấu): "Thép gió…" / "Thép cacbon…" / "Thép hợp kim…"
        if (string.IsNullOrWhiteSpace(tpl) && TemplateFromProduct(e.GetValueOrDefault("ProductName"), e.GetValueOrDefault("ProductCode")) is { } tp)
        { tpl = tp; filled.Add("Template theo tên sản phẩm ERP"); }
        // CQ 2026 (THÉP CACBON, ô H14): thấm carbon → nhiệt độ tôi 2 = 920℃
        var tplNow = string.IsNullOrWhiteSpace(q.TemplateId) ? tpl : q.TemplateId;
        if (heats is { Count: >= 2 } && tplNow == "carbon" && (method.Contains("THAM CARBON") || method.Contains("THAMC")) && heats[1] != "920℃")
        { heats[1] = "920℃"; filled.Add("Nhiệt độ tôi 2 = 920℃ (thấm carbon)"); }
        // template: chưa có → theo đơn trước của CÙNG khách + CÙNG mác
        if (string.IsNullOrWhiteSpace(tpl) && !string.IsNullOrWhiteSpace(grade))
        {
            var cust = q.CustomerOverride ?? e.GetValueOrDefault("Customer");
            var prev = customerHistory.Where(h => h.Key != o.Key && !string.IsNullOrWhiteSpace(h.Qc.TemplateId)
                                                  && (h.Qc.CustomerOverride ?? h.Erp.GetValueOrDefault("Customer")) == cust
                                                  && Grade.Normalize(h.Qc.SteelGrade ?? h.Erp.GetValueOrDefault("GradeFromErp")) == Grade.Normalize(grade))
                .OrderByDescending(h => h.Key).FirstOrDefault();
            if (prev is not null) { tpl = prev.Qc.TemplateId; filled.Add("Template theo đơn trước của khách"); }
            else needs.Add("Chọn template CQ");
        }
        if (filled.Count == 0) return (null, filled, needs);
        void Apply(QcData x)
        {
            if (string.IsNullOrWhiteSpace(x.SteelGrade)) x.SteelGrade = grade;
            if (string.IsNullOrWhiteSpace(x.HardnessRequirement)) x.HardnessRequirement = req;
            if (string.IsNullOrWhiteSpace(x.Tolerance)) x.Tolerance = tol;
            if (string.IsNullOrWhiteSpace(x.TemplateId)) x.TemplateId = tpl;
            if (x.QuenchTemps is not { Count: > 0 } && heats is not null) x.QuenchTemps = heats;
            if (x.RamValues is not { Count: > 0 } && rams is not null) x.RamValues = rams;
            x.SteelGradeRow ??= row;
        }
        return (Apply, filled, needs);
    }

    /// <summary>Tra mác trong bảng mác; mác luyện lại xỉ điện (hậu tố ESR, vd "H13ESR") không có dòng riêng → dùng dòng mác gốc ("H13").</summary>
    public static IReadOnlyList<SteelGrade> FindGrade(CqNl.Infrastructure.Excel.SteelGradeTable? steel, string? grade)
    {
        if (steel is null || string.IsNullOrWhiteSpace(grade)) return Array.Empty<SteelGrade>();
        var f = steel.Find(grade);
        if (f.Count == 0)
        {
            var n = Grade.Normalize(grade);
            if (n.EndsWith("ESR") && n.Length > 4) f = steel.Find(n[..^3]);
        }
        return f;
    }

    /// <summary>Mác thép ghi trong cột "Ghi chú" ERP: từ đầu tiên (hoặc cặp 2 từ, vd "SKD 11") tra được trong bảng mác.</summary>
    public static string? GradeFromNote(string? note, CqNl.Infrastructure.Excel.SteelGradeTable? steel)
    {
        if (steel is null || string.IsNullOrWhiteSpace(note)) return null;
        var toks = System.Text.RegularExpressions.Regex.Matches(note, @"[A-Za-z0-9][A-Za-z0-9\-\.]*").Select(m => m.Value.Trim('-', '.')).Where(t => t.Length >= 2).ToList();
        for (var i = 0; i < toks.Count; i++)
        {
            if (toks[i].Any(char.IsLetter) && FindGrade(steel, toks[i]).Count > 0) return toks[i].ToUpperInvariant();
            if (i + 1 < toks.Count && toks[i].All(char.IsLetter) && toks[i + 1].All(char.IsDigit) && FindGrade(steel, toks[i] + toks[i + 1]).Count > 0)
                return (toks[i] + toks[i + 1]).ToUpperInvariant();
        }
        return null;
    }

    /// <summary>Template theo tên sản phẩm ERP: "Thép gió …" → gió; "Thép cacbon …" → carbon; "Thép hợp kim / làm khuôn …" → khuôn dập.</summary>
    public static string? TemplateFromProduct(string? productName, string? productCode = null)
    {
        var p = Workspace.Plain(productName) + " " + Workspace.Plain(productCode);
        if (p.Contains("THEP GIO")) return "gio";
        if (p.Contains("THEP CACBON") || p.Contains("THEP CARBON") || p.Contains("THEPCACBON")) return "carbon";
        if (p.Contains("THEP HOP KIM") || p.Contains("THEPHOPKIM") || p.Contains("THEP LAM KHUON") || p.Contains("KHUON")) return "khuon";
        return null;
    }

    /// <summary>Mở đơn: tự điền các ô QC còn trống từ ERP + bảng mác + đơn trước của khách, lưu ngay (ghi nhật ký "auto.fill").</summary>
    public AutoFillResult? AutoFillQc(string key)
    {
        var s = Session;
        if (s is null || s.MustChangePassword || s.User.Role is not (Role.Admin or Role.Qc)) return null;
        var row = GetOrder(key);
        if (row is null || row.Cqs.Any(c => c.State != CqState.Deleted)) return null;          // đã có CQ → không tự sửa nữa
        if (row.Order.Qc.MergeGroup is { } mg && mg != key) return null;                         // dòng con của nhóm gộp
        CqNl.Infrastructure.Excel.SteelGradeTable? steel = null;
        try { steel = SteelTable(); } catch { }
        var hist = Cache.All<OrderRecord>(OrderService.T).Select(c => c.Value);
        IReadOnlyList<TemplateInfo>? tpls = null;
        try { tpls = TemplateList(); } catch { }
        var (apply, filled, needs) = PlanAutoFill(row.Order, steel, hist, tpls);
        if (apply is null) return new AutoFillResult(row, filled, needs);
        var r = EditQc(key, row.Version, apply, "auto.fill");
        return new AutoFillResult(r, filled, needs);
    }

    // ---------------- 3. Xác nhận tất cả số đọc từ ảnh ----------------

    /// <summary>QC xác nhận một lần mọi số máy đọc được trên ảnh (độ cứng + số cân). Ảnh không đọc được số → bỏ qua, QC nhập tay.
    /// Nếu ô Mẫu 1–6 đang trống: điền các số đo độ cứng đã xác nhận, xếp tăng dần; ngày kiểm tra trống → hôm nay.</summary>
    public ConfirmAllResult ConfirmAllReadings(string key, int baseVersion)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        var row = GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn");
        if (row.Version != baseVersion) throw new ConflictException("Đơn đã thay đổi — tải lại");
        var confirmed = new List<string>(); var skipped = new List<string>(); var filledSamples = false;
        var pending = row.Order.Qc.Images.Where(i => i.ConfirmedValue is null && (i.Kind is "Hardness" or "Sample")).OrderBy(i => i.AddedOrder).ToList();
        string Name(InspectionImage i) => (i.Kind == "Hardness" ? "Ảnh độ cứng " : "Ảnh cân ") + i.AddedOrder;
        var plan = new List<(string Id, string V)>();
        foreach (var im in pending)
        {
            // ảnh có cảnh báo OCR (gốc ≠ xử lý / tin cậy thấp / không xử lý được) → KHÔNG xác nhận hàng loạt: người dùng mở ảnh, đối chiếu, xác nhận riêng
            if (ImageVisionService.BlocksAuto(im)) { skipped.Add($"{Name(im)}: cảnh báo OCR — mở ảnh đối chiếu rồi xác nhận riêng ({im.Vision!.Flags.FirstOrDefault()})"); continue; }
            if (string.IsNullOrWhiteSpace(im.OcrValue)) { if (im.Kind == "Hardness") skipped.Add(Name(im) + ": máy không đọc được số — nhập tay"); continue; }
            string v;
            try { v = ImageService.NormalizeValue(im.OcrValue); } catch { skipped.Add($"{Name(im)}: số đọc \"{im.OcrValue}\" không hợp lệ"); continue; }
            var d = decimal.Parse(v, CultureInfo.InvariantCulture);
            if (im.Kind == "Hardness" && (d < 10 || d > 75)) { skipped.Add($"{Name(im)}: {v} ngoài khoảng HRC 10–75 — kiểm tra lại"); continue; }
            plan.Add((im.Id, v)); confirmed.Add($"{Name(im)}: {v}");
        }
        var r = EditQc(key, baseVersion, q =>
        {
            foreach (var (id, v) in plan) ImageService.ApplyConfirm(q, id, v, s.User.Username);
            ApplyAutoSamples(q);
            filledSamples = q.Hardness.SamplesAuto && q.Hardness.Samples.Count > 0;
        }, $"image.confirm_all {plan.Count}");
        if (!filledSamples && r.Order.Qc.Hardness.Samples.All(x => string.IsNullOrWhiteSpace(x)) && r.Order.Qc.Images.Count(i => i.Kind == "Hardness" && i.OnCq && i.ConfirmedValue is not null) > HardnessEvaluator.MaxSamples)
            skipped.Add($"Có hơn {HardnessEvaluator.MaxSamples} số đo — nhập các mẫu ghép nhiều điểm ở tab Kết quả đo");
        return new ConfirmAllResult(r, confirmed, skipped, filledSamples);
    }

    // ---------------- 4. Mẫu 1–6 tự lấy từ số đọc trên ảnh ----------------

    /// <summary>Số đo từ ảnh đo độ cứng đang đưa lên CQ: giá trị QC xác nhận, chưa có thì số máy đọc; xếp tăng dần.</summary>
    public static List<string> DerivedSamples(QcData q) => q.Images.Where(i => i.Kind == "Hardness" && i.OnCq && !ImageVisionService.BlocksAuto(i))
        .Select(i => i.ConfirmedValue ?? i.OcrValue)
        .Select(v => decimal.TryParse((v ?? "").Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d >= 10 && d <= 75 ? (decimal?)d : null)
        .Where(d => d is not null).Select(d => d!.Value).OrderBy(d => d).Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();

    /// <summary>Mẫu 1–6 đang trống hoặc đang "tự động" → lấy theo ảnh (đổi giá trị → bỏ xác nhận kết quả đo). Hơn 6 số → để QC nhập tay mẫu ghép.</summary>
    public static void ApplyAutoSamples(QcData q)
    {
        var h = q.Hardness;
        if (!h.SamplesAuto && h.Samples.Any(x => !string.IsNullOrWhiteSpace(x))) return;
        var d = DerivedSamples(q);
        if (d.Count > HardnessEvaluator.MaxSamples) return;
        if (!h.Samples.SequenceEqual(d)) { h.Samples = d; h.ConfirmedAt = null; h.ConfirmedBy = null; }
        h.SamplesAuto = d.Count > 0;
        if (d.Count > 0) h.CheckDate ??= DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>Thêm ảnh, TỰ NHẬN LOẠI: đọc được số độ cứng (10–75) → ảnh đo độ cứng; không đọc được → ảnh xuất hàng (sản phẩm/cân).
    /// Mẫu 1–6 tự cập nhật theo số đọc.</summary>
    public InspectionImage AddImageAuto(string key, int baseVersion, byte[] bytes, string ext)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        var kind = ImageService.DetectKind(bytes, Paths);
        var (im, blobs) = ImageService.Process(bytes, ext, kind, Paths);
        ImageService.Stamp(im, s.User.Username);
        EditQc(key, baseVersion, q => { ImageService.Attach(q, im); ApplyAutoSamples(q); }, $"image.add {im.Kind} {im.BlobSha[..12]}", blobs);
        return im;
    }

    // ---------------- 5. Một nút: xác nhận → tạo CQ → xuất bản in ----------------

    /// <summary>Ra bản CQ nhanh nhất: xác nhận các số đọc trên ảnh + kết quả đo (nếu chưa), tạo CQ (cấp số) nếu chưa có bản khớp dữ liệu,
    /// rồi xuất bản in theo kiểu: có dấu + chữ ký (người ký mặc định) / có dấu chưa ký / trắng ký tay. Trả CQ và đường dẫn PDF.</summary>
    public (CqVersionRecord Cq, string Pdf, List<string> Steps) QuickCq(string key, CqExportKind kind, string saveRoot, string? approverId = null, string? checkerId = null)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        RequireOnline("xuất CQ");
        if (HasPendingDraft(OrderService.T, key)) throw new BusinessException("Đơn còn nháp offline chưa đồng bộ — đồng bộ trước khi xuất CQ");
        // kiểm điều kiện bản in TRƯỚC khi xác nhận / cấp số (lỗi thiếu dấu/chữ ký không được tiêu tốn số báo cáo)
        var st = Settings!.Get().Value;
        SignerRecord? appr = null, chk = null; byte[]? stamp = null;
        if (kind != CqExportKind.Blank) stamp = st.StampSha is null ? throw new BusinessException("Chưa có con dấu — Quản trị cập nhật ở Cài đặt → Chữ ký và con dấu") : Store!.GetBlob(st.StampSha, "png");
        if (kind == CqExportKind.Signed)
        {
            appr = st.Signers.FirstOrDefault(x => x.Id == (approverId ?? st.DefaultApproverId) && x.Active);
            chk = st.Signers.FirstOrDefault(x => x.Id == (checkerId ?? st.DefaultCheckerId) && x.Active);
            if (appr is null || chk is null) throw new BusinessException("Chưa chọn người xác nhận (Approved) / người kiểm tra (Checker) — chọn ở tab Xuất CQ.");
        }
        var steps = new List<string>();
        var cq = PrepareCqCore(s, key, steps);
        var placements = st.Placements.GetValueOrDefault(cq.TemplateId);
        cq = Cq.Issue(s, cq.Id, kind, appr, chk, stamp, placements);
        steps.Add(kind switch { CqExportKind.Signed => "Xuất PDF có dấu + chữ ký", CqExportKind.StampOnly => "Xuất PDF có dấu, chưa ký", _ => "Xuất PDF trắng (ký tay)" });
        var pdf = ExportCqNamed(cq, (GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn")).Order, kind, saveRoot);
        steps.Add("Lưu " + Path.GetFileName(pdf));
        return (cq, pdf, steps);
    }

    /// <summary>CQ của đơn còn KHỚP dữ liệu mới nhất (nháp hoặc đã phát hành); chưa có → xác nhận các số đọc trên ảnh (độ cứng + số cân),
    /// xác nhận kết quả đo, huỷ nháp cũ (dữ liệu đã đổi) rồi tạo bản nháp mới (cấp số báo cáo). Dùng cho "Tạo file CQ nháp", "Duyệt CQ", xuất Excel/PDF.</summary>
    public (CqVersionRecord Cq, List<string> Steps) PrepareCq(string key)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        RequireOnline("tạo CQ");
        if (HasPendingDraft(OrderService.T, key)) throw new BusinessException("Đơn còn nháp offline chưa đồng bộ — đồng bộ trước khi tạo CQ");
        var steps = new List<string>();
        return (PrepareCqCore(s, key, steps), steps);
    }

    private CqVersionRecord PrepareCqCore(Session s, string key, List<string> steps)
    {
        var row = GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn");
        if (row.Order.Qc.MergeGroup is { } mg && mg != key) throw new BusinessException($"Dòng này đã gộp vào dòng {mg.Split('|').LastOrDefault()} — xuất CQ ở dòng chính");
        // chọn CQ hiện có còn khớp dữ liệu
        var live = Cq!.ForOrder(key).Where(c => c.State != CqState.Deleted).OrderBy(c => c.Ver).ToList();
        var last = live.LastOrDefault();
        CqVersionRecord? cq = last is not null && last.OrderVersion == row.Version ? last : null;
        if (cq is null && last is { State: CqState.Approved })
            throw new BusinessException($"Đơn đã sửa sau khi phát hành CQ số {last.ReportNo}. Muốn in lại theo dữ liệu mới: xoá CQ {last.ReportNo} (ghi lý do) rồi xuất lại; muốn in lại bản cũ: mở ở danh sách CQ bên dưới.");
        if (cq is null)
        {
            var review = row.Order.Qc.Images.Where(i => i.OnCq && i.Kind is "Hardness" or "Sample" && ImageVisionService.BlocksAuto(i)).OrderBy(i => i.AddedOrder).ToList();
            if (review.Count > 0)
                throw new BusinessException($"Còn {review.Count} ảnh có CẢNH BÁO OCR chưa xác nhận (" + string.Join("; ", review.Select(i => $"ảnh {i.AddedOrder}: {i.Vision!.Flags.FirstOrDefault()}")) +
                    "). Mở từng ảnh, đối chiếu số trên ảnh rồi bấm Xác nhận — app không tự đưa số chưa xác nhận vào CQ.");
            if (row.Order.Qc.Images.Any(i => i.ConfirmedValue is null && i.OcrValue is not null && (i.Kind == "Hardness" || i.Kind == "Sample")))
            {
                var ca = ConfirmAllReadings(key, row.Version); row = ca.Row;
                if (ca.Confirmed.Count > 0) steps.Add($"Xác nhận {ca.Confirmed.Count} số đọc trên ảnh");
            }
            if (row.Order.Qc.Hardness.ConfirmedAt is null)
            {
                var (r2, ev) = ConfirmHardness(key, row.Version); row = r2;
                steps.Add("Xác nhận kết quả đo: " + (ev.Status == EvaluationStatus.Pass ? "ĐẠT" : "KHÔNG ĐẠT"));
            }
            if (last is { State: CqState.Draft })
            {
                Cq.Delete(s, last.Id, "Tạo lại CQ vì dữ liệu đơn đã thay đổi");
                steps.Add($"Huỷ nháp CQ {last.ReportNo} (dữ liệu đơn đã đổi)");
            }
            cq = Cq.Create(s, key);
            steps.Add($"Tạo CQ số {cq.ReportNo}");
        }
        return cq;
    }

    // ---------------- 6. Tên file PDF chuẩn ----------------

    /// <summary>Tên file chuẩn: "Tháng-4 số cuối ĐNGC - Tên khách hàng - Vật liệu - Độ cứng yêu cầu".pdf (vd "11-0216 - HTVG - SKD11 - 58-60.pdf").
    /// Bản có dấu chưa ký / bản trắng thêm hậu tố để không đè bản có chữ ký.</summary>
    public string CqFileName(CqVersionRecord c, OrderRecord o, CqExportKind kind, string ext = ".pdf")
    {
        var month = c.ReportNo.Contains('/') ? c.ReportNo.Split('/')[1] : c.CreatedAt.ToString("MM");
        var digits = new string(o.Key.Split('|')[0].Where(char.IsDigit).ToArray());
        var last4 = digits.Length >= 4 ? digits[^4..] : digits;
        var cust = CustomerOnCq(o);
        var mat = o.Qc.SteelGrade ?? o.Erp.GetValueOrDefault("GradeFromErp") ?? "";
        var req = o.Qc.HardnessRequirement ?? o.Erp.GetValueOrDefault("HardnessRequirementErp") ?? "";
        var suffix = kind switch { CqExportKind.StampOnly => " (co dau chua ky)", CqExportKind.Blank => " (ky tay)", _ => "" };
        var name = $"{month}-{last4} - {cust} - {mat} - {req}{suffix}";
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '-');
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
        return (name.Length > 150 ? name[..150] : name) + ext;
    }

    /// <summary>Thư mục con theo THÁNG/NĂM của CQ (tháng của số báo cáo NNN/MM, năm tạo CQ), vd "11-2026".</summary>
    public static string MonthFolderName(CqVersionRecord c)
    {
        var mm = c.ReportNo.Contains('/') && int.TryParse(c.ReportNo.Split('/')[1], out var m) && m is >= 1 and <= 12 ? m : c.CreatedAt.Month;
        return $"{mm:00}-{c.CreatedAt.Year}";
    }

    /// <summary>Thư mục lưu file của CQ: &lt;thư mục lưu CQ chọn trên app&gt;\&lt;tháng-năm&gt;.</summary>
    public static string MonthFolder(string saveRoot, CqVersionRecord c) => Path.Combine(saveRoot, MonthFolderName(c));

    /// <summary>Lưu PDF bản in vào &lt;saveRoot&gt;\&lt;tháng-năm&gt;\ với tên chuẩn.</summary>
    public string ExportCqNamed(CqVersionRecord c, OrderRecord o, CqExportKind kind, string saveRoot)
    {
        var destDir = MonthFolder(saveRoot, c);
        RequireOnline("lưu PDF CQ");
        var sha = c.Pdfs.GetValueOrDefault(kind.ToString()) ?? (kind == CqExportKind.Signed ? c.PdfSha : null) ?? throw new BusinessException("CQ chưa xuất bản in kiểu này");
        Directory.CreateDirectory(destDir);
        var p = Path.Combine(destDir, CqFileName(c, o, kind));
        return WriteFileSafe(p, Store!.GetBlob(sha, "pdf"));
    }

    /// <summary>Ghi file; file cùng tên đang MỞ (Excel / trình đọc PDF giữ khoá) → ghi "tên (2).ext", "(3)"… thay vì báo lỗi.
    /// Nội dung giống hệt file đang có → giữ nguyên file cũ.</summary>
    public static string WriteFileSafe(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (var n = 1; n <= 30; n++)
        {
            var p = n == 1 ? path : Path.Combine(dir, $"{name} ({n}){ext}");
            try
            {
                if (File.Exists(p))
                {
                    try { if (File.ReadAllBytes(p).AsSpan().SequenceEqual(bytes)) return p; } catch (IOException) { continue; }
                }
                File.WriteAllBytes(p, bytes);
                return p;
            }
            catch (IOException) { }                                  // đang mở ở chương trình khác → thử tên kế tiếp
            catch (UnauthorizedAccessException) { }
        }
        throw new BusinessException($"Không ghi được file {Path.GetFileName(path)} — file đang mở ở chương trình khác (Excel / trình đọc PDF). Đóng file rồi làm lại.");
    }

    public byte[] CqPdfBytes(CqVersionRecord c, CqExportKind kind)
    {
        RequireOnline("in CQ");
        var sha = c.Pdfs.GetValueOrDefault(kind.ToString()) ?? throw new BusinessException("CQ chưa xuất bản in kiểu này");
        return Store!.GetBlob(sha, "pdf");
    }
}
