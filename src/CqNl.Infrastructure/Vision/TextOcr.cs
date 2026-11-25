using System.Globalization;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace CqNl.Infrastructure.Vision;

/// <summary>Một ký tự OCR và độ tin cậy 0–1 của chính ký tự đó.</summary>
public sealed record CharConf(string C, double Conf);

/// <summary>Kết quả OCR chữ in (Tesseract): chuỗi thô, độ tin cậy toàn kết quả (= thấp nhất trong các ký tự), độ tin cậy từng ký tự.</summary>
public sealed record TextReading(string Raw, double Confidence, List<CharConf> Chars);

/// <summary>Trường nhận dạng được trong chuỗi OCR. KHÔNG suy đoán: thiếu → null.</summary>
public sealed record OcrFields(string? Number, string? Sign, string? Unit, string? Lot, string? Date, string? DeviceCode);

/// <summary>OCR chữ in tổng quát (tem LOT, mã thiết bị, phiếu kiểm tra, ảnh ERP, máy đo không có bộ đọc riêng). Dùng Tesseract,
/// lấy độ tin cậy TỪNG KÝ TỰ. Với số đo trên LCD/LED các bộ đọc chuyên dụng (MeterReaders) được ưu tiên — OCR tổng quát chỉ là gợi ý phụ.</summary>
public static class TextOcr
{
    public const string DigitsWhitelist = "0123456789.,-";
    public const string MeasureWhitelist = "0123456789.,-HRCBVAkg ";

    /// <summary>OCR một vùng ảnh (BGR). Ảnh được phóng to nội suy cho chữ cao ≥ ~60px, nhị phân Otsu, chữ đen nền trắng.</summary>
    public static TextReading Read(Mat bgr, string tessdataDir, string? whitelist, bool singleLine)
    {
        using var gg = new Mat();
        if (bgr.Channels() == 1) bgr.CopyTo(gg); else Cv2.CvtColor(bgr, gg, ColorConversionCodes.BGR2GRAY);
        var scale = Math.Clamp(120.0 / Math.Max(1, gg.Rows), 1.0, 4.0);
        using var up = new Mat(); Cv2.Resize(gg, up, new Size(0, 0), scale, scale, InterpolationFlags.Cubic);
        using var b = new Mat(); Cv2.Threshold(up, b, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        if (Cv2.CountNonZero(b) < b.Rows * b.Cols / 2) Cv2.BitwiseNot(b, b);           // chữ đen trên nền trắng
        using var pad = new Mat(); Cv2.CopyMakeBorder(b, pad, 20, 20, 20, 20, BorderTypes.Constant, new Scalar(255));
        Cv2.ImEncode(".png", pad, out var png);
        using var engine = new Tesseract.TesseractEngine(tessdataDir, "eng", Tesseract.EngineMode.Default);
        if (whitelist is not null) engine.SetVariable("tessedit_char_whitelist", whitelist);
        using var pix = Tesseract.Pix.LoadFromMemory(png);
        using var page = engine.Process(pix, singleLine ? Tesseract.PageSegMode.SingleLine : Tesseract.PageSegMode.SparseText);
        var chars = new List<CharConf>();
        using (var it = page.GetIterator())
        {
            it.Begin();
            do
            {
                var t = it.GetText(Tesseract.PageIteratorLevel.Symbol);
                if (!string.IsNullOrEmpty(t)) chars.Add(new CharConf(t, Math.Clamp(it.GetConfidence(Tesseract.PageIteratorLevel.Symbol) / 100.0, 0, 1)));
            } while (it.Next(Tesseract.PageIteratorLevel.Symbol));
        }
        var raw = (page.GetText() ?? "").Trim();
        return new TextReading(raw, chars.Count == 0 ? 0 : chars.Min(c => c.Conf), chars);
    }

    private static readonly Regex Num = new(@"(?<sign>[-−])?\s*(?<int>\d{1,5})(?:(?<sep>[.,])(?<frac>\d{1,3}))?\s*(?<unit>HRC|HRB|HRA|HV|HB|kg|KG|Kg|g)?(?![A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex Lot = new(@"\bLOT\s*(?:NO\.?|No\.?|SỐ)?\s*[:#.]?\s*(?<v>[A-Z0-9][A-Z0-9\-/]{2,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Dt = new(@"\b(?<d>[0-3]?\d)[/.\-](?<m>[01]?\d)[/.\-](?<y>(?:19|20)?\d{2})\b|\b(?<y2>20\d{2})[/.\-](?<m2>[01]?\d)[/.\-](?<d2>[0-3]?\d)\b", RegexOptions.Compiled);
    private static readonly Regex Dev = new(@"\b(?:S/?N|SERIAL|MÃ\s*TB|MA\s*TB|ID|MODEL)\s*[:#.]?\s*(?<v>[A-Z0-9][A-Z0-9\-]{2,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Tách số đo (giữ dấu thập phân / dấu âm đúng như thấy), đơn vị, LOT, ngày, mã thiết bị. Không thấy → null (không đoán).</summary>
    public static OcrFields Parse(string? raw)
    {
        var s = (raw ?? "").Replace('\n', ' ');
        string? number = null, sign = null, unit = null;
        // ưu tiên số có đơn vị; rồi số có phần thập phân; bỏ qua số nằm trong ngày tháng / LOT
        var dateM = Dt.Match(s); var lotM = Lot.Match(s);
        bool In(Match o, int i) => o.Success && i >= o.Index && i < o.Index + o.Length;
        bool InsideOther(Match m) { var i = m.Groups["int"].Index; return In(dateM, i) || In(lotM, i); }
        var cands = Num.Matches(s).Where(m => !InsideOther(m)).ToList();
        var best = cands.FirstOrDefault(m => m.Groups["unit"].Success) ?? cands.FirstOrDefault(m => m.Groups["frac"].Success) ?? cands.FirstOrDefault();
        if (best is not null)
        {
            number = best.Groups["int"].Value + (best.Groups["frac"].Success ? "." + best.Groups["frac"].Value : "");
            if (best.Groups["sign"].Success) { sign = "-"; number = "-" + number; }
            if (best.Groups["unit"].Success) unit = best.Groups["unit"].Value is "KG" or "Kg" ? "kg" : best.Groups["unit"].Value;
        }
        string? date = null;
        if (dateM.Success)
        {
            int d, mth, y;
            if (dateM.Groups["y2"].Success) { y = int.Parse(dateM.Groups["y2"].Value); mth = int.Parse(dateM.Groups["m2"].Value); d = int.Parse(dateM.Groups["d2"].Value); }
            else { d = int.Parse(dateM.Groups["d"].Value); mth = int.Parse(dateM.Groups["m"].Value); y = int.Parse(dateM.Groups["y"].Value); if (y < 100) y += 2000; }
            if (mth is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(Math.Clamp(y, 1, 9999), mth)) date = new DateTime(y, mth, d).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }
        var dev = Dev.Match(s);
        return new OcrFields(number, sign, unit, lotM.Success ? lotM.Groups["v"].Value.ToUpperInvariant() : null, date, dev.Success ? dev.Groups["v"].Value.ToUpperInvariant() : null);
    }
}
