using System.Globalization;
using CqNl.Domain;
using CqNl.Infrastructure.Excel;

namespace CqNl.Application;

/// <summary>Thông số nhập tay theo template (Vinfast, Thấm Nitơ…): giá trị QC đã nhập, nếu chưa nhập thì mặc định của template
/// (mặc định có thể dùng {HardnessRequirement} {Unit} {Min} {Max} — lấy từ yêu cầu độ cứng / kết quả đo của đơn).</summary>
public static class ManualValues
{
    public static string? Value(ManualField mf, OrderRecord o, Evaluation? ev)
        => o.Qc.Manual is { } m && m.TryGetValue(mf.Key, out var v) ? (string.IsNullOrWhiteSpace(v) ? null : v.Trim()) : Default(mf, o, ev);

    /// <summary>Mặc định đã thay {…}; một chỗ {…} không có dữ liệu → trả rỗng (để QC nhập, không in chữ dở dang).</summary>
    public static string? Default(ManualField mf, OrderRecord o, Evaluation? ev)
    {
        var t = mf.Default;
        if (string.IsNullOrWhiteSpace(t)) return null;
        var req = o.Qc.HardnessRequirement ?? o.Erp.GetValueOrDefault("HardnessRequirementErp");
        var unit = o.Qc.Hardness.Unit.Trim('(', ')', ' ');
        string? Num(decimal? d) => d?.ToString("0.0##", CultureInfo.InvariantCulture);
        var tokens = new Dictionary<string, string?>
        {
            ["{HardnessRequirement}"] = string.IsNullOrWhiteSpace(req) ? null : req.Trim(),
            ["{Unit}"] = string.IsNullOrWhiteSpace(unit) ? null : unit,
            ["{Min}"] = ev?.Status is EvaluationStatus.Pass or EvaluationStatus.Fail ? Num(ev.Min) : null,
            ["{Max}"] = ev?.Status is EvaluationStatus.Pass or EvaluationStatus.Fail ? Num(ev.Max) : null,
        };
        foreach (var (k, v) in tokens)
        {
            if (!t.Contains(k)) continue;
            if (v is null) return null;
            t = t.Replace(k, v);
        }
        return t;
    }

    /// <summary>Đánh giá hiện tại của đơn (để tính mặc định {Min}/{Max}); lỗi nhập → null.</summary>
    public static Evaluation? Eval(OrderRecord o) => OrderService.TryEvaluate(o).Ev;
}
