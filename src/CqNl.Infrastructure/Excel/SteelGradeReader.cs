using System.Globalization;
using CqNl.Domain;

namespace CqNl.Infrastructure.Excel;

public sealed record SteelGradeTable(IReadOnlyList<SteelGrade> Grades, IReadOnlyList<string> Issues)
{
    /// <summary>Tra mác (chuẩn hóa). Nhiều dòng KHÁC giá trị → trả về tất cả để QC chọn (không tự chọn).</summary>
    public IReadOnlyList<SteelGrade> Find(string? grade)
    {
        var k = Domain.Grade.Normalize(grade);
        return k.Length == 0 ? Array.Empty<SteelGrade>() : Grades.Where(g => g.Key == k).ToList();
    }
}

/// <summary>Đọc bảng "THÔNG SỐ CÀI ĐẶT NHIỆT ĐỘ TÔI - RAM": cột C mác, D–G gia nhiệt 1–4, H–J ram 1–3, K dung sai, L thành phần, M đánh dấu.</summary>
public static class SteelGradeReader
{
    public static SteelGradeTable Read(string path)
    {
        var rows = XlsxReader.ReadSheet(path);
        // Kiểm tra đúng loại file (lỗi thực tế 13/11: chọn nhầm file template CQ vẫn "đọc được" → bảng mác rác)
        static string N(string? s) => (s ?? "").ToLowerInvariant().Replace("á", "a").Trim();
        var header = rows.Take(10).Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Length > 2 && N(x.r[2]?.Text).StartsWith("mac th"));
        if (header.r is null || !rows.Take(10).Any(r => r.Any(c => N(c?.Text).Contains("dung sai"))))
            throw new InvalidDataException("File không phải bảng mác thép (thiếu tiêu đề cột \"Mác thép\" ở cột C và \"Dung sai độ cứng\" trong 10 dòng đầu)");
        var grades = new List<SteelGrade>(); var issues = new List<string>();
        string group = "";
        for (var r = 7; r < rows.Count; r++)                                  // dữ liệu từ dòng 8 (0-based 7)
        {
            var row = rows[r];
            XCell? C(int i) => i < row.Length ? row[i] : null;
            if (C(0) is { IsEmpty: false } g0) group = g0.ToString().Trim();
            var name = C(2)?.ToString().Trim();
            if (string.IsNullOrEmpty(name)) continue;
            string? T(int i) { var c = C(i); if (c is null || c.IsEmpty) return null; var s = c.Text ?? c.Number!.Value.ToString(CultureInfo.InvariantCulture); s = s.Trim(); return c.Number is not null && c.Text is null && i <= 6 ? s + "℃" : s; }
            int? mark = C(12)?.Number is { } m ? (int)m : null;
            var g = new SteelGrade(r + 1, group, name, new[] { T(3), T(4), T(5), T(6) }, new[] { T(7), T(8), T(9) }, T(10), T(11), mark);
            grades.Add(g);
            if (g.Tolerance is null) issues.Add($"Dòng {r + 1} {name}: thiếu dung sai độ cứng");
            if (g.HeatCount == 0) issues.Add($"Dòng {r + 1} {name}: thiếu nhiệt độ tôi");
        }
        foreach (var dup in grades.GroupBy(g => g.Key).Where(x => x.Count() > 1))
        {
            var distinct = dup.Select(g => string.Join("|", g.Heats) + "#" + string.Join("|", g.Rams) + "#" + (g.Tolerance ?? "").Replace(" ", "") + "#" + g.TemplateMark).Distinct().Count();
            issues.Add(distinct > 1
                ? $"Mác {dup.First().Name} trùng ở dòng {string.Join(", ", dup.Select(g => g.SourceRow))} với GIÁ TRỊ KHÁC NHAU → QC phải chọn dòng"
                : $"Mác {dup.First().Name} trùng ở dòng {string.Join(", ", dup.Select(g => g.SourceRow))} (cùng giá trị)");
        }
        return new SteelGradeTable(grades, issues);
    }
}
