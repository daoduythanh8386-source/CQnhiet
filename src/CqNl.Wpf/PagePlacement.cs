using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CqNl.Infrastructure.Excel;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace CqNl.Wpf;

/// <summary>Ánh xạ toạ độ sheet (EMU) ↔ điểm ảnh trên ảnh trang PDF, theo cách Excel thật sự in:
/// - đầu CỘT: tuyến tính theo bề rộng cột (PxPerEmu, đo từ bề rộng khung biểu mẫu);
/// - đầu DÒNG: đo trực tiếp từng dòng (ô đánh dấu mỗi dòng);
/// - ẢNH (dấu, chữ ký) và phần lệch bên trong ô: theo tỉ lệ đơn vị tuyệt đối (DrawPxPerEmu = tỉ lệ chiều cao dòng) — Excel in cột theo
///   tỉ lệ khác (đo được 0.4707 so với 0.4935 ở mẫu khuôn, lệch tới 15% ở mẫu Vinfast), nên dùng một tỉ lệ chung làm dấu lệch / sai cỡ.
/// Không có dữ liệu dòng → tuyến tính như bản cũ.</summary>
public sealed class PageMap
{
    private readonly List<(double Y, double H, double Top)> _rows;
    private readonly List<(double X, double W)> _cols;
    public double OffsetXPx { get; }
    public double OffsetYPx { get; }
    /// <summary>Tỉ lệ bề rộng CỘT (px / EMU).</summary>
    public double PxPerEmu { get; }
    /// <summary>Tỉ lệ ẢNH và phần lệch trong ô (px / EMU) — bằng tỉ lệ chiều cao dòng đo được.</summary>
    public double DrawPxPerEmu { get; }
    public bool PerRow => _rows.Count > 1;

    public PageMap(double offsetXPx, double offsetYPx, double pxPerEmu, List<(double Y, double H, double Top)>? rows = null, List<(double X, double W)>? cols = null,
        double? drawPxPerEmu = null)
    {
        OffsetXPx = offsetXPx; OffsetYPx = offsetYPx; PxPerEmu = pxPerEmu;
        _rows = rows?.OrderBy(r => r.Y).ToList() ?? new();
        _cols = cols?.OrderBy(c => c.X).ToList() ?? new();
        RowPxPerEmu = _rows.Count > 1 && _rows[^1].Y > _rows[0].Y ? (_rows[^1].Top - _rows[0].Top) / (_rows[^1].Y - _rows[0].Y) : pxPerEmu;
        DrawPxPerEmu = drawPxPerEmu ?? RowPxPerEmu;
    }
    /// <summary>Tỉ lệ chiều cao dòng đo được (px / EMU).</summary>
    public double RowPxPerEmu { get; }

    private double Left(double colX) => OffsetXPx + colX * PxPerEmu;

    public double X(double emu)
    {
        if (_cols.Count == 0) return OffsetXPx + emu * PxPerEmu;
        foreach (var c in _cols)
            if (emu >= c.X && emu < c.X + c.W) return Left(c.X) + (emu - c.X) * DrawPxPerEmu;
        return OffsetXPx + emu * PxPerEmu;
    }

    public double EmuX(double px)
    {
        if (_cols.Count == 0) return (px - OffsetXPx) / PxPerEmu;
        for (var i = 0; i < _cols.Count; i++)
        {
            var c = _cols[i]; var l = Left(c.X); var next = i + 1 < _cols.Count ? Left(_cols[i + 1].X) : Left(c.X + c.W);
            if (px >= l && px < next) return c.X + Math.Min((px - l) / DrawPxPerEmu, c.W - 1);
        }
        return (px - OffsetXPx) / PxPerEmu;
    }

    public double Y(double emu)
    {
        if (_rows.Count == 0) return OffsetYPx + emu * PxPerEmu;
        var k = DrawPxPerEmu;
        if (emu < _rows[0].Y) return _rows[0].Top - (_rows[0].Y - emu) * RowPxPerEmu;
        foreach (var r in _rows)
            if (emu < r.Y + r.H) return r.Top + (emu - r.Y) * k;
        var last = _rows[^1];
        return last.Top + (emu - last.Y) * RowPxPerEmu;
    }

    public double EmuY(double px)
    {
        if (_rows.Count == 0) return (px - OffsetYPx) / PxPerEmu;
        var k = DrawPxPerEmu;
        if (px < _rows[0].Top) return _rows[0].Y - (_rows[0].Top - px) / RowPxPerEmu;
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            var next = i + 1 < _rows.Count ? _rows[i + 1].Top : r.Top + r.H * RowPxPerEmu;
            if (px < next) return r.Y + Math.Min((px - r.Top) / k, r.H - 1);
        }
        var last = _rows[^1];
        return last.Y + (px - last.Top) / RowPxPerEmu;
    }
}

/// <summary>
/// Dựng ảnh trang PDF (Windows.Data.Pdf) và hiệu chỉnh ánh xạ: tỉ lệ + lệch ngang lấy từ VIỀN BẢNG thực tế trên ảnh trang
/// (đúng cả khi template "vừa trang" làm Excel tự đổi tỉ lệ), lệch dọc từ lề trên của template.
/// Đã kiểm thử: dấu thử màu xanh đặt bằng công thức này nằm đúng chỗ Excel in ra (sai lệch ≤ 2 mm) trên cả 3 template.
/// </summary>
public static class PagePlacement
{
    public static async Task<BitmapSource> RenderAsync(byte[] pdf, uint widthPx)
    {
        using var mem = new InMemoryRandomAccessStream();
        using (var w = new DataWriter(mem.GetOutputStreamAt(0)))
        {
            w.WriteBytes(pdf); await w.StoreAsync(); await w.FlushAsync(); w.DetachStream();
        }
        var doc = await PdfDocument.LoadFromStreamAsync(mem);
        using var page = doc.GetPage(0);
        using var outStream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(outStream, new PdfPageRenderOptions { DestinationWidth = widthPx });
        outStream.Seek(0);
        var ms = new MemoryStream();
        using (var s = outStream.AsStreamForRead()) await s.CopyToAsync(ms);
        ms.Position = 0;
        var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit(); bi.Freeze();
        return bi;
    }

    public static (int W, int H, byte[] Bgra) Pixels(BitmapSource src)
    {
        var b = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = b.PixelWidth, h = b.PixelHeight; var px = new byte[w * h * 4];
        b.CopyPixels(px, w * 4, 0);
        return (w, h, px);
    }

    /// <summary>Ô đánh dấu hiệu chỉnh: vuông MÀU TÍM HỒNG (magenta) thuần, đặt cách góc ô neo 1 × 0.5 mm, rộng tối đa 2.5 mm (chỉ có trong ảnh hiệu chỉnh).</summary>
    public const double MarkerDxMm = 1, MarkerDyMm = 0.5, MarkerWidthMm = 2.5;

    public static byte[] MarkerPng()
    {
        using var bmp = new System.Drawing.Bitmap(60, 60);
        using (var gr = System.Drawing.Graphics.FromImage(bmp)) gr.Clear(System.Drawing.Color.FromArgb(255, 0, 255));
        using var ms = new MemoryStream(); bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray();
    }

    public static CqOverlay Marker(string cell, double widthMm = MarkerWidthMm) => new(new CqImage(MarkerPng(), 60, 60, "CalibMarker"), Cell: cell, DxMm: MarkerDxMm, DyMm: MarkerDyMm, WidthMm: widthMm);

    /// <summary>Ô đánh dấu ở cột đầu vùng in, MỖI DÒNG HIỆN một ô — để đo đỉnh từng dòng trên trang in thật.</summary>
    public static List<CqOverlay> RowMarkers(string firstCol, IReadOnlyList<(int Row, double Y, double H)> rows)
    {
        var list = rows.Select(r => Marker(firstCol + r.Row, Math.Clamp(r.H / 36000 - 1.5, 1, MarkerWidthMm))).ToList();   // dòng thấp → ô nhỏ hơn
        // ô ĐO TỈ LỆ ẢNH: rộng đúng 40 mm (sheet), đặt xa dải ô dòng — đo bề rộng in thật → tỉ lệ in của dấu / chữ ký
        if (rows.Count > 0) list.Add(new CqOverlay(new CqImage(MarkerPng(), 60, 60, "CalibScale"), Cell: firstCol + rows[0].Row, DxMm: ScaleDxMm, DyMm: 1, WidthMm: ScaleWidthMm));
        return list;
    }
    public const double ScaleDxMm = 90, ScaleWidthMm = 40;

    private static bool IsMarker(byte[] px, int i) => px[i] > 190 && px[i + 1] < 70 && px[i + 2] > 190;    // BGRA: tím hồng (magenta) thuần — không có trong biểu mẫu

    /// <summary>
    /// Hiệu chỉnh trên ảnh trang CÓ ô đánh dấu từng dòng: tỉ lệ ngang = bề rộng khung biểu mẫu / bề rộng vùng in; đỉnh mỗi dòng = vị trí ô
    /// đánh dấu của dòng đó (đo thật, không suy từ tỉ lệ ngang — Excel in dòng và cột theo hai tỉ lệ khác nhau).
    /// </summary>
    public static PageMap Calibrate(BitmapSource page, CqWriter.SheetGeometry g, double firstColXEmu, IReadOnlyList<(int Row, double Y, double H)> rows,
        IReadOnlyList<(int Col, double X, double W)>? cols = null)
    {
        var (w, h, px) = Pixels(page);
        int minX = w, maxX = -1;
        for (var y = (int)(h * 0.15); y < (int)(h * 0.80); y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (IsMarker(px, i)) continue;
                var lum = 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
                if (lum < 140) { if (x < minX) minX = x; if (x > maxX) maxX = x; }
            }
        if (maxX <= minX) throw new InvalidOperationException("Không nhận ra khung biểu mẫu trên ảnh trang");
        var k = (maxX - minX + 1) / g.AreaW;
        // các ô đánh dấu: thành phần liên thông màu magenta ở dải bên trái khung (cột đầu vùng in)
        var mmPx = w / 210.0;
        int band0 = Math.Max(0, minX - (int)(4 * mmPx)), band1 = Math.Min(w - 1, minX + (int)(25 * mmPx));
        var seen = new bool[w * h]; var comps = new List<(int X0, int Y0, int X1, int Y1, int N)>();
        var stack = new Stack<int>();
        for (var y = 0; y < h; y++) for (var x = band0; x <= band1; x++)
        {
            var id = y * w + x; if (seen[id] || !IsMarker(px, id * 4)) continue;
            int x0 = x, y0 = y, x1 = x, y1 = y, n = 0; seen[id] = true; stack.Push(id);
            while (stack.Count > 0)
            {
                var c = stack.Pop(); int cx = c % w, cy = c / w; n++;
                x0 = Math.Min(x0, cx); x1 = Math.Max(x1, cx); y0 = Math.Min(y0, cy); y1 = Math.Max(y1, cy);
                foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                {
                    if (nx < band0 || nx > band1 || ny < 0 || ny >= h) continue;
                    var nid = ny * w + nx; if (seen[nid] || !IsMarker(px, nid * 4)) continue;
                    seen[nid] = true; stack.Push(nid);
                }
            }
            if (n >= 3) comps.Add((x0, y0, x1, y1, n));
        }
        comps = comps.OrderBy(c => c.Y0).ToList();
        // ô đo tỉ lệ ảnh (ngoài dải ô dòng): bề rộng in thật / 40 mm
        int sx0 = w, sx1 = -1;
        for (var y = 0; y < h; y++) for (var x = band1 + 1; x < w; x++)
            if (IsMarker(px, (y * w + x) * 4)) { sx0 = Math.Min(sx0, x); sx1 = Math.Max(sx1, x); }
        double? kImg = sx1 > sx0 + 10 ? (sx1 - sx0 + 1) / (ScaleWidthMm * 36000) : null;
        if (comps.Count != rows.Count)
            throw new InvalidOperationException($"Hiệu chỉnh: thấy {comps.Count} ô đánh dấu, cần {rows.Count} (mỗi dòng một ô)");
        // tỉ lệ dọc (đơn vị tuyệt đối) từ ô đầu và ô cuối; đỉnh dòng = đỉnh ô đánh dấu − 0.5 mm lệch (theo tỉ lệ dọc)
        var kd = rows.Count > 1 && rows[^1].Y > rows[0].Y ? (comps[^1].Y0 - comps[0].Y0) / (rows[^1].Y - rows[0].Y) : k;
        var kdr = kImg ?? kd;                                                   // lệch trong ô theo tỉ lệ ảnh
        var offX = comps.Select(c => c.X0 - firstColXEmu * k - MarkerDxMm * 36000 * kdr).OrderBy(v => v).ElementAt(comps.Count / 2);
        var list = rows.Select((r, i) => (r.Y, r.H, Top: comps[i].Y0 - MarkerDyMm * 36000 * kdr)).ToList();
        return new PageMap(offX, list[0].Top - list[0].Y * kd, k, list, cols?.Select(c => (c.X, c.W)).ToList(), kImg);
    }
}
