using System.Text;
using System.Text.Json.Nodes;
using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Storage;
using Xunit;

namespace CqNl.Tests;

/// <summary>
/// Yêu cầu 25/11: mỗi máy chạy ĐỘC LẬP trên bản đồng bộ Synology Drive của thư mục DU LIEU NHIET (NAS edoc.oristar.vn, qua internet) —
/// ADMIN-PC tắt nguồn vẫn dùng được. Mô phỏng Synology Drive bằng hai thư mục riêng (máy A, máy B) + hàm đồng bộ hai chiều có độ trễ:
/// chép file mới qua lại; cùng tên khác nội dung → giữ bản "lên NAS trước", bản kia đổi tên "(Conflict)" như Synology (rename_conflict).
/// </summary>
public class SyncStoreTests
{
    /// <summary>Đồng bộ kiểu Synology Drive giữa 2 thư mục. Bên <paramref name="first"/> coi như đẩy lên NAS trước.</summary>
    private static void Sync(string first, string second)
    {
        static IEnumerable<string> Files(string root) => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f))
                .Where(r => !Path.GetFileName(r).StartsWith('.') && !r.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))   // bộ lọc Synology ADMIN-PC
            : Enumerable.Empty<string>();
        foreach (var rel in Files(first).ToList())
        {
            var a = Path.Combine(first, rel); var b = Path.Combine(second, rel);
            if (!File.Exists(b)) { Directory.CreateDirectory(Path.GetDirectoryName(b)!); File.Copy(a, b); continue; }
            if (File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b))) continue;
            // xung đột cùng tên: bản của "first" thắng; bản của "second" đổi tên Conflict ở CẢ hai bên
            var conflict = Path.Combine(Path.GetDirectoryName(rel)!, Path.GetFileNameWithoutExtension(rel) + " (MAY-B Conflict)" + Path.GetExtension(rel));
            File.Copy(b, Path.Combine(second, conflict), true); File.Copy(b, Path.Combine(first, conflict), true);
            File.Copy(a, b, true);
        }
        foreach (var rel in Files(second).ToList())
        {
            var b = Path.Combine(second, rel); var a = Path.Combine(first, rel);
            if (!File.Exists(a)) { Directory.CreateDirectory(Path.GetDirectoryName(a)!); File.Copy(b, a); }
        }
    }

    /// <summary>Hai máy, mỗi máy một thư mục đồng bộ (đường dẫn có "SynologyDrive" như thật). Kho có sẵn dữ liệu (đã khởi tạo trên máy A).</summary>
    private static (string KhoA, string KhoB, Workspace A, Workspace B, ErpReadResult Read) TwoMachines()
    {
        var root = TD.NewDir("sync_");
        var khoA = Path.Combine(root, "A", "SynologyDrive", "DU LIEU NHIET"); var khoB = Path.Combine(root, "B", "SynologyDrive", "DU LIEU NHIET");
        Directory.CreateDirectory(khoA); Directory.CreateDirectory(khoB);
        var a = new Workspace(Path.Combine(root, "localA"), khoA, AppEnv.Paths, "MAY-A", TimeSpan.FromSeconds(10)) { AllowNewSyncStore = true };  // lập kho lần đầu
        Assert.True(a.Probe());
        a.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026"); var s = a.Login("admin", "Admin@2026");
        a.Auth.SaveUser(s, "qc1", "QC Một", Role.Qc, true, "Tam@1234");
        a.Settings!.SetSteelTable(s, File.ReadAllBytes(TD.P("steel_grades.xlsx")), "steel_grades.xlsx");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        a.Orders!.Apply(s, a.Orders.Preview(read), "erp.xlsx", _ => false);
        a.Logout();
        // máy B chưa đồng bộ xong → KHÔNG tạo kho rỗng, không cho tạo quản trị mới
        var b0 = new Workspace(Path.Combine(root, "localB0"), khoB, AppEnv.Paths, "MAY-B", TimeSpan.FromSeconds(10));
        Assert.False(b0.Probe());
        Assert.Contains("CHƯA ĐỒNG BỘ XONG", b0.OfflineReason);
        Assert.False(Directory.Exists(Path.Combine(khoB, "CQNL")));
        Sync(khoA, khoB);                                                                    // Synology Drive tải kho về máy B
        var b = new Workspace(Path.Combine(root, "localB"), khoB, AppEnv.Paths, "MAY-B", TimeSpan.FromSeconds(10));
        Assert.True(b.Probe());
        return (khoA, khoB, a, b, read);
    }

    [Fact]
    public void Machine_b_works_alone_on_its_synced_copy_while_admin_pc_is_off_and_changes_reach_a()
    {
        var (khoA, khoB, a, b, read) = TwoMachines();
        // ADMIN-PC (máy A) "tắt": không dùng workspace A nữa. Máy B đăng nhập đúng tài khoản đã tạo ở A → dùng được ngay.
        var q = b.Login("qc1", "Tam@1234"); b.Auth!.ChangePassword(q, "Tam@1234", "Qc1@20260", "Qc1@20260"); b.Logout(); b.Login("qc1", "Qc1@20260");
        Assert.True(AppInfo.IsSyncFolder(b.StorePath));
        Assert.Equal(CheckLevel.Ok, Workspace.StoreLocationCheck(b.StorePath).Level);
        var key = read.Lines[0].Key;
        var rows = b.ListOrders(true);
        Assert.Contains(rows, r => r.Order.Key == key);
        b.EditQc(key, b.GetOrder(key)!.Version, qd => { qd.HardnessRequirement = "40-50"; qd.TemplateId = "carbon"; qd.QuenchTemps = new() { "650℃", "1030℃" }; qd.RamValues = new() { "150p", "180p", "200p" }; }, "t");
        b.SaveHardness(key, b.GetOrder(key)!.Version, new[] { "45", "46.5" }, h => h.CheckDate = "25/11/2026");
        var (cq, _) = b.PrepareCq(key);                                                     // cấp số + tạo CQ nháp trên máy B
        Assert.Equal(CqState.Draft, cq.State);
        Assert.NotNull(cq.ReportClaim);
        b.Cq!.EnsureReportNumberOwned(cq);                                                   // số là của CQ này
        // Synology đồng bộ lên NAS → máy A (bật lại) thấy đúng dữ liệu, không lỗi
        Sync(khoB, khoA);
        var a2 = new Workspace(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(khoA))!)!, "localA2"), khoA, AppEnv.Paths, "MAY-A", TimeSpan.FromSeconds(10));
        Assert.True(a2.Probe()); a2.Login("admin", "Admin@2026");
        var o = a2.GetOrder(key)!.Order;
        Assert.Equal("40-50", o.Qc.HardnessRequirement);
        Assert.Contains(a2.Cq!.ForOrder(key), c => c.ReportNo == cq.ReportNo && c.CreatedBy == "qc1");
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(khoA, "CQNL", "records"), "*Conflict*", SearchOption.AllDirectories));   // A tắt → không có xung đột
    }

    [Fact]
    public void Same_order_edited_on_two_machines_before_sync_converges_and_loser_gets_conflict_to_resolve()
    {
        var (khoA, khoB, a, b, read) = TwoMachines();
        a.Login("admin", "Admin@2026");
        var q = b.Login("qc1", "Tam@1234"); b.Auth!.ChangePassword(q, "Tam@1234", "Qc1@20260", "Qc1@20260"); b.Logout(); b.Login("qc1", "Qc1@20260");
        var key = read.Lines[1].Key;
        var va = a.GetOrder(key)!.Version; var vb = b.GetOrder(key)!.Version;
        Assert.Equal(va, vb);
        // cùng lúc, chưa đồng bộ: A ghi yêu cầu 40-50, B ghi 55-60 (cùng version gốc)
        a.EditQc(key, va, qd => qd.HardnessRequirement = "40-50", "A sửa");
        Thread.Sleep(30);
        b.EditQc(key, vb, qd => qd.HardnessRequirement = "55-60", "B sửa");
        Sync(khoA, khoB);
        // hai máy chọn CÙNG một bản thắng (ghi sớm hơn = A); bản của B vẫn còn trong kho
        var ra = a.Store!.Read("order", key)!; var rb = b.Store!.Read("order", key)!;
        Assert.Equal(ra.Meta.OpId, rb.Meta.OpId);
        Assert.Equal("40-50", ra.Data["Qc"]!["HardnessRequirement"]!.ToString());
        Assert.Equal(2, b.Store.Candidates("order", key, ra.Meta.Version).Count);
        // máy B (bị thua) được báo XUNG ĐỘT với đúng bản của mình; máy A không bị báo
        Assert.Equal(1, b.CheckLostWrites("order", FileStore.SafeId(key)));
        Assert.Equal(0, b.CheckLostWrites("order", FileStore.SafeId(key)));                // chỉ báo 1 lần
        Assert.Equal(0, a.CheckLostWrites("order", FileStore.SafeId(key)));
        var c = Assert.Single(b.Conflicts());
        Assert.Equal("55-60", c.Mine!.Qc.HardnessRequirement);
        Assert.Equal("40-50", c.Latest!.Qc.HardnessRequirement);
        // người dùng chọn giữ bản của mình → thành bản mới nhất, đồng bộ sang A
        b.ResolveConflict(c.Item.OpId, keepMine: true);
        Sync(khoB, khoA);
        Assert.Equal("55-60", a.Store.Read("order", key)!.Data["Qc"]!["HardnessRequirement"]!.ToString());
        Assert.Equal(0, a.CheckLostWrites("order", FileStore.SafeId(key)));
        Assert.Empty(b.Conflicts());
    }

    [Fact]
    public void Same_report_number_issued_on_two_machines_is_detected_and_only_one_cq_may_use_it()
    {
        var root = TD.NewDir("sync_no_");
        var khoA = Path.Combine(root, "A", "SynologyDrive", "DU LIEU NHIET", "CQNL"); var khoB = Path.Combine(root, "B", "SynologyDrive", "DU LIEU NHIET", "CQNL");
        var na = new ReportNumberAllocator(new FileStore(khoA, "MAY-A")); var nb = new ReportNumberAllocator(new FileStore(khoB, "MAY-B"));
        var (ta, tokA) = na.ClaimWithToken(2026, 11, "don-A");
        var (tb, tokB) = nb.ClaimWithToken(2026, 11, "don-B");
        Assert.Equal("001/11", ta); Assert.Equal("001/11", tb);                             // chưa đồng bộ kịp → cùng số
        Sync(khoA, khoB);
        var oa = na.Owner(2026, 11, 1)!; var ob = nb.Owner(2026, 11, 1)!;
        Assert.Equal(oa.Token, ob.Token);                                                   // mọi máy thấy cùng một chủ số
        Assert.Equal(tokA, oa.Token);
        Assert.NotEqual(tokB, ob.Token);                                                    // CQ của B phải đổi số trước khi duyệt
        // số tiếp theo không bao giờ dùng lại số đã cấp (kể cả bản bị đổi tên "Conflict")
        Assert.Equal("002/11", nb.Claim(2026, 11, "don-C"));
        // kiểm ở tầng CQ: CQ mang mã giữ số thua bị chặn phát hành
        var env = new AppEnv(Path.Combine(root, "env"), "MAY-B");
        var claimDir = Path.Combine(env.Store.Root, "counters", "reportno", "2026-11"); Directory.CreateDirectory(claimDir);
        File.Copy(Path.Combine(khoB, "counters", "reportno", "2026-11", "001.json"), Path.Combine(claimDir, "001.json"));
        var mine = new CqVersionRecord { OrderKey = "don-B", Ver = 1, ReportNo = "001/11", ReportClaim = tokB, ReportClaimMonth = "2026-11" };
        var ex = Assert.Throws<BusinessException>(() => env.Cq.EnsureReportNumberOwned(mine));
        Assert.Contains("Đổi số báo cáo", ex.Message);
        env.Cq.EnsureReportNumberOwned(new CqVersionRecord { OrderKey = "don-A", Ver = 1, ReportNo = "001/11", ReportClaim = tokA, ReportClaimMonth = "2026-11" });
        env.Cq.EnsureReportNumberOwned(new CqVersionRecord { OrderKey = "cu", Ver = 1, ReportNo = "001/11" });   // CQ cũ (trước 0.9.2) không kiểm
    }

    [Fact]
    public void Old_store_files_still_read_and_new_versions_use_machine_unique_names()
    {
        var root = TD.NewDir("legacy_");
        var st = new FileStore(root, "ADMIN-PC");
        // bản ghi kiểu cũ (v000001.json, trước 0.9.2)
        var data = JsonNode.Parse("{\"Key\":\"K1\",\"X\":1}")!;
        var canon = FileStore.Canonical(data);
        var meta = new RecordMeta(1, FileStore.Sha256Hex(Encoding.UTF8.GetBytes(canon)), "admin", "ADMIN-PC", DateTimeOffset.Now, "op-cu", 0);
        var d = Path.Combine(root, "records", "order", "K1"); Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "v000001.json"), new JsonObject { ["meta"] = System.Text.Json.JsonSerializer.SerializeToNode(meta), ["data"] = JsonNode.Parse(canon) }.ToJsonString());
        Assert.Equal(1, st.Read("order", "K1")!.Meta.Version);
        var v2 = st.Commit("order", "K1", JsonNode.Parse("{\"Key\":\"K1\",\"X\":2}")!, 1, "qc", "op-moi-123");
        Assert.Equal(2, v2);
        var f = Assert.Single(Directory.EnumerateFiles(d, "v000002*"));
        Assert.EndsWith("v000002.json", f);
        Assert.Equal(meta.Sha256, st.Read("order", "K1")!.Meta.BaseSha);
        Assert.Empty(st.LostWrites("order", "K1", "ADMIN-PC"));
        Assert.Throws<ConflictException>(() => st.Commit("order", "K1", data, 1, "qc", "op-khac"));   // CAS vẫn đúng
        Assert.Equal(2, st.Commit("order", "K1", JsonNode.Parse("{\"Key\":\"K1\",\"X\":2}")!, 1, "qc", "op-moi-123"));   // idempotent theo opId
        // bản "Conflict" do Synology đổi tên vẫn là ứng viên của CÙNG version (không sinh version mới, không đọc nhầm)
        File.Copy(f, Path.Combine(d, "v000002_MAY-B_Nov-25-0900-2026_Conflict.json"));
        Assert.Equal(new[] { 1, 2 }, st.Versions("order", "K1"));
        Assert.Equal(2, st.Candidates("order", "K1", 2).Count);
        Assert.Equal("op-moi-123", st.Read("order", "K1")!.Meta.OpId);
        File.WriteAllText(Path.Combine(d, "vflag.json"), "{}");                                   // file lạ: bỏ qua
        Assert.Equal(new[] { 1, 2 }, st.Versions("order", "K1"));
    }

    [Fact]
    public void Shared_folders_are_resolved_per_machine_inside_the_synced_store()
    {
        var root = TD.NewDir("fold_");
        var kho = Path.Combine(root, "B", "SynologyDrive", "DU LIEU NHIET"); Directory.CreateDirectory(Path.Combine(kho, "File CQ"));
        var w = new Workspace(Path.Combine(root, "l"), kho, AppEnv.Paths, "MAY-B");
        Assert.Equal(Path.Combine(kho, "File CQ"), w.ResolveFolder(@"{KHO}\File CQ"));
        Assert.Equal(Path.Combine(kho, "ERP"), w.ResolveFolder(@"C:\Users\NguoiKhac\SynologyDrive\DU LIEU NHIET\ERP"));   // đường dẫn của máy khác (máy này không có)
        Assert.Equal(Path.Combine(kho, "File CQ"), w.ResolveFolder(@"\\MAY-CU-KHONG-CO\Users\Admin\SynologyDrive\DU LIEU NHIET\File CQ"));
        Assert.Equal(@"\\NAS\khac", w.ResolveFolder(@"\\NAS\khac"));
        Assert.Equal(@"{KHO}\File CQ", w.FolderToken(Path.Combine(kho, "File CQ")));
        Assert.Equal(Path.Combine(root, "ngoai"), w.FolderToken(Path.Combine(root, "ngoai")));
        Assert.Equal(@"{KHO}\File CQ\11-2026", w.FolderToken(Path.Combine(kho, "File CQ", "11-2026")));
        Assert.Equal(System.Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\SynologyDrive\DU LIEU NHIET"),
            new Workspace(Path.Combine(root, "l2"), AppInfo.DefaultStore, AppEnv.Paths).StorePath);
        Assert.True(AppInfo.IsSyncFolder(kho)); Assert.False(AppInfo.IsSyncFolder(AppInfo.LegacyStore));
    }
}
