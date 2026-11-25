using System.Drawing;
using CqNl.Infrastructure.Imaging;
using Xunit;

namespace CqNl.Tests;

public class ImagingTests
{
    private static System.Text.Json.JsonElement Preset(string n) => ImageNormalizer.LoadPreset(TD.P(Path.Combine("presets", n)));

    private static double MeanAbsDiff(byte[] jpgA, byte[] jpgB)
    {
        var a = ImageNormalizer.Load(jpgA); var b = ImageNormalizer.Load(jpgB);
        if (a.W != b.W || a.H != b.H) b = ImageNormalizer.ResizeArea(b, a.W, a.H);
        double s = 0; var n = a.W * a.H;
        for (var i = 0; i < n; i++) s += Math.Abs(a.R[i] - b.R[i]) + Math.Abs(a.G[i] - b.G[i]) + Math.Abs(a.B[i] - b.B[i]);
        return s / (3.0 * n);
    }

    /// <summary>Ảnh xuất hàng: phải CẮT vùng sản phẩm (bỏ đầu cân, kệ) như ảnh mẫu người dùng cắt tay (100,305)-(937,1191).</summary>
    [Fact]
    public void Product_photo_is_cropped_like_user_reference()
    {
        var r = ImageNormalizer.NormalizeProduct(File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))), Preset("san-pham-cq.json"));
        Assert.False(r.NeedsManualCrop, string.Join("; ", r.Warnings));
        var userCrop = Rectangle.FromLTRB(100, 305, 937, 1191);
        Assert.InRange(Math.Abs(r.Crop.Left - userCrop.Left), 0, 10); Assert.InRange(Math.Abs(r.Crop.Top - userCrop.Top), 0, 10);
        Assert.InRange(Math.Abs(r.Crop.Right - userCrop.Right), 0, 10); Assert.InRange(Math.Abs(r.Crop.Bottom - userCrop.Bottom), 0, 10);
        Assert.True(r.Crop.Top > 250, "Phải cắt bỏ đầu cân phía trên");
        var outSize = CqImages.Size(r.Jpeg);
        Assert.Equal(r.Crop.Width, outSize.W); Assert.Equal(r.Crop.Height, outSize.H);       // ảnh ra đúng là vùng đã cắt
        var diff = MeanAbsDiff(File.ReadAllBytes(TD.P(Path.Combine("golden", "sanpham_python.jpg"))), r.Jpeg);
        Assert.True(diff < 8, $"Làm sáng/màu lệch bản Python đã duyệt: {diff:0.0}");
        var orig = ImageNormalizer.Load(File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))));
        var o = ImageNormalizer.Load(r.Jpeg);
        double Mean(ImageNormalizer.Rgb x) => Enumerable.Range(0, x.W * x.H).Average(i => (x.R[i] + x.G[i] + x.B[i]) / 3.0);
        Assert.True(Mean(o) > Mean(ImageNormalizer.Crop(orig, r.Crop)) + 20, "Ảnh phải sáng hơn rõ rệt so với gốc");
    }

    [Fact]
    public void Mitutoyo_photo_cropped_around_machine()
    {
        var r = ImageNormalizer.NormalizeMitutoyo(File.ReadAllBytes(TD.P(Path.Combine("img", "mitu_goc_38.2.jpg"))), Preset("mitutoyo-cq.json"));
        Assert.False(r.NeedsManualCrop, string.Join("; ", r.Warnings));
        var py = Rectangle.FromLTRB(0, 630, 1776, 2065);
        Assert.InRange(Math.Abs(r.Crop.Left - py.Left), 0, 8); Assert.InRange(Math.Abs(r.Crop.Top - py.Top), 0, 8);
        Assert.InRange(Math.Abs(r.Crop.Right - py.Right), 0, 8); Assert.InRange(Math.Abs(r.Crop.Bottom - py.Bottom), 0, 8);
        var diff = MeanAbsDiff(File.ReadAllBytes(TD.P(Path.Combine("golden", "mitu38_python.jpg"))), r.Jpeg);
        Assert.True(diff < 8, $"Làm sáng lệch bản Python: {diff:0.0}");
    }

    [Fact]
    public void Too_close_mitutoyo_needs_manual_crop()
    {
        var r = ImageNormalizer.NormalizeMitutoyo(File.ReadAllBytes(TD.P(Path.Combine("img", "mitu_full2_58.4.png"))), Preset("mitutoyo-cq.json"));
        Assert.True(r.NeedsManualCrop);
    }

    [Fact]
    public void Already_cropped_product_photo_is_not_recropped_blindly()
    {   // ảnh người dùng đã cắt sẵn (không còn đầu cân) — không được cắt mất sản phẩm
        var src = File.ReadAllBytes(TD.P(Path.Combine("img", "ref_sanpham_da_chinh.jpg")));
        var r = ImageNormalizer.NormalizeProduct(src, Preset("san-pham-cq.json"));
        var (w, h) = CqImages.Size(src);
        Assert.True(r.NeedsManualCrop || r.Crop.Width * r.Crop.Height > 0.6 * w * h, $"Cắt quá nhiều: {r.Crop}");
    }
}
