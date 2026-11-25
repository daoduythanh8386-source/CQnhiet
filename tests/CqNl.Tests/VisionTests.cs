using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Imaging;
using CqNl.Infrastructure.Ocr;
using CqNl.Infrastructure.Storage;
using CqNl.Infrastructure.Vision;
using OpenCvSharp;
using Xunit;

namespace CqNl.Tests;

/// <summary>Module AI xử lý ảnh + OCR công nghiệp (21/11): kiểm trên ẢNH THẬT (Mitutoyo 47.3 / 38.2 / 58.4 / 50.1, dynaROCK 47.3 / 47.4,
/// đầu cân LED 41.40) và biến thể xấu (tối, mờ, nhiễu, nghiêng, lóa, độ phân giải thấp, đen trắng).
/// Tiêu chí an toàn: KHÔNG có "sai im lặng" (đưa ra số khác số thật mà không cảnh báo). Kết quả ghi ra ocr_vision_report.txt.
/// Bộ mẫu bắt buộc theo đặc tả (người dùng gửi 23/11): Mitutoyo 47.6 HRC, Mitutoyo 47.3 HRC, cân OHAUS Defender 2000 4.90 kg.</summary>
public class VisionTests
{
    public static readonly (string File, string Truth, string Kind)[] Samples =
    {
        ("mitu1_47.3.png", "47.3", "Hardness"), ("mitu_goc_38.2.jpg", "38.2", "Hardness"), ("mitu_full2_58.4.png", "58.4", "Hardness"),
        ("ref_mitu_cropped_50.1.jpg", "50.1", "Hardness"), ("dyna1_47.3.png", "47.3", "Hardness"), ("dyna2_47.4.png", "47.4", "Hardness"),
        ("sanpham_goc.jpg", "41.40", "Sample"),
        ("mitu_47.6.jpg", "47.6", "Hardness"), ("mitu_47.3b.jpg", "47.3", "Hardness"), ("ohaus_4.90.jpg", "4.90", "Sample"),
    };

    [Fact]
    public void Required_samples_specialized_readers_read_exact_value_on_original()
    {
        // Ohaus Defender 2000: tem dán đỏ phía trên + đoạn LED rời + đèn kg/Stable → trước 0.9 bộ đọc cân trả "8" (sai im lặng). Nay phải đúng 4.90.
        Assert.Equal("4.90", ScaleLedReader.Read(ImageNormalizer.Load(Img("ohaus_4.90.jpg"))).Value);
        Assert.Equal("41.40", ScaleLedReader.Read(ImageNormalizer.Load(Img("sanpham_goc.jpg"))).Value);
        Assert.Equal("47.6", HardnessOcr.Read(Img("mitu_47.6.jpg"), AppEnv.Paths.Tessdata).Value);
        Assert.Equal("47.3", HardnessOcr.Read(Img("mitu_47.3b.jpg"), AppEnv.Paths.Tessdata).Value);
    }

    private static readonly object ReportLock = new();
    private static void Report(string line)
    {
        lock (ReportLock) File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ocr_vision_report.txt"), line + Environment.NewLine);
    }

    private static byte[] Img(string f) => File.ReadAllBytes(TD.P(Path.Combine("img", f)));

    [Fact]
    public void Real_samples_original_untouched_both_reads_agree_with_truth()
    {
        foreach (var (f, truth, kind) in Samples)
        {
            var bytes = Img(f); var sha = FileStore.Sha256Hex(bytes);
            var (im, blobs) = ImageService.Process(bytes, Path.GetExtension(f).TrimStart('.'), kind, AppEnv.Paths);
            var v = im.Vision!;
            Report($"[gốc] {f,-28} thật={truth,-6} gốc={v.Passes[0].Value ?? "—",-6} xử lý={v.Passes[1].Value ?? "—",-6} cần xác nhận={v.NeedsReview} lỗi={v.Error ?? "-"} cảnh báo: {string.Join(" | ", v.Flags)}");
            Assert.Null(v.Error);
            Assert.Equal(sha, FileStore.Sha256Hex(bytes));                                  // ảnh gốc không bị thay đổi
            Assert.Equal(sha, im.BlobSha);
            Assert.Contains(blobs, b => FileStore.Sha256Hex(b.Bytes) == v.HdSha);
            Assert.Contains(blobs, b => FileStore.Sha256Hex(b.Bytes) == v.PrintSha);
            Assert.Equal(truth, v.Passes[0].Value);
            Assert.Equal(truth, v.Passes[1].Value);
            Assert.False(v.NeedsReview, f + ": " + string.Join(" | ", v.Flags));
        }
    }

    private static IEnumerable<(string Name, Mat Img)> Variants(Mat src)
    {
        Mat N() => new();
        var dark = N(); using (var lut = new Mat(1, 256, MatType.CV_8UC1)) { for (var i = 0; i < 256; i++) lut.Set(0, i, (byte)Math.Round(Math.Pow(i / 255.0, 2.2) * 255 * 0.6)); Cv2.LUT(src, lut, dark); }
        yield return ("tối", dark);
        var blur = N(); Cv2.GaussianBlur(src, blur, new Size(0, 0), 2.0); yield return ("mờ", blur);
        var noise = N();
        using (var s16 = new Mat()) using (var n16 = new Mat(src.Size(), MatType.CV_16SC3))
        {
            src.ConvertTo(s16, MatType.CV_16SC3);
            Cv2.Randn(n16, new Scalar(0, 0, 0), new Scalar(18, 18, 18));
            Cv2.Add(s16, n16, s16);
            s16.ConvertTo(noise, MatType.CV_8UC3);
        }
        yield return ("nhiễu", noise);
        var rot = N(); using (var M = Cv2.GetRotationMatrix2D(new Point2f(src.Cols / 2f, src.Rows / 2f), 9, 1.0)) Cv2.WarpAffine(src, rot, M, src.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
        yield return ("nghiêng 9°", rot);
        // lóa: đốm sáng mềm CHỒNG lên ảnh (chỉ làm mờ lớp đốm sáng, không làm mờ cả ảnh)
        var glare = N();
        using (var spot = new Mat(src.Size(), MatType.CV_8UC3, Scalar.All(0)))
        {
            Cv2.Ellipse(spot, new Point((int)(0.62 * src.Cols), (int)(0.35 * src.Rows)), new Size((int)(0.08 * src.Cols), (int)(0.05 * src.Rows)), 0, 0, 360, Scalar.All(255), -1);
            Cv2.GaussianBlur(spot, spot, new Size(0, 0), 6);
            Cv2.Add(src, spot, glare);                                   // cộng bão hoà → vùng lóa cháy trắng
        }
        yield return ("lóa", glare);
        var low = N(); Cv2.Resize(src, low, new Size(0, 0), 0.4, 0.4, InterpolationFlags.Area); yield return ("độ phân giải thấp", low);
        var gray = N(); using (var g = new Mat()) { Cv2.CvtColor(src, g, ColorConversionCodes.BGR2GRAY); Cv2.CvtColor(g, gray, ColorConversionCodes.GRAY2BGR); }
        yield return ("đen trắng", gray);
    }

    [Fact]
    public void Degraded_photos_never_produce_silent_wrong_value()
    {
        int total = 0, agreedCorrect = 0, flagged = 0, unread = 0, silentWrong = 0;
        foreach (var (f, truth, kind) in Samples.Where(s => s.File is "mitu1_47.3.png" or "dyna2_47.4.png" or "mitu_goc_38.2.jpg" or "sanpham_goc.jpg"))
        {
            using var src = VisionPipeline.Decode(Img(f));
            foreach (var (name, mat) in Variants(src))
            {
                using (mat)
                {
                    var bytes = VisionPipeline.Png(mat);
                    var (im, _) = ImageService.Process(bytes, "png", kind, AppEnv.Paths);
                    var v = im.Vision!; total++;
                    // số app sẽ TỰ dùng (không cần người xác nhận) = số đọc trên ảnh gốc khi không có cảnh báo
                    var suggestion = v.NeedsReview ? null : v.Passes[0].Value;
                    if (v.NeedsReview) flagged++; else if (suggestion is null) unread++; else if (suggestion == truth) agreedCorrect++; else silentWrong++;
                    Report($"[{name}] {f,-24} thật={truth,-6} gốc={v.Passes[0].Value ?? "—",-6} xử lý={v.Passes[1].Value ?? "—",-6} {(v.NeedsReview ? "CẢNH BÁO: " + v.Flags.FirstOrDefault() : "khớp")}");
                    Assert.True(v.NeedsReview || v.Passes[0].Value is null || v.Passes[0].Value == truth, $"SAI IM LẶNG: {f} [{name}] đọc {v.Passes[0].Value} ≠ {truth}");
                }
            }
        }
        Report($"TỔNG biến thể: {total} · khớp đúng {agreedCorrect} · cảnh báo chờ xác nhận {flagged} · không đọc được (nhập tay) {unread} · SAI IM LẶNG {silentWrong}");
        Assert.Equal(0, silentWrong);
    }

    [Fact]
    public void Hd_output_does_not_burn_highlights_print_is_grayscale_roi_found()
    {
        using var src = VisionPipeline.Decode(Img("mitu1_47.3.png"));
        var q0 = VisionPipeline.Quality(src);
        var o = VisionPipeline.Process(Img("mitu1_47.3.png"));
        using var hd = VisionPipeline.Decode(o.HdJpeg);
        var q1 = VisionPipeline.Quality(hd);
        Assert.True(q1.Saturated <= q0.Saturated + 0.01, $"cháy sáng tăng: {q0.Saturated:P2} → {q1.Saturated:P2}");
        Assert.Equal(src.Cols / (double)src.Rows, hd.Cols / (double)hd.Rows, 2);           // không méo tỉ lệ
        using var bw = Cv2.ImDecode(o.PrintPng, ImreadModes.Unchanged);
        Assert.Equal(1, bw.Channels());
        Assert.NotNull(o.RoiPng);
        Assert.Equal("LCD", o.Measurement!.Kind);
        using var roi = VisionPipeline.Decode(o.RoiPng!);
        Assert.True(roi.Cols >= o.Measurement.W, "vùng số đo phải được phóng to");
        Assert.Contains(o.HdSteps, s => s.StartsWith("CLAHE"));
    }

    [Fact]
    public void Measurement_check_rules()
    {
        OcrPass P(string src, string? v, double c, List<CharConf>? ch = null) => new(src, "test", v, v, null, c, ch);
        var ok = MeasurementCheck.Check(P("Ảnh gốc", "47.3", 1), P("Ảnh xử lý", "47.3", 1));
        Assert.False(ok.NeedsReview); Assert.Equal("47.3", ok.Agreed);
        Assert.True(MeasurementCheck.Check(P("Ảnh gốc", "47.3", 1), P("Ảnh xử lý", "47.8", 1)).NeedsReview);       // khác nhau
        Assert.True(MeasurementCheck.Check(P("Ảnh gốc", "41.40", 1), P("Ảnh xử lý", "41.4", 1)).NeedsReview);      // khác số chữ số → không tự coi là bằng
        Assert.True(MeasurementCheck.Check(P("Ảnh gốc", null, 0), P("Ảnh xử lý", "47.3", 1)).NeedsReview);         // chỉ 1 bản đọc được
        Assert.True(MeasurementCheck.Check(P("Ảnh gốc", null, 0), P("Ảnh xử lý", null, 0)).NeedsReview);
        Assert.True(MeasurementCheck.Check(P("Ảnh gốc", "4.90", 0.5), P("Ảnh xử lý", "4.90", 0.5)).NeedsReview);    // tin cậy thấp
        var lowChar = new List<CharConf> { new("4", 0.95), new(".", 0.4), new("9", 0.9), new("0", 0.9) };
        Assert.True(MeasurementCheck.Check(P("Ảnh gốc", "4.90", 0.9, lowChar), P("Ảnh xử lý", "4.90", 0.9)).NeedsReview); // ký tự kém tin cậy
        Assert.Equal("-0.02", MeasurementCheck.Norm("−0,02"));
        Assert.Null(MeasurementCheck.Norm("4O.1"));
    }

    [Fact]
    public void Text_fields_parsing_does_not_guess()
    {
        var a = TextOcr.Parse("47.3HRC"); Assert.Equal("47.3", a.Number); Assert.Equal("HRC", a.Unit);
        var b = TextOcr.Parse("58,4 HRC"); Assert.Equal("58.4", b.Number);
        var c = TextOcr.Parse("LOT: A12-345  26/11/2026 4.90kg"); Assert.Equal("4.90", c.Number); Assert.Equal("kg", c.Unit); Assert.Equal("A12-345", c.Lot); Assert.Equal("26/11/2026", c.Date);
        var d = TextOcr.Parse("DATE 2026-11-21 LOT NO.X99"); Assert.Equal("21/11/2026", d.Date); Assert.Equal("X99", d.Lot); Assert.Null(d.Number);
        var e = TextOcr.Parse("-0.02"); Assert.Equal("-0.02", e.Number); Assert.Equal("-", e.Sign);
        var f = TextOcr.Parse("31/02/2026"); Assert.Null(f.Date);                                    // ngày không hợp lệ → không đoán
        var g = TextOcr.Parse("S/N: MT-2049 12.5g"); Assert.Equal("MT-2049", g.DeviceCode); Assert.Equal("12.5", g.Number); Assert.Equal("g", g.Unit);
        Assert.Null(TextOcr.Parse("").Number);
    }

    [Fact]
    public void Text_ocr_returns_per_character_confidence_on_printed_label()
    {
        using var m = new Mat(140, 900, MatType.CV_8UC3, new Scalar(255, 255, 255));
        Cv2.PutText(m, "LOT A12-345 4.90 kg", new Point(20, 90), HersheyFonts.HersheySimplex, 2.0, new Scalar(0, 0, 0), 4, LineTypes.AntiAlias);
        var t = TextOcr.Read(m, AppEnv.Paths.Tessdata, null, singleLine: true);
        Report($"[tem nhãn tổng hợp] chuỗi=\"{t.Raw}\" tin cậy={t.Confidence:P0} ký tự={string.Join(" ", t.Chars.Select(c => $"{c.C}:{c.Conf:0.00}"))}");
        Assert.NotEmpty(t.Chars);
        Assert.All(t.Chars, c => Assert.InRange(c.Conf, 0, 1));
        var f = TextOcr.Parse(t.Raw);
        if (f.Number is not null) Assert.Equal("4.90", f.Number);                                    // đọc được thì phải đúng, không đoán
    }

    [Fact]
    public void Large_12mp_phone_photo_clean_and_noisy_reads_without_silent_error()
    {
        using var src = VisionPipeline.Decode(Img("mitu_goc_38.2.jpg"));
        using var big = new Mat(); Cv2.Resize(src, big, new Size(3000, 4000), 0, 0, InterpolationFlags.Cubic);
        using var noisy = new Mat();
        using (var s16 = new Mat()) using (var n16 = new Mat(big.Size(), MatType.CV_16SC3))
        {
            big.ConvertTo(s16, MatType.CV_16SC3); Cv2.Randn(n16, new Scalar(0, 0, 0), new Scalar(12, 12, 12)); Cv2.Add(s16, n16, s16); s16.ConvertTo(noisy, MatType.CV_8UC3);
        }
        foreach (var (name, m) in new[] { ("12MP sạch", big), ("12MP nhiễu", noisy) })
        {
            var bytes = VisionPipeline.Jpeg(m, 95);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (im, _) = ImageService.Process(bytes, "jpg", "Hardness", AppEnv.Paths);
            sw.Stop();
            var v = im.Vision!;
            Report($"[{name}] gốc={v.Passes[0].Value ?? "—"} xử lý={v.Passes[1].Value ?? "—"} cần xác nhận={v.NeedsReview} lỗi={v.Error ?? "-"} thời gian={sw.Elapsed.TotalSeconds:0.0}s");
            Assert.Null(v.Error);
            Assert.True(v.NeedsReview || v.Passes[0].Value is null || v.Passes[0].Value == "38.2", $"SAI IM LẶNG {name}: {v.Passes[0].Value}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(90), $"{name}: xử lý quá lâu {sw.Elapsed.TotalSeconds:0}s");
        }
    }

    [Fact]
    public void Workspace_flagged_image_blocks_auto_confirm_and_cq_until_person_confirms()
    {
        var root = TD.NewDir("vis_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var a = new Workspace(Path.Combine(root, "localA"), store, AppEnv.Paths, "MAY-A", TimeSpan.FromSeconds(10));
        Assert.True(a.Probe());
        a.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        var s = a.Login("admin", "Admin@2026");
        a.Auth.SaveUser(s, "kd1", "Kinh doanh", Role.Sales, true, "Tam@1234");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        a.Orders!.Apply(s, a.Orders.Preview(read), "erp.xlsx", _ => false);
        var key = read.Lines[0].Key;
        // ảnh tốt: có ảnh HD / in đen trắng / vùng số đo trong kho, có người tải + lịch sử
        var good = a.AddImage(key, a.GetOrder(key)!.Version, Img("mitu1_47.3.png"), "png", "Hardness");
        Assert.Equal("admin", good.AddedBy); Assert.NotNull(good.AddedAt);
        Assert.Contains(good.History, h => h.Action == "Tải ảnh lên"); Assert.Contains(good.History, h => h.Action == "Xử lý tự động");
        Assert.NotNull(a.ImageBytes(good.Vision!.HdSha!, "jpg")); Assert.NotNull(a.ImageBytes(good.Vision.PrintSha!, "png"));
        Assert.False(ImageVisionService.BlocksAuto(good));
        // ảnh chụp nghiêng 9°: chỉ MỘT bản đọc được số (gốc ≠ xử lý) → bị gắn cờ, không tự điền Mẫu, không xác nhận hàng loạt, chặn tạo CQ
        using var src = VisionPipeline.Decode(Img("mitu_goc_38.2.jpg"));
        using var g3 = new Mat();
        using (var M = Cv2.GetRotationMatrix2D(new Point2f(src.Cols / 2f, src.Rows / 2f), 9, 1.0)) Cv2.WarpAffine(src, g3, M, src.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
        var bad = a.AddImage(key, a.GetOrder(key)!.Version, VisionPipeline.Png(g3), "png", "Hardness");
        Report("[workspace] ảnh nghiêng 38.2: " + ImageService.VisionSummary(bad.Vision!));
        if (!ImageVisionService.BlocksAuto(bad))
        {
            // máy đọc được cả hai bản và khớp nhau → không có gì để chặn; ép trường hợp "hai bản khác nhau" bằng dữ liệu để kiểm luồng chặn
            Assert.Equal("38.2", bad.OcrValue);
            var id = bad.Id;
            var rr = a.EditQc(key, a.GetOrder(key)!.Version, q =>
            {
                var x = q.Images.Single(i => i.Id == id);
                x.Vision!.Passes[1].Value = "88.2"; x.Vision.NeedsReview = true; x.Vision.Flags.Insert(0, "Ảnh gốc đọc 38.2 ≠ ảnh xử lý đọc 88.2 (giả lập)");
            }, "test.vision");
            bad = rr.Order.Qc.Images.Single(i => i.Id == id);
        }
        Assert.True(ImageVisionService.BlocksAuto(bad), "ảnh có hai bản đọc khác nhau phải bị gắn cờ cần xác nhận");
        Assert.Equal(new[] { "47.3" }, Workspace.DerivedSamples(a.GetOrder(key)!.Order.Qc));
        var ca = a.ConfirmAllReadings(key, a.GetOrder(key)!.Version);
        Assert.Contains(ca.Skipped, x => x.Contains("cảnh báo OCR"));
        Assert.Null(ca.Row.Order.Qc.Images.Single(i => i.Id == bad.Id).ConfirmedValue);
        var ex = Assert.Throws<BusinessException>(() => a.PrepareCq(key));
        Assert.Contains("CẢNH BÁO OCR", ex.Message);
        // người xác nhận riêng → hết chặn, lịch sử ghi lại
        var r = a.ConfirmImage(key, a.GetOrder(key)!.Version, bad.Id, "38.2");
        var x = r.Order.Qc.Images.Single(i => i.Id == bad.Id);
        Assert.False(ImageVisionService.BlocksAuto(x));
        Assert.Contains(x.History, h => h.Action == "Xác nhận số" && h.Detail!.StartsWith("38.2"));
        // xử lý lại từ ảnh gốc: ảnh gốc giữ nguyên, số đã xác nhận giữ nguyên, có lịch sử
        var r2 = a.ReprocessImage(key, a.GetOrder(key)!.Version, good.Id);
        var y = r2.Order.Qc.Images.Single(i => i.Id == good.Id);
        Assert.Equal(good.BlobSha, y.BlobSha);
        Assert.Contains(y.History, h => h.Action == "Xử lý lại");
        a.Logout();
        // Kinh doanh: chỉ xem — không xử lý lại / xác nhận
        var k = a.Login("kd1", "Tam@1234"); a.Auth.ChangePassword(k, "Tam@1234", "Kd1@20260", "Kd1@20260"); a.Logout(); a.Login("kd1", "Kd1@20260");
        Assert.Throws<PermissionException>(() => a.ReprocessImage(key, a.GetOrder(key)!.Version, good.Id));
        Assert.Throws<PermissionException>(() => a.ConfirmImage(key, a.GetOrder(key)!.Version, good.Id, "47.3"));
    }
}
