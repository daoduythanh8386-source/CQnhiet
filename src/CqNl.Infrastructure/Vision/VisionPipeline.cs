using System.Runtime.InteropServices;
using OpenCvSharp;

namespace CqNl.Infrastructure.Vision;

/// <summary>Chỉ số chất lượng ảnh (đo trên ảnh xám): độ nét (phương sai Laplace), nhiễu (MAD so với lọc trung vị ×1.4826),
/// độ sáng trung bình 0–1, tỉ lệ điểm cháy sáng (≥250), độ tương phản (độ lệch chuẩn).</summary>
public sealed record ImageQuality(double Sharpness, double Noise, double Brightness, double Saturated, double Contrast)
{
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (Brightness < 0.30) p.Add("ảnh tối");
        if (Brightness > 0.85) p.Add("ảnh quá sáng");
        if (Saturated > 0.05) p.Add($"lóa / cháy sáng {Saturated:P0} diện tích");
        if (Sharpness < 60) p.Add("ảnh mờ");
        if (Noise > 6) p.Add("ảnh nhiễu");
        if (Contrast < 30) p.Add("tương phản thấp");
        return p;
    }
}

/// <summary>Vùng phát hiện trên ẢNH GỐC (toạ độ điểm ảnh). Kind: LCD | LED | Màn hình | Tem nhãn | Đồng hồ kim.</summary>
public sealed record DetectedRegion(string Kind, int X, int Y, int W, int H, double Score, double Angle = 0, int[]? Quad = null);

/// <summary>Kết quả xử lý ảnh: ảnh HD màu (JPEG), ảnh tối ưu in đen trắng (PNG xám), ảnh vùng số đo phóng to (PNG),
/// ảnh dùng để ĐỌC SỐ (PNG, cùng kích thước ảnh gốc), các bước đã làm. Ảnh gốc KHÔNG bị thay đổi.</summary>
public sealed class VisionOutput
{
    public required byte[] HdJpeg { get; init; }
    public required byte[] PrintPng { get; init; }
    public byte[]? RoiPng { get; init; }
    public required byte[] ReadingPng { get; init; }
    public required ImageQuality Quality { get; init; }
    public required List<DetectedRegion> Regions { get; init; }
    public required List<string> HdSteps { get; init; }
    public required List<string> ReadingSteps { get; init; }
    public DetectedRegion? Measurement { get; init; }
}

/// <summary>
/// Xử lý ảnh công nghiệp bằng OpenCV (OpenCvSharp4). Port 1-1 tham số từ bản mẫu research/vision/vision.py đã kiểm trên ảnh thật.
/// NGUYÊN TẮC: chỉ dùng phép biến đổi tuyến tính / cục bộ (khử nhiễu NLMeans, CLAHE, gamma, unsharp mask, nắn phối cảnh, xoay, phóng to
/// nội suy bicubic). KHÔNG vẽ lại, KHÔNG tái tạo ký tự, KHÔNG dùng AI sinh ảnh. Phóng to chỉ là nội suy — không phục hồi chi tiết đã mất.
/// </summary>
public static class VisionPipeline
{
    public const string Version = "vision-1.0";
    public const int WorkMax = 2400;            // ảnh HD: cạnh dài tối đa (ảnh điện thoại 4000px → 2400px, đủ in A4)
    public const double ClaheClip = 2.0;
    public const int ClaheTile = 8;
    public const double NoiseDenoise = 3.0;
    public const double UnsharpAmount = 0.6, UnsharpSigma = 1.2;
    public const int UpscaleBelow = 1600;       // ảnh nhỏ hơn → phóng to 2x (nội suy)
    public const int RoiScale = 3;              // vùng số đo phóng to 3x (nội suy)

    public static Mat Decode(byte[] bytes)
    {
        var m = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (m.Empty()) { m.Dispose(); throw new ArgumentException("File không phải ảnh hợp lệ"); }
        return m;
    }

    public static byte[] Png(Mat m) { Cv2.ImEncode(".png", m, out var b); return b; }
    public static byte[] Jpeg(Mat m, int q = 92) { Cv2.ImEncode(".jpg", m, out var b, new ImageEncodingParam(ImwriteFlags.JpegQuality, q)); return b; }

    private static byte[] Bytes(Mat m)
    {
        using var c = m.Clone();                                   // bản sao liên tục trong bộ nhớ
        var b = new byte[c.Total() * c.ElemSize()];
        Marshal.Copy(c.Data, b, 0, b.Length);
        return b;
    }

    private static double Percentile(byte[] v, double p)
    {
        var h = new long[256]; foreach (var x in v) h[x]++;
        var target = p / 100.0 * v.Length; long acc = 0;
        for (var i = 0; i < 256; i++) { acc += h[i]; if (acc >= target) return i; }
        return 255;
    }

    // ---------------- chất lượng ----------------
    public static ImageQuality Quality(Mat bgr)
    {
        using var g = new Mat(); Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY);
        using var lap = new Mat(); Cv2.Laplacian(g, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var lsd);
        using var med = new Mat(); Cv2.MedianBlur(g, med, 3);
        using var diff = new Mat(); Cv2.Absdiff(g, med, diff);
        var noise = Percentile(Bytes(diff), 50) * 1.4826;
        Cv2.MeanStdDev(g, out var mean, out var sd);
        using var sat = new Mat(); Cv2.Threshold(g, sat, 249, 255, ThresholdTypes.Binary);
        var satFrac = Cv2.CountNonZero(sat) / (double)(g.Rows * g.Cols);
        return new ImageQuality(lsd.Val0 * lsd.Val0, noise, mean.Val0 / 255.0, satFrac, sd.Val0);
    }

    private static Mat Lut(Func<int, double> f)
    {
        var lut = new Mat(1, 256, MatType.CV_8UC1);
        for (var i = 0; i < 256; i++) lut.Set(0, i, (byte)Math.Clamp(Math.Round(f(i)), 0, 255));
        return lut;
    }

    private static Mat Shrink(Mat bgr, List<string> steps)
    {
        var mx = Math.Max(bgr.Cols, bgr.Rows);
        if (mx <= WorkMax) return bgr.Clone();
        var s = WorkMax / (double)mx; var o = new Mat();
        Cv2.Resize(bgr, o, new Size(0, 0), s, s, InterpolationFlags.Area);
        steps.Add($"thu về cạnh dài {WorkMax}px");
        return o;
    }

    // ---------------- ảnh HD màu ----------------
    /// <summary>Ảnh HD màu: khử nhiễu (khi đo thấy nhiễu) → CLAHE kênh sáng → gamma tự động (chỉ khi tối/quá sáng) → chống cháy sáng
    /// → unsharp mask → phóng to 2x nội suy nếu ảnh nhỏ.</summary>
    public static Mat EnhanceHd(Mat src, ImageQuality q, List<string> steps)
    {
        var img = Shrink(src, steps);
        if (q.Noise > NoiseDenoise)
        {
            var h = (float)Math.Min(10.0, q.Noise * 1.2); var d = new Mat();
            Cv2.FastNlMeansDenoisingColored(img, d, h, h, 7, 21); img.Dispose(); img = d;
            steps.Add($"khử nhiễu NLMeans h={h:0.0}");
        }
        using (var lab = new Mat())
        {
            Cv2.CvtColor(img, lab, ColorConversionCodes.BGR2Lab);
            var ch = Cv2.Split(lab);
            try
            {
                using var L0 = ch[0].Clone();
                using (var clahe = Cv2.CreateCLAHE(ClaheClip, new Size(ClaheTile, ClaheTile))) clahe.Apply(ch[0], ch[0]);
                steps.Add($"CLAHE clip={ClaheClip}");
                var m = Cv2.Mean(ch[0]).Val0 / 255.0; var gm = 1.0;
                if (m < 0.40) gm = Math.Max(0.55, Math.Log(0.5) / Math.Log(Math.Max(m, 1e-3)));
                else if (m > 0.72) gm = Math.Min(1.6, Math.Log(0.5) / Math.Log(Math.Min(m, 0.999)));
                if (Math.Abs(gm - 1.0) > 0.02)
                {
                    using var lut = Lut(i => Math.Pow(i / 255.0, gm) * 255.0);
                    Cv2.LUT(ch[0], lut, ch[0]); steps.Add($"gamma={gm:0.00}");
                }
                // chống cháy sáng: điểm mới vượt 250 mà ở ảnh gốc < 235 → giữ ở 250
                using var hi = new Mat(); Cv2.Threshold(ch[0], hi, 250, 255, ThresholdTypes.Binary);
                using var lo = new Mat(); Cv2.Threshold(L0, lo, 234, 255, ThresholdTypes.BinaryInv);
                using var over = new Mat(); Cv2.BitwiseAnd(hi, lo, over);
                if (Cv2.CountNonZero(over) > 0) ch[0].SetTo(new Scalar(250), over);
                Cv2.Merge(ch, lab);
            }
            finally { foreach (var c in ch) c.Dispose(); }
            Cv2.CvtColor(lab, img, ColorConversionCodes.Lab2BGR);
        }
        using (var blur = new Mat())
        {
            Cv2.GaussianBlur(img, blur, new Size(0, 0), UnsharpSigma);
            Cv2.AddWeighted(img, 1 + UnsharpAmount, blur, -UnsharpAmount, 0, img);
            steps.Add($"unsharp mask {UnsharpAmount}/{UnsharpSigma}");
        }
        if (Math.Max(img.Cols, img.Rows) < UpscaleBelow)
        {
            var up = new Mat(); Cv2.Resize(img, up, new Size(0, 0), 2, 2, InterpolationFlags.Cubic); img.Dispose(); img = up;
            steps.Add("phóng to 2x (nội suy bicubic — không thêm chi tiết mới)");
        }
        return img;
    }

    /// <summary>Ảnh tối ưu in đen trắng: xám → giãn mức 1–99.5% → CLAHE → làm nét nhẹ. Giữ thang xám (không nhị phân hoá → không mất nét số).</summary>
    public static Mat PrintBw(Mat bgr)
    {
        var g = new Mat(); Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY);
        var b = Bytes(g); double lo = Percentile(b, 1), hi = Percentile(b, 99.5);
        if (hi - lo > 10) { using var lut = Lut(i => (i - lo) * 255.0 / (hi - lo)); Cv2.LUT(g, lut, g); }
        using (var clahe = Cv2.CreateCLAHE(2.5, new Size(8, 8))) clahe.Apply(g, g);
        using var bl = new Mat(); Cv2.GaussianBlur(g, bl, new Size(0, 0), 1.0);
        Cv2.AddWeighted(g, 1.5, bl, -0.5, 0, g);
        return g;
    }

    // ---------------- dò vùng ----------------
    public static List<DetectedRegion> DetectRegions(Mat bgr)
    {
        int H = bgr.Rows, W = bgr.Cols; double A = (double)H * W;
        var outL = new List<DetectedRegion>();
        using var hsv = new Mat(); Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        // 1. LCD nền vàng-xanh
        using (var m = new Mat())
        {
            Cv2.InRange(hsv, new Scalar(25, 60, 120), new Scalar(50, 255, 255), m);
            using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 15));
            Cv2.MorphologyEx(m, m, MorphTypes.Close, k);
            Cv2.FindContours(m, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            foreach (var c in cs)
            {
                var r = Cv2.BoundingRect(c); var ar = r.Width / (double)Math.Max(r.Height, 1); var ca = Cv2.ContourArea(c);
                if (ca > 0.002 * A && ar >= 1.8 && ar <= 8)
                {
                    var rr = Cv2.MinAreaRect(c); var ang = rr.Angle; if (ang > 45) ang -= 90; if (ang < -45) ang += 90;
                    outL.Add(new DetectedRegion("LCD", r.X, r.Y, r.Width, r.Height, 1 + ca / A, ang));
                }
            }
        }
        // 2. LED sáng đỏ / xanh lá
        using (var red1 = new Mat()) using (var red2 = new Mat()) using (var red = new Mat()) using (var grn = new Mat())
        {
            Cv2.InRange(hsv, new Scalar(0, 90, 170), new Scalar(10, 255, 255), red1);
            Cv2.InRange(hsv, new Scalar(170, 90, 170), new Scalar(180, 255, 255), red2);
            Cv2.BitwiseOr(red1, red2, red);
            Cv2.InRange(hsv, new Scalar(50, 120, 200), new Scalar(90, 255, 255), grn);
            foreach (var mm in new[] { red, grn })
            {
                var k = Math.Max(3, W / 60);
                using var se = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k / 2 + 1));
                using var m2 = new Mat(); Cv2.MorphologyEx(mm, m2, MorphTypes.Close, se);
                Cv2.FindContours(m2, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                foreach (var c in cs)
                {
                    var r = Cv2.BoundingRect(c); var ar = r.Width / (double)Math.Max(r.Height, 1);
                    if (r.Width * (double)r.Height <= 0.0008 * A || ar < 1.5 || ar > 10) continue;
                    using var sub = new Mat(mm, r); var lit = Cv2.CountNonZero(sub) / (double)(r.Width * r.Height);
                    if (lit >= 0.08 && lit <= 0.7) outL.Add(new DetectedRegion("LED", r.X, r.Y, r.Width, r.Height, 0.5 + r.Width * (double)r.Height / A));
                }
            }
        }
        // 3. Màn hình / tem nhãn sáng hình chữ nhật: biên Canny → đa giác 4 đỉnh lồi
        using (var g = new Mat()) using (var e = new Mat())
        {
            Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY); Cv2.GaussianBlur(g, g, new Size(5, 5), 0);
            Cv2.Canny(g, e, 40, 120);
            using (var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3))) Cv2.Dilate(e, e, k3);
            Cv2.FindContours(e, out var cs, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            foreach (var c in cs)
            {
                var ca = Cv2.ContourArea(c);
                if (ca < 0.01 * A || ca > 0.7 * A) continue;
                var p = Cv2.ApproxPolyDP(c, 0.02 * Cv2.ArcLength(c, true), true);
                if (p.Length != 4 || !Cv2.IsContourConvex(p)) continue;
                var r = Cv2.BoundingRect(p); var ar = r.Width / (double)Math.Max(r.Height, 1);
                if (ar < 0.9 || ar > 6) continue;
                var inner = new Rect(r.X + r.Width / 8, r.Y + r.Height / 8, Math.Max(1, r.Width - r.Width / 4), Math.Max(1, r.Height - r.Height / 4));
                using var sub = new Mat(hsv, inner); var mean = Cv2.Mean(sub);
                if (mean.Val2 < 110) continue;
                var kind = mean.Val1 < 35 && mean.Val2 > 170 ? "Tem nhãn" : "Màn hình";
                var quad = p.SelectMany(pt => new[] { pt.X, pt.Y }).ToArray();
                outL.Add(new DetectedRegion(kind, r.X, r.Y, r.Width, r.Height, 0.3 + ca / A, 0, quad));
            }
            // 4. Đồng hồ kim (vòng tròn lớn)
            var circles = Cv2.HoughCircles(g, HoughModes.Gradient, 1.5, Math.Min(H, W) / 3.0, 120, 80, (int)(0.12 * Math.Min(H, W)), (int)(0.48 * Math.Min(H, W)));
            foreach (var ci in circles.Take(2))
                outL.Add(new DetectedRegion("Đồng hồ kim", (int)(ci.Center.X - ci.Radius), (int)(ci.Center.Y - ci.Radius), (int)(2 * ci.Radius), (int)(2 * ci.Radius), 0.2));
        }
        var keep = new List<DetectedRegion>();
        foreach (var d in outL.OrderByDescending(d => d.Score))
            if (keep.All(k => Overlap(d, k) < 0.6)) keep.Add(d);
        return keep.Take(6).ToList();
    }

    private static double Overlap(DetectedRegion a, DetectedRegion b)
    {
        var ix = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X)); var iy = Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
        return ix * (double)iy / Math.Max(1, Math.Min(a.W * (double)a.H, b.W * (double)b.H));
    }

    private static Point2f[] OrderQuad(int[] q)
    {
        var p = Enumerable.Range(0, 4).Select(i => new Point2f(q[2 * i], q[2 * i + 1])).ToArray();
        var s = p.Select(v => v.X + v.Y).ToArray(); var d = p.Select(v => v.Y - v.X).ToArray();
        int Arg(double[] a, bool max) { var k = 0; for (var i = 1; i < a.Length; i++) if (max ? a[i] > a[k] : a[i] < a[k]) k = i; return k; }
        var sd = s.Select(x => (double)x).ToArray(); var dd = d.Select(x => (double)x).ToArray();
        return new[] { p[Arg(sd, false)], p[Arg(dd, false)], p[Arg(sd, true)], p[Arg(dd, true)] };
    }

    /// <summary>Cắt + nắn thẳng vùng (phối cảnh nếu có 4 góc; xoay theo góc vùng LCD) — chỉ biến đổi hình học.</summary>
    public static Mat Rectify(Mat bgr, DetectedRegion r, List<string>? steps = null)
    {
        if (r.Quad is { Length: 8 } q)
        {
            var src = OrderQuad(q);
            static double D(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            var w = (int)Math.Max(D(src[0], src[1]), D(src[3], src[2])); var h = (int)Math.Max(D(src[0], src[3]), D(src[1], src[2]));
            if (w > 8 && h > 8)
            {
                using var M = Cv2.GetPerspectiveTransform(src, new[] { new Point2f(0, 0), new Point2f(w, 0), new Point2f(w, h), new Point2f(0, h) });
                var o = new Mat(); Cv2.WarpPerspective(bgr, o, M, new Size(w, h), InterpolationFlags.Cubic, BorderTypes.Replicate);
                steps?.Add("nắn phối cảnh 4 góc");
                return o;
            }
        }
        int px = (int)(r.W * 0.04), py = (int)(r.H * 0.04);
        var rect = new Rect(Math.Max(0, r.X - px), Math.Max(0, r.Y - py), 0, 0);
        rect.Width = Math.Min(bgr.Cols, r.X + r.W + px) - rect.X; rect.Height = Math.Min(bgr.Rows, r.Y + r.H + py) - rect.Y;
        var crop = new Mat(bgr, rect).Clone();
        if (Math.Abs(r.Angle) >= 0.5 && Math.Abs(r.Angle) <= 20)
        {
            using var M = Cv2.GetRotationMatrix2D(new Point2f(crop.Cols / 2f, crop.Rows / 2f), r.Angle, 1.0);
            var o = new Mat(); Cv2.WarpAffine(crop, o, M, crop.Size(), InterpolationFlags.Cubic, BorderTypes.Replicate);
            crop.Dispose(); steps?.Add($"xoay thẳng {r.Angle:0.0}°");
            return o;
        }
        steps?.Add("cắt vùng");
        return crop;
    }

    /// <summary>Vùng số đo chính: ưu tiên LCD → LED → màn hình → tem nhãn → đồng hồ kim.</summary>
    public static DetectedRegion? MainRegion(IEnumerable<DetectedRegion> regs) =>
        regs.OrderBy(r => r.Kind switch { "LCD" => 0, "LED" => 1, "Màn hình" => 2, "Tem nhãn" => 3, _ => 4 }).ThenByDescending(r => r.Score).FirstOrDefault();

    // ---------------- bản dùng để đọc số ----------------
    /// <summary>Bản để ĐỌC SỐ: chỉ sửa đúng lỗi đo được, GIỮ nguyên kích thước (bộ đọc chuyên dụng giả định tỉ lệ ảnh gốc); ảnh tốt → giữ nguyên.
    /// Nghiêng → xoay thẳng; nhiễu → khử nhiễu; tối → gamma; tương phản thấp → CLAHE.</summary>
    public static Mat ForReading(Mat bgr, IReadOnlyList<DetectedRegion> regs, List<string> steps)
    {
        var img = bgr.Clone();
        var main = regs.FirstOrDefault(r => r.Kind is "LCD" or "Màn hình" or "LED" or "Tem nhãn");
        double ang = 0;
        if (main is not null)
        {
            if (main.Kind == "LCD") ang = main.Angle;
            else if (main.Quad is { Length: 8 } q) { var p = OrderQuad(q); ang = Math.Atan2(p[1].Y - p[0].Y, p[1].X - p[0].X) * 180 / Math.PI; }
        }
        if (Math.Abs(ang) >= 0.7 && Math.Abs(ang) <= 25)
        {
            using var M = Cv2.GetRotationMatrix2D(new Point2f(img.Cols / 2f, img.Rows / 2f), ang, 1.0);
            var o = new Mat(); Cv2.WarpAffine(img, o, M, img.Size(), InterpolationFlags.Cubic, BorderTypes.Replicate); img.Dispose(); img = o;
            steps.Add($"xoay thẳng {ang:0.0}°");
        }
        var q2 = Quality(img);
        if (q2.Noise > NoiseDenoise)
        {
            // ảnh điện thoại > 6 MP (bản đọc số giữ nguyên kích thước): cửa sổ tìm 11 thay 21 → nhanh ~3 lần (12 MP nhiễu: 11 s → 4 s)
            var h = (float)Math.Min(10.0, q2.Noise * 1.2); var d = new Mat(); var sw = (long)img.Rows * img.Cols > 6_000_000 ? 11 : 21;
            Cv2.FastNlMeansDenoisingColored(img, d, h, h, 7, sw); img.Dispose(); img = d; steps.Add($"khử nhiễu h={h:0.0}");
        }
        if (q2.Brightness < 0.30)
        {
            var gm = Math.Max(0.55, Math.Log(0.45) / Math.Log(Math.Max(q2.Brightness, 1e-3)));
            using var lut = Lut(i => Math.Pow(i / 255.0, gm) * 255.0);
            Cv2.LUT(img, lut, img); steps.Add($"gamma={gm:0.00}");
        }
        else if (q2.Contrast < 30)
        {
            using var lab = new Mat(); Cv2.CvtColor(img, lab, ColorConversionCodes.BGR2Lab);
            var ch = Cv2.Split(lab);
            try { using var cl = Cv2.CreateCLAHE(ClaheClip, new Size(8, 8)); cl.Apply(ch[0], ch[0]); Cv2.Merge(ch, lab); }
            finally { foreach (var c in ch) c.Dispose(); }
            Cv2.CvtColor(lab, img, ColorConversionCodes.Lab2BGR); steps.Add("CLAHE");
        }
        return img;
    }

    /// <summary>Chạy trọn pipeline trên ảnh gốc (bytes không bị thay đổi).</summary>
    public static VisionOutput Process(byte[] original)
    {
        using var src = Decode(original);
        var q = Quality(src);
        var regs = DetectRegions(src);
        var hdSteps = new List<string>();
        using var hd = EnhanceHd(src, q, hdSteps);
        using var bw = PrintBw(hd);
        var main = MainRegion(regs);
        byte[]? roi = null;
        if (main is not null)
        {
            // vùng số đo lấy từ ảnh HD (cùng hệ toạ độ sau khi quy đổi tỉ lệ), phóng to 3x nội suy
            var s = hd.Cols / (double)src.Cols;
            var r2 = main with { X = (int)(main.X * s), Y = (int)(main.Y * s), W = (int)(main.W * s), H = (int)(main.H * s), Quad = main.Quad?.Select(v => (int)(v * s)).ToArray() };
            using var rc = Rectify(hd, r2);
            using var up = new Mat();
            var f = Math.Min(RoiScale, Math.Max(1.0, 1600.0 / Math.Max(1, rc.Cols)));
            Cv2.Resize(rc, up, new Size(0, 0), f, f, InterpolationFlags.Cubic);
            roi = Png(up);
        }
        var rs = new List<string>();
        using var reading = ForReading(src, regs, rs);
        return new VisionOutput
        {
            HdJpeg = Jpeg(hd), PrintPng = Png(bw), RoiPng = roi, ReadingPng = Png(reading), Quality = q, Regions = regs,
            HdSteps = hdSteps, ReadingSteps = rs, Measurement = main,
        };
    }
}
