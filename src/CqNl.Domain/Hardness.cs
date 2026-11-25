using System.Globalization;
using System.Text.RegularExpressions;

namespace CqNl.Domain;

/// <summary>Yêu cầu độ cứng "a-b" (HRC). Chỉ chấp nhận 0 &lt; a &lt; b ≤ 70; không đoán (MAX, ngày, HV…).</summary>
public readonly record struct HardnessRequirement(decimal Min, decimal Max)
{
    private static readonly Regex Rx = new(@"^\s*(\d{1,2}(?:[.,]\d+)?)\s*[-–]\s*(\d{1,2}(?:[.,]\d+)?)\s*$", RegexOptions.Compiled);

    public static HardnessRequirement? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = Rx.Match(raw);
        if (!m.Success) return null;
        var a = Num.Parse(m.Groups[1].Value);
        var b = Num.Parse(m.Groups[2].Value);
        if (!(a > 0 && a < b && b <= 70)) return null;
        return new HardnessRequirement(a, b);
    }

    public override string ToString() => $"{Num.Fmt(Min)}-{Num.Fmt(Max)}";
}

public sealed class ToleranceException(string message) : Exception(message);

/// <summary>Giới hạn chấp nhận từ dung sai bảng mác (OQ-01 đã chốt: nới đúng đầu được ghi).</summary>
public static class Tolerance
{
    private static readonly (Regex rx, Func<decimal, decimal, decimal, (decimal, decimal)> f)[] Rules =
    {
        (new Regex(@"^\(MAX-MIN\)±(\d+(?:\.\d+)?)$"), (a, b, n) => (a - n, b + n)),
        (new Regex(@"^MIN-(\d+(?:\.\d+)?)$"), (a, b, n) => (a - n, b)),
        (new Regex(@"^MIN±(\d+(?:\.\d+)?)$"), (a, b, n) => (a - n, b)),
        (new Regex(@"^MAX\+(\d+(?:\.\d+)?)$"), (a, b, n) => (a, b + n)),
    };

    public static (decimal Low, decimal High) Limits(HardnessRequirement req, string? tolerance)
    {
        if (string.IsNullOrWhiteSpace(tolerance)) return (req.Min, req.Max);
        var t = Regex.Replace(tolerance, @"\s+", "").ToUpperInvariant();
        foreach (var (rx, f) in Rules)
        {
            var m = rx.Match(t);
            if (m.Success) return f(req.Min, req.Max, Num.Parse(m.Groups[1].Value));
        }
        throw new ToleranceException($"Dung sai không nhận dạng được: '{tolerance}' → chặn tạo CQ");
    }
}

public enum EvaluationStatus { InvalidRequirement, NoData, Pass, Fail }

public sealed record Evaluation(EvaluationStatus Status, decimal? Low = null, decimal? High = null,
    decimal? Min = null, decimal? Max = null, decimal? Average = null, IReadOnlyList<decimal>? Failing = null);

public static class HardnessEvaluator
{
    public const int MaxSamples = 6;
    private static readonly Regex Split = new(@"[\s;/]+", RegexOptions.Compiled);   // "/" = cách ghi trên CQ (" / ") → nhập lại y như vậy cũng hợp lệ

    /// <summary>"58.4 58,8;59 / 60" → [58.4, 58.8, 59, 60]. Điểm lỗi → FormatException (không bỏ qua lặng lẽ).</summary>
    public static IReadOnlyList<decimal> ParsePoints(string? text)
    {
        var list = new List<decimal>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var tok in Split.Split(text.Trim()))
        {
            if (tok.Length == 0) continue;
            if (!decimal.TryParse(tok.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"Điểm đo không hợp lệ: '{tok}'");
            if (v <= 0 || v > 100) throw new FormatException($"Điểm đo ngoài khoảng: '{tok}'");
            list.Add(v);
        }
        return list;
    }

    /// <summary>ĐẠT khi MỌI điểm nằm trong giới hạn (OQ-02).</summary>
    public static Evaluation Evaluate(string? requirement, string? tolerance, IReadOnlyList<string?> samples)
    {
        if (samples.Count > MaxSamples) throw new ArgumentException($"Tối đa {MaxSamples} mẫu");
        var req = HardnessRequirement.TryParse(requirement);
        if (req is null) return new Evaluation(EvaluationStatus.InvalidRequirement);
        var (lo, hi) = Tolerance.Limits(req.Value, tolerance);
        var pts = samples.SelectMany(ParsePoints).ToList();
        if (pts.Count == 0) return new Evaluation(EvaluationStatus.NoData, lo, hi);
        var fails = pts.Where(p => p < lo || p > hi).ToList();
        var avg = Math.Round(pts.Sum() / pts.Count, 2, MidpointRounding.AwayFromZero);
        return new Evaluation(fails.Count == 0 ? EvaluationStatus.Pass : EvaluationStatus.Fail, lo, hi, pts.Min(), pts.Max(), avg, fails);
    }

    /// <summary>Ô kết quả trên CQ: các điểm nối " / ", giữ nguyên số chữ số người nhập; trống → "-".</summary>
    public static string CqSampleCell(string? text)
    {
        var toks = Split.Split((text ?? "").Trim()).Where(t => t.Length > 0).Select(t => t.Replace(',', '.')).ToList();
        return toks.Count == 0 ? "-" : string.Join(" / ", toks);
    }

    /// <summary>Thứ tự in lên CQ (người dùng chốt 13/11/2026): Mẫu 1–6 TĂNG DẦN, cùng thứ tự với ảnh độ cứng.
    /// Khóa sắp = điểm đo nhỏ nhất của mẫu; bằng nhau giữ thứ tự nhập. Mẫu trống xếp cuối. Chỉ đổi thứ tự HIỂN THỊ,
    /// dữ liệu đã nhập giữ nguyên. Trả về ô đã định dạng (CqSampleCell).</summary>
    public static IReadOnlyList<string> CqSampleCellsAscending(IReadOnlyList<string?> samples)
    {
        return samples.Select((s, i) => (s, i, pts: ParsePoints(s)))
            .OrderBy(x => x.pts.Count == 0).ThenBy(x => x.pts.Count == 0 ? 0 : x.pts.Min()).ThenBy(x => x.i)
            .Select(x => CqSampleCell(x.s)).ToList();
    }

    public static string Remark(EvaluationStatus status, string? note = null) => status == EvaluationStatus.Pass
        ? "Nhận xét/Remark: Chất lượng đạt"
        : "Nhận xét/Remark: Không đạt độ cứng" + (string.IsNullOrWhiteSpace(note) ? "" : "; " + note);
}

internal static class Num
{
    public static decimal Parse(string s) => decimal.Parse(s.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    public static string Fmt(decimal d) => d.ToString("0.###", CultureInfo.InvariantCulture);
}
