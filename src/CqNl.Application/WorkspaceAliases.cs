using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CqNl.Infrastructure.Excel;

namespace CqNl.Application;

/// <summary>Đề xuất tên viết tắt: tên khách hàng ERP → tên in trên CQ (cột "KHÁCH HÀNG" sổ THEO DÕI ĐƠN HÀNG).</summary>
public sealed record AliasProposal(string ErpName, string ShortName, string Source, int Orders);
public sealed record AliasScan(string Sheet, int TrackingRows, IReadOnlyList<string> ShortNames, IReadOnlyList<AliasProposal> Proposals, int UnmappedCustomers);

public sealed partial class Workspace
{
    /// <summary>Bỏ dấu, chữ hoa, chỉ giữ chữ-số (so khớp tên khách hàng).</summary>
    public static string Plain(string? s)
    {
        var d = (s ?? "").Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return Regex.Replace(sb.ToString().ToUpperInvariant(), @"[^A-Z0-9]+", " ").Trim();
    }

    /// <summary>Tên in trên CQ cho khách hàng ERP (sửa tay ở đơn &gt; tên viết tắt &gt; tên ERP).</summary>
    public string CustomerOnCq(OrderRecord o)
    {
        if (!string.IsNullOrWhiteSpace(o.Qc.CustomerOverride)) return o.Qc.CustomerOverride!;
        var erp = o.Erp.GetValueOrDefault("Customer");
        return SettingsService.AliasOf(CustomerAliases(), erp) ?? erp ?? "";
    }

    private Dictionary<string, string>? _aliases; private DateTime _aliasesAt;
    /// <summary>Bảng tên viết tắt (đọc lại kho tối đa 1 lần/30 giây; offline dùng bản đã đọc).</summary>
    public IReadOnlyDictionary<string, string> CustomerAliases(bool refresh = false)
    {
        if (Online && (refresh || _aliases is null || DateTime.Now - _aliasesAt > TimeSpan.FromSeconds(30)))
            try { _aliases = new(Settings!.Get().Value.CustomerAliases); _aliasesAt = DateTime.Now; } catch { }
        return _aliases ?? new Dictionary<string, string>();
    }

    /// <summary>Đọc sổ "THEO DÕI ĐƠN HÀNG" (file CQ 20xx.xlsx): ghép số ĐNGC với đơn ERP đã có → tên viết tắt; khách chưa ghép được
    /// thì dò tên viết tắt nằm trong tên ERP (vd. "Takara" trong "Công ty TNHH Takara Tool &amp; Die Hà Nội"). Không ghi gì.</summary>
    public AliasScan ScanTrackingAliases(string path)
    {
        var names = XlsxReader.SheetNames(path);
        string N(string? x) => Plain(x);
        var sheet = names.FirstOrDefault(n => N(n).Contains("THEO DOI DON HANG")) ?? names.FirstOrDefault(n => N(n).Contains("THEO DOI"))
                    ?? throw new BusinessException("File không có sheet \"THEO DÕI ĐƠN HÀNG\"");
        var rows = XlsxReader.ReadSheet(path, sheet, 20000);
        int hdr = -1, cD = -1, cK = -1;
        for (var r = 0; r < Math.Min(12, rows.Count) && hdr < 0; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                var t = N(rows[r][c]?.Text);
                if (cD < 0 && Regex.IsMatch(t, @"\bDNGC\b")) cD = c;
                if (cK < 0 && t.StartsWith("KHACH HANG")) cK = c;
            }
            if (cD >= 0 && cK >= 0) hdr = r; else { cD = -1; cK = -1; }
        }
        if (hdr < 0) throw new BusinessException("Không thấy dòng tiêu đề có cột \"SỐ ĐNGC\" và \"KHÁCH HÀNG\"");
        var byDngc = new Dictionary<string, Dictionary<string, int>>();
        var shorts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var n = 0;
        for (var r = hdr + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var e = row.ElementAtOrDefault(cD); var h = row.ElementAtOrDefault(cK)?.Text?.Trim();
            var num = e?.Number is { } d ? ((long)d).ToString(CultureInfo.InvariantCulture) : Regex.Match(e?.Text ?? "", @"\d{6,}").Value;
            if (string.IsNullOrWhiteSpace(h) || num.Length < 6 || Regex.IsMatch(h, @"^\d+$")) continue;
            n++;
            shorts[h] = shorts.GetValueOrDefault(h) + 1;
            if (!byDngc.TryGetValue(num, out var m)) byDngc[num] = m = new();
            m[h] = m.GetValueOrDefault(h) + 1;
        }
        // khách hàng ERP đang có trong danh sách đơn
        var orders = ListOrders().Select(x => x.Order).ToList();
        var existing = CustomerAliases(true);
        var props = new List<AliasProposal>(); var unmapped = 0;
        foreach (var g in orders.GroupBy(o => SettingsService.CustomerKey(o.Erp.GetValueOrDefault("Customer"))).Where(g => g.Key.Length > 0))
        {
            var erpName = g.First().Erp.GetValueOrDefault("Customer")!.Trim();
            if (existing.ContainsKey(g.Key)) continue;
            var votes = new Dictionary<string, int>();
            foreach (var o in g)
            {
                var num = Regex.Match(o.Key.Split('|')[0], @"(\d{6,})$").Groups[1].Value;
                if (byDngc.TryGetValue(num, out var m)) foreach (var (k, v) in m) votes[k] = votes.GetValueOrDefault(k) + v;
            }
            if (votes.Count > 0) { var best = votes.OrderByDescending(x => x.Value).First().Key; props.Add(new(erpName, best, "ghép theo số ĐNGC", g.Count())); continue; }
            var pe = " " + N(erpName) + " ";
            var cands = shorts.Keys.Where(s => N(s).Length >= 3 && pe.Contains(" " + N(s) + " ")).OrderByDescending(s => N(s).Length).ThenByDescending(s => shorts[s]).ToList();
            var distinct = cands.Select(N).Distinct().ToList();
            if (distinct.Count >= 1 && (distinct.Count == 1 || N(cands[0]).Contains(distinct[1])))
                props.Add(new(erpName, cands[0], "tên viết tắt nằm trong tên ERP", g.Count()));
            else unmapped++;
        }
        return new AliasScan(sheet, n, shorts.OrderByDescending(x => x.Value).Select(x => x.Key).ToList(), props.OrderByDescending(p => p.Orders).ToList(), unmapped);
    }

    public int ApplyAliasProposals(IEnumerable<AliasProposal> proposals)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        RequireOnline("lưu tên viết tắt khách hàng");
        var n = Settings!.SetCustomerAliases(s, proposals.ToDictionary(p => p.ErpName, p => (string?)p.ShortName));
        CustomerAliases(true);
        return n;
    }

    public void SetCustomerAlias(string erpName, string? shortName)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        RequireOnline("lưu tên viết tắt khách hàng");
        Settings!.SetCustomerAlias(s, erpName, shortName);
        CustomerAliases(true);
    }
}
