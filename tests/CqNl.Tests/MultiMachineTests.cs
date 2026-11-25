using CqNl.Application;
using Xunit;

namespace CqNl.Tests;

/// <summary>Yêu cầu 17/11: nhiều máy dùng chung kho — thư mục lưu CQ chung, "Kiểm tra máy này", cảnh báo vị trí kho.</summary>
public class MultiMachineTests
{
    [Fact]
    public void Store_location_check_flags_local_and_synced_paths()
    {
        Assert.Equal(CheckLevel.Ok, Workspace.StoreLocationCheck(@"\\TEN-NAS\DU LIEU NHIET").Level);
        Assert.Equal(CheckLevel.Ok, Workspace.StoreLocationCheck(@"\\ADMIN-PC\Users\Admin\SynologyDrive\DU LIEU NHIET").Level);   // kho trên ADMIN-PC (máy bật nguồn là được)
        var local = Workspace.StoreLocationCheck(@"C:\Users\Admin\SynologyDrive\DU LIEU NHIET");   // 0.9.2: bản đồng bộ trên từng máy = chạy độc lập
        Assert.Equal(CheckLevel.Ok, local.Level);
        Assert.Contains("ADMIN-PC tắt vẫn dùng được", local.Detail);
        Assert.Equal(CheckLevel.Fail, Workspace.StoreLocationCheck(@"D:\KHO").Level);
    }

    [Fact]
    public void Shared_cq_folder_reaches_other_machine_and_machine_check_reports_what_is_missing()
    {
        var root = TD.NewDir("multi_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var shared = Path.Combine(root, "CQ CHUNG"); Directory.CreateDirectory(shared);
        var a = new Workspace(Path.Combine(root, "localA"), store, AppEnv.Paths, "MAY-A", TimeSpan.FromSeconds(10));
        Assert.True(a.Probe());
        a.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026"); var s = a.Login("admin", "Admin@2026");
        a.Auth.SaveUser(s, "qc5", "QC 5", Role.Qc, true, "Tam@123");
        // thư mục không ghi được (đường dẫn là một FILE) → báo lỗi rõ, không lưu
        var notDir = Path.Combine(root, "file.txt"); File.WriteAllText(notDir, "x");
        Assert.Throws<BusinessException>(() => a.Settings!.SetCqFolder(s, notDir));
        a.Settings!.SetCqFolder(s, shared);
        Assert.Equal(shared, a.Settings.Get().Value.CqFolder);
        Assert.Empty(Directory.GetFiles(shared));                                                    // file thử đã xoá

        // máy B (QC) — cấu hình máy mới tinh: tự nhận thư mục lưu CQ chung
        var b = new Workspace(Path.Combine(root, "localB"), store, AppEnv.Paths, "MAY-B", TimeSpan.FromSeconds(10));
        Assert.True(b.Probe());
        var q = b.Login("qc5", "Tam@123"); b.Auth!.ChangePassword(q, "Tam@123", "Qc5@2026", "Qc5@2026"); b.Logout(); q = b.Login("qc5", "Qc5@2026");
        Assert.Throws<PermissionException>(() => b.Settings!.SetCqFolder(q, shared));                 // chỉ quản trị đặt
        var cfg = new LocalConfig();
        Assert.True(b.SyncSharedCqFolder(cfg));
        Assert.Equal(shared, cfg.PdfRoot);
        Assert.Equal("thư mục chung", cfg.PdfRootSource);
        Assert.False(b.SyncSharedCqFolder(cfg));                                                     // không đổi → không ghi lại
        cfg.PdfFolder = Path.Combine(root, "rieng");                                                  // máy chọn riêng → ưu tiên
        Assert.Equal(cfg.PdfFolder, cfg.PdfRoot);
        Assert.Equal("riêng máy này", cfg.PdfRootSource);
        cfg.PdfFolder = null;

        var items = b.CheckMachine(cfg, (true, "Microsoft Excel 16.0"), new[] { "HP LaserJet" });
        CheckLevel L(string name) => items.Single(i => i.Name.StartsWith(name)).Level;
        Assert.Equal(CheckLevel.Ok, L("Kho chung"));
        Assert.Equal(CheckLevel.Fail, L("Vị trí kho chung"));                                         // kho thử là ổ cục bộ
        Assert.Equal(CheckLevel.Ok, L("Microsoft Excel"));
        Assert.Equal(CheckLevel.Ok, L("Thư mục lưu file CQ"));
        Assert.Equal(CheckLevel.Ok, L("Máy in"));
        Assert.Equal(CheckLevel.Warn, L("Thư mục ERP theo dõi"));                                     // chưa đặt: vẫn nhập tay được
        Assert.Equal(CheckLevel.Fail, L("Chữ ký và con dấu"));                                        // kho mới chưa có chữ ký / dấu
        Assert.Equal(CheckLevel.Warn, L("Người duyệt / kiểm tra mặc định"));
        Assert.Equal(CheckLevel.Warn, L("Phiên bản ứng dụng"));
        Assert.Equal(CheckLevel.Warn, L("Đường dẫn dùng chung"));                                     // thư mục lưu CQ chung là ổ cục bộ
        Assert.Null(b.SharedPathFor(shared));                                                        // kho không phải đường dẫn mạng → không tự đổi
        Assert.Null(b.SharedPathFor(@"\\NAS\CQ"));                                       // chưa phát hành bản nào
        Assert.All(items.Where(i => i.Level != CheckLevel.Ok), i => Assert.False(string.IsNullOrWhiteSpace(i.Fix), "mục chưa đạt phải có cách sửa: " + i.Name));

        // máy thiếu Excel / không có máy in / thư mục lưu không ghi được
        var cfg2 = new LocalConfig { PdfFolder = notDir };
        var bad = b.CheckMachine(cfg2, (false, "Máy chưa cài Microsoft Excel."), Array.Empty<string>());
        Assert.Equal(CheckLevel.Fail, bad.Single(i => i.Name.StartsWith("Microsoft Excel")).Level);
        Assert.Equal(CheckLevel.Fail, bad.Single(i => i.Name.StartsWith("Thư mục lưu file CQ")).Level);
        Assert.Equal(CheckLevel.Warn, bad.Single(i => i.Name.StartsWith("Máy in")).Level);

        // kho mất kết nối → báo LỖI kho, không văng lỗi
        var off = new Workspace(Path.Combine(root, "localC"), Path.Combine(root, "khong-co"), AppEnv.Paths, "MAY-C", TimeSpan.FromSeconds(3));
        var offItems = off.CheckMachine(new LocalConfig { PdfFolder = Path.Combine(root, "luu") }, (true, "Microsoft Excel"), new[] { "P" });
        Assert.Equal(CheckLevel.Fail, offItems.Single(i => i.Name.StartsWith("Kho chung")).Level);
    }

    [Fact]
    public void Installer_store_path_applies_only_when_changed()
    {
        var root = TD.NewDir("inst_"); var store = Path.Combine(root, "kho"); var nas = Path.Combine(root, "kho-moi");
        // bộ cài: đường dẫn kho nhập ở trang "Kho dữ liệu chung" chỉ áp dụng khi khác lần trước
        var cfg = new LocalConfig { StorePath = store };
        Assert.True(cfg.ApplyInstallerStore(nas)); Assert.Equal(nas, cfg.StorePath);
        cfg.StorePath = Path.Combine(root, "doi-trong-app");
        Assert.False(cfg.ApplyInstallerStore(nas)); Assert.Equal(Path.Combine(root, "doi-trong-app"), cfg.StorePath);
        Assert.False(cfg.ApplyInstallerStore("  "));
        var appDir = TD.NewDir("appdir_"); File.WriteAllText(Path.Combine(appDir, "store.txt"), "\uFEFF" + nas + "\r\n", new System.Text.UTF8Encoding(false));
        Assert.Equal(nas, LocalConfig.ReadInstallerStore(appDir));
        Assert.Null(LocalConfig.ReadInstallerStore(TD.NewDir("appdir2_")));
    }
}
