using System.Globalization;

namespace CqNl.Infrastructure.Vision;

/// <summary>Một lần đọc số: trên ảnh gốc hoặc ảnh xử lý, bằng bộ đọc nào, kết quả + độ tin cậy (+ từng ký tự nếu bộ đọc có).</summary>
public sealed record OcrPass(string Source, string Engine, string? Raw, string? Value, string? Unit, double Confidence, List<CharConf>? Chars = null);

/// <summary>Kết luận kiểm soát OCR: cần người xác nhận riêng hay không + lý do. Không bao giờ tự chọn giá trị khi hai lần đọc khác nhau.</summary>
public sealed record OcrVerdict(bool NeedsReview, List<string> Flags, string? Agreed);

/// <summary>So sánh OCR ảnh gốc với ảnh xử lý theo quy tắc an toàn: chỉ coi là "khớp" khi CẢ HAI đọc được, CÙNG chuỗi số
/// (kể cả số chữ số sau dấu thập phân) và đủ tin cậy. Còn lại → cảnh báo, bắt buộc người dùng xác nhận.</summary>
public static class MeasurementCheck
{
    public const double MinConfidence = 0.8;

    public static string? Norm(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim().Replace(',', '.').Replace('−', '-');
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ? s : null;
    }

    public static OcrVerdict Check(OcrPass original, OcrPass processed, ImageQuality? q = null)
    {
        var flags = new List<string>(); var review = false;
        var a = Norm(original.Value); var b = Norm(processed.Value);
        if (a is null && b is null) { flags.Add("Không đọc được số trên ảnh (cả bản gốc lẫn bản xử lý) — nhập tay rồi xác nhận"); review = true; }
        else if (a is null) { flags.Add($"Chỉ ảnh XỬ LÝ đọc được {b}; ảnh gốc không đọc được — đối chiếu ảnh rồi xác nhận"); review = true; }
        else if (b is null) { flags.Add($"Chỉ ảnh GỐC đọc được {a}; ảnh xử lý không đọc được — đối chiếu ảnh rồi xác nhận"); review = true; }
        else if (a != b) { flags.Add($"Ảnh gốc đọc {a} ≠ ảnh xử lý đọc {b} — chọn đúng số trên màn hình rồi xác nhận"); review = true; }
        var conf = Math.Max(a is null ? 0 : original.Confidence, b is null ? 0 : processed.Confidence);
        if ((a is not null || b is not null) && conf < MinConfidence) { flags.Add($"Độ tin cậy thấp ({conf:P0}) — kiểm tra kỹ"); review = true; }
        foreach (var p in new[] { original, processed })
        {
            var low = p.Chars?.Where(c => c.Conf < 0.6 && !string.IsNullOrWhiteSpace(c.C)).Select(c => c.C).ToList();
            if (low is { Count: > 0 }) { flags.Add($"{p.Source}: ký tự kém tin cậy \"{string.Join("", low)}\""); review = true; }
        }
        if (original.Unit is { } u1 && processed.Unit is { } u2 && u1 != u2) { flags.Add($"Đơn vị khác nhau: {u1} / {u2}"); review = true; }
        if (q is not null) foreach (var pr in q.Problems()) flags.Add("Chất lượng ảnh: " + pr);
        return new OcrVerdict(review, flags, !review && a is not null && a == b ? a : null);
    }
}
