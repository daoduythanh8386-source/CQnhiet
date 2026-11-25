using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CqNl.Domain;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Imaging;
using CqNl.Infrastructure.Ocr;
using CqNl.Infrastructure.Pdf;
using CqNl.Infrastructure.Storage;

namespace CqNl.Application;

/// <summary>Đơn gia công: import ERP (chỉ ghi phần Erp), sửa phần QC, xác nhận kết quả đo.</summary>
public sealed class OrderService(Repo repo, AuditLog audit)
{
    public const string T = "order";
    public static string Key(string dngc, int line) => $"{dngc}|{line}";

    public IReadOnlyList<OrderRecord> All() => repo.Ids(T).Select(id => repo.Get<OrderRecord>(T, id).Value).Where(o => o is not null).Select(o => o!).ToList();
    public (OrderRecord? Order, int Version) Get(string key) => repo.Get<OrderRecord>(T, key);

    public ImportPlan Preview(ErpReadResult read)
    {
        var current = All().ToDictionary(o => o.Key, o => (IReadOnlyDictionary<string, string?>)o.Erp);
        return ErpImportPlanner.Plan(current, read.Lines);
    }

    /// <summary>Áp dụng kế hoạch import. Đơn có sẵn: CHỈ thay trường ERP; dữ liệu QC/ảnh/CQ giữ nguyên; đơn đã có CQ → gắn cờ.</summary>
    public (int Added, int Updated, int Conflicts) Apply(Session s, ImportPlan plan, string sourceFile, Func<string, bool> hasCq)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        int a = 0, u = 0, c = 0;
        foreach (var l in plan.Add)
        {
            try { repo.Put(T, l.Key, new OrderRecord { Key = l.Key, Erp = l.ErpFields().ToDictionary(kv => kv.Key, kv => kv.Value) }, 0, s.User.Username); a++; }
            catch (ConflictException) { c++; }
        }
        foreach (var (l, ch) in plan.Update)
        {
            var (o, ver) = Get(l.Key);
            if (o is null) { c++; continue; }
            foreach (var (k, (_, nv)) in ch) o.Erp[k] = nv;
            // cột mới bổ sung (vd "Ghi chú" từ 0.6) trước đây chưa đọc → không coi là ERP thay đổi
            var flagKeys = ch.Where(kv => !(kv.Key == "Note" && kv.Value.Item1 is null)).Select(kv => kv.Key).ToList();
            if (flagKeys.Count > 0 && hasCq(l.Key)) o.Flags.Add($"ERP thay đổi sau CQ ({DateTime.Now:dd/MM/yyyy HH:mm}): {string.Join(", ", flagKeys)}");
            try { repo.Put(T, l.Key, o, ver, s.User.Username); u++; } catch (ConflictException) { c++; }
        }
        audit.Write(s.User.Username, "erp.import", Path.GetFileName(sourceFile), new { added = a, updated = u, same = plan.Same.Count, conflicts = c });
        return (a, u, c);
    }

    /// <summary>Sửa phần QC (thông tin nhập tay, ảnh, kết quả đo) với CAS — xung đột thì báo, không ghi đè.</summary>
    public OrderRecord EditQc(Session s, string key, int baseVersion, Action<QcData> edit, string what)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        var (o, ver) = Get(key);
        if (o is null) throw new BusinessException("Không tìm thấy đơn " + key);
        if (ver != baseVersion) throw new ConflictException($"Đơn {key} đã được người khác sửa (v{ver}) — tải lại trước khi sửa");
        edit(o.Qc);
        repo.Put(T, key, o, ver, s.User.Username);
        audit.Write(s.User.Username, "order.edit", key, what);
        return o;
    }

    /// <summary>Xác nhận kết quả đo: mọi ảnh độ cứng đưa lên CQ phải có giá trị QC xác nhận; mẫu phải hợp lệ.</summary>
    public Evaluation ConfirmHardness(Session s, string key, int baseVersion, string? tolerance)
    {
        var (o, _) = Get(key);
        if (o is null) throw new BusinessException("Không tìm thấy đơn");
        var ev = CheckConfirmable(o, tolerance);
        EditQc(s, key, baseVersion, q => { q.Hardness.ConfirmedBy = s.User.Username; q.Hardness.ConfirmedAt = DateTimeOffset.Now; }, "hardness.confirm " + ev.Status);
        audit.Write(s.User.Username, "hardness.confirm", key, new { status = ev.Status.ToString(), ev.Low, ev.High });
        return ev;
    }

    /// <summary>Đánh giá trực tiếp (để hiển thị khi đang nhập) — lỗi định dạng trả về thông báo thay vì ngoại lệ.</summary>
    public static (Evaluation? Ev, string? Error) TryEvaluate(OrderRecord o, string? tolerance = null)
    {
        try { return (HardnessEvaluator.Evaluate(o.Qc.HardnessRequirement ?? o.Erp.GetValueOrDefault("HardnessRequirementErp"), tolerance ?? o.Qc.Tolerance, o.Qc.Hardness.Samples), null); }
        catch (Exception e) when (e is FormatException or ToleranceException or ArgumentException) { return (null, e.Message); }
    }

    public static Evaluation CheckConfirmable(OrderRecord o, string? tolerance)
    {
        var unconfirmed = o.Qc.Images.Where(i => i.Kind == "Hardness" && i.OnCq && i.ConfirmedValue is null).ToList();
        if (unconfirmed.Count > 0) throw new BusinessException($"Còn {unconfirmed.Count} ảnh độ cứng chưa xác nhận số (OCR chỉ là gợi ý)");
        var (evq, err) = TryEvaluate(o, tolerance);
        if (evq is null) throw new BusinessException(err!);
        var ev = evq;
        if (ev.Status is EvaluationStatus.InvalidRequirement) throw new BusinessException("Yêu cầu độ cứng không hợp lệ/thiếu — nhập tay trước khi xác nhận");
        if (ev.Status is EvaluationStatus.NoData) throw new BusinessException("Chưa có điểm đo nào");
        return ev;
    }
}

/// <summary>Vòng đời CQ: tạo bản (cấp số báo cáo) → duyệt (dấu, chữ ký, PDF Excel A4) → in → xóa (bắt buộc lý do).
/// CQ đã duyệt bất biến; sửa = tạo bản mới. Mọi bước ghi nhật ký.</summary>
public sealed class CqService(Repo repo, AuditLog audit, ReportNumberAllocator numbers, OrderService orders, AppPaths paths)
{
    public const string T = "cq";
    public TemplateCatalog Templates { get; } = new(repo, audit, paths);

    /// <summary>Bản đồ ô dùng cho một bản CQ: bản lưu kèm lúc tạo (bất biến); bản CQ cũ chưa lưu → danh mục hiện tại.</summary>
    public TemplateMap MapOf(CqVersionRecord c) => c.TemplateMapJson is { } j ? TemplateMap.Parse(j) : Templates.Resolve(c.TemplateId).Map;

    public IReadOnlyList<CqVersionRecord> All() => repo.Ids(T).Select(id => repo.Get<CqVersionRecord>(T, id).Value).Where(x => x is not null).Select(x => x!).ToList();
    /// <summary>Chỉ đọc các bản CQ của đơn này (lọc theo tên thư mục) — không quét toàn bộ CQ trong kho.</summary>
    public IReadOnlyList<CqVersionRecord> ForOrder(string orderKey)
    {
        var prefix = FileStore.SafeId(orderKey + "|v");
        return repo.Ids(T).Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).Select(id => repo.Get<CqVersionRecord>(T, id).Value)
            .Where(c => c is not null && c.OrderKey == orderKey).Select(c => c!).OrderBy(c => c.Ver).ToList();
    }
    public bool HasActiveCq(string orderKey) => ForOrder(orderKey).Any(c => c.State != CqState.Deleted);

    public CqVersionRecord Create(Session s, string orderKey, string? reportNoOverride = null, DateTime? today = null)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        var (o, orderVer) = orders.Get(orderKey);
        if (o is null) throw new BusinessException("Không tìm thấy đơn");
        var q = o.Qc;
        if (q.Hardness.ConfirmedAt is null) throw new BusinessException("Chưa xác nhận kết quả độ cứng");
        if (q.MergeGroup is { } mg && mg != orderKey) throw new BusinessException($"Dòng này đã gộp vào dòng {mg.Split('|').LastOrDefault()} — tạo CQ ở dòng chính của nhóm");
        if (string.IsNullOrEmpty(q.TemplateId)) throw new BusinessException("Chưa chọn template CQ");
        var (evq, err) = OrderService.TryEvaluate(o);
        if (evq is null) throw new BusinessException(err!);
        var ev = evq;
        if (ev.Status is not (EvaluationStatus.Pass or EvaluationStatus.Fail)) throw new BusinessException("Kết quả đo không hợp lệ");
        var (map, tplBytes) = Templates.Resolve(q.TemplateId);
        var d = BuildData(o, map, tplBytes, ev);
        var now = today ?? DateTime.Now;
        d.Values["ReportNo"] = reportNoOverride ?? $"000/{now.Month:00}";
        CqWriter.Build(tplBytes, map, d);      // dựng thử TRƯỚC khi cấp số: lỗi template/giai đoạn → không tiêu tốn số báo cáo
        string? claim = null;
        var reportNo = reportNoOverride;
        if (reportNo is null) (reportNo, claim) = numbers.ClaimWithToken(now.Year, now.Month, orderKey);
        var warnings = new List<string>();
        var noScale = q.Images.Count(i => i.Kind == "Sample" && i.OnCq && i.ConfirmedValue is null);
        if (noScale > 0) warnings.Add($"{noScale} ảnh xuất hàng chưa xác nhận số cân → không ghi chú số trên ảnh");
        if (reportNoOverride is not null && All().Any(c => c.ReportNo == reportNoOverride && c.State != CqState.Deleted)) warnings.Add("Trùng số báo cáo");
        d.Values["ReportNo"] = reportNo;
        var xlsx = CqWriter.Build(tplBytes, map, d);
        var ver = ForOrder(orderKey).Select(c => c.Ver).DefaultIfEmpty(0).Max() + 1;
        var rec = new CqVersionRecord
        {
            OrderKey = orderKey, Ver = ver, ReportNo = reportNo, ReportClaim = claim, ReportClaimMonth = claim is null ? null : $"{now.Year:0000}-{now.Month:00}", TemplateId = q.TemplateId!, TemplateSha = FileStore.Sha256Hex(tplBytes), TemplateMapJson = map.Json, TemplateName = string.IsNullOrEmpty(map.DisplayName) ? q.TemplateId! : map.DisplayName,
            XlsxSha = repo.Store.PutBlob(xlsx, "xlsx"), OrderVersion = orderVer, Result = ev.Status.ToString(), CreatedBy = s.User.Username, CreatedAt = DateTimeOffset.Now, Warnings = warnings,
        };
        repo.Put(T, rec.Id, rec, 0, s.User.Username);
        audit.Write(s.User.Username, "cq.create", rec.Id, new { reportNo, result = rec.Result, warnings });
        return rec;
    }

    /// <summary>Dựng bản có dấu + chữ ký TRÊN BẢN NHÁP ĐÃ CẤP SỐ (không dựng lại dữ liệu) và xuất PDF bằng Excel (kiểm A4, 1 trang).</summary>
    private (byte[] Xlsx, byte[] Pdf, CqVersionRecord Cq, int Ver) Render(string cqId, CqExportKind kind, SignerRecord? approver, SignerRecord? checker, byte[]? stampPng,
        IReadOnlyList<CqOverlayPlacement>? placements, bool allowIssued = false)
    {
        var lib = repo.Get<SharedSettings>("settings", "shared").Value?.Signers ?? new();
        bool Current(SignerRecord x) => lib.Any(l => l.Id == x.Id && l.Active && l.TransparentSha == x.TransparentSha);
        if (kind == CqExportKind.Signed && (approver is null || checker is null || !Current(approver) || !Current(checker)))
            throw new BusinessException("Chữ ký không có trong thư viện hoặc đã được thay — chọn chữ ký hiện hành");
        if (kind != CqExportKind.Blank && stampPng is null) throw new BusinessException("Chưa có con dấu (Cài đặt → Chữ ký và con dấu)");
        var (c, ver) = repo.Get<CqVersionRecord>(T, cqId);
        if (c is null) throw new BusinessException("Không tìm thấy CQ");
        if (c.State == CqState.Deleted) throw new BusinessException("CQ đã xoá");
        if (c.State != CqState.Draft && !allowIssued) throw new BusinessException("Chỉ duyệt được CQ ở trạng thái nháp (CQ đã duyệt là bất biến)");
        var map = MapOf(c);
        CqImage Img(byte[] b, string n) { var (w, h) = CqImages.Size(b); return new CqImage(b, w, h, n); }
        CqOverlay Ov(CqImage im, string role, string defaultFrame)
        {
            var p = placements?.FirstOrDefault(x => x.Role == role);
            return p is null ? new CqOverlay(im, Frame: defaultFrame) : new CqOverlay(im, Cell: p.Cell, DxMm: p.DxMm, DyMm: p.DyMm, WidthMm: p.WidthMm);
        }
        var ovs = new List<CqOverlay>();
        if (kind == CqExportKind.Signed)
        {
            ovs.Add(Ov(Img(repo.Store.GetBlob(CqConventions.TransparentSignatureOnCq ? approver!.TransparentSha : approver!.PaperSha, "png"), "Approver"), "Approver", map.Ranges("ApproverSignatureFrame")[0]));
            ovs.Add(Ov(Img(repo.Store.GetBlob(CqConventions.TransparentSignatureOnCq ? checker!.TransparentSha : checker!.PaperSha, "png"), "Checker"), "Checker", map.Ranges("CheckerSignatureFrame")[0]));
        }
        if (kind != CqExportKind.Blank) ovs.Add(Ov(Img(stampPng!, "Stamp"), "Stamp", map.Ranges("ApproverSignatureFrame")[0]));
        var draft = repo.Store.GetBlob(c.XlsxSha, "xlsx");
        var xlsx = ovs.Count > 0 ? CqWriter.AddOverlays(draft, map, ovs) : draft;
        var tmp = Path.Combine(Path.GetTempPath(), "cqnl_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        try
        {
            var xp = Path.Combine(tmp, "cq.xlsx"); var pp = Path.Combine(tmp, "cq.pdf");
            File.WriteAllBytes(xp, xlsx);
            ExcelPdfExporter.Export(xp, pp);                        // kiểm A4 + đúng 1 trang, sai → ngoại lệ, không chuyển trạng thái
            return (xlsx, File.ReadAllBytes(pp), c, ver);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Số báo cáo của CQ còn đúng là của CQ này: kho đồng bộ (Synology Drive) có thể để 2 máy cấp cùng số trong lúc chưa đồng bộ
    /// kịp — bản giữ số còn lại không phải của CQ này → chặn phát hành, yêu cầu đổi số (không bao giờ ra 2 CQ trùng số).</summary>
    public void EnsureReportNumberOwned(CqVersionRecord c)
    {
        if (c.ReportClaim is null || c.ReportClaimMonth is null) return;
        var parts = c.ReportClaimMonth.Split('-'); var no = c.ReportNo.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m) || !int.TryParse(no[0], out var n)) return;
        var owner = numbers.Owner(y, m, n);
        if (owner is not null && owner.Token.Length > 0 && owner.Token != c.ReportClaim)
            throw new BusinessException($"Số báo cáo {c.ReportNo} đã bị máy {owner.Machine} cấp cho CQ khác cùng lúc (hai máy làm khi chưa đồng bộ kịp). " +
                "Bấm \"Đổi số báo cáo (tạo lại CQ)\" để lấy số mới rồi duyệt lại.");
    }

    /// <summary>PDF của bản nháp (chưa có dấu/chữ ký) — làm nền cho trình kéo-thả vị trí dấu/chữ ký.
    /// markers: ô đánh dấu màu (chỉ dùng để hiệu chỉnh vị trí trên ảnh trang, không lưu vào CQ).</summary>
    public (byte[] Xlsx, byte[] Pdf) RenderDraft(Session s, string cqId, IReadOnlyList<CqOverlay>? markers = null)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        var (c, _) = repo.Get<CqVersionRecord>(T, cqId);
        if (c is null) throw new BusinessException("Không tìm thấy CQ");
        var draft = repo.Store.GetBlob(c.XlsxSha, "xlsx");
        var xlsx = markers is { Count: > 0 } ? CqWriter.AddOverlays(draft, MapOf(c), markers) : draft;
        var tmp = Path.Combine(Path.GetTempPath(), "cqnl_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        try
        {
            var xp = Path.Combine(tmp, "cq.xlsx"); var pp = Path.Combine(tmp, "cq.pdf");
            File.WriteAllBytes(xp, xlsx); ExcelPdfExporter.Export(xp, pp);
            return (xlsx, File.ReadAllBytes(pp));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Xem trước PDF với dấu/chữ ký ở vị trí đang chỉnh — không đổi trạng thái, không ghi kho.</summary>
    public byte[] PreviewApproval(Session s, string cqId, SignerRecord approver, SignerRecord checker, byte[] stampPng, IReadOnlyList<CqOverlayPlacement>? placements = null)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        return Render(cqId, CqExportKind.Signed, approver, checker, stampPng, placements, allowIssued: CanApprove(cqId)).Pdf;
    }

    /// <summary>CQ còn duyệt được (có dấu + chữ ký): đang nháp, hoặc đã phát hành bản khác (vd bản không dấu) nhưng CHƯA có bản ký.</summary>
    public bool CanApprove(string cqId)
    {
        var (c, _) = repo.Get<CqVersionRecord>(T, cqId);
        return c is not null && c.State != CqState.Deleted && !c.Pdfs.ContainsKey(nameof(CqExportKind.Signed)) && !(c.State == CqState.Approved && c.ApproverSigner is not null);
    }

    /// <summary>File Excel bản nháp + dấu + chữ ký (vị trí mặc định) — KHÔNG đổi trạng thái CQ, không xuất PDF. Để người làm sửa tay trong Excel.</summary>
    public byte[] SignedXlsx(Session s, string cqId, SignerRecord approver, SignerRecord checker, byte[] stampPng, IReadOnlyList<CqOverlayPlacement>? placements)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        var (c, _) = repo.Get<CqVersionRecord>(T, cqId);
        if (c is null || c.State == CqState.Deleted) throw new BusinessException("Không tìm thấy CQ");
        var map = MapOf(c);
        CqImage Img(byte[] b, string n) { var (w, h) = CqImages.Size(b); return new CqImage(b, w, h, n); }
        CqOverlay Ov(CqImage im, string role, string frame)
        {
            var p = placements?.FirstOrDefault(x => x.Role == role);
            return p is null ? new CqOverlay(im, Frame: frame) : new CqOverlay(im, Cell: p.Cell, DxMm: p.DxMm, DyMm: p.DyMm, WidthMm: p.WidthMm);
        }
        var ovs = new List<CqOverlay>
        {
            Ov(Img(repo.Store.GetBlob(CqConventions.TransparentSignatureOnCq ? approver.TransparentSha : approver.PaperSha, "png"), "Approver"), "Approver", map.Ranges("ApproverSignatureFrame")[0]),
            Ov(Img(repo.Store.GetBlob(CqConventions.TransparentSignatureOnCq ? checker.TransparentSha : checker.PaperSha, "png"), "Checker"), "Checker", map.Ranges("CheckerSignatureFrame")[0]),
            Ov(Img(stampPng, "Stamp"), "Stamp", map.Ranges("ApproverSignatureFrame")[0]),
        };
        return CqWriter.AddOverlays(repo.Store.GetBlob(c.XlsxSha, "xlsx"), map, ovs);
    }

    // ---------------- Sửa tay trong Excel rồi mới xuất PDF ----------------

    /// <summary>Kiểm tra file Excel người dùng sửa tay đúng là CQ này: đúng sheet template, còn số báo cáo.</summary>
    private void CheckEditedXlsx(CqVersionRecord c, byte[] xlsx)
    {
        if (xlsx.Length < 4 || xlsx[0] != (byte)'P' || xlsx[1] != (byte)'K') throw new BusinessException("File không phải Excel (.xlsx)");
        try { CqWriter.Geometry(xlsx, MapOf(c)); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new BusinessException("File Excel không đúng mẫu CQ của bản này (" + e.Message + ")"); }
        if (!CqWriter.ContainsText(xlsx, c.ReportNo)) throw new BusinessException($"File Excel không còn số báo cáo {c.ReportNo} — có thể đã chọn nhầm file");
    }

    private static byte[] PdfOf(byte[] xlsx)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cqnl_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        try
        {
            var xp = Path.Combine(tmp, "cq.xlsx"); var pp = Path.Combine(tmp, "cq.pdf");
            File.WriteAllBytes(xp, xlsx);
            ExcelPdfExporter.Export(xp, pp);                        // kiểm A4 + đúng 1 trang
            return File.ReadAllBytes(pp);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Xem trước PDF từ file Excel đã sửa tay (không đổi trạng thái, không ghi kho).</summary>
    public byte[] PdfFromEditedXlsx(Session s, string cqId, byte[] xlsx)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        var (c, _) = repo.Get<CqVersionRecord>(T, cqId);
        if (c is null || c.State == CqState.Deleted) throw new BusinessException("Không tìm thấy CQ");
        CheckEditedXlsx(c, xlsx);
        return PdfOf(xlsx);
    }

    /// <summary>File Excel của một kiểu bản in để người làm SỬA TAY trước khi in: Signed = nháp + dấu + chữ ký; StampOnly = nháp + dấu;
    /// Blank = bản nháp (ký tay). Dấu / chữ ký là ảnh rời (kéo được). Không đổi trạng thái CQ.</summary>
    public byte[] KindXlsx(Session s, string cqId, CqExportKind kind, SignerRecord? approver, SignerRecord? checker, byte[]? stampPng,
        IReadOnlyList<CqOverlayPlacement>? placements = null)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        if (kind == CqExportKind.Signed)
        {
            if (approver is null || checker is null) throw new BusinessException("Chọn Người duyệt / Người kiểm tra trước");
            if (stampPng is null) throw new BusinessException("Chưa có con dấu (Cài đặt → Chữ ký và con dấu)");
            return SignedXlsx(s, cqId, approver, checker, stampPng, placements);
        }
        var (c, _) = repo.Get<CqVersionRecord>(T, cqId);
        if (c is null || c.State == CqState.Deleted) throw new BusinessException("Không tìm thấy CQ");
        var draft = repo.Store.GetBlob(c.XlsxSha, "xlsx");
        if (kind == CqExportKind.Blank) return draft;
        if (stampPng is null) throw new BusinessException("Chưa có con dấu (Cài đặt → Chữ ký và con dấu)");
        var map = MapOf(c); var (w, h) = CqImages.Size(stampPng);
        var p = placements?.FirstOrDefault(x => x.Role == "Stamp");
        var im = new CqImage(stampPng, w, h, "Stamp");
        var ov = p is null ? new CqOverlay(im, Frame: map.Ranges("ApproverSignatureFrame")[0]) : new CqOverlay(im, Cell: p.Cell, DxMm: p.DxMm, DyMm: p.DyMm, WidthMm: p.WidthMm);
        return CqWriter.AddOverlays(draft, map, new[] { ov });
    }

    /// <summary>Phát hành bản in từ file Excel ĐÃ SỬA TAY, cho MỌI kiểu in. Signed → như duyệt (ApproveEdited, bản duyệt bất biến);
    /// StampOnly / Blank → PDF đúng như file Excel đã sửa; đã có bản kiểu này → thay bằng bản mới, bản cũ vẫn giữ trong kho + lịch sử.
    /// File Excel đã sửa được lưu cùng CQ; ghi cảnh báo "sửa tay" + nhật ký để truy vết.</summary>
    public CqVersionRecord IssueEdited(Session s, string cqId, CqExportKind kind, byte[] xlsx, SignerRecord? approver, SignerRecord? checker)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        if (kind == CqExportKind.Signed)
        {
            if (approver is null || checker is null) throw new BusinessException("Chọn Người duyệt / Người kiểm tra trước");
            var r = ApproveEdited(s, cqId, xlsx, approver, checker);
            return repo.Update<CqVersionRecord>(T, cqId, s.User.Username, c =>
                c.EditedPrints.Add(new CqEditedPrint(nameof(CqExportKind.Signed), r.Xlsxs[nameof(CqExportKind.Signed)], r.Pdfs[nameof(CqExportKind.Signed)], s.User.Username, DateTimeOffset.Now, null)));
        }
        var (cur, ver) = repo.Get<CqVersionRecord>(T, cqId);
        if (cur is null || cur.State == CqState.Deleted) throw new BusinessException("Không tìm thấy CQ");
        CheckEditedXlsx(cur, xlsx);
        EnsureReportNumberOwned(cur);
        var pdf = PdfOf(xlsx);
        var pdfSha = repo.Store.PutBlob(pdf, "pdf"); var xSha = repo.Store.PutBlob(xlsx, "xlsx");
        var k = kind.ToString();
        var prev = cur.Pdfs.GetValueOrDefault(k);
        cur.Pdfs[k] = pdfSha; cur.Xlsxs[k] = xSha; cur.PdfSha ??= pdfSha;
        cur.EditedPrints.Add(new CqEditedPrint(k, xSha, pdfSha, s.User.Username, DateTimeOffset.Now, prev));
        var kindText = kind == CqExportKind.StampOnly ? "có dấu, chưa ký" : "ký tay";
        cur.Warnings.Add($"Bản in {kindText} được SỬA TAY trong Excel trước khi xuất PDF ({s.User.Username}, {DateTimeOffset.Now:dd/MM/yyyy HH:mm})");
        if (cur.State == CqState.Draft) { cur.State = CqState.Approved; cur.ApprovedBy = s.User.Username; cur.ApprovedAt = DateTimeOffset.Now; }
        repo.Put(T, cqId, cur, ver, s.User.Username);
        audit.Write(s.User.Username, "cq.issue", cqId, new { kind = k, edited = true, pdfSha, xlsxSha = xSha, previousPdfSha = prev });
        return cur;
    }

    /// <summary>Duyệt từ file Excel người làm ĐÃ SỬA TAY (nội dung, vị trí dấu / chữ ký): PDF xuất đúng như file Excel đó.
    /// Lưu cả file Excel đã sửa làm bản duyệt; ghi cảnh báo "sửa tay" vào CQ + nhật ký để truy vết.</summary>
    public CqVersionRecord ApproveEdited(Session s, string cqId, byte[] xlsx, SignerRecord approver, SignerRecord checker)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        var lib = repo.Get<SharedSettings>("settings", "shared").Value?.Signers ?? new();
        if (!lib.Any(l => l.Id == approver.Id && l.Active) || !lib.Any(l => l.Id == checker.Id && l.Active))
            throw new BusinessException("Người duyệt / người kiểm tra không có trong thư viện chữ ký");
        if (!CanApprove(cqId)) throw new BusinessException("CQ đã duyệt (có dấu + chữ ký) — không duyệt lại được");
        var (c, ver) = repo.Get<CqVersionRecord>(T, cqId);
        if (c is null || c.State == CqState.Deleted) throw new BusinessException("Không tìm thấy CQ");
        CheckEditedXlsx(c, xlsx);
        EnsureReportNumberOwned(c);
        var pdf = PdfOf(xlsx);
        var pdfSha = repo.Store.PutBlob(pdf, "pdf"); var xSha = repo.Store.PutBlob(xlsx, "xlsx");
        c.Pdfs[nameof(CqExportKind.Signed)] = pdfSha; c.Xlsxs[nameof(CqExportKind.Signed)] = xSha;
        c.ApprovedXlsxSha = xSha; c.PdfSha = pdfSha; c.Placements = null; c.ApproverSigner = approver.Name; c.CheckerSigner = checker.Name;
        c.Warnings.Add($"Bản duyệt được SỬA TAY trong Excel trước khi xuất PDF ({s.User.Username}, {DateTimeOffset.Now:dd/MM/yyyy HH:mm}) — nội dung in theo file Excel đã sửa");
        if (c.State == CqState.Draft) { c.State = CqState.Approved; c.ApprovedBy = s.User.Username; c.ApprovedAt = DateTimeOffset.Now; }
        repo.Put(T, cqId, c, ver, s.User.Username);
        audit.Write(s.User.Username, "cq.approve", cqId, new { kind = "Signed", edited = true, approver = approver.Name, checker = checker.Name, pdfSha, xlsxSha = xSha });
        return c;
    }

    /// <summary>Duyệt: chọn người duyệt/kiểm tra (từ thư viện chữ ký do admin quản lý), vị trí dấu/chữ ký có thể chỉnh; xuất PDF A4.
    /// Bản duyệt = bản nháp đã cấp số + dấu/chữ ký. Đơn sửa sau khi tạo CQ → cảnh báo (cần tạo CQ mới nếu muốn dữ liệu mới).</summary>
    public CqVersionRecord Approve(Session s, string cqId, SignerRecord approver, SignerRecord checker, byte[] stampPng, IReadOnlyList<CqOverlayPlacement>? placements = null)
        => Issue(s, cqId, CqExportKind.Signed, approver, checker, stampPng, placements);

    /// <summary>Phát hành bản in của CQ: có dấu + chữ ký / có dấu chưa ký / trắng để ký tay. Cùng một CQ (cùng số báo cáo, cùng dữ liệu)
    /// có thể xuất nhiều kiểu bản in; dữ liệu CQ bất biến. Lần phát hành đầu chuyển CQ sang "Đã duyệt/phát hành".</summary>
    public CqVersionRecord Issue(Session s, string cqId, CqExportKind kind, SignerRecord? approver, SignerRecord? checker, byte[]? stampPng,
        IReadOnlyList<CqOverlayPlacement>? placements = null)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        var (cur, _) = repo.Get<CqVersionRecord>(T, cqId);
        if (cur is not null && cur.Pdfs.TryGetValue(kind.ToString(), out _) && cur.State == CqState.Approved) return cur;   // đã có bản này
        if (cur is not null && cur.State == CqState.Approved && kind == CqExportKind.Signed && cur.ApproverSigner is not null && cur.PdfSha is not null && cur.Pdfs.Count == 0)
            return cur;                                                                                                       // bản duyệt cũ (trước 0.4)
        if (cur is not null) EnsureReportNumberOwned(cur);
        var (xlsx, pdf, c, ver) = Render(cqId, kind, approver, checker, stampPng, placements, allowIssued: true);
        var (_, orderVer) = orders.Get(c.OrderKey);
        if (c.OrderVersion > 0 && orderVer != c.OrderVersion && !c.Warnings.Any(w => w.StartsWith("Đơn đã được sửa")))
            c.Warnings.Add($"Đơn đã được sửa sau khi tạo CQ (v{c.OrderVersion} → v{orderVer}); bản in dùng dữ liệu lúc tạo CQ");
        var pdfSha = repo.Store.PutBlob(pdf, "pdf");
        c.Pdfs[kind.ToString()] = pdfSha;
        c.Xlsxs[kind.ToString()] = kind == CqExportKind.Blank ? c.XlsxSha : repo.Store.PutBlob(xlsx, "xlsx");
        if (kind == CqExportKind.Signed)
        {
            c.ApprovedXlsxSha = repo.Store.PutBlob(xlsx, "xlsx");
            c.PdfSha = pdfSha; c.Placements = placements?.ToList(); c.ApproverSigner = approver!.Name; c.CheckerSigner = checker!.Name;
        }
        else c.PdfSha ??= pdfSha;
        if (c.State == CqState.Draft) { c.State = CqState.Approved; c.ApprovedBy = s.User.Username; c.ApprovedAt = DateTimeOffset.Now; }
        repo.Put(T, cqId, c, ver, s.User.Username);
        audit.Write(s.User.Username, kind == CqExportKind.Signed ? "cq.approve" : "cq.issue", cqId,
            new { kind = kind.ToString(), approver = approver?.Name, checker = checker?.Name, pdfSha, placements });
        return c;
    }

    public void MarkPrinted(Session s, string cqId, bool printed)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        repo.Update<CqVersionRecord>(T, cqId, s.User.Username, c =>
        {
            if (c.State == CqState.Deleted) throw new BusinessException("CQ đã xóa");
            c.Printed = printed; c.PrintedBy = s.User.Username; c.PrintedAt = DateTimeOffset.Now;
        });
        audit.Write(s.User.Username, printed ? "cq.printed" : "cq.unprinted", cqId);
    }

    /// <summary>Xóa = đánh dấu (không xóa vật lý file/blob), bắt buộc lý do; hiện trong Lịch sử xóa.</summary>
    public void Delete(Session s, string cqId, string reason)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) throw new BusinessException("Bắt buộc nhập lý do xóa");
        repo.Update<CqVersionRecord>(T, cqId, s.User.Username, c =>
        {
            if (c.State == CqState.Deleted) throw new BusinessException("CQ đã bị xóa trước đó");
            c.StateWhenDeleted = c.State.ToString(); c.State = CqState.Deleted; c.DeletedBy = s.User.Username; c.DeletedAt = DateTimeOffset.Now; c.DeleteReason = reason.Trim();
        });
        audit.Write(s.User.Username, "cq.delete", cqId, reason.Trim());
    }

    // ---------- dữ liệu CQ từ đơn ----------
    private CqData BuildData(OrderRecord o, TemplateMap map, byte[] tplBytes, Evaluation ev)
    {
        var q = o.Qc; var e = o.Erp; var d = new CqData();
        var aliases = repo.Get<SharedSettings>("settings", "shared").Value?.CustomerAliases;
        d.Values["Customer"] = q.CustomerOverride ?? SettingsService.AliasOf(aliases, e.GetValueOrDefault("Customer")) ?? e.GetValueOrDefault("Customer");
        d.Values["Material"] = q.SteelGrade ?? e.GetValueOrDefault("GradeFromErp");
        d.Values["Size"] = q.Size; d.Values["PartNo"] = q.PartNo; d.Values["PartName"] = q.PartName;
        d.Values["Dngc"] = o.Key.Split('|')[0];
        d.Values["ReceivedDate"] = DateText(e.GetValueOrDefault("ReceivedAt"));
        d.Values["CheckDate"] = q.Hardness.CheckDate;
        d.Values["HardnessRequirement"] = q.HardnessRequirement ?? e.GetValueOrDefault("HardnessRequirementErp");
        d.Values["HardnessUnit"] = q.Hardness.Unit;
        d.Values["HardnessTolerance"] = string.IsNullOrWhiteSpace(q.Tolerance) ? "-" : q.Tolerance;
        d.Values["AppearanceResult"] = Dash(q.Hardness.Appearance); d.Values["CurvatureRequirement"] = Dash(q.Hardness.CurvatureRequirement);
        d.Values["CurvatureResult"] = Dash(q.Hardness.CurvatureResult); d.Values["OtherResult"] = Dash(q.Hardness.Other);
        d.Values["Remark"] = HardnessEvaluator.Remark(ev.Status, q.Hardness.Note);
        // nhóm gộp: số lượng / khối lượng cộng dồn các dòng trong nhóm
        var group = new List<OrderRecord> { o };
        foreach (var ck in q.MergedChildren ?? new()) if (orders.Get(ck).Order is { } co && co.Qc.MergeGroup == o.Key) group.Add(co);
        decimal? Sum(string f) { decimal t = 0; foreach (var g in group) { if (!decimal.TryParse(g.Erp.GetValueOrDefault(f), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return null; t += v; } return t; }
        if (Sum("Quantity") is { } qty) d.Numbers["Quantity"] = qty;
        if (Sum("WeightKg") is { } kg) d.Numbers["WeightKg"] = kg;
        d.Samples.AddRange(HardnessEvaluator.CqSampleCellsAscending(q.Hardness.Samples.Cast<string?>().ToList()));
        d.QuenchTemps = q.QuenchTemps; d.RamValues = q.RamValues;
        foreach (var mf in map.Manual) d.Manual[mf.Key] = ManualValues.Value(mf, o, ev);
        // ảnh xuất hàng: bản đã cắt/làm sáng + số cân (đã xác nhận) góc dưới trái kèm "kg"
        foreach (var im in q.Images.Where(i => i.Kind == "Sample" && i.OnCq).OrderBy(i => i.AddedOrder))
        {
            var b = repo.Store.GetBlob(im.NormalizedSha ?? im.BlobSha, im.NormalizedSha is null ? im.Ext : "jpg");
            if (im.ConfirmedValue is not null)
            {
                var (w, h) = CqImages.Size(b);
                var mm = CqWriter.PrintedHeightMm(tplBytes, map, "SampleImageFrame", w, h);
                b = CqImages.CornerLabel(b, CqConventions.ScaleLabel(im.ConfirmedValue), h * 4.0 / mm);
            }
            var (w2, h2) = CqImages.Size(b); d.SampleImages.Add(new CqImage(b, w2, h2, "SampleImage"));
        }
        // ảnh khác (ảnh kim tương 300X — mẫu Thấm Nitơ): giữ nguyên ảnh, không ghi số
        foreach (var im in q.Images.Where(i => i.Kind == "Other" && i.OnCq).OrderBy(i => i.AddedOrder))
        {
            var b = repo.Store.GetBlob(im.NormalizedSha ?? im.BlobSha, im.NormalizedSha is null ? im.Ext : "jpg");
            var (w3, h3) = CqImages.Size(b); d.OtherImages.Add(new CqImage(b, w3, h3, "OtherImage"));
        }
        // ảnh độ cứng: sắp tăng dần + bỏ trùng (≥3 ảnh), thẻ ô đều (không nhãn số)
        var hp = q.Images.Where(i => i.Kind == "Hardness" && i.OnCq).ToList();
        var (kept, _) = HardnessPhotoLayout.Arrange(hp.Select(i => new HardnessPhoto(i.Id,
            decimal.TryParse(i.ConfirmedValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null,
            d.Values["Dngc"]!, d.Values["Customer"] ?? "", d.Values["Material"] ?? "", i.AddedOrder)).ToList());
        // KHÔNG in số dưới ảnh độ cứng: ảnh màn hình máy đo đã hiện số (yêu cầu 16/11). Số cân trên ảnh xuất hàng vẫn giữ (ở trên).
        foreach (var k in kept)
        {
            var im = hp.First(x => x.Id == k.Id);
            var b = repo.Store.GetBlob(im.NormalizedSha ?? im.BlobSha, im.NormalizedSha is null ? im.Ext : "jpg");
            var t = CqImages.Tile(b, null, 1.0);
            var (w, h) = CqImages.Size(t); d.HardnessImages.Add(new CqImage(t, w, h, "HardnessImage"));
        }
        return d;
    }

    private static string Dash(string? v) => string.IsNullOrWhiteSpace(v) ? "-" : v;
    private static string? DateText(string? iso) => DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt.ToString("dd/MM/yyyy") : iso;
}

public sealed record CqOverlayPlacement(string Role, string Cell, double DxMm, double DyMm, double WidthMm);

/// <summary>Đường dẫn tài nguyên cài kèm ứng dụng (template gốc, bản đồ ô, preset ảnh, tessdata) — không hard-code đường dẫn người dùng.</summary>
public sealed class AppPaths(string templatesDir, string mapsDir, string presetsDir, string tessdataDir)
{
    public static AppPaths FromInstall(string assetsDir) => new(Path.Combine(assetsDir, "templates"), Path.Combine(assetsDir, "template-maps"),
        Path.Combine(assetsDir, "image-presets"), Path.Combine(assetsDir, "tessdata"));
    public string Template(string fileName) => Path.Combine(templatesDir, fileName);
    public string TemplateMap(string id) => Path.Combine(mapsDir, id + ".json");
    public string Preset(string name) => Path.Combine(presetsDir, name);
    public string Tessdata => tessdataDir;
    public IReadOnlyList<string> TemplateIds() => Directory.EnumerateFiles(mapsDir, "*.json").Select(Path.GetFileNameWithoutExtension).Select(x => x!).OrderBy(x => x).ToList();
}

/// <summary>Cấu hình chung lưu trong kho (đồng bộ mọi máy): thư viện chữ ký, con dấu, vị trí mặc định dấu/chữ ký theo template. Chỉ admin sửa.</summary>
public sealed class SettingsService(Repo repo, AuditLog audit)
{
    private const string T = "settings", Id = "shared";
    public (SharedSettings Value, int Version) Get() { var (v, ver) = repo.Get<SharedSettings>(T, Id); return (v ?? new SharedSettings(), ver); }

    /// <summary>Đổi đường dẫn lưu trong cài đặt chung ("{KHO}\ERP", đường dẫn cũ của máy khác) thành đường dẫn THẬT trên máy này,
    /// và ngược lại (thư mục nằm trong kho → lưu dạng "{KHO}\…" để mọi máy đồng bộ Synology Drive dùng chung được). Workspace đặt.</summary>
    public Func<string?, string?> ResolveFolder { get; set; } = p => p;
    public Func<string?, string?> TokenizeFolder { get; set; } = p => p;
    public string? ErpFolderPath() => ResolveFolder(Get().Value.ErpFolder);
    public string? CqFolderPath() => ResolveFolder(Get().Value.CqFolder);

    private SharedSettings Save(Session s, Action<SharedSettings> edit, string action, object? detail)
    {
        AuthService.Require(s, Role.Admin);
        var (v, ver) = Get(); edit(v); repo.Put(T, Id, v, ver, s.User.Username);
        audit.Write(s.User.Username, action, Id, detail); return v;
    }

    /// <summary>Thêm/thay chữ ký: giữ ảnh gốc; thư viện = bản nền giấy A4 + nét đậm; CQ = bản trong suốt (cùng nét).</summary>
    public SignerRecord AddSigner(Session s, string name, byte[] original, string ext)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessException("Thiếu tên người ký");
        var rec = new SignerRecord
        {
            Name = name.Trim(), OriginalSha = repo.Store.PutBlob(original, ext), OriginalExt = ext.TrimStart('.'),
            PaperSha = repo.Store.PutBlob(CqImages.Signature(original, transparent: false), "png"),
            TransparentSha = repo.Store.PutBlob(CqImages.Signature(original, transparent: true), "png"),
        };
        Save(s, v =>
        {
            foreach (var old in v.Signers.Where(x => x.Active && string.Equals(x.Name, rec.Name, StringComparison.OrdinalIgnoreCase)))
            {
                old.Active = false;                                                    // thay chữ ký cũ, vẫn giữ lịch sử
                if (v.DefaultApproverId == old.Id) v.DefaultApproverId = rec.Id;       // người ký mặc định chuyển sang chữ ký mới
                if (v.DefaultCheckerId == old.Id) v.DefaultCheckerId = rec.Id;
            }
            v.Signers.Add(rec);
        }, "signer.add", new { rec.Name, rec.OriginalSha });
        return rec;
    }

    /// <summary>Cập nhật bảng mác (dùng chung mọi máy). Đọc thử trước; lưu bản gốc nguyên vẹn theo hash.</summary>
    public SteelGradeTable SetSteelTable(Session s, byte[] xlsx, string fileName)
    {
        AuthService.Require(s, Role.Admin);
        var tmp = Path.Combine(Path.GetTempPath(), "cqnl_steel_" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.WriteAllBytes(tmp, xlsx);
        SteelGradeTable t;
        try { t = SteelGradeReader.Read(tmp); } catch (Exception e) { throw new BusinessException("File bảng mác không đọc được: " + e.Message); } finally { File.Delete(tmp); }
        if (t.Grades.Count == 0) throw new BusinessException("File bảng mác không có dòng mác nào");
        var sha = repo.Store.PutBlob(xlsx, "xlsx");
        Save(s, v => { v.SteelTableSha = sha; v.SteelTableName = fileName; }, "steel.set", new { fileName, sha, grades = t.Grades.Count, issues = t.Issues.Count });
        return t;
    }

    // ---------- tên viết tắt khách hàng (in trên CQ) ----------
    public static string CustomerKey(string? erpName) => System.Text.RegularExpressions.Regex.Replace((erpName ?? "").Trim(), @"\s+", " ").ToLowerInvariant();
    public static string? AliasOf(IReadOnlyDictionary<string, string>? aliases, string? erpName)
        => aliases is not null && !string.IsNullOrWhiteSpace(erpName) && aliases.TryGetValue(CustomerKey(erpName), out var a) && !string.IsNullOrWhiteSpace(a) ? a : null;

    /// <summary>Ghi nhớ / sửa / xoá (shortName rỗng) tên viết tắt cho một khách hàng ERP. QC và Quản trị đều làm được.</summary>
    public void SetCustomerAlias(Session s, string erpName, string? shortName) => SetCustomerAliases(s, new Dictionary<string, string?> { [erpName] = shortName }, "customer.alias");

    public int SetCustomerAliases(Session s, IReadOnlyDictionary<string, string?> map, string action = "customer.alias.import")
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        if (map.Count == 0) return 0;
        var (v, ver) = Get(); var n = 0;
        foreach (var (erp, sh) in map)
        {
            if (string.IsNullOrWhiteSpace(erp)) continue;
            var k = CustomerKey(erp);
            if (string.IsNullOrWhiteSpace(sh)) { if (v.CustomerAliases.Remove(k)) n++; }
            else if (v.CustomerAliases.GetValueOrDefault(k) != sh.Trim()) { v.CustomerAliases[k] = sh.Trim(); n++; }
        }
        if (n == 0) return 0;
        repo.Put(T, Id, v, ver, s.User.Username);
        audit.Write(s.User.Username, action, Id, new { count = n, sample = map.Take(5).ToDictionary(x => x.Key, x => x.Value) });
        return n;
    }

    /// <summary>Thư mục lưu CQ chung: phải tồn tại và GHI được (kiểm bằng file thử rồi xoá).</summary>
    public void SetCqFolder(Session s, string? folder)
    {
        AuthService.Require(s, Role.Admin);
        folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        if (folder is not null) Workspace.ProbeWritable(ResolveFolder(folder)!);
        var saved = TokenizeFolder(folder);
        Save(s, v => v.CqFolder = saved, "cq.folder", saved);
    }

    public void SetErpFolder(Session s, string? folder)
    {
        folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        if (folder is not null && !Directory.Exists(ResolveFolder(folder))) throw new BusinessException("Không truy cập được thư mục: " + folder);
        var saved = TokenizeFolder(folder);
        Save(s, v => v.ErpFolder = saved, "erp.folder", saved);
    }

    /// <summary>Đặt con dấu dùng chung: PNG nền trong suốt giữ nguyên; ảnh chụp / quét (JPG, PNG nền trắng) → tự bỏ nền thành trong suốt.
    /// Ảnh gốc được lưu kèm (truy vết).</summary>
    public void SetStamp(Session s, byte[] image)
    {
        AuthService.Require(s, Role.Admin);
        byte[] png;
        try { png = CqImages.HasTransparency(image) ? image : CqImages.StampTransparent(image); }
        catch (ArgumentException e) { throw new BusinessException(e.Message.StartsWith("Không thấy") ? e.Message : "File không phải ảnh hợp lệ"); }
        var origSha = repo.Store.PutBlob(image, "img");
        var sha = repo.Store.PutBlob(png, "png");
        Save(s, v => v.StampSha = sha, "stamp.set", new { sha, origSha });
    }

    /// <summary>Xem trước con dấu sau khi xử lý (không lưu).</summary>
    public static byte[] PrepareStamp(byte[] image) => CqImages.HasTransparency(image) ? image : CqImages.StampTransparent(image);
    public byte[]? Stamp() { var v = Get().Value; return v.StampSha is null ? null : repo.Store.GetBlob(v.StampSha, "png"); }
    /// <summary>Đổi tên người ký (CQ đã duyệt giữ nguyên ảnh chữ ký đã in).</summary>
    public void RenameSigner(Session s, string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessException("Nhập tên người ký");
        name = name.Trim();
        var cur = Get().Value;
        if (cur.Signers.Any(x => x.Active && x.Id != id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new BusinessException($"Đã có người ký tên \"{name}\"");
        Save(s, v => (v.Signers.FirstOrDefault(x => x.Id == id) ?? throw new BusinessException("Không tìm thấy chữ ký")).Name = name, "signer.rename", new { id, name });
    }

    /// <summary>Bỏ chữ ký khỏi danh sách dùng (không xoá ảnh — CQ đã duyệt vẫn tra được). Đang là người ký mặc định → bỏ mặc định.</summary>
    public void RemoveSigner(Session s, string id) => Save(s, v =>
    {
        var x = v.Signers.FirstOrDefault(r => r.Id == id) ?? throw new BusinessException("Không tìm thấy chữ ký");
        x.Active = false;
        if (v.DefaultApproverId == id) v.DefaultApproverId = null;
        if (v.DefaultCheckerId == id) v.DefaultCheckerId = null;
    }, "signer.remove", id);

    public void SetDefaults(Session s, string? approverId, string? checkerId) => Save(s, v => { v.DefaultApproverId = approverId; v.DefaultCheckerId = checkerId; }, "signer.defaults", new { approverId, checkerId });
    public void SetPlacements(Session s, string templateId, List<CqOverlayPlacement> p) => Save(s, v => v.Placements[templateId] = p, "placement.set", new { templateId, p });
}

/// <summary>Thêm ảnh vào đơn: lưu gốc bất biến → chuẩn hóa theo preset người dùng duyệt → OCR GỢI Ý (không tự xác nhận).</summary>
public sealed class ImageService(Repo repo, OrderService orders, AppPaths paths)
{
    public InspectionImage Add(Session s, string orderKey, int baseVersion, byte[] bytes, string ext, string kind)
    {
        AuthService.Require(s, Role.Admin, Role.Qc);
        var (im, blobs) = Process(bytes, ext, kind, paths);
        Stamp(im, s.User.Username);
        foreach (var (b, e) in blobs) repo.Store.PutBlob(b, e);
        orders.EditQc(s, orderKey, baseVersion, q => Attach(q, im), $"image.add {kind} {im.BlobSha[..12]}");
        return im;
    }

    /// <summary>Ghi người tải, thời điểm và lịch sử "tải lên" + "xử lý tự động" (bước xử lý, kết quả OCR, cảnh báo).</summary>
    public static void Stamp(InspectionImage im, string user)
    {
        var now = DateTimeOffset.Now;
        im.AddedBy ??= user; im.AddedAt ??= now;
        im.History.Add(new ImageHistoryEntry { At = now, By = user, Action = "Tải ảnh lên", Detail = $"gốc {im.BlobSha[..12]}… ({im.Ext}), loại {im.Kind}" });
        if (im.Vision is { } v)
            im.History.Add(new ImageHistoryEntry { At = now, By = user, Action = "Xử lý tự động", Detail = VisionSummary(v) });
    }

    public static string VisionSummary(ImageVision v) =>
        v.Error is not null ? "lỗi: " + v.Error
        : string.Join(" · ", v.Passes.Take(2).Select(p => $"{p.Source}: {p.Value ?? "—"}")) + (v.NeedsReview ? " · CẦN XÁC NHẬN: " + string.Join("; ", v.Flags.Take(2)) : " · khớp");

    public static void Attach(QcData q, InspectionImage im)
    {
        im.AddedOrder = q.Images.Select(x => x.AddedOrder).DefaultIfEmpty(-1).Max() + 1;
        q.Images.Add(im);
    }

    /// <summary>Xử lý ảnh HOÀN TOÀN trên máy (không cần kho): mã gốc, chuẩn hóa theo preset, OCR gợi ý. Trả về các blob cần lưu
    /// (gốc + bản chuẩn hóa) → dùng được cả online (ghi thẳng kho) và offline (đưa vào hàng đợi).</summary>
    /// <summary>Tự nhận loại ảnh: màn hình Mitutoyo đọc được số HRC → ảnh độ cứng; đầu cân LED đỏ đọc được số → ảnh sản phẩm (xuất hàng);
    /// màn hình dynaROCK đọc được số HRC → ảnh độ cứng; còn lại → ảnh sản phẩm. Đọc số cân TRƯỚC dynaROCK để ảnh sản phẩm có cân
    /// (số cân 10–75 kg) không bị nhận nhầm thành ảnh độ cứng.</summary>
    public static string DetectKind(byte[] bytes, AppPaths paths)
    {
        static bool Hrc(string? v) => v is not null && decimal.TryParse(v.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d >= 10 && d <= 75;
        try
        {
            var im = ImageNormalizer.Load(bytes);
            if (ImageNormalizer.MitutoyoLcd(im) is not null && Hrc(MitutoyoReader.Read(im).Value)) return "Hardness";
            if (ScaleLedReader.Read(im).Value is not null) return "Sample";
            if (Hrc(DynaRockReader.Read(im, paths.Tessdata).Value)) return "Hardness";
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { }
        return "Sample";
    }

    public static (InspectionImage Image, List<(byte[] Bytes, string Ext)> Blobs) Process(byte[] bytes, string ext, string kind, AppPaths paths)
    {
        if (kind is not ("Sample" or "Hardness" or "Other")) throw new BusinessException("Loại ảnh không hợp lệ");
        if (bytes.Length == 0) throw new BusinessException("File ảnh rỗng");
        if (bytes.Length > 40 * 1024 * 1024) throw new BusinessException("Ảnh quá lớn (> 40 MB)");
        ext = ext.TrimStart('.').ToLowerInvariant();
        try { CqImages.Size(bytes); } catch { throw new BusinessException("File không phải ảnh hợp lệ"); }
        var blobs = new List<(byte[], string)> { (bytes, ext) };
        var im = new InspectionImage { BlobSha = FileStore.Sha256Hex(bytes), Ext = ext, Kind = kind };
        void Norm(NormalizeResult n) { blobs.Add((n.Jpeg, "jpg")); im.NormalizedSha = FileStore.Sha256Hex(n.Jpeg); im.Crop = new[] { n.Crop.X, n.Crop.Y, n.Crop.Width, n.Crop.Height }; }
        MeterReading? origReading = null;
        try
        {
            if (kind == "Sample")
            {
                var r = ScaleLedReader.Read(ImageNormalizer.Load(bytes));            // đọc số cân trên ẢNH GỐC
                origReading = r;
                im.OcrRaw = r.Raw; im.OcrValue = r.Value; im.OcrConfidence = r.Confidence;
                var n = ImageNormalizer.NormalizeProduct(bytes, ImageNormalizer.LoadPreset(paths.Preset("san-pham-cq.json")));
                Norm(n); im.NeedsManualCrop = n.NeedsManualCrop;
            }
            else if (kind == "Hardness")
            {
                var r = HardnessOcr.Read(bytes, paths.Tessdata);
                origReading = r;
                im.OcrRaw = r.Raw; im.OcrValue = r.Value; im.OcrConfidence = r.Confidence;
                if (r.Device.StartsWith("Mitutoyo", StringComparison.OrdinalIgnoreCase))
                {
                    var n = ImageNormalizer.NormalizeMitutoyo(bytes, ImageNormalizer.LoadPreset(paths.Preset("mitutoyo-cq.json")));
                    if (!n.NeedsManualCrop) Norm(n); else im.NeedsManualCrop = true;
                }
            }
        }
        catch (Exception e) when (e is not (PermissionException or ConflictException)) { im.NeedsManualCrop = true; im.OcrRaw = "lỗi xử lý ảnh: " + e.Message; }
        // module xử lý ảnh + kiểm soát OCR (ảnh HD màu, ảnh in đen trắng, vùng số đo phóng to, so sánh OCR gốc / xử lý)
        var (vis, vb) = ImageVisionService.Analyze(bytes, kind, origReading, paths);
        im.Vision = vis; blobs.AddRange(vb);
        return (im, blobs);
    }

    /// <summary>Cắt tay trên ảnh gốc (xử lý trên máy). Trả về blob ảnh mới.</summary>
    public static byte[] CropBytes(byte[] original, InspectionImage im, System.Drawing.Rectangle crop, AppPaths paths)
    {
        var preset = im.Kind == "Sample" ? "san-pham-cq.json" : "mitutoyo-cq.json";
        return ImageNormalizer.NormalizeManual(original, crop, ImageNormalizer.LoadPreset(paths.Preset(preset))).Jpeg;
    }

    /// <summary>QC xác nhận/sửa số đọc được. Chuỗi giữ nguyên chữ số và dấu thập phân như trên màn hình máy (dấu phẩy → chấm).</summary>
    public void Confirm(Session s, string orderKey, int baseVersion, string imageId, string value)
    {
        var v = NormalizeValue(value);
        orders.EditQc(s, orderKey, baseVersion, q => ApplyConfirm(q, imageId, v, s.User.Username), $"image.confirm {imageId} = {v}");
    }

    public static string NormalizeValue(string value)
    {
        var v = (value ?? "").Trim().Replace(',', '.');
        if (!decimal.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) || d <= 0) throw new BusinessException("Giá trị không hợp lệ: " + value);
        return v;
    }

    /// <summary>Ghi giá trị QC xác nhận vào ảnh (dùng chung online/offline). Ảnh độ cứng: 10–75 HRC.</summary>
    public static void ApplyConfirm(QcData q, string imageId, string normalizedValue, string user)
    {
        var d = decimal.Parse(normalizedValue, CultureInfo.InvariantCulture);
        var im = q.Images.FirstOrDefault(i => i.Id == imageId) ?? throw new BusinessException("Không tìm thấy ảnh");
        if (im.Kind == "Hardness" && (d < 10 || d > 75)) throw new BusinessException($"{normalizedValue} ngoài khoảng HRC hợp lý (10–75)");
        var before = im.ConfirmedValue;
        im.ConfirmedValue = normalizedValue; im.ConfirmedBy = user; im.ConfirmedAt = DateTimeOffset.Now;
        var ocr = im.OcrValue ?? im.Vision?.Passes.Select(p => p.Value).FirstOrDefault(x => x is not null);
        im.History.Add(new ImageHistoryEntry { At = im.ConfirmedAt.Value, By = user, Action = before is null ? "Xác nhận số" : "Sửa số đã xác nhận",
            Detail = $"{normalizedValue}" + (before is not null && before != normalizedValue ? $" (trước: {before})" : "") + (ocr is not null && ocr != normalizedValue ? $" — khác số máy đọc {ocr}" : "") });
    }

    public void ManualCrop(Session s, string orderKey, int baseVersion, string imageId, System.Drawing.Rectangle crop)
    {
        var (o, _) = orders.Get(orderKey); var im = o?.Qc.Images.FirstOrDefault(i => i.Id == imageId) ?? throw new BusinessException("Không tìm thấy ảnh");
        var preset = im.Kind == "Sample" ? "san-pham-cq.json" : "mitutoyo-cq.json";
        var n = ImageNormalizer.NormalizeManual(repo.Store.GetBlob(im.BlobSha, im.Ext), crop, ImageNormalizer.LoadPreset(paths.Preset(preset)));
        var sha = repo.Store.PutBlob(n.Jpeg, "jpg");
        orders.EditQc(s, orderKey, baseVersion, q => { var x = q.Images.First(i => i.Id == imageId); x.NormalizedSha = sha; x.Crop = new[] { crop.X, crop.Y, crop.Width, crop.Height }; x.NeedsManualCrop = false; }, $"image.crop {imageId}");
    }
}

/// <summary>Sao lưu / kiểm tra / khôi phục kho. Restore chỉ vào thư mục TRỐNG (không ghi đè kho đang chạy).</summary>
public static class BackupService
{
    private static readonly string[] Skip = { "staging", "locks", "backup" };

    public static string Backup(string storeRoot, string destRoot)
    {
        var dest = Path.Combine(destRoot, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dest);
        var manifest = new Dictionary<string, string>();
        foreach (var f in Directory.EnumerateFiles(storeRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(storeRoot, f);
            if (Skip.Any(sk => rel.StartsWith(sk + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            var target = Path.Combine(dest, rel); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
            manifest[rel.Replace('\\', '/')] = FileStore.Sha256Hex(File.ReadAllBytes(target));
        }
        File.WriteAllText(Path.Combine(dest, "manifest.json"), JsonSerializer.Serialize(manifest));
        var bad = Verify(dest);
        if (bad.Count > 0) throw new IOException("Bản sao lưu lỗi: " + string.Join(", ", bad.Take(5)));
        return dest;
    }

    public static IReadOnlyList<string> Verify(string backupDir)
    {
        var m = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(backupDir, "manifest.json")))!;
        return m.Where(kv => { var p = Path.Combine(backupDir, kv.Key); return !File.Exists(p) || FileStore.Sha256Hex(File.ReadAllBytes(p)) != kv.Value; }).Select(kv => kv.Key).ToList();
    }

    public static void Restore(string backupDir, string emptyTarget)
    {
        if (Directory.Exists(emptyTarget) && Directory.EnumerateFileSystemEntries(emptyTarget).Any()) throw new IOException("Chỉ khôi phục vào thư mục trống");
        var bad = Verify(backupDir);
        if (bad.Count > 0) throw new IOException("Bản sao lưu hỏng: " + string.Join(", ", bad.Take(5)));
        var m = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(backupDir, "manifest.json")))!;
        foreach (var rel in m.Keys)
        {
            var t = Path.Combine(emptyTarget, rel); Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(Path.Combine(backupDir, rel), t);
        }
        var re = m.Where(kv => FileStore.Sha256Hex(File.ReadAllBytes(Path.Combine(emptyTarget, kv.Key))) != kv.Value).ToList();
        if (re.Count > 0) throw new IOException("Khôi phục không khớp hash");
    }
}
