using CqNl.Application;
using CqNl.Infrastructure.Imaging;
using Xunit;

namespace CqNl.Tests;

/// <summary>Yêu cầu 17/11: đưa chữ ký / con dấu từ ngoài vào app (ảnh JPG / PNG nền trắng / PNG trong suốt), lưu file khi file cũ đang mở.</summary>
public class SignatureStampTests
{
    private static byte[] RedStampOnWhite(System.Drawing.Imaging.ImageFormat fmt)
    {
        using var b = new System.Drawing.Bitmap(300, 300);
        using (var g = System.Drawing.Graphics.FromImage(b))
        {
            g.Clear(System.Drawing.Color.FromArgb(246, 244, 240));                                   // giấy hơi ngả màu như ảnh chụp
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 30, 40), 14);
            g.DrawEllipse(pen, 40, 40, 220, 220);
            g.FillEllipse(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, 30, 40)), 130, 130, 40, 40);
        }
        using var ms = new MemoryStream(); b.Save(ms, fmt); return ms.ToArray();
    }

    [Fact]
    public void Stamp_photo_on_white_paper_becomes_transparent_and_keeps_red_ink()
    {
        var jpg = RedStampOnWhite(System.Drawing.Imaging.ImageFormat.Jpeg);
        Assert.False(CqImages.HasTransparency(jpg));
        var png = CqImages.StampTransparent(jpg);
        Assert.True(CqImages.HasTransparency(png));
        using var o = new System.Drawing.Bitmap(new MemoryStream(png));
        Assert.True(o.Width < 300 && o.Width > 200, $"cắt sát con dấu: {o.Width}");
        Assert.True(o.GetPixel(1, 1).A < 20, "góc (nền giấy) phải trong suốt");
        var c = o.GetPixel(o.Width / 2, o.Height / 2);                                               // chấm đỏ giữa con dấu
        Assert.True(c.A > 230 && c.R > 150 && c.G < 90, $"mực dấu giữ màu đỏ, đậm: {c}");
        Assert.Equal(png, SettingsService.PrepareStamp(png));                                        // PNG trong suốt → giữ nguyên
        var blank = new System.Drawing.Bitmap(50, 50); using (var g = System.Drawing.Graphics.FromImage(blank)) g.Clear(System.Drawing.Color.White);
        using var ms = new MemoryStream(); blank.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        Assert.Throws<ArgumentException>(() => CqImages.StampTransparent(ms.ToArray()));
    }

    [Fact]
    public void Admin_sets_stamp_from_jpg_and_replacing_a_signer_keeps_default_signers_working()
    {
        var root = TD.NewDir("sig_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var ws = new Workspace(Path.Combine(root, "local"), store, AppEnv.Paths, "MAY-S", TimeSpan.FromSeconds(10));
        Assert.True(ws.Probe());
        ws.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026"); var s = ws.Login("admin", "Admin@2026");
        ws.Settings!.SetStamp(s, RedStampOnWhite(System.Drawing.Imaging.ImageFormat.Jpeg));
        Assert.True(CqImages.HasTransparency(ws.Settings.Stamp()!));                                 // JPG → con dấu nền trong suốt
        var tp = CqImages.StampTransparent(RedStampOnWhite(System.Drawing.Imaging.ImageFormat.Png));
        ws.Settings.SetStamp(s, tp);
        Assert.Equal(tp, ws.Settings.Stamp());                                                       // PNG trong suốt → lưu nguyên
        Assert.Throws<BusinessException>(() => ws.Settings.SetStamp(s, new byte[] { 1, 2, 3 }));
        var sig = RedStampOnWhite(System.Drawing.Imaging.ImageFormat.Jpeg);
        var a = ws.Settings.AddSigner(s, "Trương Ngọc Hùng", sig, "jpg");
        var c = ws.Settings.AddSigner(s, "Đào Duy Thanh", sig, "jpg");
        ws.Settings.SetDefaults(s, a.Id, c.Id);
        var a2 = ws.Settings.AddSigner(s, "trương ngọc hùng", sig, "jpg");                              // thay chữ ký (trùng tên)
        var st = ws.Settings.Get().Value;
        Assert.Equal(a2.Id, st.DefaultApproverId);
        Assert.Equal(c.Id, st.DefaultCheckerId);
        Assert.False(st.Signers.Single(x => x.Id == a.Id).Active);
        // tài khoản QC không được đổi chữ ký / con dấu
        ws.Auth.SaveUser(s, "qc9", "QC 9", Role.Qc, true, "Tam@123"); ws.Logout();
        var q = ws.Login("qc9", "Tam@123"); ws.Auth.ChangePassword(q, "Tam@123", "Qc9@2026", "Qc9@2026"); ws.Logout(); q = ws.Login("qc9", "Qc9@2026");
        Assert.Throws<PermissionException>(() => ws.Settings.SetStamp(q, tp));
        Assert.Throws<PermissionException>(() => ws.Settings.AddSigner(q, "X", sig, "jpg"));
    }

    [Fact]
    public void Export_writes_new_name_when_old_file_is_open_in_excel()
    {
        var d = TD.NewDir("lock_"); var p = Path.Combine(d, "11-0216 - HTVG - SKD11 - 58-62.xlsx");
        Assert.Equal(p, Workspace.WriteFileSafe(p, new byte[] { 1, 2, 3 }));
        Assert.Equal(p, Workspace.WriteFileSafe(p, new byte[] { 1, 2, 3 }));                          // cùng nội dung → giữ file cũ
        using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None))                 // đang mở trong Excel
        {
            var p2 = Workspace.WriteFileSafe(p, new byte[] { 4, 5, 6 });
            Assert.Equal(Path.Combine(d, "11-0216 - HTVG - SKD11 - 58-62 (2).xlsx"), p2);
            Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(p2));
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(p));
    }
}
