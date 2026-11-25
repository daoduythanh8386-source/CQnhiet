using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CqNl.Infrastructure.Imaging;

/// <summary>Xử lý ảnh cho bản sao trong CQ. KHÔNG bao giờ sửa ảnh gốc: mọi hàm nhận bytes và trả bytes mới (PNG).</summary>
public static class CqImages
{
    public static (int W, int H) Size(byte[] bytes) { using var ms = new MemoryStream(bytes); using var img = Image.FromStream(ms, false, false); return (img.Width, img.Height); }

    private static byte[] Png(Bitmap b) { using var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); return ms.ToArray(); }

    /// <summary>Thẻ ảnh độ cứng: cùng tỉ lệ ô bảng, ảnh căn giữa giữ tỉ lệ, dải nhãn trắng bên dưới ghi giá trị (vd "38.2 HRC").
    /// label = null: KHÔNG có dải nhãn (ảnh máy đo đã hiện số) — ảnh chiếm trọn ô.</summary>
    public static byte[] Tile(byte[] img, string? label, double aspect, int width = 900, double labelFrac = 0.13)
    {
        using var src = new Bitmap(new MemoryStream(img));
        if (label is null) labelFrac = 0;
        var h = (int)Math.Round(width / aspect); var sh = (int)(h * labelFrac); var ah = h - sh;
        using var bmp = new Bitmap(width, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.SmoothingMode = SmoothingMode.HighQuality;
        var s = Math.Min(width / (double)src.Width, ah / (double)src.Height);
        int iw = (int)(src.Width * s), ih = (int)(src.Height * s);
        g.DrawImage(src, (width - iw) / 2, (ah - ih) / 2, iw, ih);
        if (label is not null && sh > 0) DrawCentered(g, label, new RectangleF(0, ah, width, sh), sh * 0.62f);
        return Png(bmp);
    }

    /// <summary>Ghi chú số cân ở GÓC DƯỚI TRÁI: chữ đen đậm, nền trắng, viền đen mảnh (in A4 đen trắng vẫn rõ). boxPx = chiều cao ô chữ.</summary>
    public static byte[] CornerLabel(byte[] img, string text, double boxPx)
    {
        using var src = new Bitmap(new MemoryStream(img));
        using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(src, 0, 0, src.Width, src.Height);
        g.SmoothingMode = SmoothingMode.HighQuality; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var bh = (float)Math.Max(14, boxPx); var pad = Math.Max(2, bh / 6);
        using var font = new Font("Arial", bh * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
        var tw = g.MeasureString(text, font).Width;
        var m = Math.Max(3, 0.015f * Math.Min(src.Width, src.Height));
        var rect = new RectangleF(m, src.Height - m - bh, tw + 2 * pad, bh);
        g.FillRectangle(Brushes.White, rect);
        using (var pen = new Pen(Color.Black, Math.Max(1, bh / 14))) g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        DrawCentered(g, text, rect, bh * 0.62f);
        return Png(bmp);
    }

    /// <summary>Chữ ký: nền đổi thành màu giấy A4 đều, bỏ bóng/đốm, nét mực đậm và rõ hơn, giữ màu mực; cắt sát + lề.
    /// transparent=true: nền trong suốt (cùng nét) để chèn lên CQ không lộ khung (OQ-18).</summary>
    public static byte[] Signature(byte[] img, bool transparent = false, Color? paper = null, double noise = 0.10, double inkFull = 0.32)
    {
        var bg = paper ?? Color.FromArgb(250, 248, 245);
        using var orig = new Bitmap(new MemoryStream(img));
        // chữ ký chụp điện thoại có thể rất lớn → thu về tối đa 1600 px chiều rộng trước khi xử lý từng điểm ảnh
        var k = Math.Min(1.0, 1600.0 / orig.Width);
        using var src = new Bitmap(orig, Math.Max(1, (int)(orig.Width * k)), Math.Max(1, (int)(orig.Height * k)));
        int W = src.Width, H = src.Height;
        var lum = new float[W * H]; var rgb = new Color[W * H];
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) { var c = src.GetPixel(x, y); rgb[y * W + x] = c; lum[y * W + x] = (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f; }
        // nền giấy ước lượng: cực đại cục bộ (thô) theo lưới 32 px → khử bóng
        const int cell = 32; int gw = (W + cell - 1) / cell, gh = (H + cell - 1) / cell; var paperLum = new float[gw * gh];
        for (var gy = 0; gy < gh; gy++) for (var gx = 0; gx < gw; gx++)
        {
            var vals = new List<float>();
            for (var y = gy * cell; y < Math.Min(H, (gy + 1) * cell); y++) for (var x = gx * cell; x < Math.Min(W, (gx + 1) * cell); x++) vals.Add(lum[y * W + x]);
            vals.Sort(); paperLum[gy * gw + gx] = vals[(int)(vals.Count * 0.9)];
        }
        var alpha = new float[W * H]; double sr = 0, sg = 0, sb = 0; int n = 0;
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++)
        {
            var p = Math.Max(0.05f, paperLum[(y / cell) * gw + x / cell]);
            var dark = 1 - Math.Min(1, lum[y * W + x] / p);
            var a = (float)Math.Clamp((dark - noise) / (inkFull - noise), 0, 1);
            alpha[y * W + x] = a;
            if (a > 0.7) { var c = rgb[y * W + x]; sr += c.R; sg += c.G; sb += c.B; n++; }
        }
        var ink = n > 0 ? Color.FromArgb((int)(sr / n * 0.72), (int)(sg / n * 0.72), (int)(sb / n * 0.72)) : Color.FromArgb(40, 40, 120);
        int x0 = W, y0 = H, x1 = 0, y1 = 0;
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) if (alpha[y * W + x] > 0.2) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        if (x1 <= x0) { x0 = 0; y0 = 0; x1 = W - 1; y1 = H - 1; }
        var mg = (int)(0.04 * Math.Max(x1 - x0, y1 - y0));
        x0 = Math.Max(0, x0 - mg); y0 = Math.Max(0, y0 - mg); x1 = Math.Min(W - 1, x1 + mg); y1 = Math.Min(H - 1, y1 + mg);
        using var outBmp = new Bitmap(x1 - x0 + 1, y1 - y0 + 1, PixelFormat.Format32bppArgb);
        for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
        {
            var a = alpha[y * W + x];
            outBmp.SetPixel(x - x0, y - y0, transparent
                ? Color.FromArgb((int)(a * 255), ink)
                : Color.FromArgb(255, (int)(bg.R * (1 - a) + ink.R * a), (int)(bg.G * (1 - a) + ink.G * a), (int)(bg.B * (1 - a) + ink.B * a)));
        }
        return Png(outBmp);
    }

    /// <summary>Ảnh con dấu đã có nền trong suốt (PNG có kênh alpha thật)?</summary>
    public static bool HasTransparency(byte[] img)
    {
        using var b = new Bitmap(new MemoryStream(img));
        if (!Image.IsAlphaPixelFormat(b.PixelFormat)) return false;
        int step = Math.Max(1, Math.Min(b.Width, b.Height) / 60), clear = 0, total = 0;
        for (var y = 0; y < b.Height; y += step) for (var x = 0; x < b.Width; x += step) { total++; if (b.GetPixel(x, y).A < 200) clear++; }
        return clear > total * 0.05;
    }

    /// <summary>Con dấu chụp / quét (JPG, PNG nền trắng): bỏ nền giấy → nền trong suốt, giữ màu mực dấu, cắt sát + lề.
    /// Nền ước lượng = trung vị các điểm ở viền ảnh; điểm càng khác nền càng đậm (mép mềm, không răng cưa).</summary>
    public static byte[] StampTransparent(byte[] img)
    {
        using var orig = new Bitmap(new MemoryStream(img));
        var k = Math.Min(1.0, 1200.0 / Math.Max(orig.Width, orig.Height));
        using var src = new Bitmap(orig, Math.Max(1, (int)(orig.Width * k)), Math.Max(1, (int)(orig.Height * k)));
        int W = src.Width, H = src.Height;
        var px = new Color[W * H];
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) px[y * W + x] = src.GetPixel(x, y);
        var ring = new List<Color>(); var bw = Math.Max(1, Math.Min(W, H) / 50);
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) if (x < bw || y < bw || x >= W - bw || y >= H - bw) ring.Add(px[y * W + x]);
        int Med(Func<Color, int> f) { var v = ring.Select(f).OrderBy(z => z).ToList(); return v[v.Count / 2]; }
        var paper = Color.FromArgb(Med(c => c.R), Med(c => c.G), Med(c => c.B));
        var alpha = new float[W * H];
        for (var i = 0; i < px.Length; i++)
        {
            var c = px[i];
            var d = Math.Max(Math.Abs(c.R - paper.R), Math.Max(Math.Abs(c.G - paper.G), Math.Abs(c.B - paper.B)));
            alpha[i] = Math.Clamp((d - 28f) / (90f - 28f), 0f, 1f);
        }
        int x0 = W, y0 = H, x1 = 0, y1 = 0;
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) if (alpha[y * W + x] > 0.3f) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        if (x1 <= x0 || y1 <= y0) throw new ArgumentException("Không thấy hình con dấu trong ảnh (ảnh trắng hoặc quá mờ)");
        var mg = (int)(0.03 * Math.Max(x1 - x0, y1 - y0));
        x0 = Math.Max(0, x0 - mg); y0 = Math.Max(0, y0 - mg); x1 = Math.Min(W - 1, x1 + mg); y1 = Math.Min(H - 1, y1 + mg);
        using var outBmp = new Bitmap(x1 - x0 + 1, y1 - y0 + 1, PixelFormat.Format32bppArgb);
        for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
        {
            var a = alpha[y * W + x]; var c = px[y * W + x];
            outBmp.SetPixel(x - x0, y - y0, Color.FromArgb((int)(a * 255), c.R, c.G, c.B));
        }
        return Png(outBmp);
    }

    private static void DrawCentered(Graphics g, string text, RectangleF rect, float px)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Arial", px, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, Brushes.Black, rect, sf);
    }
}
