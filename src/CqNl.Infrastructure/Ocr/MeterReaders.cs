using System.Globalization;
using System.Text.RegularExpressions;
using CqNl.Infrastructure.Imaging;
using static CqNl.Infrastructure.Imaging.ImageNormalizer;

namespace CqNl.Infrastructure.Ocr;

/// <summary>Kết quả đọc số: LUÔN là gợi ý. Value = null nếu không chắc → QC bắt buộc nhập/xác nhận tay.</summary>
public sealed record MeterReading(string Device, string? Raw, string? Value, double Confidence, string Info, string? DecimalSeparatorOnDevice = null, string? Unit = null);

internal static class Gray
{
    public static float[] FromRgb(Rgb im, System.Drawing.Rectangle r)
    {   // giống cv2.COLOR_BGR2GRAY
        var o = new float[r.Width * r.Height];
        for (var y = 0; y < r.Height; y++) for (var x = 0; x < r.Width; x++) { var i = (r.Y + y) * im.W + r.X + x; o[y * r.Width + x] = (float)Math.Round(0.299 * im.R[i] + 0.587 * im.G[i] + 0.114 * im.B[i]); }
        return o;
    }

    /// <summary>Phóng to bicubic kiểu OpenCV INTER_CUBIC (a = −0.75).</summary>
    public static float[] ResizeCubic(float[] s, int w, int h, int nw, int nh)
    {
        static float K(float x) { const float a = -0.75f; x = Math.Abs(x); return x <= 1 ? ((a + 2) * x - (a + 3)) * x * x + 1 : x < 2 ? ((a * x - 5 * a) * x + 8 * a) * x - 4 * a : 0; }
        var o = new float[nw * nh]; float fx = w / (float)nw, fy = h / (float)nh;
        for (var y = 0; y < nh; y++)
        {
            var sy = (y + 0.5f) * fy - 0.5f; var iy = (int)Math.Floor(sy); var ty = sy - iy;
            for (var x = 0; x < nw; x++)
            {
                var sx = (x + 0.5f) * fx - 0.5f; var ix = (int)Math.Floor(sx); var tx = sx - ix; float acc = 0;
                for (var m = -1; m <= 2; m++)
                {
                    var yy = Math.Clamp(iy + m, 0, h - 1); var wy = K(m - ty);
                    for (var n = -1; n <= 2; n++) acc += s[yy * w + Math.Clamp(ix + n, 0, w - 1)] * K(n - tx) * wy;
                }
                o[y * nw + x] = Math.Clamp(acc, 0, 255);
            }
        }
        return o;
    }

    public static float[] Gauss3(float[] s, int w, int h) => Blur(s, w, h, new[] { 0.25f, 0.5f, 0.25f });

    /// <summary>Lọc max/min ảnh xám theo cửa sổ chữ nhật (dilate/erode thang xám), bỏ qua điểm ngoài biên.</summary>
    public static float[] MinMax(float[] s, int w, int h, int k, bool max)
    {
        var a = k / 2; var t = new float[w * h]; var o = new float[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var v = max ? float.MinValue : float.MaxValue;
            for (var j = Math.Max(0, x - a); j <= Math.Min(w - 1, x + k - 1 - a); j++) v = max ? Math.Max(v, s[y * w + j]) : Math.Min(v, s[y * w + j]);
            t[y * w + x] = v;
        }
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var v = max ? float.MinValue : float.MaxValue;
            for (var j = Math.Max(0, y - a); j <= Math.Min(h - 1, y + k - 1 - a); j++) v = max ? Math.Max(v, t[j * w + x]) : Math.Min(v, t[j * w + x]);
            o[y * w + x] = v;
        }
        return o;
    }
}

/// <summary>Mitutoyo: LCD ma trận điểm 5×7 font HD44780. Đọc theo lưới điểm, so khớp tuyệt đối với bảng font (kể cả . , -).
/// Port từ research/ocr/mitutoyo_dotmatrix.py (golden: 38.2, 47.3, 58.4 tin cậy 1.0; 50.1 tin cậy 0.8).</summary>
public static class MitutoyoReader
{
    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['0'] = "01110 10001 10011 10101 11001 10001 01110", ['1'] = "00100 01100 00100 00100 00100 00100 01110",
        ['2'] = "01110 10001 00001 00010 00100 01000 11111", ['3'] = "11111 00010 00100 00010 00001 10001 01110",
        ['4'] = "00010 00110 01010 10010 11111 00010 00010", ['5'] = "11111 10000 11110 00001 00001 10001 01110",
        ['6'] = "00110 01000 10000 11110 10001 10001 01110", ['7'] = "11111 00001 00010 00100 01000 01000 01000",
        ['8'] = "01110 10001 10001 01110 10001 10001 01110", ['9'] = "01110 10001 10001 01111 00001 00010 01100",
        ['.'] = "00000 00000 00000 00000 00000 01100 01100", [','] = "00000 00000 00000 00000 01100 00100 01000",
        ['-'] = "00000 00000 00000 11111 00000 00000 00000", [' '] = "00000 00000 00000 00000 00000 00000 00000",
        ['H'] = "10001 10001 10001 11111 10001 10001 10001", ['R'] = "11110 10001 10001 11110 10100 10010 10001",
        ['C'] = "01110 10001 10000 10000 10000 10001 01110", ['B'] = "11110 10001 10001 11110 10001 10001 11110",
        ['V'] = "10001 10001 10001 10001 10001 01010 00100", ['A'] = "01110 10001 10001 10001 11111 10001 10001",
        ['S'] = "01111 10000 10000 01110 00001 00001 11110", ['N'] = "10001 10001 11001 10101 10011 10001 10001",
        ['W'] = "10001 10001 10001 10101 10101 10101 01010", ['K'] = "10001 10010 10100 11000 10100 10010 10001",
    };
    private static readonly (char ch, int[] bits)[] G = Glyphs.Select(kv => (kv.Key, kv.Value.Replace(" ", "").Select(c => c - '0').ToArray())).ToArray();
    private static readonly Regex Value = new(@"^(-?\d{1,3})([.,])(\d{1,2})\s*([A-Z]*)$");

    public static MeterReading Read(Rgb im)
    {
        var lcd = MitutoyoLcd(im);
        if (lcd is null) return new MeterReading("Mitutoyo", null, null, 0, "không tìm thấy LCD");
        var (text, conf, info) = ReadLcd(im, lcd.Value);
        var m = Value.Match(text ?? "");
        if (m.Success && conf >= 0.8)
        {
            var unit = m.Groups[4].Value is "HRC" or "HRB" or "HRA" or "HV" or "HB" or "HRF" ? m.Groups[4].Value : null;
            return new MeterReading("Mitutoyo", text, $"{m.Groups[1].Value}.{m.Groups[3].Value}", conf, info + (unit is null ? $"; đơn vị không rõ '{m.Groups[4].Value}'" : ""), m.Groups[2].Value, unit);
        }
        return new MeterReading("Mitutoyo", text, null, conf, info + "; không chắc chắn → nhập tay/xác nhận");
    }

    private static (string? text, double conf, string info) ReadLcd(Rgb im, System.Drawing.Rectangle roi)
    {
        var g0 = Gray.FromRgb(im, roi);
        var scale = 300.0 / roi.Height; int W = (int)Math.Round(roi.Width * scale), H = (int)Math.Round(roi.Height * scale);
        var g = Gray.Gauss3(Gray.ResizeCubic(g0, roi.Width, roi.Height, W, H).Select(v => (float)Math.Round(v)).ToArray(), W, H);
        var bg = Gray.MinMax(Gray.MinMax(g, W, H, 61, true), W, H, 61, false);         // đóng 61×61 (nền)
        var dark = new float[W * H]; for (var i = 0; i < dark.Length; i++) dark[i] = Math.Clamp((bg[i] - g[i]) / Math.Max(bg[i], 1), 0, 1);
        var d8 = dark.Select(v => (byte)(v * 255)).ToArray(); var t = Otsu(d8);
        var bin = d8.Select(v => v > t).ToArray();
        // bỏ viền khung LCD: nét ngang/dọc dài
        var lh = Morph(Morph(bin, W, H, (int)(W * 0.3), 1, false), W, H, (int)(W * 0.3), 1, true);
        var lv = Morph(Morph(bin, W, H, 1, (int)(H * 0.8), false), W, H, 1, (int)(H * 0.8), true);
        var lines = new bool[W * H]; for (var i = 0; i < lines.Length; i++) lines[i] = lh[i] || lv[i];
        lines = Morph(lines, W, H, 5, 5, true);
        for (var i = 0; i < bin.Length; i++) bin[i] = bin[i] && !lines[i];
        var lab = Label(bin, W, H, out var areas);
        for (var i = 0; i < bin.Length; i++) if (bin[i] && areas[lab[i]] < 15) bin[i] = false;
        // dải chữ
        var rs = new int[H]; for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) if (bin[y * W + x]) rs[y]++;
        var rmax = rs.Max(); var runs = Runs(Enumerable.Range(0, H).Select(y => rs[y] > 0.15 * rmax).ToArray());
        if (runs.Count == 0) return (null, 0, "không thấy chữ trên LCD");
        var (t0, t1) = runs.OrderByDescending(r => r.e - r.s).First();
        var colAny = new int[W]; for (var y = t0; y < t1; y++) for (var x = 0; x < W; x++) if (bin[y * W + x]) colAny[x]++;
        if (t1 - t0 < 10 || colAny.Count(c => c > 0) < 10) return (null, 0, "không thấy chữ trên LCD");
        var p = (t1 - t0) / 7.0;
        // tách ô ký tự theo cột trống
        var cr = new List<(int a, int b)>(); int? st = null; int last = 0, gap = 0;
        for (var x = 0; x < W + (int)p; x++)
        {
            var on = x < W && colAny[x] > 0.3 * p;
            if (on) { st ??= x; last = x; gap = 0; }
            else if (st is not null) { gap++; if (gap >= 0.18 * p) { cr.Add((st.Value, last + 1)); st = null; } }
        }
        cr = cr.Where(r => r.b - r.a >= 0.5 * p).ToList();
        if (cr.Count == 0) return (null, 0, "không tách được ký tự");
        var wide = cr.Where(r => r.b - r.a >= 0.45 * (t1 - t0)).ToList();
        if (wide.Count == 0) return (null, 0, "không thấy ký tự đủ rộng");
        double W5 = wide.Min(r => r.b - r.a);
        var gaps0 = cr.Zip(cr.Skip(1), (u, v) => (double)(v.a - u.b)).Where(x => x < 0.5 * W5).ToList();
        var gg = gaps0.Count > 0 ? Median(gaps0) : 0.1 * W5;
        var anchors = new List<(double x, double w)>();
        foreach (var (a0, a1) in wide)
        {
            var k = Math.Max(1, (int)Math.Round((a1 - a0 + gg) / (W5 + gg)));
            var cw = (a1 - a0 - (k - 1) * gg) / k;
            for (var i = 0; i < k; i++) anchors.Add((a0 + i * (cw + gg), cw));
        }
        var cells = new List<(double x, double w)>();
        double lead = cr[0].a;
        if (lead < anchors[0].x - 0.5 * W5)
        {
            var n = (int)Math.Round((anchors[0].x - lead) / (W5 + gg)); var pitch = (anchors[0].x - lead) / Math.Max(1, n);
            for (var i = 0; i < n; i++) cells.Add((anchors[0].x - (n - i) * pitch, W5));
        }
        for (var i = 0; i < anchors.Count; i++)
        {
            cells.Add(anchors[i]);
            if (i == anchors.Count - 1) break;
            var free = anchors[i + 1].x - (anchors[i].x + anchors[i].w); var n = (int)Math.Round((free - gg) / (W5 + gg));
            if (n > 0) { var pitch = (free - gg) / n; for (var j = 0; j < n; j++) cells.Add((anchors[i].x + anchors[i].w + gg + j * pitch, pitch - gg)); }
        }
        double tail = cr[^1].b, lastEnd = anchors[^1].x + anchors[^1].w;
        if (tail > lastEnd + 0.3 * W5) { var n = (int)Math.Round((tail - lastEnd) / (W5 + gg)); for (var i = 0; i < Math.Max(1, n); i++) cells.Add((lastEnd + gg + i * (W5 + gg), W5)); }
        double onS = 0, offS = 0; int onN = 0, offN = 0;
        for (var y = t0; y < t1; y++) for (var x = 0; x < W; x++) { var i = y * W + x; if (bin[i]) { onS += dark[i]; onN++; } else { offS += dark[i]; offN++; } }
        var thr = 0.5 * (onS / Math.Max(1, onN) + offS / Math.Max(1, offN));
        var sb = new System.Text.StringBuilder(); var dists = new List<(int d, int margin, char ch)>();
        foreach (var (cx0, cw) in cells)
        {
            var M = Sample(dark, W, H, t0, cx0, p, 5, cw / 5);
            var bits = M.Select(v => v > thr ? 1 : 0).ToArray();
            var ranked = G.Select(gl => (d: gl.bits.Zip(bits, (a, b) => Math.Abs(a - b)).Sum(), gl.ch)).OrderBy(r => r.d).ThenBy(r => r.ch).ToList();
            var (d0, ch) = ranked[0]; sb.Append(ch);
            if (!(ch == ' ' && d0 == 0)) dists.Add((d0, ranked[1].d - d0, ch));
        }
        var text = sb.ToString().Trim();
        var num = dists.TakeWhile(x => "0123456789.,-".Contains(x.ch)).ToList();
        var conf = num.Count == 0 ? 0.0 : num.All(x => x.d == 0) ? 1.0 : num.All(x => x.d <= 3 && x.margin >= 4) ? 0.8 : 0.3;
        return (text, conf, $"bước dọc {p:0.0}, ô {W5 + gg:0.0}; lệch [{string.Join(",", dists.Select(x => $"{x.ch}:{x.d}"))}]");
    }

    private static double[] Sample(float[] dark, int W, int H, double top, double x0, double p, int ncols, double px)
    {
        var M = new double[7 * ncols]; var r = Math.Max(1, (int)(Math.Min(p, px) * 0.25));
        for (var i = 0; i < 7; i++)
        {
            var cy = (int)(top + (i + 0.5) * p);
            for (var j = 0; j < ncols; j++)
            {
                var cx = (int)(x0 + (j + 0.5) * px);
                if (cy < 0 || cy >= H || cx < 0 || cx >= W) continue;
                double s = 0; var n = 0;
                for (var y = Math.Max(0, cy - r); y < Math.Min(H, cy + r + 1); y++) for (var x = Math.Max(0, cx - r); x < Math.Min(W, cx + r + 1); x++) { s += dark[y * W + x]; n++; }
                M[i * ncols + j] = s / n;
            }
        }
        return M;
    }

    internal static List<(int s, int e)> Runs(bool[] act)
    {
        var res = new List<(int, int)>(); int? st = null;
        for (var i = 0; i <= act.Length; i++) { var a = i < act.Length && act[i]; if (a && st is null) st = i; if (!a && st is not null) { res.Add((st.Value, i)); st = null; } }
        return res;
    }

    internal static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); var n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; }

    internal static int[] Label(bool[] m, int w, int h, out int[] areas)
    {
        var lab = new int[w * h]; var ar = new List<int> { 0 }; var q = new Queue<int>();
        for (var s = 0; s < m.Length; s++)
        {
            if (!m[s] || lab[s] != 0) continue;
            var id = ar.Count; ar.Add(0); lab[s] = id; q.Enqueue(s);
            while (q.Count > 0)
            {
                var p = q.Dequeue(); ar[id]++; int px = p % w, py = p / w;
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                { int nx = px + dx, ny = py + dy; if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue; var ni = ny * w + nx; if (m[ni] && lab[ni] == 0) { lab[ni] = id; q.Enqueue(ni); } }
            }
        }
        areas = ar.ToArray(); return lab;
    }
}

/// <summary>Đầu cân LED đỏ 7 đoạn (ảnh xuất hàng gốc). Port từ research/ocr/scale_led.py — kiểm trên ảnh thật 41.40 và Ohaus Defender 2000 4.90 → tối đa 0.8.</summary>
public static class ScaleLedReader
{
    private static readonly (string segs, char ch)[] Seg =
    {
        ("abcdef", '0'), ("bc", '1'), ("abged", '2'), ("abgcd", '3'), ("fgbc", '4'), ("afgcd", '5'), ("afgedc", '6'),
        ("abc", '7'), ("abcdefg", '8'), ("abcdfg", '9'), ("g", '-'), ("fbc", '7'), ("afbc", '7'), ("abcfg", '9'), ("afgcde", '6'),
    };

    public static MeterReading Read(Rgb im)
    {
        int Wf = im.W, Hf = im.H;
        Hsv(im, out var Hh, out var S, out var V);
        var red = new bool[Wf * Hf]; for (var i = 0; i < red.Length; i++) red[i] = (Hh[i] < 12 || Hh[i] > 165) && S[i] > 80 && V[i] > 120;
        var faces = Components(Morph(red, Wf, Hf, 15, 15, true), Wf, Hf);
        if (faces.Count == 0) return new MeterReading("Đầu cân", null, null, 0, "không thấy màn hình LED");
        var f = faces.OrderByDescending(c => c.Area).First();
        // mặt đỏ lớn nhất, nới lên/xuống 1.5 lần chiều cao: đầu cân Ohaus Defender có mặt đỏ bị cửa sổ tối chia thành 2 dải trên/dưới
        int fx = f.X, fy = Math.Max(0, f.Y - (int)(1.5 * f.H)), w = f.W, h = Math.Min(Hf, f.Y + f.H + (int)(1.5 * f.H)) - fy;
        var dark = new bool[w * h]; var core = new bool[w * h]; var vlow = new bool[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var i = (fy + y) * Wf + fx + x; var k = y * w + x;
            core[k] = im.G[i] > 120 && im.R[i] > 180; vlow[k] = V[i] < 110; dark[k] = vlow[k] || core[k];
        }
        dark = Morph(Morph(dark, w, h, 7, 7, true), w, h, 7, 7, false);
        dark = Morph(Morph(dark, w, h, 5, 5, false), w, h, 5, 5, true);
        int Count(bool[] m, int cx, int cy, int cw, int ch) { var n = 0; for (var y = cy; y < cy + ch; y++) for (var x = cx; x < cx + cw; x++) if (m[y * w + x]) n++; return n; }
        var cand = Components(dark, w, h)
            .Where(c => c.W > 2.0 * c.H && c.Area > 0.015 * w * h)
            .Where(c => Count(vlow, c.X, c.Y, c.W, c.H) >= 0.4 * c.W * c.H)          // tem dán / nhãn trắng (sáng) → không phải cửa sổ hiển thị
            .Select(c => (c, lit: Count(core, c.X, c.Y, c.W, c.H))).ToList();
        if (cand.Count == 0) return new MeterReading("Đầu cân", null, null, 0, "không thấy cửa sổ hiển thị");
        var win = cand.OrderByDescending(z => z.lit).ThenByDescending(z => z.c.Area).First().c;   // cửa sổ có nhiều điểm LED sáng nhất
        var ext = (int)(0.15 * win.W); int wx = Math.Max(0, win.X - ext), ww = Math.Min(w, win.X + win.W + ext) - wx, wy = win.Y, wh = win.H;
        var gch = new float[ww * wh];
        for (var y = 0; y < wh; y++) for (var x = 0; x < ww; x++) gch[y * ww + x] = im.G[(fy + wy + y) * Wf + fx + wx + x];
        var gb = Gray.Gauss3(gch, ww, wh).Select(v => (byte)Math.Clamp(Math.Round(v), 0, 255)).ToArray();
        var t = Math.Max(Otsu(gb), 90);
        var lit = gb.Select(v => v > t).ToArray();
        var comps = Components(lit, ww, wh).Where(c => c.Area >= 4).ToList();
        var inner = comps.Where(c => c.X > 1 && c.Y > 1 && c.X + c.W < ww - 1 && c.Y + c.H < wh - 1).ToList();
        if (inner.Count == 0) return new MeterReading("Đầu cân", null, null, 0, "không thấy chữ số");
        var hmax = inner.Max(c => c.H);
        var tall = inner.Where(c => c.H >= 0.4 * hmax).ToList();
        var big = tall.Where(c => c.H >= 0.8 * hmax).ToList();
        int y0 = big.Min(c => c.Y), y1 = big.Max(c => c.Y + c.H); double H = y1 - y0;
        tall = tall.Where(c => c.Y >= y0 - 0.2 * H && c.Y + c.H <= y1 + 0.2 * H).ToList();
        // bỏ đèn báo (kg, Stable…) và chấm: khối thấp (< 0.5 chiều cao số) gần vuông — đoạn LED dọc thì mảnh
        tall = tall.Where(c => !(c.H < 0.5 * H && c.W >= 0.6 * c.H)).ToList();
        // LED đoạn rời (Ohaus Defender: mỗi đoạn một khối): thêm đoạn NGANG (a/g/d) trong dải dòng số để nối đoạn dọc trái + phải
        // của cùng chữ số khi gom cột (không có chúng '4' bị tách thành 2 chữ số '1')
        var bars = inner.Where(c => c.W >= 0.2 * H && c.H < 0.3 * H && c.W > 2 * c.H && c.Y >= y0 - 0.35 * H && c.Y + c.H <= y1 + 0.2 * H && !tall.Contains(c)).ToList();
        tall = tall.Concat(bars).OrderBy(c => c.X).ToList();
        var groups = new List<int[]>();
        foreach (var c in tall) { if (groups.Count > 0 && c.X <= groups[^1][1] + 0.12 * H) groups[^1][1] = Math.Max(groups[^1][1], c.X + c.W); else groups.Add(new[] { c.X, c.X + c.W }); }
        var dots = inner.Where(c => c.H < 0.3 * H && c.W < 0.3 * H && c.Y + c.H >= y1 - 0.3 * H && c.Y >= y0 + 0.55 * H).ToList();
        var tokens = new List<(double x, char ch)>();
        foreach (var gr in groups)
        {
            int xa = gr[0], xb = gr[1];
            int ry0 = Math.Max(0, (int)(y0 - 0.35 * H)), ry1 = Math.Min(wh, (int)(y1 + 0.1 * H));
            var rows = Enumerable.Range(ry0, ry1 - ry0).Where(y => Enumerable.Range(xa, xb - xa).Any(x => lit[y * ww + x])).ToList();
            int sy0 = rows.Count > 0 ? rows.Min() : y0, sy1 = rows.Count > 0 ? rows.Max() + 1 : y1, sh = sy1 - sy0, sw = xb - xa;
            bool At(int yy, int xx) => lit[(sy0 + yy) * ww + xa + xx];
            if (sw < 0.3 * H)
            {
                tokens.Add((xa, '1'));
                var stem = Enumerable.Range(0, sw).Where(xx => Enumerable.Range((int)(0.2 * sh), (int)(0.6 * sh) - (int)(0.2 * sh)).Any(yy => At(yy, xx))).ToList();
                if (stem.Count > 0)
                {
                    var cnt = 0; for (var yy = (int)(0.65 * sh); yy < sh; yy++) for (var xx = stem.Max() + 2; xx < sw; xx++) if (At(yy, xx)) cnt++;
                    if (cnt >= 4) tokens.Add((xb - 0.5, '.'));
                }
                continue;
            }
            bool On(double ya, double yb, double xa2, double xb2)
            {
                int a0 = (int)(ya * sh), a1 = Math.Max(a0 + 1, (int)(yb * sh)), b0 = (int)(xa2 * sw), b1 = Math.Max(b0 + 1, (int)(xb2 * sw)); int n = 0, on = 0;
                for (var yy = a0; yy < Math.Min(sh, a1); yy++) for (var xx = b0; xx < Math.Min(sw, b1); xx++) { n++; if (At(yy, xx)) on++; }
                return n > 0 && on / (double)n > 0.15;
            }
            var s = new Dictionary<char, bool>
            {
                ['a'] = On(0, 0.16, 0.3, 0.85), ['g'] = On(0.42, 0.58, 0.25, 0.75), ['d'] = On(0.84, 1, 0.15, 0.7),
                ['f'] = On(0.12, 0.45, 0, 0.35), ['b'] = On(0.12, 0.45, 0.65, 1), ['e'] = On(0.55, 0.88, 0, 0.35), ['c'] = On(0.55, 0.88, 0.65, 1),
            };
            var key = new string("abcdefg".Where(k => s[k]).ToArray());
            var m = Seg.FirstOrDefault(z => z.segs.OrderBy(c => c).SequenceEqual(key));
            tokens.Add((xa, m.segs is null ? '?' : m.ch));
        }
        foreach (var c in dots)
        {
            var cx = c.X + c.W / 2.0;
            if (groups.Count > 0 && groups[0][0] < cx && cx < groups[^1][1] && !groups.Any(gr => gr[0] <= cx && cx <= gr[1])) tokens.Add((c.X, '.'));
        }
        var outS = new string(tokens.OrderBy(z => z.x).Select(z => z.ch).ToArray());
        return Regex.IsMatch(outS, @"^-?\d{1,4}(\.\d{1,3})?$")
            ? new MeterReading("Đầu cân", outS, outS, 0.8, "đọc LED 7 đoạn (gợi ý, chờ QC xác nhận)", ".")
            : new MeterReading("Đầu cân", outS, null, 0, $"không chắc ('{outS}') → nhập tay");
    }
}

/// <summary>dynaROCK II: số xanh lớn → Tesseract (eng.traineddata người dùng cung cấp) trên vùng số lớn + xác minh hình học
/// đúng 1 dấu chấm thập phân ở chân dòng. Tối đa 0.8 (luôn chờ QC xác nhận). Không lấy số nhỏ (Ø, 2/10, lịch sử).</summary>
public static class DynaRockReader
{
    public static MeterReading Read(Rgb im, string tessdataDir)
    {
        int W = im.W, Hh = im.H;
        Hsv(im, out var H, out var S, out var V);
        var m = new bool[W * Hh]; for (var i = 0; i < m.Length; i++) m[i] = H[i] >= 95 && H[i] <= 115 && S[i] >= 120 && V[i] >= 120;
        var comps = Components(m, W, Hh).Where(c => c.H > 0.04 * Hh && c.H < 0.2 * Hh && c.Area > 50).ToList();
        if (comps.Count == 0) return new MeterReading("dynaROCK II", null, null, 0, "không nhận ra màn hình dynaROCK");
        var hmax = comps.Max(c => c.H); var big = comps.Where(c => c.H > 0.6 * hmax).ToList();
        int x0 = big.Min(c => c.X), y0 = big.Min(c => c.Y), x1 = big.Max(c => c.X + c.W), y1 = big.Max(c => c.Y + c.H), pad = (int)(0.15 * hmax);
        var roi = System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(x0 - pad, y0 - pad, x1 - x0 + 2 * pad, y1 - y0 + 2 * pad), new System.Drawing.Rectangle(0, 0, W, Hh));
        var g = Gray.FromRgb(im, roi); var sc = 200.0 / roi.Height; int nw = (int)Math.Round(roi.Width * sc), nh = 200;
        var big8 = Gray.ResizeCubic(g, roi.Width, roi.Height, nw, nh).Select(v => (byte)Math.Round(v)).ToArray();
        var t = Otsu(big8); var white = big8.Select(v => v > t).ToArray();                     // true = trắng
        if (white.Count(v => !v) > 0.5 * white.Length) white = white.Select(v => !v).ToArray();  // chữ đen trên nền trắng
        string raw;
        using (var bmp = new System.Drawing.Bitmap(nw, nh))
        {
            for (var y = 0; y < nh; y++) for (var x = 0; x < nw; x++) bmp.SetPixel(x, y, white[y * nw + x] ? System.Drawing.Color.White : System.Drawing.Color.Black);
            using var ms = new MemoryStream(); bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            using var engine = new Tesseract.TesseractEngine(tessdataDir, "eng", Tesseract.EngineMode.Default);
            engine.SetVariable("tessedit_char_whitelist", "0123456789.,");
            using var pix = Tesseract.Pix.LoadFromMemory(ms.ToArray());
            using var page = engine.Process(pix, Tesseract.PageSegMode.SingleLine);
            raw = (page.GetText() ?? "").Trim();
        }
        var ink = white.Select(v => !v).ToArray(); var cs = Components(ink, nw, nh);
        var Hmax = cs.Count > 0 ? cs.Max(c => c.H) : 0; var bottom = cs.Count > 0 ? cs.Max(c => c.Y + c.H) : 0;
        var dots = cs.Count(c => c.H < 0.3 * Hmax && c.W < 0.3 * Hmax && c.Y + c.H > 0.75 * bottom && c.Area > 20);
        var mm = Regex.Match(raw.Replace(" ", ""), @"^(\d{1,3})[.,](\d)$");
        return mm.Success && dots == 1
            ? new MeterReading("dynaROCK II", raw, $"{mm.Groups[1].Value}.{mm.Groups[2].Value}", 0.8, "Tesseract + xác minh 1 dấu chấm", raw.Contains(',') ? "," : ".")
            : new MeterReading("dynaROCK II", raw, null, 0.3, $"không chắc (chuỗi '{raw}', số dấu chấm thấy được {dots}) → nhập tay");
    }
}

/// <summary>Tự nhận loại máy và đọc. Giá trị ngoài 10–75 bị bỏ (không hợp lý với HRC).</summary>
public static class HardnessOcr
{
    public static MeterReading Read(byte[] imageBytes, string tessdataDir)
    {
        var im = Load(imageBytes);
        var r = MitutoyoLcd(im) is not null ? MitutoyoReader.Read(im) : DynaRockReader.Read(im, tessdataDir);
        if (r.Value is not null && decimal.TryParse(r.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && (v < 10 || v > 75))
            return r with { Value = null, Info = r.Info + $"; giá trị {r.Value} ngoài khoảng HRC hợp lý → bỏ" };
        return r;
    }
}
