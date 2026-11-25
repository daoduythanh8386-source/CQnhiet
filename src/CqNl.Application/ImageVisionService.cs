using CqNl.Infrastructure.Imaging;
using CqNl.Infrastructure.Ocr;
using CqNl.Infrastructure.Storage;
using CqNl.Infrastructure.Vision;

namespace CqNl.Application;

/// <summary>
/// Module xử lý ảnh + kiểm soát OCR DÙNG CHUNG cho mọi loại ảnh (đồng hồ độ cứng, cân, thước cặp, panme, máy đo chiều dày,
/// tem LOT, phiếu kiểm tra, ảnh ERP…). Chạy hoàn toàn trên máy (không cần kho) → dùng được cả khi OFFLINE.
/// - Ảnh gốc: KHÔNG đụng tới (bytes chỉ đọc); các bản xử lý là blob MỚI theo mã SHA-256.
/// - Số đo: đọc bằng CÙNG bộ đọc trên ảnh gốc và trên bản xử lý (cùng kích thước) → so sánh. Khác / thiếu / tin cậy thấp → NeedsReview.
/// - Không tự sửa giá trị, không suy đoán: thiếu → null.
/// </summary>
public static class ImageVisionService
{
    /// <summary>Phân tích một ảnh. <paramref name="original"/> = kết quả bộ đọc chuyên dụng trên ẢNH GỐC (đã chạy ở bước cũ), có thể null.</summary>
    public static (ImageVision Vision, List<(byte[] Bytes, string Ext)> Blobs) Analyze(byte[] bytes, string kind, MeterReading? original, AppPaths paths)
    {
        var v = new ImageVision { Version = VisionPipeline.Version, ProcessedAt = DateTimeOffset.Now };
        var blobs = new List<(byte[], string)>();
        try
        {
            var o = VisionPipeline.Process(bytes);
            v.Sharpness = Math.Round(o.Quality.Sharpness, 1); v.Noise = Math.Round(o.Quality.Noise, 2); v.Brightness = Math.Round(o.Quality.Brightness, 3);
            v.Saturated = Math.Round(o.Quality.Saturated, 4); v.Contrast = Math.Round(o.Quality.Contrast, 1);
            v.Regions = o.Regions.Select(r => new VisionRegion { Kind = r.Kind, X = r.X, Y = r.Y, W = r.W, H = r.H }).ToList();
            v.MeasurementKind = o.Measurement?.Kind;
            v.Steps = o.HdSteps; v.ReadingSteps = o.ReadingSteps.Count > 0 ? o.ReadingSteps : new List<string> { "ảnh đạt — giữ nguyên để đọc số" };
            blobs.Add((o.HdJpeg, "jpg")); v.HdSha = FileStore.Sha256Hex(o.HdJpeg);
            blobs.Add((o.PrintPng, "png")); v.PrintSha = FileStore.Sha256Hex(o.PrintPng);
            if (o.RoiPng is not null) { blobs.Add((o.RoiPng, "png")); v.RoiSha = FileStore.Sha256Hex(o.RoiPng); }

            OcrPass po, pp;
            if (kind is "Hardness" or "Sample")
            {
                // cùng bộ đọc chuyên dụng trên ảnh gốc / ảnh xử lý (cùng kích thước)
                var proc = ReadSpecific(o.ReadingPng, kind, paths);
                po = ToPass("Ảnh gốc", original); pp = ToPass("Ảnh xử lý", proc);
            }
            else
            {
                // ảnh khác (thước, panme, tem LOT, phiếu, ERP…): OCR chữ tổng quát trên vùng chính của ảnh gốc / ảnh xử lý
                (po, pp) = GenericPasses(bytes, o, paths);
            }
            v.Passes.Add(ToModel(po)); v.Passes.Add(ToModel(pp));
            // OCR chữ trên vùng số đo phóng to: lấy LOT / ngày / mã thiết bị / đơn vị (chỉ thông tin phụ, không ghi đè số đo)
            if (o.RoiPng is not null && Directory.Exists(paths.Tessdata))
            {
                try
                {
                    using var roi = VisionPipeline.Decode(o.RoiPng);
                    var t = TextOcr.Read(roi, paths.Tessdata, null, singleLine: false);
                    var f = TextOcr.Parse(t.Raw);
                    v.Passes.Add(new VisionPass { Source = "OCR chữ (vùng số đo)", Engine = "Tesseract", Raw = t.Raw, Value = f.Number, Unit = f.Unit, Confidence = t.Confidence,
                                                  Chars = t.Chars.Select(c => new VisionChar { C = c.C, Conf = Math.Round(c.Conf, 3) }).ToList() });
                    v.Lot = f.Lot; v.Date = f.Date; v.DeviceCode = f.DeviceCode; v.Unit = po.Unit ?? pp.Unit ?? f.Unit;
                }
                catch (Exception e) when (e is not OutOfMemoryException) { v.Flags.Add("OCR chữ lỗi: " + e.Message); }
            }
            var verdict = MeasurementCheck.Check(po, pp, o.Quality);
            v.Flags.InsertRange(0, verdict.Flags);
            // ảnh "khác" không bắt buộc có số đo: chỉ cần người xem lại khi hai bản đọc ra số KHÁC nhau
            // CHỈ chặn khi có SỐ ĐỌC ĐƯỢC (số đó có thể bị đưa vào CQ). Không bản nào đọc được số → không có gì để tự điền
            // (ảnh sản phẩm không có cân, số độ cứng nhập tay ở tab Kết quả đo) → chỉ ghi chú, không chặn luồng cũ.
            var anyNumber = MeasurementCheck.Norm(po.Value) is not null || MeasurementCheck.Norm(pp.Value) is not null;
            v.NeedsReview = kind is "Hardness" or "Sample" ? anyNumber && verdict.NeedsReview
                : MeasurementCheck.Norm(po.Value) is { } a && MeasurementCheck.Norm(pp.Value) is { } b && a != b;
            if (!anyNumber && kind is not "Hardness") v.Flags.RemoveAll(x => x.StartsWith("Không đọc được số"));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // thiếu thư viện OpenCV / ảnh lạ: ảnh vẫn được lưu & dùng như cũ; số đo bắt buộc xác nhận tay
            v.Error = e.GetType().Name + ": " + e.Message;
            // chỉ bắt xác nhận tay khi bộ đọc cũ có ra số (không kiểm chéo được số đó)
            v.NeedsReview = kind is "Hardness" or "Sample" && MeasurementCheck.Norm(original?.Value) is not null;
            v.Flags.Add("Không xử lý được ảnh tự động (" + e.Message + ") — số đo phải nhập / xác nhận tay");
        }
        return (v, blobs);
    }

    private static MeterReading? ReadSpecific(byte[] png, string kind, AppPaths paths)
    {
        try { return kind == "Hardness" ? HardnessOcr.Read(png, paths.Tessdata) : ScaleLedReader.Read(ImageNormalizer.Load(png)); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new MeterReading("?", null, null, 0, "lỗi đọc: " + e.Message); }
    }

    private static (OcrPass, OcrPass) GenericPasses(byte[] bytes, VisionOutput o, AppPaths paths)
    {
        if (o.Measurement is null || !Directory.Exists(paths.Tessdata))
            return (new OcrPass("Ảnh gốc", "—", null, null, null, 0), new OcrPass("Ảnh xử lý", "—", null, null, null, 0));
        using var src = VisionPipeline.Decode(bytes);
        using var a = VisionPipeline.Rectify(src, o.Measurement);
        using var b = VisionPipeline.Decode(o.RoiPng!);
        TextReading ta = TextOcr.Read(a, paths.Tessdata, null, singleLine: false), tb = TextOcr.Read(b, paths.Tessdata, null, singleLine: false);
        var fa = TextOcr.Parse(ta.Raw); var fb = TextOcr.Parse(tb.Raw);
        // OCR tổng quát: luôn tối đa 0.6 → luôn chờ xác nhận nếu dùng làm số đo
        return (new OcrPass("Ảnh gốc", "Tesseract", ta.Raw, fa.Number, fa.Unit, Math.Min(0.6, ta.Confidence), ta.Chars),
                new OcrPass("Ảnh xử lý", "Tesseract", tb.Raw, fb.Number, fb.Unit, Math.Min(0.6, tb.Confidence), tb.Chars));
    }

    private static OcrPass ToPass(string source, MeterReading? r) =>
        r is null ? new OcrPass(source, "—", null, null, null, 0) : new OcrPass(source, r.Device, r.Raw, r.Value, r.Unit, r.Confidence);

    private static VisionPass ToModel(OcrPass p) => new()
    {
        Source = p.Source, Engine = p.Engine, Raw = p.Raw, Value = MeasurementCheck.Norm(p.Value), Unit = p.Unit, Confidence = Math.Round(p.Confidence, 3),
        Chars = p.Chars?.Select(c => new VisionChar { C = c.C, Conf = Math.Round(c.Conf, 3) }).ToList(),
    };

    /// <summary>Ảnh có cảnh báo OCR chưa được người xác nhận → KHÔNG được tự xác nhận hàng loạt / tự điền Mẫu 1–6.</summary>
    public static bool BlocksAuto(InspectionImage im) => im.ConfirmedValue is null && im.Vision is { NeedsReview: true };
}
