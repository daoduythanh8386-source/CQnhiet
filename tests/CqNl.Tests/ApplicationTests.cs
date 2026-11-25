using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Pdf;
using CqNl.Infrastructure.Storage;
using Xunit;

namespace CqNl.Tests;

/// <summary>Môi trường ứng dụng trên một kho tạm (giả lập 1 máy).</summary>
internal sealed class AppEnv
{
    public readonly FileStore Store; public readonly Repo Repo; public readonly AuditLog Audit; public readonly AuthService Auth;
    public readonly OrderService Orders; public readonly CqService Cq; public readonly SettingsService Settings; public readonly ImageService Images;
    public static AppPaths Paths => new(TD.P(""), TD.P("maps"), TD.P("presets"), TD.P("tessdata"));
    public AppEnv(string root, string machine)
    {
        Store = new FileStore(root, machine); Repo = new Repo(Store); Audit = new AuditLog(Store);
        Auth = new AuthService(Repo, new LeaseLocks(Store), Audit); Orders = new OrderService(Repo, Audit);
        Cq = new CqService(Repo, Audit, new ReportNumberAllocator(Store), Orders, Paths); Settings = new SettingsService(Repo, Audit);
        Images = new ImageService(Repo, Orders, Paths);
    }
    public int Audits(string action) => Audit.Read().Count(a => (string?)a["action"] == action);
}

public class AuthTests
{
    [Fact]
    public void First_admin_login_session_lease_permissions()
    {
        var root = TD.NewDir("app_");
        var a = new AppEnv(root, "MAY-A"); var b = new AppEnv(root, "MAY-B");
        Assert.False(a.Auth.HasAnyUser());
        Assert.Throws<BusinessException>(() => a.Auth.CreateFirstAdmin("admin", "Quản trị", "123"));     // quá ngắn
        a.Auth.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        Assert.Throws<BusinessException>(() => b.Auth.CreateFirstAdmin("x", "x", "Xxxxxx@1"));           // chỉ một lần
        Assert.Throws<BusinessException>(() => a.Auth.Login("admin", "sai-mat-khau"));
        var sa = a.Auth.Login("admin", "Admin@2026");
        Assert.False(sa.MustChangePassword);
        // một phiên / tài khoản: máy B bị chặn khi máy A đang đăng nhập
        var ex = Assert.Throws<BusinessException>(() => b.Auth.Login("admin", "Admin@2026"));
        Assert.Contains("MAY-A", ex.Message);
        Assert.Single(a.Auth.OnlineSessions());
        // phiên quá hạn (máy A tắt đột ngột) → máy B được thu hồi sau TTL + grace
        var later = DateTimeOffset.Now + AuthService.SessionTtl + AuthService.SessionGrace + TimeSpan.FromSeconds(5);
        var sb = b.Auth.Login("admin", "Admin@2026", later);
        Assert.Throws<BusinessException>(() => a.Auth.Heartbeat(sa, later));                             // A biết phiên đã mất
        b.Auth.Logout(sb);
        Assert.Empty(a.Auth.OnlineSessions());
        sa = a.Auth.Login("admin", "Admin@2026");

        // tài khoản QC: mật khẩu mặc định → bắt buộc đổi; mật khẩu mới phải khác mặc định
        a.Auth.SaveUser(sa, "qc1", "Nguyễn QC", Role.Qc, true, "Tam@123");
        var sq = b.Auth.Login("qc1", "Tam@123");
        Assert.True(sq.MustChangePassword);
        Assert.Throws<BusinessException>(() => b.Auth.ChangePassword(sq, "Tam@123", "Tam@123", "Tam@123"));
        Assert.Throws<BusinessException>(() => b.Auth.ChangePassword(sq, "Tam@123", "Moi@2026", "Khac@2026"));
        b.Auth.ChangePassword(sq, "Tam@123", "Moi@2026", "Moi@2026");
        b.Auth.Logout(sq);
        Assert.False(b.Auth.Login("qc1", "Moi@2026").MustChangePassword);

        // phân quyền ở tầng nghiệp vụ (không chỉ giao diện)
        Assert.Throws<PermissionException>(() => a.Auth.SaveUser(sq, "x", "x", Role.Admin, true, "Xxxxxx@1"));   // còn cờ đổi mật khẩu
        var sq2 = b.Auth.Login("qc1", "Moi@2026");
        Assert.Throws<PermissionException>(() => a.Auth.SaveUser(sq2, "x", "x", Role.Admin, true, "Xxxxxx@1"));  // QC không quản lý tài khoản
        a.Auth.SaveUser(sa, "xem", "Người xem", Role.Viewer, true, "Xem@123");
        var sv = a.Auth.Login("xem", "Xem@123");   // cùng máy A, tài khoản khác → được
        Assert.Throws<PermissionException>(() => a.Orders.Apply(sv, new ImportPlan(new List<ErpLine>(), new List<(ErpLine, IReadOnlyDictionary<string, (string?, string?)>)>(), new List<ErpLine>()), "x.xlsx", _ => false));
        Assert.Throws<PermissionException>(() => a.Cq.Delete(sv, "x|v1", "lý do"));

        // không khóa/hạ quyền admin cuối cùng; khóa tài khoản → không đăng nhập được
        Assert.Throws<BusinessException>(() => a.Auth.SaveUser(sa, "admin", "Quản trị", Role.Qc, true, null));
        Assert.Throws<BusinessException>(() => a.Auth.SaveUser(sa, "admin", "Quản trị", Role.Admin, false, null));
        a.Auth.SaveUser(sa, "xem", "Người xem", Role.Viewer, false, null);
        a.Auth.Logout(sv);
        Assert.Throws<BusinessException>(() => a.Auth.Login("xem", "Xem@123"));
        // mật khẩu chỉ lưu dạng băm, không có bản rõ trong kho
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "records", "user"), "*.json", SearchOption.AllDirectories))
            Assert.DoesNotContain("Admin@2026", File.ReadAllText(f));
        Assert.True(a.Audits("login.fail") >= 1); Assert.True(a.Audits("login.denied_other_session") >= 1); Assert.True(a.Audits("user.create") >= 2);
    }
}

public class CqLifecycleTests
{
    private static (AppEnv env, Session s, string key) Setup(string machine = "MAY-A")
    {
        var env = new AppEnv(TD.NewDir("cq_"), machine);
        env.Auth.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        var s = env.Auth.Login("admin", "Admin@2026");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        var plan = env.Orders.Preview(read);
        var (added, _, conflicts) = env.Orders.Apply(s, plan, "erp.xlsx", env.Cq.HasActiveCq);
        Assert.Equal(read.Lines.Count, added); Assert.Equal(0, conflicts);
        return (env, s, read.Lines[0].Key);
    }

    private static void FillQc(AppEnv env, Session s, string key)
    {
        var (_, ver) = env.Orders.Get(key);
        env.Orders.EditQc(s, key, ver, q =>
        {
            q.TemplateId = "carbon"; q.SteelGrade = "SKD11"; q.HardnessRequirement = "58-62"; q.Tolerance = "MIN-1";
            q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" };   // carbon: 3 ô ram trên hình vẽ
            q.Hardness.Samples = new() { "59.5 / 58.2", "57.4", "60" };
            q.Hardness.CheckDate = "13/11/2026";
        }, "test fill");
    }

    [Fact]
    public void Import_keeps_qc_data_and_flags_change_after_cq()
    {
        var (env, s, key) = Setup();
        FillQc(env, s, key);
        // import lại cùng file: không đổi gì, dữ liệu QC giữ nguyên
        var plan = env.Orders.Preview(ErpReader.Read(TD.P("erp.xlsx")));
        Assert.Empty(plan.Add); Assert.Empty(plan.Update);
        var (o, ver) = env.Orders.Get(key);
        // giả lập ERP đổi khách hàng → chỉ trường ERP thay, QC giữ, gắn cờ vì đơn đã có CQ
        var line = ErpReader.Read(TD.P("erp.xlsx")).Lines.First(l => l.Key == key) with { Customer = "Khách mới" };
        var p2 = env.Orders.Preview(new ErpReadResult(new[] { line }, Array.Empty<(int, string?, string)>()));
        Assert.Single(p2.Update);
        env.Orders.Apply(s, p2, "erp2.xlsx", _ => true);
        var (o2, ver2) = env.Orders.Get(key);
        Assert.Equal("Khách mới", o2!.Erp["Customer"]); Assert.Equal("58-62", o2.Qc.HardnessRequirement); Assert.Equal(3, o2.Qc.Hardness.Samples.Count);
        Assert.Contains(o2.Flags, f => f.StartsWith("ERP thay đổi sau CQ"));
        // sửa trên bản cũ → xung đột, không ghi đè
        Assert.Throws<ConflictException>(() => env.Orders.EditQc(s, key, ver, q => q.PartNo = "X", "stale"));
        Assert.True(ver2 > ver);
    }

    [Fact]
    public void Create_requires_confirmation_then_numbers_and_delete_rules()
    {
        var (env, s, key) = Setup();
        FillQc(env, s, key);
        Assert.Throws<BusinessException>(() => env.Cq.Create(s, key));                // chưa xác nhận kết quả
        var ev = env.Orders.ConfirmHardness(s, key, env.Orders.Get(key).Version, null);
        Assert.Equal(CqNl.Domain.EvaluationStatus.Pass, ev.Status);                    // MIN-1 → [57,62], 57.4 đạt
        var nov = new DateTime(2026, 11, 13);
        var c1 = env.Cq.Create(s, key, today: nov);
        Assert.Equal("001/11", c1.ReportNo); Assert.Equal(CqState.Draft, c1.State); Assert.Equal("Pass", c1.Result);
        // xlsx lưu trong kho, ô kết quả sắp tăng dần
        var x = env.Store.GetBlob(c1.XlsxSha, "xlsx");
        Assert.True(x.Length > 1000);
        var c2 = env.Cq.Create(s, key, today: nov);
        Assert.Equal("002/11", c2.ReportNo); Assert.Equal(2, c2.Ver);
        // xóa: bắt buộc lý do; số đã xóa KHÔNG dùng lại
        Assert.Throws<BusinessException>(() => env.Cq.Delete(s, c2.Id, " "));
        env.Cq.Delete(s, c2.Id, "Tạo nhầm");
        Assert.Throws<BusinessException>(() => env.Cq.Delete(s, c2.Id, "lần hai"));
        Assert.Equal("003/11", env.Cq.Create(s, key, today: nov).ReportNo);
        Assert.Equal("001/12", env.Cq.Create(s, key, today: new DateTime(2026, 12, 1)).ReportNo);   // tháng mới về 001
        // số nhập tay trùng → cảnh báo
        Assert.Contains("Trùng số báo cáo", env.Cq.Create(s, key, "001/11", nov).Warnings);
        var del = env.Cq.ForOrder(key).Single(c => c.State == CqState.Deleted);
        Assert.Equal("Tạo nhầm", del.DeleteReason); Assert.Equal("admin", del.DeletedBy); Assert.Equal("Draft", del.StateWhenDeleted);
        env.Cq.MarkPrinted(s, c1.Id, true);
        Assert.True(env.Cq.ForOrder(key).First().Printed);
        Assert.Equal(1, env.Audits("cq.delete")); Assert.Equal(5, env.Audits("cq.create")); Assert.Equal(1, env.Audits("cq.printed"));
    }

    [Fact]
    public void Stage_mismatch_blocks_without_consuming_number()
    {
        var (env, s, key) = Setup();
        FillQc(env, s, key);
        var (_, v) = env.Orders.Get(key);
        env.Orders.EditQc(s, key, v, q => q.QuenchTemps = new() { "650℃", "850℃", "1030℃" }, "3 giai đoạn");   // carbon cần đúng 2
        env.Orders.ConfirmHardness(s, key, env.Orders.Get(key).Version, null);
        Assert.ThrowsAny<Exception>(() => env.Cq.Create(s, key, today: new DateTime(2026, 11, 13)));
        Assert.False(new ReportNumberAllocator(env.Store).IsTaken(2026, 11, 1));
    }

    [Fact]
    public void Unconfirmed_hardness_image_blocks_confirmation()
    {
        var (env, s, key) = Setup();
        FillQc(env, s, key);
        var bytes = File.ReadAllBytes(TD.P(Path.Combine("img", "mitu_goc_38.2.jpg")));
        var im = env.Images.Add(s, key, env.Orders.Get(key).Version, bytes, "jpg", "Hardness");
        Assert.Equal("38.2", im.OcrValue);                         // OCR chỉ gợi ý
        Assert.Null(im.ConfirmedValue);
        Assert.NotNull(im.NormalizedSha);                          // Mitutoyo: đã cắt + làm sáng
        Assert.Throws<BusinessException>(() => env.Orders.ConfirmHardness(s, key, env.Orders.Get(key).Version, null));
        Assert.Throws<BusinessException>(() => env.Images.Confirm(s, key, env.Orders.Get(key).Version, im.Id, "382"));   // ngoài 10–75
        env.Images.Confirm(s, key, env.Orders.Get(key).Version, im.Id, "38,2");
        Assert.Equal("38.2", env.Orders.Get(key).Order!.Qc.Images[0].ConfirmedValue);
        // ảnh xuất hàng: số cân đọc trên ảnh gốc, ảnh đã cắt
        var prod = env.Images.Add(s, key, env.Orders.Get(key).Version, File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))), "jpg", "Sample");
        Assert.Equal("41.40", prod.OcrValue); Assert.False(prod.NeedsManualCrop); Assert.True(prod.Crop![1] > 250);
    }

    [Fact]
    public void Approve_produces_A4_pdf_and_is_immutable()
    {
        if (!ExcelPdfExporter.IsAvailable()) return;
        var sigs = TD.P("sig");
        if (!Directory.Exists(sigs)) return;
        var (env, s, key) = Setup();
        FillQc(env, s, key);
        var (_, v) = env.Orders.Get(key);
        env.Images.Add(s, key, v, File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))), "jpg", "Sample");
        foreach (var f in new[] { "mitu_goc_38.2.jpg", "mitu1_47.3.png", "dyna2_47.4.png" })
            env.Images.Add(s, key, env.Orders.Get(key).Version, File.ReadAllBytes(TD.P(Path.Combine("img", f))), Path.GetExtension(f).TrimStart('.'), "Hardness");
        var o = env.Orders.Get(key).Order!;
        foreach (var im in o.Qc.Images) env.Images.Confirm(s, key, env.Orders.Get(key).Version, im.Id, im.OcrValue ?? (im.Kind == "Sample" ? "41.40" : "47.4"));
        env.Orders.ConfirmHardness(s, key, env.Orders.Get(key).Version, null);
        var c = env.Cq.Create(s, key);
        var ap = env.Settings.AddSigner(s, "Người duyệt", File.ReadAllBytes(Path.Combine(sigs, "approver.png")), "png");
        var ck = env.Settings.AddSigner(s, "Đào Duy Thanh", File.ReadAllBytes(Path.Combine(sigs, "thanh_moi_goc.jpg")), "jpg");
        env.Settings.SetStamp(s, File.ReadAllBytes(Path.Combine(sigs, "stamp.png")));
        var a = env.Cq.Approve(s, c.Id, ap, ck, env.Settings.Stamp()!, new[] { new CqOverlayPlacement("Stamp", "N33", -15, 4, 38) });
        Assert.Equal(CqState.Approved, a.State);
        var pdf = env.Store.GetBlob(a.PdfSha!, "pdf");
        var outDir = Path.Combine(AppContext.BaseDirectory, "e2e"); Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "app_approved.pdf"), pdf);
        var chk = ExcelPdfExporter.Inspect(Path.Combine(outDir, "app_approved.pdf"));
        Assert.Equal(1, chk.Pages); Assert.InRange(chk.WidthPt, 593, 597); Assert.InRange(chk.HeightPt, 840, 844);
        Assert.Equal(a.PdfSha, env.Cq.Approve(s, c.Id, ap, ck, env.Settings.Stamp()!).PdfSha);   // đã duyệt = bất biến (duyệt lại → trả bản cũ)
        // thay chữ ký Thanh → bản cũ ngừng dùng, không duyệt bằng bản cũ được
        env.Settings.AddSigner(s, "Đào Duy Thanh", File.ReadAllBytes(Path.Combine(sigs, "thanh_moi_goc.jpg")), "jpg");
        var c2 = env.Cq.Create(s, key);
        Assert.Throws<BusinessException>(() => env.Cq.Approve(s, c2.Id, ap, ck, env.Settings.Stamp()!));
    }

    [Fact]
    public void Backup_verify_restore()
    {
        var (env, s, key) = Setup();
        FillQc(env, s, key);
        var dest = TD.NewDir("bk_");
        var bk = BackupService.Backup(env.Store.Root, dest);
        Assert.Empty(BackupService.Verify(bk));
        var target = Path.Combine(TD.NewDir("rs_"), "kho");
        BackupService.Restore(bk, target);
        var restored = new OrderService(new Repo(new FileStore(target, "MAY-R")), new AuditLog(new FileStore(target, "MAY-R")));
        Assert.Equal("58-62", restored.Get(key).Order!.Qc.HardnessRequirement);
        Assert.Throws<IOException>(() => BackupService.Restore(bk, target));         // không ghi đè kho có dữ liệu
        // làm hỏng 1 file trong bản sao lưu → phát hiện
        var f = Directory.EnumerateFiles(Path.Combine(bk, "records"), "*.json", SearchOption.AllDirectories).First();
        File.AppendAllText(f, " ");
        Assert.NotEmpty(BackupService.Verify(bk));
        Assert.Throws<IOException>(() => BackupService.Restore(bk, Path.Combine(TD.NewDir("rs_"), "kho")));
    }
}
