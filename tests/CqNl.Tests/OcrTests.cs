using CqNl.Infrastructure.Imaging;
using CqNl.Infrastructure.Ocr;
using Xunit;

namespace CqNl.Tests;

/// <summary>Bộ ảnh vàng (giống research/ocr/golden.json). Quy tắc an toàn: được phép TỪ CHỐI, KHÔNG được đọc SAI.</summary>
public class OcrTests
{
    private static string Tess => Path.Combine(AppContext.BaseDirectory, "TestData", "tessdata");

    [Theory]
    [InlineData("mitu_goc_38.2.jpg", "38.2")]
    [InlineData("ref_mitu_cropped_50.1.jpg", "50.1")]
    [InlineData("mitu1_47.3.png", "47.3")]
    [InlineData("mitu_full2_58.4.png", "58.4")]
    [InlineData("dyna1_47.3.png", "47.3")]
    [InlineData("dyna2_47.4.png", "47.4")]
    [InlineData("ocr_sample_machine.png", null)]
    public void Never_wrong(string file, string? expected)
    {
        var r = HardnessOcr.Read(File.ReadAllBytes(TD.P(Path.Combine("img", file))), Tess);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ocr_report.txt"), $"{file}\tmong={expected}\tdoc={r.Value}\ttin={r.Confidence}\traw={r.Raw}\t{r.Info}{Environment.NewLine}");
        Assert.True(r.Value is null || r.Value == expected, $"ĐỌC SAI: {file} → {r.Value} (đúng {expected}) | {r.Info}");
    }

    [Fact]
    public void Mitutoyo_reads_exact_with_decimal_point()
    {   // các ảnh bản Python đọc tin cậy 1.0 — bản .NET phải đọc đúng (không chỉ "không sai")
        foreach (var (f, v) in new[] { ("mitu_goc_38.2.jpg", "38.2"), ("mitu1_47.3.png", "47.3"), ("mitu_full2_58.4.png", "58.4") })
            Assert.Equal(v, MitutoyoReader.Read(ImageNormalizer.Load(File.ReadAllBytes(TD.P(Path.Combine("img", f))))).Value);
    }

    [Fact]
    public void Scale_led_reads_original_and_rejects_cropped()
    {
        Assert.Equal("41.40", ScaleLedReader.Read(ImageNormalizer.Load(File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))))).Value);
        Assert.Null(ScaleLedReader.Read(ImageNormalizer.Load(File.ReadAllBytes(TD.P(Path.Combine("img", "ref_sanpham_da_chinh.jpg"))))).Value);
        Assert.Null(ScaleLedReader.Read(ImageNormalizer.Load(File.ReadAllBytes(TD.P(Path.Combine("img", "mitu_goc_38.2.jpg"))))).Value);
    }
}
