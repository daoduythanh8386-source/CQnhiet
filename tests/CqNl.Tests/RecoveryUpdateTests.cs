using CqNl.Application;
using Xunit;

namespace CqNl.Tests;

/// <summary>Chống dò mật khẩu (tạm khoá), khởi tạo quản trị, phát hành/nhận bản cập nhật qua kho chung. (Đã bỏ "quên mật khẩu quản trị" — 21/11.)</summary>
public class RecoveryUpdateTests
{
    private static (string root, Workspace ws) Setup(string store)
    {
        var root = TD.NewDir("rec_");
        var ws = new Workspace(Path.Combine(root, "local"), store, AppEnv.Paths, "MAY-R", TimeSpan.FromSeconds(10));
        Assert.True(ws.Probe());
        return (root, ws);
    }

    [Fact]
    public void Store_host_detection()
    {
        Assert.True(Workspace.IsStoreHost(@"\\ADMIN-PC\Users\Admin\SynologyDrive\DU LIEU NHIET", "ADMIN-PC"));
        Assert.True(Workspace.IsStoreHost(@"\\admin-pc.local\Users\x", "ADMIN-PC"));
        Assert.True(Workspace.IsStoreHost(@"\\localhost\c$\kho", "MAY-QC1"));
        Assert.False(Workspace.IsStoreHost(@"\\ADMIN-PC\Users\Admin\SynologyDrive\DU LIEU NHIET", "MAY-QC1"));
        Assert.False(Workspace.IsStoreHost(@"\\192.168.1.10\kho", "ADMIN-PC"));
        Assert.True(Workspace.IsStoreHost(Path.GetTempPath(), "BAT-KY"));                     // ổ đĩa cục bộ
        Assert.False(Workspace.IsStoreHost("", "ADMIN-PC"));
    }

    [Fact]
    public void Wrong_password_five_times_locks_account_temporarily_admin_unlocks()
    {
        var store = Path.Combine(TD.NewDir("lock_kho_"), "kho"); Directory.CreateDirectory(store);
        var (_, ws) = Setup(store);
        ws.Auth!.CreateFirstAdmin("admin", "Quản trị", "Cu@123456");
        var s = ws.Login("admin", "Cu@123456");
        ws.Auth.SaveUser(s, "qc1", "QC 1", Role.Qc, true, "Tam@1234");
        var t0 = DateTimeOffset.Now;
        for (var i = 1; i < AuthService.MaxFailedLogins; i++)
            Assert.Contains("còn", Assert.Throws<BusinessException>(() => ws.Auth.Login("qc1", "SaiMatKhau1", t0)).Message);
        var ex = Assert.Throws<BusinessException>(() => ws.Auth.Login("qc1", "SaiMatKhau1", t0));
        Assert.Contains("TẠM KHOÁ", ex.Message);
        // đang khoá: mật khẩu ĐÚNG cũng bị từ chối
        Assert.Contains("TẠM KHOÁ", Assert.Throws<BusinessException>(() => ws.Auth.Login("qc1", "Tam@1234", t0.AddMinutes(1))).Message);
        // hết thời gian khoá → đăng nhập được, bộ đếm về 0
        var q = ws.Auth.Login("qc1", "Tam@1234", t0 + AuthService.LockoutTime + TimeSpan.FromMinutes(1));
        ws.Auth.Logout(q);
        Assert.Equal(0, ws.Auth.Users().Single(u => u.Username == "qc1").FailedLogins);
        Assert.Null(ws.Auth.Users().Single(u => u.Username == "qc1").LockedUntil);
        // khoá lại rồi quản trị bấm Lưu → mở ngay
        for (var i = 0; i < AuthService.MaxFailedLogins; i++) Assert.Throws<BusinessException>(() => ws.Auth.Login("qc1", "SaiMatKhau1"));
        Assert.NotNull(ws.Auth.Users().Single(u => u.Username == "qc1").LockedUntil);
        ws.Auth.SaveUser(s, "qc1", "QC 1", Role.Qc, true, null);
        Assert.Null(ws.Auth.Users().Single(u => u.Username == "qc1").LockedUntil);
        Assert.Contains(ws.Audit!.Read(), n => (string?)n["action"] == "login.lockout");
    }

    [Fact]
    public void Reinstall_never_creates_admin_unknown_user_is_told_to_contact_admin()
    {
        var store = Path.Combine(TD.NewDir("init_kho_"), "kho"); Directory.CreateDirectory(store);
        var (_, ws) = Setup(store);
        Assert.Throws<BusinessException>(() => ws.InitFirstAdmin("admin", "Quản trị", "Admin@2026", "Khac@2026"));
        ws.InitFirstAdmin("admin", "Quản trị", "Admin@2026", "Admin@2026");          // kho trống + máy chứa kho → được
        // "cài lại app" trên máy khác / cùng máy: kho đã có tài khoản → không tạo quản trị mới
        var (_, again) = Setup(store);
        var ex = Assert.Throws<BusinessException>(() => again.InitFirstAdmin("admin2", "Khác", "Admin@2026", "Admin@2026"));
        Assert.Contains("đã có tài khoản", ex.Message);
        Assert.Single(again.Auth!.Users());
        // tài khoản chưa có → báo liên hệ quản trị
        var ex2 = Assert.Throws<BusinessException>(() => again.Login("qc9", "Abc@12345"));
        Assert.Contains("liên hệ quản trị", ex2.Message);
        Assert.Contains("qc9", ex2.Message);
        Assert.Contains(again.Audit!.Read(), n => (string?)n["action"] == "login.unknown");
        var ex3 = Assert.Throws<BusinessException>(() => again.Login("admin", "SaiMatKhau"));
        Assert.StartsWith("Sai mật khẩu", ex3.Message);
        again.Login("admin", "Admin@2026");
    }

    [Fact]
    public void Admin_publishes_release_other_machines_see_newer_version_and_download_verified_copy()
    {
        var store = Path.Combine(TD.NewDir("upd_kho_"), "kho"); Directory.CreateDirectory(store);
        var (root, ws) = Setup(store);
        ws.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        var s = ws.Login("admin", "Admin@2026");
        ws.Auth.SaveUser(s, "qc1", "QC 1", Role.Qc, true, "Tam@123");
        // file giả lập bộ cài (đầu "MZ"); phiên bản đọc từ tên file
        var setup = Path.Combine(root, "CQNhietLuyen-Setup-9.4.1-x64.exe");
        var bytes = new byte[300_000]; new Random(5).NextBytes(bytes); bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        File.WriteAllBytes(setup, bytes);
        Assert.Equal(new Version(9, 4, 1), Workspace.SetupVersion(setup));
        var bad = Path.Combine(root, "ghi_chu.exe"); File.WriteAllBytes(bad, bytes);
        Assert.Throws<BusinessException>(() => ws.PublishRelease(bad, ""));                                    // không phải bộ cài CQ
        var notMz = Path.Combine(root, "CQNhietLuyen-Setup-9.4.2-x64.exe"); File.WriteAllText(notMz, "abc");
        Assert.Throws<BusinessException>(() => ws.PublishRelease(notMz, ""));                                   // không phải file .exe thật
        Assert.Null(ws.NewerRelease());
        var rel = ws.PublishRelease(setup, "Sửa lỗi in");
        Assert.Equal("9.4.1", rel.Version);
        Assert.Throws<BusinessException>(() => ws.PublishRelease(setup, ""));                                   // trùng phiên bản
        ws.Logout();

        // máy QC: thấy bản mới, tải về đúng nội dung (kiểm SHA-256)
        var (_, qc) = Setup(store);
        var sq = qc.Login("qc1", "Tam@123"); qc.Auth!.ChangePassword(sq, "Tam@123", "Qc1@2026", "Qc1@2026"); qc.Logout(); qc.Login("qc1", "Qc1@2026");
        Assert.Throws<PermissionException>(() => qc.PublishRelease(setup, ""));                               // chỉ quản trị phát hành
        var newer = qc.NewerRelease(new Version(0, 2, 0));
        Assert.NotNull(newer); Assert.Equal("9.4.1", newer!.Version); Assert.Equal("Sửa lỗi in", newer.Notes);
        Assert.Null(qc.NewerRelease(new Version(9, 4, 1)));                                                   // đã là bản mới nhất
        var p = qc.PrepareInstall(newer);
        Assert.Equal(bytes, File.ReadAllBytes(p));
        Assert.Contains(qc.Audit!.Read(), n => (string?)n["action"] == "release.install");
        Assert.True(Workspace.CurrentVersion >= new Version(0, 2, 0));
    }
}
