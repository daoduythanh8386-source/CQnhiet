using System.Text.RegularExpressions;

namespace CqNl.Domain;

public static class Grade
{
    /// <summary>Chuẩn hóa mác: bỏ khoảng trắng, '-', '_', '.'; viết hoa. "S-Star" = "S STAR".</summary>
    public static string Normalize(string? g) => Regex.Replace(g ?? "", @"[\s\-_.]", "").ToUpperInvariant();
}

public sealed record MergeCandidate(string Dngc, int LineNo, string? SteelGrade, string? HardnessRequirement, bool HasCq, string? MergeGroup);

public static class MergeRules
{
    public static (bool Ok, string Reason) CanMerge(IReadOnlyList<MergeCandidate> lines)
    {
        if (lines.Count < 2) return (false, "Cần chọn ít nhất 2 dòng");
        if (lines.Select(l => l.Dngc).Distinct().Count() > 1) return (false, "Khác số ĐNGC");
        if (lines.Select(l => Grade.Normalize(l.SteelGrade)).Distinct().Count() > 1) return (false, "Khác mác thép");
        var reqs = lines.Select(l => HardnessRequirement.TryParse(l.HardnessRequirement)).ToList();
        if (reqs.Any(r => r is null) || reqs.Distinct().Count() > 1) return (false, "Khác (hoặc thiếu) yêu cầu độ cứng");
        if (lines.Any(l => l.HasCq)) return (false, "Có dòng đã có CQ");
        if (lines.Any(l => !string.IsNullOrEmpty(l.MergeGroup))) return (false, "Có dòng đã thuộc nhóm gộp khác");
        return (true, "");
    }
}

public static class ReportNumber
{
    /// <summary>NNN/MM; số mới = lớn nhất đã cấp + 1; không dùng lại số của CQ đã xóa (OQ-09).</summary>
    public static (string Text, int N) Next(IEnumerable<int> issuedThisMonth, int month)
    {
        var n = issuedThisMonth.DefaultIfEmpty(0).Max() + 1;
        if (n > 999) throw new InvalidOperationException("Hết số báo cáo trong tháng");
        return ($"{n:000}/{month:00}", n);
    }
}

/// <summary>Một dòng bảng mác thép (sheet "THÔNG SỐ LĐ NHIỆT").</summary>
public sealed record SteelGrade(int SourceRow, string Group, string Name, IReadOnlyList<string?> Heats, IReadOnlyList<string?> Rams,
    string? Tolerance, string? Chemistry, int? TemplateMark)
{
    public string Key => Grade.Normalize(Name);
    public int HeatCount => Heats.Count(h => !string.IsNullOrWhiteSpace(h));
}

/// <summary>Ảnh độ cứng đưa lên CQ: sắp tăng dần theo giá trị xác nhận; ≥3 ảnh thì bỏ ảnh trùng kết quả
/// (cùng ĐNGC + khách + mác) — chỉ bỏ khỏi CQ, ảnh vẫn ở trong đơn.</summary>
public sealed record HardnessPhoto(string Id, decimal? ConfirmedValue, string Dngc, string Customer, string Grade, int AddedOrder);

public static class HardnessPhotoLayout
{
    public static (IReadOnlyList<HardnessPhoto> Kept, IReadOnlyList<(HardnessPhoto Dropped, HardnessPhoto KeptAs)> Dropped) Arrange(IReadOnlyList<HardnessPhoto> photos, int dedupFrom = 3)
    {
        var ordered = photos.OrderBy(p => p.AddedOrder).ToList();
        var dropped = new List<(HardnessPhoto, HardnessPhoto)>();
        var kept = new List<HardnessPhoto>();
        if (ordered.Count >= dedupFrom)
        {
            var seen = new Dictionary<(string, string, string, decimal), HardnessPhoto>();
            foreach (var p in ordered)
            {
                if (p.ConfirmedValue is null) { kept.Add(p); continue; }
                var k = (p.Dngc, p.Customer.Trim().ToUpperInvariant(), Domain.Grade.Normalize(p.Grade), p.ConfirmedValue.Value); // decimal: 47.3m == 47.30m, hash bằng nhau
                if (seen.TryGetValue(k, out var first)) { dropped.Add((p, first)); continue; }
                seen[k] = p; kept.Add(p);
            }
        }
        else kept.AddRange(ordered);
        var sorted = kept.OrderBy(p => p.ConfirmedValue is null).ThenBy(p => p.ConfirmedValue ?? 0).ThenBy(p => p.AddedOrder).ToList();
        return (sorted, dropped);
    }
}

/// <summary>Quy ước in CQ đã chốt với người dùng (13/11/2026).</summary>
public static class CqConventions
{
    /// <summary>Chú thích số cân góc dưới trái ảnh xuất hàng: giữ đúng chữ số như màn hình cân + " kg".</summary>
    public static string ScaleLabel(string confirmedValue) => $"{confirmedValue.Trim()} kg";

    /// <summary>Chữ ký chèn lên CQ: bản TRONG SUỐT (cùng nét đã làm nét) — không lộ khung nền, không che dấu.
    /// Ảnh lưu trong thư viện chữ ký vẫn là bản nền giấy A4 (quy ước chuẩn hóa).</summary>
    public const bool TransparentSignatureOnCq = true;
}
