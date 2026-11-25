using System.Globalization;
using CqNl.Domain;

namespace CqNl.Application;

public static class NoteService { public const string T = "note"; }

public sealed record DashFilter(DateTime? From, DateTime? To, string? Customer);

/// <summary>Trạng thái tổng hợp của MỘT dòng đơn — mọi đơn đều có dòng, kể cả chưa làm CQ (ghi nhận đủ cả đơn chưa làm).</summary>
public sealed record DashLine(
    string Key, string Dngc, string Line, DateTime? Received, string Customer, string Product, string Grade, string Requirement,
    decimal? Quantity, decimal? WeightKg,
    string CqStatus,        // "Chưa làm CQ" | "CQ nháp" | "Đã duyệt"
    string ReportNo, string Result,   // "Đạt" | "Không đạt" | "Chưa có kết quả"
    string ResultSource,    // "CQ" | "Kết quả đo đã xác nhận" | ""
    string PrintStatus,     // "Đã in" | "Chưa in" | "—" (chưa duyệt)
    string ApprovedBy, DateTimeOffset? ApprovedAt, string PrintedBy, DateTimeOffset? PrintedAt, int DeletedCqs, int Notes, string Flags)
{
    public bool HasCq => CqStatus != "Chưa làm CQ";
    public string ReceivedText => Received?.ToString("dd/MM/yyyy") ?? "";
}

public sealed record DashGroup(string Name, int Total, int Pass, int Fail, int NoResult, int WithCq, int WithoutCq, int Approved, int Printed, int NotPrinted)
{
    public string PassRate => Pass + Fail == 0 ? "—" : (100.0 * Pass / (Pass + Fail)).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}

public sealed record DashResult(DashFilter Filter, IReadOnlyList<DashLine> Lines, DashGroup Totals, int DistinctDngc, int DraftCq, int WithNotes, int WithFlags,
    int DeletedCq, IReadOnlyList<DashGroup> ByMonth, IReadOnlyList<DashGroup> ByCustomer, DateTimeOffset At);

/// <summary>Thống kê cho màn hình Tổng quan và báo cáo Excel (hàm thuần, kiểm thử được).</summary>
public static class DashboardService
{
    public const string NoCq = "Chưa làm CQ", DraftCq = "CQ nháp", ApprovedCq = "Đã duyệt";
    public const string Pass = "Đạt", Fail = "Không đạt", NoResult = "Chưa có kết quả";
    public const string Printed = "Đã in", NotPrinted = "Chưa in", NotApproved = "—";

    public static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date :
        DateTime.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : null;

    private static decimal? Dec(string? s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static DashLine ToLine(Workspace.OrderRow r, int notes)
    {
        var o = r.Order; var e = o.Erp; var p = o.Key.Split('|');
        var live = r.Cqs.Where(c => c.State != CqState.Deleted).OrderBy(c => c.Ver).ToList();
        var last = live.LastOrDefault();
        var lastApproved = live.LastOrDefault(c => c.State == CqState.Approved);
        string result, source;
        if (last is not null) { result = last.Result == "Pass" ? Pass : Fail; source = "CQ"; }
        else if (o.Qc.Hardness.ConfirmedAt is not null && OrderService.TryEvaluate(o).Ev is { } ev && ev.Status is EvaluationStatus.Pass or EvaluationStatus.Fail)
        { result = ev.Status == EvaluationStatus.Pass ? Pass : Fail; source = "Kết quả đo đã xác nhận"; }
        else { result = NoResult; source = ""; }
        var cq = last is null ? NoCq : lastApproved is not null ? ApprovedCq : DraftCq;
        var print = lastApproved is null ? NotApproved : lastApproved.Printed ? Printed : NotPrinted;
        return new DashLine(o.Key, p[0], p.ElementAtOrDefault(1) ?? "", ParseDate(e.GetValueOrDefault("ReceivedAt")),
            o.Qc.CustomerOverride ?? e.GetValueOrDefault("Customer") ?? "", e.GetValueOrDefault("ProductName") ?? "",
            o.Qc.SteelGrade ?? e.GetValueOrDefault("GradeFromErp") ?? "", o.Qc.HardnessRequirement ?? e.GetValueOrDefault("HardnessRequirementErp") ?? "",
            Dec(e.GetValueOrDefault("Quantity")), Dec(e.GetValueOrDefault("WeightKg")),
            cq, (lastApproved ?? last)?.ReportNo ?? "", result, source, print,
            lastApproved?.ApprovedBy ?? "", lastApproved?.ApprovedAt, lastApproved?.Printed == true ? lastApproved.PrintedBy ?? "" : "", lastApproved?.Printed == true ? lastApproved.PrintedAt : null,
            r.Cqs.Count(c => c.State == CqState.Deleted), notes, string.Join("; ", o.Flags.Select(f => f.Split(':')[0]).Distinct()));
    }

    private static DashGroup Group(string name, IReadOnlyCollection<DashLine> l) => new(name, l.Count,
        l.Count(x => x.Result == Pass), l.Count(x => x.Result == Fail), l.Count(x => x.Result == NoResult),
        l.Count(x => x.HasCq), l.Count(x => !x.HasCq), l.Count(x => x.CqStatus == ApprovedCq), l.Count(x => x.PrintStatus == Printed), l.Count(x => x.PrintStatus == NotPrinted));

    public static DashResult Compute(IEnumerable<Workspace.OrderRow> rows, IReadOnlyDictionary<string, int> noteCounts, DashFilter f)
    {
        var list = rows.ToList();
        var byKey = list.GroupBy(r => r.Order.Key).ToDictionary(g => g.Key, g => g.First());
        // dòng đã gộp vào dòng khác: trạng thái CQ theo dòng chính của nhóm
        var all = list.Select(r => r.Order.Qc.MergeGroup is { } p && p != r.Order.Key && byKey.TryGetValue(p, out var pr) ? r with { Cqs = pr.Cqs } : r)
            .Select(r => ToLine(r, noteCounts.GetValueOrDefault(r.Order.Key))).ToList();
        var cust = (f.Customer ?? "").Trim().ToLowerInvariant();
        var lines = all.Where(l => (f.From is null || (l.Received is { } d && d >= f.From.Value.Date)) && (f.To is null || (l.Received is { } d2 && d2 <= f.To.Value.Date))
                                   && (cust.Length == 0 || l.Customer.ToLowerInvariant().Contains(cust)))
            .OrderByDescending(l => l.Received ?? DateTime.MinValue).ThenByDescending(l => l.Dngc).ThenBy(l => l.Line).ToList();
        var byMonth = lines.GroupBy(l => l.Received is { } d ? d.ToString("yyyy-MM") : "Không rõ ngày").OrderByDescending(g => g.Key)
            .Select(g => Group(g.Key, g.ToList()) with { Name = g.Key.Length == 7 ? $"{g.Key[5..]}/{g.Key[..4]}" : g.Key }).ToList();
        var byCustomer = lines.GroupBy(l => l.Customer.Trim()).Select(g => Group(g.Key.Length == 0 ? "(không rõ)" : g.Key, g.ToList()))
            .OrderByDescending(g => g.Total).ThenBy(g => g.Name).ToList();
        return new DashResult(f, lines, Group("Tổng", lines), lines.Select(l => l.Dngc).Distinct().Count(), lines.Count(l => l.CqStatus == DraftCq),
            lines.Count(l => l.Notes > 0), lines.Count(l => l.Flags.Length > 0), lines.Sum(l => l.DeletedCqs), byMonth, byCustomer, DateTimeOffset.Now);
    }

    /// <summary>Báo cáo Excel: Tổng hợp, Theo tháng, Theo khách hàng, Chi tiết (MỌI dòng đơn trong bộ lọc, kể cả chưa làm CQ).</summary>
    public static byte[] ExportXlsx(DashResult r, string user)
    {
        var t = r.Totals;
        string Range() => $"{(r.Filter.From?.ToString("dd/MM/yyyy") ?? "đầu")} – {(r.Filter.To?.ToString("dd/MM/yyyy") ?? "nay")}";
        var summary = new List<object?[]>
        {
            new object?[] { "Báo cáo", "Tổng hợp đơn nhiệt luyện / CQ" }, new object?[] { "Khoảng ngày nhận", Range() },
            new object?[] { "Khách hàng lọc", string.IsNullOrWhiteSpace(r.Filter.Customer) ? "(tất cả)" : r.Filter.Customer },
            new object?[] { "Lập lúc", r.At.ToString("dd/MM/yyyy HH:mm") }, new object?[] { "Người lập", user }, new object?[] { "", "" },
            new object?[] { "Tổng dòng đơn", t.Total }, new object?[] { "Số phiếu ĐNGC", r.DistinctDngc },
            new object?[] { "Đã làm CQ", t.WithCq }, new object?[] { "Chưa làm CQ", t.WithoutCq }, new object?[] { "CQ nháp chờ duyệt", r.DraftCq },
            new object?[] { "CQ đã duyệt", t.Approved }, new object?[] { "Đạt", t.Pass }, new object?[] { "Không đạt", t.Fail },
            new object?[] { "Chưa có kết quả", t.NoResult }, new object?[] { "Tỉ lệ đạt (trên đơn có kết quả)", t.PassRate },
            new object?[] { "Đã in", t.Printed }, new object?[] { "Đã duyệt, chưa in", t.NotPrinted }, new object?[] { "Đơn có lưu ý", r.WithNotes },
            new object?[] { "Đơn có cảnh báo ERP", r.WithFlags }, new object?[] { "Số bản CQ đã xóa", r.DeletedCq },
        };
        string[] gh = { "Nhóm", "Tổng", "Đạt", "Không đạt", "Chưa có KQ", "Tỉ lệ đạt", "Đã làm CQ", "Chưa làm CQ", "Đã duyệt", "Đã in", "Chưa in" };
        object?[] G(DashGroup g) => new object?[] { g.Name, g.Total, g.Pass, g.Fail, g.NoResult, g.PassRate, g.WithCq, g.WithoutCq, g.Approved, g.Printed, g.NotPrinted };
        string[] dh = { "Số ĐNGC", "Dòng", "Ngày nhận", "Khách hàng", "Sản phẩm", "Mác", "Yêu cầu", "SL", "KL (kg)", "CQ", "Số báo cáo", "Kết quả",
                        "Nguồn kết quả", "In", "Duyệt bởi", "Duyệt lúc", "In bởi", "In lúc", "CQ đã xóa", "Số lưu ý", "Cảnh báo" };
        var detail = r.Lines.Select(l => new object?[] { l.Dngc, l.Line, l.ReceivedText, l.Customer, l.Product, l.Grade, l.Requirement, l.Quantity, l.WeightKg,
            l.CqStatus, l.ReportNo, l.Result, l.ResultSource, l.PrintStatus, l.ApprovedBy, l.ApprovedAt?.ToString("dd/MM/yyyy HH:mm") ?? "",
            l.PrintedBy, l.PrintedAt?.ToString("dd/MM/yyyy HH:mm") ?? "", l.DeletedCqs, l.Notes, l.Flags }).ToList();
        return CqNl.Infrastructure.Excel.XlsxReportWriter.Build(new[]
        {
            new CqNl.Infrastructure.Excel.ReportSheet("Tổng hợp", new[] { "Chỉ tiêu", "Giá trị" }, summary, new double[] { 38, 40 }),
            new CqNl.Infrastructure.Excel.ReportSheet("Theo tháng", gh, r.ByMonth.Select(G).Append(G(t)).ToList(), null),
            new CqNl.Infrastructure.Excel.ReportSheet("Theo khách hàng", gh, r.ByCustomer.Select(G).ToList(), new double[] { 48 }),
            new CqNl.Infrastructure.Excel.ReportSheet("Chi tiết", dh, detail, new double[] { 22, 9, 12, 40, 30, 10, 10, 8, 10, 14, 12, 14, 22, 10, 12, 17, 12, 17, 10, 9, 30 }),
        });
    }
}
