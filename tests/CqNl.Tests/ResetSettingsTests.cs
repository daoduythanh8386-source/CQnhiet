using CqNl.Application;
using CqNl.Infrastructure.Erp;
using Xunit;

namespace CqNl.Tests;

/// <summary>Yêu cầu 17/11 (trang Cài đặt gọn): Reset dữ liệu (chỉ admin, tự sao lưu trước), đổi tên / bỏ chữ ký, người ký mặc định.</summary>
public class ResetSettingsTests
{
    [Fact]
    public void Reset_backs_up_first_clears_business_data_keeps_settings_and_other_machines_refresh()
    {
        var root = TD.NewDir("reset_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var a = new Workspace(Path.Combine(root, "localA"), store, AppEnv.Paths, "MAY-A", TimeSpan.FromSeconds(10));
        var b = new Workspace(Path.Combine(root, "localB"), store, AppEnv.Paths, "MAY-B", TimeSpan.FromSeconds(10));
        Assert.True(a.Probe());
        a.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        var s = a.Login("admin", "Admin@2026");
        a.Auth.SaveUser(s, "qc1", "QC 1", Role.Qc, true, "Tam@123");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        a.Orders!.Apply(s, a.Orders.Preview(read), "erp.xlsx", _ => false);
        var key = read.Lines[0].Key;
        a.Images!.Add(s, key, a.Orders.Get(key).Version, File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))), "jpg", "Sample");
        a.AddNote(key, "Khách cần ảnh rõ số", false, Array.Empty<(byte[], string, string)>());
        var sigBytes = File.ReadAllBytes(TD.P(Path.Combine("img", "mitu1_47.3.png")));
        var sig = a.Settings!.AddSigner(s, "Người duyệt", sigBytes, "png");
        var sig2 = a.Settings.AddSigner(s, "Người kiểm tra", sigBytes, "png");
        a.Settings.SetDefaults(s, sig.Id, sig2.Id);

        Assert.True(b.Probe());
        var sb = b.Login("qc1", "Tam@123"); b.Auth!.ChangePassword(sb, "Tam@123", "Qc1@2026", "Qc1@2026"); b.Logout(); b.Login("qc1", "Qc1@2026");
        Assert.NotEmpty(b.ListOrders(true));

        // chỉ quản trị; còn người dùng ở máy khác → chặn, chưa xoá gì
        Assert.Throws<PermissionException>(() => b.PlanReset());
        var bkRoot = TD.NewDir("reset_bk_");
        Assert.Contains("qc1 @ MAY-B", a.PlanReset().OtherSessions);
        Assert.Throws<BusinessException>(() => a.ResetData(bkRoot));
        Assert.NotEmpty(a.Repo!.Ids(OrderService.T));
        b.Logout();
        Assert.Throws<BusinessException>(() => a.ResetData(Path.Combine(a.DataRoot, "bk")));          // sao lưu không được nằm trong kho
        Assert.NotEmpty(a.Repo.Ids(OrderService.T));

        var before = a.Repo.Ids(OrderService.T).Count();
        var msgs = new List<string>();
        var r = a.ResetData(bkRoot, new Progress<string>(msgs.Add));
        Assert.True(r.Records >= before);
        Assert.True(r.BlobsDeleted > 0);                                                              // ảnh của đơn
        Assert.Empty(BackupService.Verify(r.BackupDir));                                             // bản sao lưu nguyên vẹn
        Assert.True(Directory.EnumerateFiles(Path.Combine(r.BackupDir, "records", OrderService.T), "*", SearchOption.AllDirectories).Any());
        foreach (var t in Workspace.ResetTypes) Assert.Empty(a.Repo.Ids(t));
        // giữ: tài khoản, cài đặt, chữ ký (ảnh còn đọc được)
        Assert.Contains(a.Auth.Users(), u => u.Username == "qc1");
        var st = a.Settings.Get().Value;
        Assert.Equal(sig.Id, st.DefaultApproverId);
        Assert.NotNull(a.ImageBytes(sig.PaperSha, "png"));
        Assert.Empty(a.ListOrders(true));
        Assert.Contains(a.Audit!.Read(), n => (string?)n["action"] == "data.reset");

        // máy khác kết nối lại → bỏ bản sao cục bộ cũ (cất sang thư mục truoc-reset-…)
        Assert.True(b.Probe());
        b.Login("qc1", "Qc1@2026");
        Assert.Empty(b.ListOrders(true));
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(root, "localB"), "truoc-reset-*"));

        // sau reset: nhập lại ERP được (danh sách file ERP đã nhập cũng được xoá)
        a.Orders.Apply(s, a.Orders.Preview(read), "erp.xlsx", _ => false);
        Assert.NotEmpty(a.Repo.Ids(OrderService.T));

        // đổi tên / bỏ chữ ký
        Assert.Throws<BusinessException>(() => a.Settings.RenameSigner(s, sig.Id, "người kiểm tra"));   // trùng tên
        a.Settings.RenameSigner(s, sig.Id, "Trương Ngọc Hùng");
        Assert.Equal("Trương Ngọc Hùng", a.Settings.Get().Value.Signers.Single(x => x.Id == sig.Id).Name);
        a.Settings.RemoveSigner(s, sig2.Id);
        st = a.Settings.Get().Value;
        Assert.False(st.Signers.Single(x => x.Id == sig2.Id).Active);
        Assert.Null(st.DefaultCheckerId);
        var q = b.Session!;
        Assert.Throws<PermissionException>(() => b.Settings!.RenameSigner(q, sig.Id, "X"));
    }

    [Fact]
    public void Admin_creates_and_deletes_accounts_but_not_self_or_last_admin()
    {
        var root = TD.NewDir("users_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var a = new Workspace(Path.Combine(root, "localA"), store, AppEnv.Paths, "MAY-A", TimeSpan.FromSeconds(10));
        Assert.True(a.Probe());
        a.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        var s = a.Login("admin", "Admin@2026");
        a.Auth.SaveUser(s, "kd1", "Kinh doanh 1", Role.Sales, true, "Tam@123");
        a.Auth.SaveUser(s, "qc9", "QC 9", Role.Qc, true, "Tam@123");
        Assert.Throws<BusinessException>(() => a.Auth.DeleteUser(s, "admin"));                       // chính mình / admin cuối cùng
        Assert.Throws<BusinessException>(() => a.Auth.DeleteUser(s, "khong-co"));
        a.Auth.DeleteUser(s, "kd1");
        Assert.DoesNotContain(a.Auth.Users(), u => u.Username == "kd1");
        Assert.ThrowsAny<Exception>(() => a.Login("kd1", "Tam@123"));                                 // không đăng nhập được nữa
        Assert.Contains(a.Audit!.Read(), n => (string?)n["action"] == "user.delete");
        a.Auth.SaveUser(s, "kd1", "Kinh doanh mới", Role.Sales, true, "Moi@1234");                    // tạo lại cùng tên được
        Assert.Equal("Kinh doanh mới", a.Auth.Users().Single(u => u.Username == "kd1").FullName);
        // QC không được xoá tài khoản
        var b = new Workspace(Path.Combine(root, "localB"), store, AppEnv.Paths, "MAY-B", TimeSpan.FromSeconds(10));
        Assert.True(b.Probe());
        var q = b.Login("qc9", "Tam@123"); b.Auth!.ChangePassword(q, "Tam@123", "Qc9@2026", "Qc9@2026"); b.Logout(); q = b.Login("qc9", "Qc9@2026");
        Assert.Throws<PermissionException>(() => b.Auth.DeleteUser(q, "kd1"));
    }
}
