using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CqNl.Infrastructure.Imaging;

/// <summary>Kết quả chuẩn hóa: ảnh mới (JPEG) + khung cắt trên ảnh gốc + cảnh báo. Ảnh gốc KHÔNG đổi.</summary>
public sealed record NormalizeResult(byte[] Jpeg, Rectangle Crop, bool NeedsManualCrop, IReadOnlyList<string> Warnings, Rectangle? Lcd = null, double Scale = 1);

/// <summary>
/// Chuẩn hóa ảnh cho CQ theo ảnh mẫu người dùng (port từ research/image_normalize.py, cùng preset JSON):
/// - "product": cắt vùng sản phẩm (bao các vật kim loại sáng không chạm mép, bỏ đầu cân LED đỏ) + làm sáng/làm nét.
/// - "mitutoyo": cắt quanh mặt máy theo vị trí LCD + làm sáng.
/// Toán tử ảnh (HSV, Otsu, Gauss, hình thái, thành phần liên thông) cài đặt giống OpenCV để kết quả khớp bản Python đã kiểm.
/// </summary>
public static class ImageNormalizer
{
    // ---------- ảnh RGB trong bộ nhớ ----------
    public sealed class Rgb
    {
        public readonly int W, H; public readonly byte[] R, G, B;
        public Rgb(int w, int h) { W = w; H = h; R = new byte[w * h]; G = new byte[w * h]; B = new byte[w * h]; }
    }

    public static Rgb Load(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var img = Image.FromStream(ms, false, false);
        ApplyExifOrientation(img);                                   // OpenCV imread xoay theo EXIF → làm giống
        using var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, img.Width, img.Height);
        var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var buf = new byte[d.Stride * bmp.Height]; Marshal.Copy(d.Scan0, buf, 0, buf.Length); bmp.UnlockBits(d);
        var o = new Rgb(bmp.Width, bmp.Height);
        for (var y = 0; y < o.H; y++) for (var x = 0; x < o.W; x++) { var p = y * d.Stride + x * 3; var i = y * o.W + x; o.B[i] = buf[p]; o.G[i] = buf[p + 1]; o.R[i] = buf[p + 2]; }
        return o;
    }

    private static void ApplyExifOrientation(Image img)
    {
        if (!img.PropertyIdList.Contains(0x0112)) return;
        var v = img.GetPropertyItem(0x0112)!.Value![0];
        var rf = v switch { 2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone, 4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX, 6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX, 8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone };
        if (rf != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(rf);
    }

    public static byte[] SaveJpeg(Rgb im, int quality)
    {
        using var bmp = new Bitmap(im.W, im.H, PixelFormat.Format24bppRgb);
        var d = bmp.LockBits(new Rectangle(0, 0, im.W, im.H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var buf = new byte[d.Stride * im.H];
        for (var y = 0; y < im.H; y++) for (var x = 0; x < im.W; x++) { var p = y * d.Stride + x * 3; var i = y * im.W + x; buf[p] = im.B[i]; buf[p + 1] = im.G[i]; buf[p + 2] = im.R[i]; }
        Marshal.Copy(buf, 0, d.Scan0, buf.Length); bmp.UnlockBits(d);
        var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        using var ms = new MemoryStream(); bmp.Save(ms, enc, ps); return ms.ToArray();
    }

    public static Rgb Crop(Rgb s, Rectangle r)
    {
        var o = new Rgb(r.Width, r.Height);
        for (var y = 0; y < r.Height; y++) for (var x = 0; x < r.Width; x++) { var si = (r.Y + y) * s.W + r.X + x; var di = y * r.Width + x; o.R[di] = s.R[si]; o.G[di] = s.G[si]; o.B[di] = s.B[si]; }
        return o;
    }

    public static Rgb ResizeArea(Rgb s, int w, int h)
    {   // thu nhỏ trung bình vùng (tương đương INTER_AREA khi thu nhỏ)
        var o = new Rgb(w, h); double fx = s.W / (double)w, fy = s.H / (double)h;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            int x0 = (int)(x * fx), x1 = Math.Max(x0 + 1, (int)((x + 1) * fx)), y0 = (int)(y * fy), y1 = Math.Max(y0 + 1, (int)((y + 1) * fy));
            double r = 0, g = 0, b = 0; var n = 0;
            for (var yy = y0; yy < y1 && yy < s.H; yy++) for (var xx = x0; xx < x1 && xx < s.W; xx++) { var i = yy * s.W + xx; r += s.R[i]; g += s.G[i]; b += s.B[i]; n++; }
            var di = y * w + x; o.R[di] = (byte)Math.Round(r / n); o.G[di] = (byte)Math.Round(g / n); o.B[di] = (byte)Math.Round(b / n);
        }
        return o;
    }

    // ---------- HSV kiểu OpenCV (H 0..179, S,V 0..255) ----------
    public static void Hsv(Rgb im, out byte[] H, out byte[] S, out byte[] V)
    {
        var n = im.W * im.H; H = new byte[n]; S = new byte[n]; V = new byte[n];
        for (var i = 0; i < n; i++)
        {
            int r = im.R[i], g = im.G[i], b = im.B[i]; int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            V[i] = (byte)mx; var diff = mx - mn;
            S[i] = mx == 0 ? (byte)0 : (byte)Math.Round(255.0 * diff / mx);
            double h = 0;
            if (diff > 0) h = mx == r ? 60.0 * (g - b) / diff : mx == g ? 120 + 60.0 * (b - r) / diff : 240 + 60.0 * (r - g) / diff;
            if (h < 0) h += 360;
            H[i] = (byte)Math.Min(179, Math.Round(h / 2));
        }
    }

    private static float[] GaussKernel(double sigma, int ksize = 0)
    {
        if (ksize <= 0) ksize = Math.Max(3, (int)Math.Round(sigma * 6 + 1) | 1);   // OpenCV: ksize = round(sigma*6+1)|1 cho ảnh 8 bit
        if (sigma <= 0) sigma = 0.3 * ((ksize - 1) * 0.5 - 1) + 0.8;
        var k = new float[ksize]; var c = ksize / 2; double s = 0;
        for (var i = 0; i < ksize; i++) { k[i] = (float)Math.Exp(-(i - c) * (i - c) / (2 * sigma * sigma)); s += k[i]; }
        for (var i = 0; i < ksize; i++) k[i] /= (float)s;
        return k;
    }

    private static int Reflect101(int i, int n) { if (n == 1) return 0; while (i < 0 || i >= n) i = i < 0 ? -i : 2 * n - 2 - i; return i; }

    public static float[] Blur(float[] src, int w, int h, float[] k)
    {
        var c = k.Length / 2; var tmp = new float[w * h]; var o = new float[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { float s = 0; for (var j = 0; j < k.Length; j++) s += k[j] * src[y * w + Reflect101(x + j - c, w)]; tmp[y * w + x] = s; }
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { float s = 0; for (var j = 0; j < k.Length; j++) s += k[j] * tmp[Reflect101(y + j - c, h) * w + x]; o[y * w + x] = s; }
        return o;
    }

    public static int Otsu(byte[] v)
    {
        var hist = new double[256]; foreach (var b in v) hist[b]++;
        double total = v.Length, sum = 0; for (var i = 0; i < 256; i++) sum += i * hist[i];
        double sumB = 0, wB = 0, best = -1; var t = 0;
        for (var i = 0; i < 256; i++)
        {
            wB += hist[i]; if (wB == 0) continue; var wF = total - wB; if (wF == 0) break;
            sumB += i * hist[i]; var mB = sumB / wB; var mF = (sum - sumB) / wF; var between = wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; t = i; }
        }
        return t;
    }

    public static bool[] Morph(bool[] m, int w, int h, int kw, int kh, bool dilate)
    {   // phần tử cấu trúc chữ nhật, tách 2 chiều (giống OpenCV với kernel toàn 1)
        var t = new bool[w * h]; var o = new bool[w * h]; int ax = kw / 2, ay = kh / 2;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var acc = !dilate;
            for (var j = -ax; j < kw - ax; j++) { var xx = x + j; if (xx < 0 || xx >= w) continue; var v = m[y * w + xx]; if (dilate ? v : !v) { acc = dilate; break; } }
            t[y * w + x] = acc;
        }
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var acc = !dilate;
            for (var j = -ay; j < kh - ay; j++) { var yy = y + j; if (yy < 0 || yy >= h) continue; var v = t[yy * w + x]; if (dilate ? v : !v) { acc = dilate; break; } }
            o[y * w + x] = acc;
        }
        return o;
    }

    public sealed record Comp(int X, int Y, int W, int H, int Area);

    public static List<Comp> Components(bool[] m, int w, int h)
    {   // 8-liên thông, BFS
        var lab = new int[w * h]; var res = new List<Comp>(); var q = new Queue<int>(); var id = 0;
        for (var s = 0; s < m.Length; s++)
        {
            if (!m[s] || lab[s] != 0) continue;
            id++; lab[s] = id; q.Enqueue(s); int x0 = w, y0 = h, x1 = 0, y1 = 0, a = 0;
            while (q.Count > 0)
            {
                var p = q.Dequeue(); int px = p % w, py = p / w; a++;
                x0 = Math.Min(x0, px); y0 = Math.Min(y0, py); x1 = Math.Max(x1, px); y1 = Math.Max(y1, py);
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy; if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var ni = ny * w + nx; if (m[ni] && lab[ni] == 0) { lab[ni] = id; q.Enqueue(ni); }
                }
            }
            res.Add(new Comp(x0, y0, x1 - x0 + 1, y1 - y0 + 1, a));
        }
        return res;
    }

    // ---------- preset ----------
    public static JsonElement LoadPreset(string path) { using var d = JsonDocument.Parse(File.ReadAllText(path)); return d.RootElement.Clone(); }
    private static double D(JsonElement e, string name, double dflt) => e.TryGetProperty(name, out var v) ? v.GetDouble() : dflt;

    // ---------- tìm vùng ----------
    /// <summary>Ảnh xuất hàng: bao các vật kim loại sáng KHÔNG chạm mép (loại sàn, kệ) và không chứa LED đỏ (đầu cân), rồi nới lề.</summary>
    public static (Rectangle? Box, int Count) ProductBox(Rgb im, JsonElement det)
    {
        int W = im.W, H = im.H, n = W * H;
        Hsv(im, out var Hh, out var S, out var V);
        var vb = Blur(V.Select(b => (float)b).ToArray(), W, H, GaussKernel(0, 5));
        var vbb = vb.Select(f => (byte)Math.Clamp(Math.Round(f), 0, 255)).ToArray();
        var t = Otsu(vbb);
        var maxS = D(det, "max_saturation", 90);
        var mask = new bool[n]; for (var i = 0; i < n; i++) mask[i] = vbb[i] > t && S[i] < maxS;
        mask = Morph(Morph(mask, W, H, 5, 5, false), W, H, 5, 5, true);    // mở (open) 5x5
        var red = new bool[n]; for (var i = 0; i < n; i++) red[i] = (Hh[i] < 10 || Hh[i] > 170) && S[i] > 120 && V[i] > 120;
        var minA = D(det, "min_component_area", 0.0008) * W * H;
        var boxes = new List<Comp>();
        foreach (var c in Components(mask, W, H))
        {
            if (c.Area < minA) continue;
            if (c.X <= 2 || c.Y <= 2 || c.X + c.W >= W - 2 || c.Y + c.H >= H - 2) continue;
            var rc = 0; for (var y = c.Y; y < c.Y + c.H; y++) for (var x = c.X; x < c.X + c.W; x++) if (red[y * W + x]) rc++;
            if (rc / (double)(c.W * c.H) > 0.02) continue;
            boxes.Add(c);
        }
        if (boxes.Count < (int)D(det, "min_components", 3)) return (null, boxes.Count);
        int x0 = boxes.Min(b => b.X), y0 = boxes.Min(b => b.Y), x1 = boxes.Max(b => b.X + b.W), y1 = boxes.Max(b => b.Y + b.H);
        var mg = det.GetProperty("margin"); double bw = x1 - x0, bh = y1 - y0;
        int l = Math.Max(0, (int)(x0 - mg.GetProperty("left").GetDouble() * bw)), tp = Math.Max(0, (int)(y0 - mg.GetProperty("top").GetDouble() * bh));
        int r = Math.Min(W, (int)(x1 + mg.GetProperty("right").GetDouble() * bw)), bt = Math.Min(H, (int)(y1 + mg.GetProperty("bottom").GetDouble() * bh));
        return (Rectangle.FromLTRB(l, tp, r, bt), boxes.Count);
    }

    /// <summary>LCD Mitutoyo: vùng vàng-xanh (H 25–50, S≥60, V≥120), đóng 15x15, bao chữ nhật dẹt lớn nhất.</summary>
    public static Rectangle? MitutoyoLcd(Rgb im)
    {
        int W = im.W, H = im.H; Hsv(im, out var Hh, out var S, out var V);
        var m = new bool[W * H]; for (var i = 0; i < m.Length; i++) m[i] = Hh[i] >= 25 && Hh[i] <= 50 && S[i] >= 60 && V[i] >= 120;
        m = Morph(Morph(m, W, H, 15, 15, true), W, H, 15, 15, false);       // đóng (close)
        Rectangle? best = null;
        foreach (var c in Components(m, W, H))
            if (c.W > 2.5 * c.H && c.W < 8 * c.H && c.W * c.H > 0.002 * W * H && (best is null || c.W * c.H > best.Value.Width * best.Value.Height))
                best = new Rectangle(c.X, c.Y, c.W, c.H);
        return best;
    }

    // ---------- làm sáng / màu / nét ----------
    public static Rgb Enhance(Rgb im, JsonElement p)
    {
        int n = im.W * im.H; var f = new float[3][]; f[0] = new float[n]; f[1] = new float[n]; f[2] = new float[n];
        for (var i = 0; i < n; i++) { f[0][i] = im.B[i]; f[1][i] = im.G[i]; f[2][i] = im.R[i]; }
        // 0) cân màu theo tỉ lệ B:G:R của ảnh mẫu
        var wb = D(p, "white_balance_strength", 0);
        if (wb > 0)
        {
            var means = f.Select(c => c.Average()).ToArray(); var gm = means.Average();
            var tr = p.TryGetProperty("target_bgr_ratio", out var t) ? t.EnumerateArray().Select(x => x.GetDouble()).ToArray() : new[] { 1.0, 1, 1 };
            var trm = tr.Average();
            for (var c = 0; c < 3; c++) { var k = 1 + wb * (tr[c] / trm * gm / means[c] - 1); for (var i = 0; i < n; i++) f[c][i] = (float)Math.Clamp(f[c][i] * k, 0, 255); }
        }
        // 1) kéo giãn theo phân vị độ sáng (cùng hệ số 3 kênh)
        var lum = new float[n]; for (var i = 0; i < n; i++) lum[i] = 0.114f * f[0][i] + 0.587f * f[1][i] + 0.299f * f[2][i];
        var lumOrig = new float[n]; for (var i = 0; i < n; i++) lumOrig[i] = 0.114f * im.B[i] + 0.587f * im.G[i] + 0.299f * im.R[i];
        var sorted = (float[])lumOrig.Clone(); Array.Sort(sorted);
        float Pct(double q) { var pos = q / 100 * (sorted.Length - 1); var lo = (int)Math.Floor(pos); var hi = Math.Min(sorted.Length - 1, lo + 1); return (float)(sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo)); }
        float lo_ = Pct(D(p, "stretch_low_pct", 1)), hi_ = Pct(D(p, "stretch_high_pct", 99));
        var white = (float)D(p, "stretch_white", 255); var sc = white / Math.Max(1f, hi_ - lo_);
        for (var c = 0; c < 3; c++) for (var i = 0; i < n; i++) f[c][i] = Math.Clamp((f[c][i] - lo_) * sc, 0, 255);
        // 2) gamma tự chọn theo độ sáng trung bình mục tiêu
        double mean = Math.Max(1, f.Average(c => c.Average()));
        var gamma = Math.Clamp(Math.Log(D(p, "target_mean", 128) / 255.0) / Math.Log(mean / 255.0), D(p, "gamma_min", 0.5), D(p, "gamma_max", 1.2));
        var lut = new byte[256]; for (var i = 0; i < 256; i++) lut[i] = (byte)Math.Clamp(Math.Round(255 * Math.Pow(i / 255.0, gamma)), 0, 255);
        var o = new Rgb(im.W, im.H);
        for (var i = 0; i < n; i++) { o.B[i] = lut[(int)f[0][i]]; o.G[i] = lut[(int)f[1][i]]; o.R[i] = lut[(int)f[2][i]]; }
        // 3) bão hòa (thang S của HSV, giữ H và V)
        var sat = D(p, "saturation", 1);
        if (Math.Abs(sat - 1) > 1e-6)
            for (var i = 0; i < n; i++)
            {
                int r = o.R[i], g = o.G[i], b = o.B[i]; int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx == 0 || mx == mn) continue;
                var s0 = (mx - mn) / (double)mx; var s1 = Math.Min(1, s0 * sat); var fac = s1 / s0;
                o.R[i] = (byte)Math.Clamp(Math.Round(mx - (mx - r) * fac), 0, 255); o.G[i] = (byte)Math.Clamp(Math.Round(mx - (mx - g) * fac), 0, 255); o.B[i] = (byte)Math.Clamp(Math.Round(mx - (mx - b) * fac), 0, 255);
            }
        // 4) làm nét (unsharp mask)
        var amt = D(p, "sharpen_amount", 0);
        if (amt > 0)
        {
            var k = GaussKernel(D(p, "sharpen_sigma", 1.5));
            foreach (var ch in new[] { o.R, o.G, o.B })
            {
                var src = ch.Select(b => (float)b).ToArray(); var bl = Blur(src, o.W, o.H, k);
                for (var i = 0; i < n; i++) ch[i] = (byte)Math.Clamp(Math.Round(src[i] * (1 + amt) - bl[i] * amt), 0, 255);
            }
        }
        return o;
    }

    // ---------- quy trình đầy đủ ----------
    public static NormalizeResult NormalizeProduct(byte[] original, JsonElement preset)
    {
        var im = Load(original); var warn = new List<string>(); var manual = false;
        var det = preset.GetProperty("detect");
        var (box, cnt) = ProductBox(im, det);
        Rectangle crop;
        if (box is null) { manual = true; warn.Add($"Không nhận ra vùng sản phẩm ({cnt} vật) → giữ nguyên khung, cần cắt tay"); crop = new Rectangle(0, 0, im.W, im.H); }
        else
        {
            crop = box.Value; var frac = crop.Width * (double)crop.Height / (im.W * im.H);
            if (frac < D(det, "area_min", 0.15) || frac > D(det, "area_max", 0.95)) { manual = true; warn.Add($"Vùng sản phẩm chiếm {frac:P0} ảnh (bất thường) → kiểm tra/cắt tay"); }
        }
        return Finish(im, crop, preset, manual, warn, null, 1);
    }

    public static NormalizeResult NormalizeMitutoyo(byte[] original, JsonElement preset)
    {
        var im = Load(original); var warn = new List<string>(); var manual = false;
        var lcd = MitutoyoLcd(im);
        if (lcd is null) { warn.Add("Không tìm thấy màn hình LCD Mitutoyo → giữ nguyên khung, cần cắt tay"); return Finish(im, new Rectangle(0, 0, im.W, im.H), preset, true, warn, null, 1); }
        var c = preset.GetProperty("crop"); var L = lcd.Value;
        double cx = L.X + L.Width / 2.0, cy = L.Y + L.Height / 2.0, w = L.Width;
        double kL = c.GetProperty("left").GetDouble(), kR = c.GetProperty("right").GetDouble(), kT = c.GetProperty("top").GetDouble(), kB = c.GetProperty("bottom").GetDouble();
        var k = Math.Min(1.0, Math.Min(Math.Min(cx / (kL * w), (im.W - cx) / (kR * w)), Math.Min(cy / (kT * w), (im.H - cy) / (kB * w))));
        var crop = Rectangle.FromLTRB(Math.Max(0, (int)Math.Round(cx - k * kL * w)), Math.Max(0, (int)Math.Round(cy - k * kT * w)),
            Math.Min(im.W, (int)Math.Round(cx + k * kR * w)), Math.Min(im.H, (int)Math.Round(cy + k * kB * w)));
        if (k < D(preset, "min_scale", 0.75)) { manual = true; warn.Add($"Ảnh chụp quá sát (chỉ lấy được {k:P0} khung chuẩn) → cần kiểm tra/cắt tay"); }
        var asp = L.Width / (double)L.Height;
        if (asp < D(preset, "lcd_aspect_min", 2.8) || asp > D(preset, "lcd_aspect_max", 4.8)) { manual = true; warn.Add($"Tỉ lệ LCD bất thường ({asp:0.00}) → cần cắt tay"); }
        return Finish(im, crop, preset, manual, warn, lcd, k);
    }

    private static NormalizeResult Finish(Rgb im, Rectangle crop, JsonElement preset, bool manual, List<string> warn, Rectangle? lcd, double k)
    {
        var o = Enhance(Crop(im, crop), preset.GetProperty("enhance"));
        var maxW = (int)D(preset, "max_width", 1600);
        if (o.W > maxW) o = ResizeArea(o, maxW, (int)Math.Round(o.H * maxW / (double)o.W));
        return new NormalizeResult(SaveJpeg(o, (int)D(preset, "jpeg_quality", 92)), crop, manual, warn, lcd, k);
    }

    /// <summary>Cắt tay do QC chọn (khung trên ảnh gốc) + cùng bước làm sáng. "Bỏ cắt" = gọi lại với cả ảnh.</summary>
    public static NormalizeResult NormalizeManual(byte[] original, Rectangle crop, JsonElement preset)
    {
        var im = Load(original); crop.Intersect(new Rectangle(0, 0, im.W, im.H));
        return Finish(im, crop, preset, false, new List<string>(), null, 1);
    }
}
