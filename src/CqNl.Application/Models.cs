using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CqNl.Application;

/// <summary>Admin, QC, Viewer (chỉ xem), Sales = Kinh doanh: xem đơn/ảnh/báo cáo và ghi LƯU Ý đơn hàng; không sửa dữ liệu QC/CQ.</summary>
public enum Role { Admin, Qc, Viewer, Sales }

public sealed class UserRecord
{
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public Role Role { get; set; } = Role.Qc;
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; } = true;
    public bool Active { get; set; } = true;
    /// <summary>Hash mật khẩu tạm/mặc định gần nhất — để bắt "mật khẩu mới phải khác mật khẩu mặc định" (PDF-4).</summary>
    public string? TemporaryHash { get; set; }
    /// <summary>Số lần nhập sai mật khẩu liên tiếp; đủ AuthService.MaxFailedLogins → tạm khoá đến LockedUntil.</summary>
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>Ảnh kiểm tra gắn với đơn. Gốc bất biến (BlobSha); bản chuẩn hóa riêng (NormalizedSha).</summary>
public sealed class InspectionImage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BlobSha { get; set; } = "";
    public string Ext { get; set; } = "jpg";
    public string Kind { get; set; } = "Sample";              // Sample | Hardness | Other
    public bool OnCq { get; set; } = true;
    public string? NormalizedSha { get; set; }
    public int[]? Crop { get; set; }                         // x,y,w,h trên ảnh gốc
    public bool NeedsManualCrop { get; set; }
    public string? OcrRaw { get; set; }
    public string? OcrValue { get; set; }
    public double OcrConfidence { get; set; }
    public string? ConfirmedValue { get; set; }              // giá trị QC xác nhận (ảnh độ cứng: HRC; ảnh xuất hàng: số cân)
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public int AddedOrder { get; set; }
    /// <summary>Người tải ảnh lên + thời điểm (ảnh cũ trước 0.9: null). Mã hồ sơ = số ĐNGC|dòng của đơn chứa ảnh.</summary>
    public string? AddedBy { get; set; }
    public DateTimeOffset? AddedAt { get; set; }
    /// <summary>Kết quả xử lý ảnh + kiểm soát OCR (module AI xử lý ảnh, 0.9). Ảnh cũ: null (vẫn dùng bình thường).</summary>
    public ImageVision? Vision { get; set; }
    /// <summary>Lịch sử xử lý / sửa / xác nhận của ảnh — chỉ thêm, không xoá.</summary>
    public List<ImageHistoryEntry> History { get; set; } = new();
}

public sealed class ImageHistoryEntry
{
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
}

public sealed class VisionChar { public string C { get; set; } = ""; public double Conf { get; set; } }

public sealed class VisionPass
{
    public string Source { get; set; } = "";       // Ảnh gốc | Ảnh xử lý | OCR chữ …
    public string Engine { get; set; } = "";
    public string? Raw { get; set; }
    public string? Value { get; set; }
    public string? Unit { get; set; }
    public double Confidence { get; set; }
    public List<VisionChar>? Chars { get; set; }
}

public sealed class VisionRegion { public string Kind { get; set; } = ""; public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; } }

/// <summary>Kết quả module xử lý ảnh: 3 bản (gốc = BlobSha bất biến; HD màu; tối ưu in đen trắng), vùng số đo phóng to,
/// chất lượng ảnh, vùng phát hiện, các lần đọc số (ảnh gốc / ảnh xử lý), cảnh báo. NeedsReview = phải người xác nhận riêng.</summary>
public sealed class ImageVision
{
    public string Version { get; set; } = "";
    public string? HdSha { get; set; }
    public string? PrintSha { get; set; }
    public string? RoiSha { get; set; }
    public double Sharpness { get; set; }
    public double Noise { get; set; }
    public double Brightness { get; set; }
    public double Saturated { get; set; }
    public double Contrast { get; set; }
    public List<VisionRegion> Regions { get; set; } = new();
    public string? MeasurementKind { get; set; }
    public List<string> Steps { get; set; } = new();
    public List<string> ReadingSteps { get; set; } = new();
    public List<VisionPass> Passes { get; set; } = new();
    public List<string> Flags { get; set; } = new();
    public bool NeedsReview { get; set; }
    public string? Lot { get; set; }
    public string? Date { get; set; }
    public string? DeviceCode { get; set; }
    public string? Unit { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class HardnessData
{
    public string Unit { get; set; } = "(HRC)";
    public string? CheckDate { get; set; }
    public string? Appearance { get; set; }
    public string? CurvatureRequirement { get; set; }
    public string? CurvatureResult { get; set; }
    public string? Other { get; set; }
    public List<string> Samples { get; set; } = new();
    /// <summary>true = Mẫu 1–6 đang lấy tự động từ số đọc trên ảnh (thêm/sửa ảnh → tự cập nhật); QC gõ tay khác đi → false.</summary>
    public bool SamplesAuto { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>Phần dữ liệu do QC/ứng dụng sở hữu — import ERP KHÔNG BAO GIỜ ghi vào đây.</summary>
public sealed class QcData
{
    public string? SteelGrade { get; set; }
    public int? SteelGradeRow { get; set; }                  // dòng bảng mác đã chọn (mác trùng khác giá trị)
    public string? TemplateId { get; set; }
    public string? HardnessRequirement { get; set; }         // sửa tay (null = lấy ERP)
    public string? Tolerance { get; set; }
    public List<string>? QuenchTemps { get; set; }
    public List<string>? RamValues { get; set; }
    public string? PartNo { get; set; }
    public string? PartName { get; set; }
    public string? Size { get; set; }
    public string? CustomerOverride { get; set; }
    public List<InspectionImage> Images { get; set; } = new();
    public HardnessData Hardness { get; set; } = new();
    public string? MergeGroup { get; set; }
    public List<string>? MergedChildren { get; set; }
    /// <summary>Thông số nhập tay theo template (key của "manual" trong bản đồ template, vd NitDepthRes). Không có key = dùng mặc định của template.</summary>
    public Dictionary<string, string>? Manual { get; set; }
}

public sealed class OrderRecord
{
    public string Key { get; set; } = "";                    // Dngc|LineNo
    public Dictionary<string, string?> Erp { get; set; } = new();
    public QcData Qc { get; set; } = new();
    public List<string> Flags { get; set; } = new();
}

/// <summary>Lưu ý đơn hàng (QC + Kinh doanh). Mỗi lưu ý là một bản ghi RIÊNG (tạo mới, không sửa chung một file) → nhiều người
/// ghi cùng lúc không xung đột, không ghi đè. Chỉ "ẩn" (giữ lịch sử), không xóa.</summary>
public sealed class NoteRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OrderKey { get; set; } = "";
    public string Customer { get; set; } = "";               // tên khách lúc ghi (để nhắc ở đơn sau)
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public Role AuthorRole { get; set; }
    public DateTimeOffset At { get; set; }
    public string Machine { get; set; } = "";
    public bool ForCustomer { get; set; }                     // nhắc lại ở các đơn sau của cùng khách hàng
    public List<NoteImage> Images { get; set; } = new();
    public bool Hidden { get; set; }
    public string? HiddenBy { get; set; }
    public DateTimeOffset? HiddenAt { get; set; }
}

public sealed class NoteImage
{
    public string Sha { get; set; } = "";
    public string Ext { get; set; } = "jpg";
    public string Name { get; set; } = "";
}

public enum CqState { Draft, Approved, Deleted }

/// <summary>Kiểu bản in CQ: có dấu + chữ ký / có dấu chưa ký / trắng (không dấu, không ký — in rồi ký tay).</summary>
public enum CqExportKind { Signed, StampOnly, Blank }
public sealed record CqEditedPrint(string Kind, string XlsxSha, string PdfSha, string By, DateTimeOffset At, string? PreviousPdfSha);

public sealed class CqVersionRecord
{
    public string OrderKey { get; set; } = "";
    public int Ver { get; set; }
    public string ReportNo { get; set; } = "";
    /// <summary>Mã giữ số báo cáo (0.9.2) + tháng giữ số "yyyy-MM" — kiểm số không bị trùng khi kho đồng bộ giữa nhiều máy. null: CQ cũ / số nhập tay.</summary>
    public string? ReportClaim { get; set; }
    public string? ReportClaimMonth { get; set; }
    public string TemplateId { get; set; } = "";
    public string TemplateSha { get; set; } = "";
    public string XlsxSha { get; set; } = "";
    public int OrderVersion { get; set; }
    public string? TemplateMapJson { get; set; }               // bản đồ ô lúc tạo (bất biến cùng bản CQ)
    public string? TemplateName { get; set; }                    // version đơn lúc tạo CQ (phát hiện sửa sau khi tạo)
    public List<CqOverlayPlacement>? Placements { get; set; } // vị trí dấu/chữ ký dùng khi duyệt (bất biến cùng PDF)
    public string Result { get; set; } = "";                 // Pass | Fail
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public CqState State { get; set; } = CqState.Draft;
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApproverSigner { get; set; }
    public string? CheckerSigner { get; set; }
    public string? ApprovedXlsxSha { get; set; }
    public string? PdfSha { get; set; }
    /// <summary>Các bản PDF đã xuất của CÙNG một CQ (cùng số báo cáo): Signed = dấu + chữ ký; StampOnly = có dấu, chưa ký; Blank = không dấu/không ký (in ký tay).</summary>
    public Dictionary<string, string> Pdfs { get; set; } = new();
    /// <summary>File Excel tương ứng từng bản in (để in thẳng ra máy in / mở lại).</summary>
    public Dictionary<string, string> Xlsxs { get; set; } = new();
    /// <summary>Lịch sử bản in SỬA TAY trong Excel (mọi kiểu in): file Excel đã sửa + PDF tạo từ nó, ai sửa, lúc nào, bản PDF trước đó.</summary>
    public List<CqEditedPrint> EditedPrints { get; set; } = new();
    public bool Printed { get; set; }
    public string? PrintedBy { get; set; }
    public DateTimeOffset? PrintedAt { get; set; }
    public string? DeletedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeleteReason { get; set; }
    public string? StateWhenDeleted { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string Id => $"{OrderKey}|v{Ver}";
}

public sealed class SignerRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string OriginalSha { get; set; } = "";            // ảnh gốc người dùng cung cấp (giữ nguyên)
    public string OriginalExt { get; set; } = "png";
    public string PaperSha { get; set; } = "";               // bản chuẩn hóa nền giấy A4 (thư viện)
    public string TransparentSha { get; set; } = "";         // bản trong suốt (chèn CQ)
    public bool Active { get; set; } = true;
}

public sealed class SharedSettings
{
    public string? DefaultApproverId { get; set; }
    public string? DefaultCheckerId { get; set; }
    public string? StampSha { get; set; }
    public string? SteelTableSha { get; set; }
    public string? SteelTableName { get; set; }
    /// <summary>Thư mục ERP theo dõi (file MF0015 xuất từ ERP) — nút "Đồng bộ ERP" quét thư mục này.</summary>
    public string? ErpFolder { get; set; }
    /// <summary>Thư mục lưu file CQ (PDF + Excel) dùng chung mọi máy — quản trị đặt; máy nào cần vẫn chọn riêng được.</summary>
    public string? CqFolder { get; set; }
    public List<SignerRecord> Signers { get; set; } = new();
    /// <summary>Vị trí mặc định dấu/chữ ký theo template (người làm có thể chỉnh lại từng CQ khi duyệt).</summary>
    public Dictionary<string, List<CqOverlayPlacement>> Placements { get; set; } = new();
    /// <summary>Tên khách hàng in trên CQ (tên viết tắt như sổ "THEO DÕI ĐƠN HÀNG"): khoá = tên khách hàng ERP đã chuẩn hoá.</summary>
    public Dictionary<string, string> CustomerAliases { get; set; } = new();
}

public static class Json
{
    public static readonly JsonSerializerOptions Opt = new() { Converters = { new JsonStringEnumConverter() } };
    public static JsonNode ToNode<T>(T v) => JsonSerializer.SerializeToNode(v, Opt)!;
    public static T From<T>(JsonNode n) => n.Deserialize<T>(Opt)!;
}

public sealed class PermissionException(string message) : Exception(message);
public sealed class BusinessException(string message) : Exception(message);
