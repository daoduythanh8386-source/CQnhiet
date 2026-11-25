using CqNl.Domain;
using Xunit;

namespace CqNl.Tests;

public class DomainTests
{
    [Theory]
    [InlineData("58-62", "MIN -0.7", "57.3", EvaluationStatus.Pass)]
    [InlineData("58-62", "MIN -0.7", "57.2", EvaluationStatus.Fail)]
    [InlineData("58-62", "MIN -0.7", "62.1", EvaluationStatus.Fail)]
    [InlineData("56-60", "(MAX-MIN)±1", "55 ; 61", EvaluationStatus.Pass)]
    [InlineData("56-60", "( MAX-MIN ) ±1", "61.1", EvaluationStatus.Fail)]
    [InlineData("58-60", null, "58 60", EvaluationStatus.Pass)]
    [InlineData("MAX", "MIN -0.7", "58", EvaluationStatus.InvalidRequirement)]
    public void Evaluate_vectors(string req, string? tol, string sample, EvaluationStatus want)
        => Assert.Equal(want, HardnessEvaluator.Evaluate(req, tol, new[] { sample }).Status);

    [Fact]
    public void Max_tolerance_fails_on_low_point()
        => Assert.Equal(EvaluationStatus.Fail, HardnessEvaluator.Evaluate("60-62", "MAX +0.5", new[] { "62.5", "59.9" }).Status);

    [Theory]
    [InlineData("MIN - 0.7", 57.3, 62)]
    [InlineData("MIN ±0.7", 57.3, 62)]
    [InlineData("MAX+0.7", 58, 62.7)]
    [InlineData("", 58, 62)]
    public void Limits(string tol, double lo, double hi)
    {
        var (l, h) = Tolerance.Limits(new HardnessRequirement(58, 62), tol);
        Assert.Equal((decimal)lo, l); Assert.Equal((decimal)hi, h);
    }

    [Fact] public void Unknown_tolerance_blocks() => Assert.Throws<ToleranceException>(() => Tolerance.Limits(new HardnessRequirement(58, 62), "abc"));

    [Theory]
    [InlineData("MAX")] [InlineData(null)] [InlineData("65.67")] [InlineData("49-91")] [InlineData("Bề măt: 350-450hv; Lõi: 250-320HV")] [InlineData("62-58")]
    public void Invalid_requirements_are_rejected(string? raw) => Assert.Null(HardnessRequirement.TryParse(raw));

    [Fact] public void Requirement_dash_and_spaces() => Assert.Equal(new HardnessRequirement(58, 62), HardnessRequirement.TryParse(" 58 – 62 "));
    [Fact] public void Points_parse() => Assert.Equal(new[] { 58.4m, 58.8m, 59m }, HardnessEvaluator.ParsePoints("58,4;58.8  59"));
    [Fact] public void Points_parse_cq_style_roundtrip()
    {
        Assert.Equal(new[] { 59.5m, 58.2m, 60m }, HardnessEvaluator.ParsePoints("59.5 / 58.2/60"));
        Assert.Equal("59.5 / 58.2", HardnessEvaluator.CqSampleCell(HardnessEvaluator.CqSampleCell("59.5 58.2")));   // ô CQ đọc lại được
    }
    [Fact] public void Bad_point_throws() => Assert.Throws<FormatException>(() => HardnessEvaluator.ParsePoints("58.4 5x"));
    [Fact] public void Cq_cell_keeps_digits() => Assert.Equal("58.4 / 58.80", HardnessEvaluator.CqSampleCell("58.4 58,80"));
    [Fact] public void Cq_cell_empty_dash() => Assert.Equal("-", HardnessEvaluator.CqSampleCell(""));
    [Fact]
    public void Samples_sorted_ascending_for_cq()
        => Assert.Equal(new[] { "38.2", "47.3", "47.3", "47.4", "58.4", "-" },
            HardnessEvaluator.CqSampleCellsAscending(new[] { "58.4", "", "38.2", "47.4", "47.3", "47,3" }));

    [Fact]
    public void Multi_point_sample_sorted_by_smallest_point()
        => Assert.Equal(new[] { "59.1 / 57.9", "58.2", "58.4 / 58.8" } /* mẫu xếp theo điểm nhỏ nhất; điểm TRONG mẫu giữ thứ tự người nhập */, HardnessEvaluator.CqSampleCellsAscending(new[] { "58.4 58.8", "58.2", "59.1;57.9" }));

    [Fact] public void Remark_fail() => Assert.Equal("Nhận xét/Remark: Không đạt độ cứng; mẫu 2 thấp", HardnessEvaluator.Remark(EvaluationStatus.Fail, "mẫu 2 thấp"));
    [Fact] public void Max_six_samples() => Assert.Throws<ArgumentException>(() => HardnessEvaluator.Evaluate("58-62", null, Enumerable.Repeat("58", 7).ToList()));

    private static MergeCandidate L(string dngc = "A", string grade = "SKD11", string req = "58-62", bool cq = false) => new(dngc, 1, grade, req, cq, null);
    [Fact] public void Merge_normalized_grade() => Assert.True(MergeRules.CanMerge(new[] { L(), L(grade: "skd-11") }).Ok);
    [Fact] public void Merge_diff_dngc() => Assert.False(MergeRules.CanMerge(new[] { L(), L(dngc: "B") }).Ok);
    [Fact] public void Merge_diff_req() => Assert.False(MergeRules.CanMerge(new[] { L(), L(req: "58-60") }).Ok);
    [Fact] public void Merge_with_cq() => Assert.False(MergeRules.CanMerge(new[] { L(), L(cq: true) }).Ok);

    [Fact] public void Report_number_never_reuses() => Assert.Equal(("005/11", 5), ReportNumber.Next(new[] { 1, 2, 4 }, 11));
    [Fact] public void Report_number_new_month() => Assert.Equal(("001/12", 1), ReportNumber.Next(Array.Empty<int>(), 12));

    [Fact]
    public void Photos_sorted_and_deduplicated_when_three_or_more()
    {
        HardnessPhoto P(string id, decimal? v, int o, string dngc = "D1") => new(id, v, dngc, "Takara", "SKD11", o);
        var (kept, dropped) = HardnessPhotoLayout.Arrange(new[] { P("a", 58.4m, 1), P("b", 38.2m, 2), P("c", 47.4m, 3), P("d", 47.3m, 4), P("e", 47.30m, 5) });
        Assert.Equal(new[] { "b", "d", "c", "a" }, kept.Select(k => k.Id));
        Assert.Single(dropped); Assert.Equal("e", dropped[0].Dropped.Id);
    }

    [Fact]
    public void Two_duplicate_photos_kept()
    {
        var (kept, dropped) = HardnessPhotoLayout.Arrange(new[] { new HardnessPhoto("a", 47.3m, "D", "K", "G", 1), new HardnessPhoto("b", 47.30m, "D", "K", "G", 2) });
        Assert.Equal(2, kept.Count); Assert.Empty(dropped);
    }

    [Fact]
    public void Other_order_and_unconfirmed_not_dropped()
    {
        var ps = new[] { new HardnessPhoto("a", 47.3m, "D1", "K", "G", 1), new HardnessPhoto("b", 47.3m, "D2", "K", "G", 2), new HardnessPhoto("c", null, "D1", "K", "G", 3) };
        var (kept, dropped) = HardnessPhotoLayout.Arrange(ps);
        Assert.Equal(3, kept.Count); Assert.Empty(dropped); Assert.Equal("c", kept[^1].Id);
    }
}

public class ConventionTests
{
    [Fact] public void Scale_label_has_kg() => Assert.Equal("41.40 kg", CqConventions.ScaleLabel("41.40"));
    [Fact] public void Scale_label_keeps_digits() => Assert.Equal("135.2 kg", CqConventions.ScaleLabel(" 135.2 "));
    [Fact] public void Signatures_transparent_on_cq() => Assert.True(CqConventions.TransparentSignatureOnCq);
}
