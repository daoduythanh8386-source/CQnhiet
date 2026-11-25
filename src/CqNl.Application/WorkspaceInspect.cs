using System.Globalization;
using System.Text.RegularExpressions;
using CqNl.Infrastructure.Excel;

namespace CqNl.Application;

/// <summary>Tab "Ảnh &amp; kết quả đo độ cứng": đổi loại ảnh (đọc lại số), nhập điểm đo từ CSV/Excel; xuất Excel CQ để chỉnh sửa.</summary>
public sealed partial class Workspace
{
    /// <summary>Đổi loại ảnh (ảnh đo độ cứng ↔ ảnh sản phẩm/xuất hàng): xử lý lại từ ẢNH GỐC theo loại mới (cắt + đọc số đúng loại),
    /// bỏ số đã xác nhận cũ (số HRC ≠ số cân). Mẫu 1–6 tự cập nhật nếu đang lấy tự động.</summary>
    public OrderRow ChangeImageKind(string key, int baseVersion, string imageId, string kind)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        if (kind is not ("Hardness" or "Sample" or "Other")) throw new BusinessException("Loại ảnh không hợp lệ");
        var row = GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn");
        var im = row.Order.Qc.Images.FirstOrDefault(i => i.Id == imageId) ?? throw new BusinessException("Không tìm thấy ảnh");
        if (im.Kind == kind) return row;
        var orig = ImageBytes(im.BlobSha, im.Ext) ?? throw new BusinessException("Ảnh gốc chưa có trên máy này — cần kết nối kho chung");
        var (n, blobs) = ImageService.Process(orig, im.Ext, kind, Paths);
        return EditQc(key, baseVersion, q =>
        {
            var x = q.Images.First(i => i.Id == imageId);
            x.Kind = kind; x.NormalizedSha = n.NormalizedSha; x.Crop = n.Crop; x.NeedsManualCrop = n.NeedsManualCrop;
            x.OcrRaw = n.OcrRaw; x.OcrValue = n.OcrValue; x.OcrConfidence = n.OcrConfidence; x.Vision = n.Vision;
            x.ConfirmedValue = null; x.ConfirmedBy = null; x.ConfirmedAt = null;
            x.History.Add(new ImageHistoryEntry { At = DateTimeOffset.Now, By = s.User.Username, Action = "Đổi loại ảnh → " + kind, Detail = n.Vision is null ? null : ImageService.VisionSummary(n.Vision) });
            ApplyAutoSamples(q);
        }, $"image.kind {imageId} {kind}", blobs);
    }

    /// <summary>"Xử lý lại" (Quản trị / QC): chạy lại module xử lý ảnh + đọc số từ ẢNH GỐC (bất biến). Số đã xác nhận GIỮ NGUYÊN;
    /// kết quả đọc mới khác số đã xác nhận → thêm cảnh báo để người dùng xem lại. Ghi lịch sử + nhật ký.</summary>
    public OrderRow ReprocessImage(string key, int baseVersion, string imageId)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        var row = GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn");
        var im = row.Order.Qc.Images.FirstOrDefault(i => i.Id == imageId) ?? throw new BusinessException("Không tìm thấy ảnh");
        var orig = ImageBytes(im.BlobSha, im.Ext) ?? throw new BusinessException("Ảnh gốc chưa có trên máy này — cần kết nối kho chung");
        if (CqNl.Infrastructure.Storage.FileStore.Sha256Hex(orig) != im.BlobSha) throw new BusinessException("Ảnh gốc không khớp mã SHA-256 — dừng để bảo toàn chứng cứ");
        var (n, blobs) = ImageService.Process(orig, im.Ext, im.Kind, Paths);
        return EditQc(key, baseVersion, q =>
        {
            var x = q.Images.First(i => i.Id == imageId);
            x.OcrRaw = n.OcrRaw; x.OcrValue = n.OcrValue; x.OcrConfidence = n.OcrConfidence; x.Vision = n.Vision;
            if (x.NormalizedSha is null || x.Crop is null) { x.NormalizedSha = n.NormalizedSha; x.Crop = n.Crop; x.NeedsManualCrop = n.NeedsManualCrop; }
            if (x.ConfirmedValue is not null && n.Vision is { } v && v.Passes.Take(2).Any(p => p.Value is not null && p.Value != x.ConfirmedValue))
                v.Flags.Add($"Số đã xác nhận {x.ConfirmedValue} khác số đọc lại — kiểm tra lại ảnh");
            x.History.Add(new ImageHistoryEntry { At = DateTimeOffset.Now, By = s.User.Username, Action = "Xử lý lại", Detail = n.Vision is null ? null : ImageService.VisionSummary(n.Vision) });
            ApplyAutoSamples(q);
        }, $"image.reprocess {imageId}", blobs);
    }

    /// <summary>Ghi nhật ký khi người dùng tải ảnh xuống (gốc / HD màu / in đen trắng). Offline: bỏ qua (không chặn việc xem).</summary>
    public void LogImageExport(string key, string imageId, string mode, string file)
    {
        if (!Online || Session is null) return;
        try { Audit!.Write(Session.User.Username, "image.export", key, new { imageId, mode, file = Path.GetFileName(file) }); } catch { }
    }

    /// <summary>Đọc điểm đo từ file CSV/TXT/XLSX (xuất từ máy đo): mỗi DÒNG có số = 1 mẫu (các điểm cách nhau khoảng trắng), tối đa 6 mẫu.
    /// Bỏ chữ (tiêu đề), bỏ số thứ tự / số nguyên &lt; 10 (không phải độ cứng).</summary>
    public static List<string> SamplesFromTable(string path, int max = 6)
    {
        var rows = new List<List<string>>();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xlsx" or ".xlsm")
        {
            foreach (var r in XlsxReader.ReadSheet(path, maxRows: 2000))
                rows.Add(r.Select(c => c is null ? "" : c.Number is { } d && !c.IsDate ? d.ToString(CultureInfo.InvariantCulture) : c.Text ?? "").ToList());
        }
        else if (ext is ".csv" or ".txt")
        {
            foreach (var line in File.ReadAllLines(path))
            {
                // dấu phẩy thập phân kiểu VN ("56,1") khi phân cách bằng ; hoặc tab
                var sep = line.Contains(';') || line.Contains('\t') ? new[] { ';', '\t' } : new[] { ',' };
                rows.Add(line.Split(sep).Select(x => sep[0] == ';' ? x.Replace(',', '.') : x).ToList());
            }
        }
        else throw new BusinessException("Chọn file .csv, .txt hoặc .xlsx");
        var res = new List<string>();
        foreach (var r in rows)
        {
            var nums = new List<string>();
            foreach (var cell in r)
                foreach (Match m in Regex.Matches(cell.Trim(), @"^\d+(\.\d+)?$|(?<=^|\s)\d+(\.\d+)?(?=\s|$)"))
                    if (decimal.TryParse(m.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && (v >= 10 || m.Value.Contains('.')) && v < 1000)
                        nums.Add(v.ToString(CultureInfo.InvariantCulture));
            if (nums.Count == 0) continue;
            res.Add(string.Join(" ", nums));
            if (res.Count == max) break;
        }
        if (res.Count == 0) throw new BusinessException("Không tìm thấy số đo độ cứng trong file");
        return res;
    }

    /// <summary>Lưu file Excel CQ (bản in kiểu kind) ra thư mục với tên chuẩn như PDF. Dấu và chữ ký là ẢNH RỜI trên sheet (không khoá) —
    /// mở bằng Excel kéo di chuyển / đổi cỡ được, sửa được nội dung ô.</summary>
    public string ExportCqXlsxNamed(CqVersionRecord c, OrderRecord o, CqExportKind kind, string saveRoot)
    {
        var destDir = MonthFolder(saveRoot, c);
        RequireOnline("lưu file Excel CQ");
        var sha = c.Xlsxs.GetValueOrDefault(kind.ToString()) ?? (kind == CqExportKind.Signed ? c.ApprovedXlsxSha : null) ?? throw new BusinessException("CQ chưa có file Excel kiểu này");
        Directory.CreateDirectory(destDir);
        var p = Path.Combine(destDir, CqFileName(c, o, kind, ".xlsx"));
        return WriteFileSafe(p, Store!.GetBlob(sha, "xlsx"));
    }

    /// <summary>Xuất Excel để chỉnh sửa (KHÔNG duyệt CQ): CQ đã duyệt → đúng file Excel bản duyệt; CQ còn nháp (tạo nháp nếu chưa có) →
    /// bản nháp + con dấu + chữ ký người chọn, đặt theo vị trí mặc định. Dấu, chữ ký là ẢNH RỜI → kéo di chuyển / đổi cỡ trong Excel.</summary>
    public (CqVersionRecord Cq, string Xlsx, List<string> Steps) ExportCqExcel(string key, string saveRoot, string? approverId, string? checkerId)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        var st = Settings!.Get().Value;
        var stamp = st.StampSha is null ? throw new BusinessException("Chưa có con dấu — Quản trị cập nhật ở Cài đặt → Chữ ký và con dấu") : Store!.GetBlob(st.StampSha, "png");
        var appr = st.Signers.FirstOrDefault(x => x.Id == (approverId ?? st.DefaultApproverId) && x.Active);
        var chk = st.Signers.FirstOrDefault(x => x.Id == (checkerId ?? st.DefaultCheckerId) && x.Active);
        if (appr is null || chk is null) throw new BusinessException("Chưa chọn người xác nhận (Approved) / người kiểm tra (Checker) — chọn ở tab Xuất CQ.");
        var (cq, steps) = PrepareCq(key);
        var row = GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn");
        var destDir = MonthFolder(saveRoot, cq);
        Directory.CreateDirectory(destDir);
        var p = Path.Combine(destDir, CqFileName(cq, row.Order, CqExportKind.Signed, ".xlsx"));
        if (cq.Xlsxs.ContainsKey(nameof(CqExportKind.Signed)) || cq.ApprovedXlsxSha is not null)
        {
            p = WriteFileSafe(p, Store!.GetBlob(cq.Xlsxs.GetValueOrDefault(nameof(CqExportKind.Signed)) ?? cq.ApprovedXlsxSha!, "xlsx"));
            steps.Add("Lưu " + Path.GetFileName(p) + " (bản đã duyệt)");
        }
        else
        {
            p = WriteFileSafe(p, Cq!.SignedXlsx(s, cq.Id, appr, chk, stamp, st.Placements.GetValueOrDefault(cq.TemplateId)));
            steps.Add("Lưu " + Path.GetFileName(p) + " (bản nháp + dấu + chữ ký; CQ chưa duyệt)");
        }
        Audit!.Write(s.User.Username, "cq.excel", cq.Id, new { file = Path.GetFileName(p) });
        return (cq, p, steps);
    }

    // ---------------- Sửa tay trong Excel rồi in — MỌI kiểu bản in (yêu cầu 25/11) ----------------

    /// <summary>Tên file Excel để sửa tay của một kiểu bản in (cạnh PDF, trong thư mục tháng): "... - sua.xlsx".</summary>
    public string EditFileName(CqVersionRecord c, OrderRecord o, CqExportKind kind) => CqFileName(c, o, kind, " - sua.xlsx");

    /// <summary>Bước 1: tạo (hoặc mở lại) file Excel của kiểu bản in <paramref name="kind"/> để người làm sửa nội dung bất kỳ ô nào trong Excel.
    /// Tạo bản nháp CQ nếu chưa có. File đã có (đang sửa dở, đúng CQ) → GIỮ NGUYÊN, không ghi đè phần đã sửa (trừ khi fresh).</summary>
    public (CqVersionRecord Cq, string Xlsx, List<string> Steps) EditableCqXlsx(string key, CqExportKind kind, string saveRoot, string? approverId, string? checkerId, bool fresh = false)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        RequireOnline("tạo file Excel để sửa");
        var st = Settings!.Get().Value;
        var (cq, steps) = PrepareCq(key);
        var row = GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn");
        var destDir = MonthFolder(saveRoot, cq); Directory.CreateDirectory(destDir);
        var p = Path.Combine(destDir, EditFileName(cq, row.Order, kind));
        if (!fresh && File.Exists(p))
        {
            try
            {
                if (CqWriter.ContainsText(File.ReadAllBytes(p), cq.ReportNo)) { steps.Add("Mở lại file đang sửa " + Path.GetFileName(p)); return (cq, p, steps); }
            }
            catch (IOException) { steps.Add("Mở lại file đang sửa (đang mở trong Excel) " + Path.GetFileName(p)); return (cq, p, steps); }
        }
        byte[] bytes;
        if (cq.Xlsxs.TryGetValue(kind.ToString(), out var issued)) { bytes = Store!.GetBlob(issued, "xlsx"); steps.Add("Lấy file Excel của bản in đã phát hành"); }
        else
        {
            var stamp = kind == CqExportKind.Blank ? null : st.StampSha is null ? throw new BusinessException("Chưa có con dấu — Quản trị cập nhật ở Cài đặt → Chữ ký và con dấu") : Store!.GetBlob(st.StampSha, "png");
            SignerRecord? appr = null, chk = null;
            if (kind == CqExportKind.Signed)
            {
                appr = st.Signers.FirstOrDefault(x => x.Id == (approverId ?? st.DefaultApproverId) && x.Active);
                chk = st.Signers.FirstOrDefault(x => x.Id == (checkerId ?? st.DefaultCheckerId) && x.Active);
                if (appr is null || chk is null) throw new BusinessException("Chưa chọn người xác nhận (Approved) / người kiểm tra (Checker) — chọn ở tab Xuất CQ.");
            }
            bytes = Cq!.KindXlsx(s, cq.Id, kind, appr, chk, stamp, st.Placements.GetValueOrDefault(cq.TemplateId));
        }
        p = WriteFileSafe(p, bytes);
        steps.Add("Lưu " + Path.GetFileName(p) + " để sửa trong Excel");
        Audit!.Write(s.User.Username, "cq.excel_edit", cq.Id, new { kind = kind.ToString(), file = Path.GetFileName(p) });
        return (cq, p, steps);
    }

    /// <summary>Bước 2: đọc file Excel ĐÃ SỬA (người dùng đã lưu trong Excel) → kiểm đúng CQ → PDF đúng như file đó → lưu cùng CQ
    /// (kèm lịch sử) → ghi PDF tên chuẩn vào thư mục lưu CQ.</summary>
    public (CqVersionRecord Cq, string Pdf) IssueFromEditedFile(string cqId, CqExportKind kind, string xlsxPath, string saveRoot, string? approverId, string? checkerId)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        RequireOnline("xuất PDF từ file đã sửa");
        byte[] x;
        try { x = File.ReadAllBytes(xlsxPath); }
        catch (IOException) { throw new BusinessException("Không đọc được file " + Path.GetFileName(xlsxPath) + " — hãy LƯU file trong Excel (Ctrl+S) rồi bấm lại. Không cần đóng Excel."); }
        var st = Settings!.Get().Value;
        var appr = st.Signers.FirstOrDefault(v => v.Id == (approverId ?? st.DefaultApproverId) && v.Active);
        var chk = st.Signers.FirstOrDefault(v => v.Id == (checkerId ?? st.DefaultCheckerId) && v.Active);
        var c = Cq!.IssueEdited(s, cqId, kind, x, appr, chk);
        var row = GetOrder(c.OrderKey) ?? throw new BusinessException("Không tìm thấy đơn");
        var (_, cv) = Repo!.Get<CqVersionRecord>(CqService.T, c.Id); Cache.Put(CqService.T, c.Id, c, cv, false);
        return (c, ExportCqNamed(c, row.Order, kind, saveRoot));
    }
}
