using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Storage;
using Xunit;

namespace CqNl.Tests;

/// <summary>Làm việc offline / đồng bộ / xung đột ở tầng Workspace (giao diện gọi tầng này). Mất kết nối mô phỏng bằng cách đổi tên thư mục kho.</summary>
public class WorkspaceTests
{
    private static (string root, string store, Workspace a, Workspace b, string key) Setup()
    {
        var root = TD.NewDir("ws_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var a = new Workspace(Path.Combine(root, "localA"), store, AppEnv.Paths, "MAY-A", TimeSpan.FromSeconds(10));
        var b = new Workspace(Path.Combine(root, "localB"), store, AppEnv.Paths, "MAY-B", TimeSpan.FromSeconds(10));
        Assert.True(a.Probe());
        a.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        var s = a.Login("admin", "Admin@2026");
        a.Auth.SaveUser(s, "qc1", "QC 1", Role.Qc, true, "Tam@123");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        a.Orders!.Apply(s, a.Orders.Preview(read), "erp.xlsx", _ => false);
        Assert.True(b.Probe());
        var sb = b.Login("qc1", "Tam@123");
        b.Auth!.ChangePassword(sb, "Tam@123", "Qc1@2026", "Qc1@2026");
        b.Logout();
        b.Login("qc1", "Qc1@2026");                                  // lần online → lưu hash để đăng nhập offline
        return (root, store, a, b, read.Lines[0].Key);
    }

    private static void Offline(string store) => Directory.Move(store, store + "_mat");
    private static void Online(string store) => Directory.Move(store + "_mat", store);

    [Fact]
    public void Offline_draft_syncs_when_store_returns()
    {
        var (_, store, a, b, key) = Setup();
        var rows = b.ListOrders(); Assert.NotEmpty(rows);                  // tạo bản sao cục bộ
        var v0 = b.GetOrder(key)!.Version;
        Offline(store);
        Assert.False(b.Probe());
        Assert.Contains("kho chung", b.OfflineReason);
        // offline: danh sách lấy từ bản sao; sửa QC → nháp outbox (không ghi đâu khác)
        Assert.Equal(rows.Count, b.ListOrders().Count);
        b.SaveHardness(key, v0, new[] { "59", "60.5" }, h => h.CheckDate = "13/11/2026");
        var r1 = b.GetOrder(key)!;
        Assert.True(r1.Draft);
        b.EditQc(key, r1.Version, q => q.Size = "100x50", "size");         // sửa tiếp: gộp vào cùng 1 nháp (không tự xung đột)
        Assert.Single(b.Outbox.Items());
        Assert.Throws<BusinessException>(() => b.CreateCq(key));           // tạo CQ cần kho
        Assert.Throws<BusinessException>(() => b.AddImage(key, v0, new byte[] { 1 }, "jpg", "Hardness"));
        Assert.False(Directory.Exists(store));                              // không âm thầm tạo kho khác
        // đăng nhập offline: chỉ tài khoản đã từng đăng nhập online trên máy này
        b.Logout();
        Assert.Throws<BusinessException>(() => b.Login("admin", "Admin@2026"));
        Assert.Throws<BusinessException>(() => b.Login("qc1", "sai"));
        var so = b.Login("qc1", "Qc1@2026");
        Assert.True(so.OfflineLogin);
        // kho trở lại → Tick tự đồng bộ, đọc lại khớp hash, xóa khỏi outbox
        Online(store);
        var st = b.Tick();
        Assert.True(st.Online); Assert.Equal(0, st.Pending); Assert.Equal(0, st.Conflicts);
        Assert.False(b.Session!.OfflineLogin);
        var (o, v) = a.Orders!.Get(key);
        Assert.Equal(new[] { "59", "60.5" }, o!.Qc.Hardness.Samples); Assert.Equal("100x50", o.Qc.Size); Assert.Equal(v0 + 1, v);
        Assert.Contains(a.Audit!.Read(), n => (string?)n["action"] == "sync");
        Assert.Contains(a.Audit!.Read(), n => (string?)n["action"] == "login.offline_resumed");
    }

    [Fact]
    public void Offline_edit_conflicting_with_other_machine_keeps_both()
    {
        var (_, store, a, b, key) = Setup();
        b.ListOrders();
        var v0 = b.GetOrder(key)!.Version;
        Offline(store);
        b.Probe();
        b.EditQc(key, v0, q => q.PartNo = "CUA-B", "offline");
        Online(store);
        a.Probe();
        a.EditQc(key, a.GetOrder(key)!.Version, q => q.PartNo = "CUA-A", "online");   // máy A sửa trong lúc B offline
        var st = b.Tick();
        Assert.Equal(1, st.Conflicts);
        Assert.Equal("CUA-A", a.Orders!.Get(key).Order!.Qc.PartNo);                      // không ghi đè bản của A
        var cf = b.Conflicts().Single();
        Assert.Equal("CUA-B", cf.Mine!.Qc.PartNo); Assert.Equal("CUA-A", cf.Latest!.Qc.PartNo);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(store, AppInfo.DataFolder, "conflicts"), "*", SearchOption.AllDirectories));
        Assert.Throws<BusinessException>(() => b.CreateCq(key));                         // còn xung đột → không phát hành CQ
        b.ResolveConflict(cf.Item.OpId, keepMine: true);                                 // người dùng chọn rõ ràng
        Assert.Equal("CUA-B", a.Orders.Get(key).Order!.Qc.PartNo);
        Assert.Empty(b.Outbox.Items());
        Assert.Contains(a.Audit!.Read(), n => (string?)n["action"] == "conflict.keep_mine");
    }

    [Fact]
    public void Order_lock_one_editor_and_hardness_change_requires_reconfirm()
    {
        var (_, _, a, b, key) = Setup();
        Assert.Null(a.LockOrder(key));
        Assert.Contains("admin", b.LockOrder(key));                                       // máy B: chỉ đọc
        a.ReleaseOrder(key);
        Assert.Null(b.LockOrder(key));
        var r = a.GetOrder(key)!;
        r = a.EditQc(key, r.Version, q => { q.HardnessRequirement = "58-62"; q.TemplateId = "carbon"; }, "t");
        r = a.SaveHardness(key, r.Version, new[] { "59" }, h => h.CheckDate = "13/11/2026");
        var (r2, ev) = a.ConfirmHardness(key, r.Version);
        Assert.Equal(CqNl.Domain.EvaluationStatus.Pass, ev.Status);
        Assert.NotNull(r2.Order.Qc.Hardness.ConfirmedAt);
        var r3 = a.SaveHardness(key, r2.Version, new[] { "59", "61" }, _ => { });         // đổi điểm đo → bỏ xác nhận
        Assert.Null(r3.Order.Qc.Hardness.ConfirmedAt);
        Assert.Throws<BusinessException>(() => a.SaveHardness(key, r3.Version, new[] { "59 x" }, _ => { }));
    }
}
