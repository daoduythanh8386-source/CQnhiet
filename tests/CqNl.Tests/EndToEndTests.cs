using System.Text.Json.Nodes;
using CqNl.Domain;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Imaging;
using CqNl.Infrastructure.Pdf;
using CqNl.Infrastructure.Storage;
using Xunit;

namespace CqNl.Tests;

public class EndToEndTests
{
    private static CqImage Img(byte[] b, string name) { var (w, h) = CqImages.Size(b); return new CqImage(b, w, h, name); }

    /// <summary>Luồng thật: ảnh thật → thẻ sắp xếp/bỏ trùng, nhãn số cân, chữ ký chuẩn hóa → CQ 3 template → PDF bằng Excel (A4, 1 trang).</summary>
    [Theory]
    [InlineData("carbon", new[] { "650℃", "800℃" })]
    [InlineData("khuon", new[] { "650℃", "850℃", "1080℃" })]
    [InlineData("gio", new[] { "650℃", "850℃", "1000℃", "1180℃" })]
    public void Real_images_to_A4_pdf(string tid, string[] temps)
    {
        if (!ExcelPdfExporter.IsAvailable()) return;   // máy không có Excel: bỏ qua (ghi trong báo cáo)
        var map = TemplateMap.Load(TD.P(Path.Combine("maps", tid + ".json")));
        var d = new CqData();
        d.Values["Customer"] = "Công ty TNHH Takara Tool & Die Hà Nội"; d.Values["Material"] = "SKD11"; d.Values["Dngc"] = "DNGC/HY3-26090216";
        d.Values["ReportNo"] = "001/11"; d.Values["HardnessRequirement"] = "35-60"; d.Values["HardnessTolerance"] = "(MAX-MIN)±1";
        d.Values["Remark"] = HardnessEvaluator.Remark(EvaluationStatus.Pass);
        d.Numbers["Quantity"] = 2; d.Numbers["WeightKg"] = 41.4m;
        d.QuenchTemps = temps.ToList(); d.RamValues = new() { "150p", "180p", "200p" };
        var photos = new[] { ("mitu_full2_58.4.png", 58.4m), ("mitu_goc_38.2.jpg", 38.2m), ("dyna2_47.4.png", 47.4m), ("mitu1_47.3.png", 47.3m), ("dyna1_47.3.png", 47.30m) }
            .Select((p, i) => (bytes: File.ReadAllBytes(TD.P(Path.Combine("img", p.Item1))), photo: new HardnessPhoto(p.Item1, p.Item2, "D1", "Takara", "SKD11", i))).ToList();
        var (kept, dropped) = HardnessPhotoLayout.Arrange(photos.Select(p => p.photo).ToList());
        Assert.Single(dropped);
        foreach (var k in kept)
        {
            var b = photos.First(p => p.photo.Id == k.Id).bytes;
            if (k.Id.StartsWith("mitu", StringComparison.Ordinal))    // ảnh Mitutoyo: cắt quanh mặt máy + làm sáng (preset người dùng duyệt)
            {
                var nm = ImageNormalizer.NormalizeMitutoyo(b, ImageNormalizer.LoadPreset(TD.P(Path.Combine("presets", "mitutoyo-cq.json"))));
                if (!nm.NeedsManualCrop) b = nm.Jpeg;
            }
            d.HardnessImages.Add(Img(CqImages.Tile(b, $"{k.ConfirmedValue} HRC", 1.0), k.Id));

        }
        // Ảnh xuất hàng: (1) đọc số cân trên ẢNH GỐC (QC xác nhận 41.40) → (2) CẮT + làm sáng theo preset → (3) ghi số góc dưới trái.
        // (Lỗi #13: bản trước bỏ sót bước 2 – chỉ ghi số lên ảnh gốc; người dùng phát hiện.)
        var prod = ImageNormalizer.NormalizeProduct(File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))),
            ImageNormalizer.LoadPreset(TD.P(Path.Combine("presets", "san-pham-cq.json"))));
        Assert.False(prod.NeedsManualCrop); Assert.True(prod.Crop.Top > 250, "Ảnh xuất hàng chưa được cắt bỏ đầu cân");
        var (pw, ph) = CqImages.Size(prod.Jpeg);
        var printedMm = CqWriter.PrintedHeightMm(File.ReadAllBytes(TD.P(map.FileName)), map, "SampleImageFrame", pw, ph);
        d.SampleImages.Add(Img(CqImages.CornerLabel(prod.Jpeg, CqConventions.ScaleLabel("41.40"), ph * 4.0 / printedMm), "SampleImage"));   // ô chữ cao ≈ 4 mm khi in; có "kg" (chốt 13/11)
        // Bảng Mẫu 1–6: TĂNG DẦN như ảnh (chốt 13/11); đủ mọi giá trị đã nhập (kể cả 47.3 trùng — chỉ ảnh mới bỏ trùng)
        d.Samples.AddRange(HardnessEvaluator.CqSampleCellsAscending(photos.Select(p => (string?)p.photo.ConfirmedValue!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList()));
        Assert.Equal(new[] { "38.2", "47.3", "47.30", "47.4", "58.4" }, d.Samples);
        var sigs = Path.Combine(AppContext.BaseDirectory, "TestData", "sig");
        if (Directory.Exists(sigs))
        {
            d.Overlays.Add(new CqOverlay(Img(CqImages.Signature(File.ReadAllBytes(Path.Combine(sigs, "approver.png")), transparent: CqConventions.TransparentSignatureOnCq), "Approver"), Frame: map.Ranges("ApproverSignatureFrame")[0]));
            d.Overlays.Add(new CqOverlay(Img(CqImages.Signature(File.ReadAllBytes(Path.Combine(sigs, "thanh_moi_goc.jpg")), transparent: CqConventions.TransparentSignatureOnCq), "Checker"), Frame: map.Ranges("CheckerSignatureFrame")[0]));
            d.Overlays.Add(new CqOverlay(Img(File.ReadAllBytes(Path.Combine(sigs, "stamp.png")), "Stamp"), Cell: "N33", DxMm: -15, DyMm: 4, WidthMm: 38));
        }
        var outDir = Path.Combine(AppContext.BaseDirectory, "e2e"); Directory.CreateDirectory(outDir);
        var xlsx = Path.Combine(outDir, $"CQ_{tid}.xlsx"); var pdf = Path.Combine(outDir, $"CQ_{tid}.pdf");
        File.WriteAllBytes(xlsx, CqWriter.Build(File.ReadAllBytes(TD.P(map.FileName)), map, d));
        var chk = ExcelPdfExporter.Export(xlsx, pdf);
        Assert.True(chk.IsA4); Assert.Equal(1, chk.Pages);
    }

    /// <summary>Kho chung qua SMB thật (đường dẫn UNC thử nghiệm, KHÔNG phải kho nghiệp vụ). Bật bằng biến môi trường CQNL_UNC_TEST.</summary>
    [Fact]
    public void Unc_store_concurrency()
    {
        var unc = Environment.GetEnvironmentVariable("CQNL_UNC_TEST");
        if (string.IsNullOrEmpty(unc)) return;
        var root = Path.Combine(unc, "run_" + Guid.NewGuid().ToString("N"));
        int ok = 0, conflicts = 0;
        Parallel.For(0, 8, i =>
        {
            var s = new FileStore(root, $"MAY-{i}");
            for (var k = 0; k < 15; k++)
            {
                var vs = s.Versions("order", "race"); var bas = vs.Count == 0 ? 0 : vs[^1];
                try { s.Commit("order", "race", new JsonObject { ["base"] = bas }, bas, $"u{i}", Guid.NewGuid().ToString()); Interlocked.Increment(ref ok); }
                catch (ConflictException) { Interlocked.Increment(ref conflicts); }
            }
        });
        var st = new FileStore(root);
        Assert.Equal(Enumerable.Range(1, ok), st.Versions("order", "race"));
        foreach (var v in st.Versions("order", "race")) Assert.Equal(v - 1, (int)st.Read("order", "race", v)!.Data["base"]!);
        var nums = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 8, i => { var al = new ReportNumberAllocator(new FileStore(root, $"M{i}")); for (var k = 0; k < 5; k++) nums.Add(al.Claim(2026, 11, $"cq{i}")); });
        Assert.Equal(40, nums.Distinct().Count());
        File.WriteAllText(Path.Combine(unc, "unc_result.txt"), $"{DateTime.Now:O} UNC={root} commits={ok} conflicts={conflicts} reportNos=40 unique");
    }
}
