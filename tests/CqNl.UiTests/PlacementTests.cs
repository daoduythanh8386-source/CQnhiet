using System.IO;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Pdf;
using CqNl.Wpf;
using Xunit;

namespace CqNl.UiTests;

/// <summary>
/// Kéo-thả dấu/chữ ký: vị trí khung hiển thị trên ảnh trang phải TRÙNG chỗ Excel in thật — ở MỌI chỗ trên trang, kể cả xa ô neo
/// (lỗi người dùng báo 23/11: dấu treo kéo lên đầu trang, neo N34 lệch dọc -477 mm sheet → in lệch ~1 cm, vì Excel in dòng theo tỉ lệ
/// 0.4935 còn cột/ảnh theo 0.4707 mà trình kéo-thả cũ dùng một tỉ lệ cho cả hai chiều).
/// Cách kiểm: hiệu chỉnh bằng ô đánh dấu MỖI DÒNG như app → đặt ô vuông màu ở vị trí "kéo tới" → app neo lại vào ô dưới con trỏ →
/// Excel xuất PDF → tìm ô màu trên trang in thật, so với vị trí người dùng đã thấy khi kéo.
/// </summary>
public class PlacementTests
{
    private static string P(string n) => Path.Combine(AppContext.BaseDirectory, "TestData", n);

    private static byte[] SquarePng()
    {
        using var bmp = new System.Drawing.Bitmap(200, 200);
        using (var gr = System.Drawing.Graphics.FromImage(bmp)) gr.Clear(System.Drawing.Color.FromArgb(0, 200, 0));
        using var ms = new MemoryStream(); bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray();
    }

    private static byte[] Pdf(byte[] xlsx)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cqnl_calib_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.xlsx"), xlsx);
        ExcelPdfExporter.Export(Path.Combine(dir, "a.xlsx"), Path.Combine(dir, "a.pdf"));
        return File.ReadAllBytes(Path.Combine(dir, "a.pdf"));
    }

    private static (int X, int Y, int W) FindGreen(byte[] pdf, uint width)
    {
        var page = PagePlacement.RenderAsync(pdf, width).GetAwaiter().GetResult();
        var (w, h, px) = PagePlacement.Pixels(page);
        int x0 = w, y0 = h, x1 = -1;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4; // BGRA
            if (px[i + 1] > 150 && px[i + 2] < 90 && px[i] < 90) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); }
        }
        Assert.True(x1 > 0, "Không thấy ô xanh trên trang in");
        return (x0, y0, x1 - x0 + 1);
    }

    [Theory]
    [InlineData("khuon")]
    [InlineData("carbon")]
    [InlineData("gio")]
    [InlineData("nito")]
    [InlineData("vinfast")]
    public void Drag_anywhere_on_page_prints_exactly_where_shown(string tid)
    {
        if (!ExcelPdfExporter.IsAvailable()) return;
        var map = TemplateMap.Load(P(Path.Combine("maps", tid + ".json")));
        var d = new CqData();
        d.Values["Customer"] = "Công ty TNHH Takara Tool & Die Hà Nội"; d.Values["Material"] = "SKD11"; d.Values["ReportNo"] = "001/11";
        foreach (var m in map.Manual) d.Manual[m.Key] = "0.1";                     // mẫu Thấm Nitơ bắt buộc nhập thông số tay
        var draft = CqWriter.Build(File.ReadAllBytes(P(map.FileName)), map, d);
        const uint W = 1240; var mm = W / 210.0;
        // 1) hiệu chỉnh như app: ô đánh dấu mỗi dòng
        var (fc, rows) = CqWriter.AreaRows(draft, map);
        var marked = Pdf(CqWriter.AddOverlays(draft, map, PagePlacement.RowMarkers(fc, rows)));
        var pm = PagePlacement.Calibrate(PagePlacement.RenderAsync(marked, W).GetAwaiter().GetResult(), CqWriter.Geometry(draft, map),
                                         CqWriter.CellOriginEmu(draft, map, fc + rows[0].Row).X, rows, CqWriter.AreaCols(draft, map));
        Assert.True(pm.PerRow);
        var appr = map.Ranges("ApproverSignatureFrame")[0].Split(':')[0];
        var org = CqWriter.CellOriginEmu(draft, map, appr);
        var sq = new CqImage(SquarePng(), 200, 200, "Square");
        var report = new List<string>();
        // 2) các vị trí: dấu treo đầu trang (xa ô neo nhất), gần chữ ký người duyệt, giữa trang
        var cases = new (string Name, double PxX, double PxY)[]
        {
            ("dau_treo_dau_trang", pm.X(org.X) - 90 * mm, pm.Y(rows[1].Y) + 2 * mm),
            ("canh_chu_ky", pm.X(org.X + 4 * 36000), pm.Y(org.Y + 1 * 36000)),
            ("giua_trang", pm.X(org.X) - 40 * mm, pm.Y(rows[rows.Count / 2].Y) + 1 * mm),
        };
        foreach (var (name, pxX, pxY) in cases)
        {
            // app: thả tại (pxX, pxY) → neo lại vào ô dưới góc trên-trái, lệch làm tròn xuống 0.5 mm
            var (cell, offX, offY) = CqWriter.CellAt(draft, map, pm.EmuX(pxX), pm.EmuY(pxY));
            double Fl(double v) => Math.Floor(v / 36000 * 2) / 2;
            var ov = new CqOverlay(sq, Cell: cell, DxMm: Fl(offX), DyMm: Fl(offY), WidthMm: 20);
            var r = CqWriter.OverlayRectEmu(draft, map, ov);
            double shownX = pm.X(r.X), shownY = pm.Y(r.Y), shownW = r.W * pm.DrawPxPerEmu;     // khung hiển thị sau khi neo lại
            var (x, y, w) = FindGreen(Pdf(CqWriter.AddOverlays(draft, map, new[] { ov })), W);
            var line = $"{tid} {name}: ô {cell} lệch {ov.DxMm}/{ov.DyMm} mm · in thật x={x} y={y} w={w}; hiển thị x={shownX:0} y={shownY:0} w={shownW:0}; " +
                       $"sai lệch {(x - shownX) / mm:0.0} mm ngang, {(y - shownY) / mm:0.0} mm dọc";
            report.Add(line);
            Assert.True(Math.Abs(x - shownX) <= 1.5 * mm, line);
            Assert.True(Math.Abs(y - shownY) <= 1.5 * mm, line);
            Assert.True(Math.Abs(w - shownW) <= 0.8 * mm, line);                       // cỡ dấu / chữ ký in đúng như khung (ô đo tỉ lệ ảnh)
            Assert.True(Math.Abs(shownY - pxY) <= 0.8 * mm && Math.Abs(shownX - pxX) <= 0.8 * mm, line + " (neo lại làm dịch chỗ thả)");
        }
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "ui"));
        File.AppendAllLines(Path.Combine(AppContext.BaseDirectory, "ui", "hieu_chinh_vi_tri.txt"), report);
    }
}
