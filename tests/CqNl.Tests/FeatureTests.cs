using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Storage;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Xunit;

namespace CqNl.Tests;

/// <summary>Tổng quan/báo cáo, lưu ý đơn hàng (QC + Kinh doanh), ảnh khi offline, đọc danh sách tăng dần.</summary>
public class FeatureTests
{
    private static (string store, Workspace a, Workspace b, ErpReadResult read) Setup()
    {
        var root = TD.NewDir("ft_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var a = new Workspace(Path.Combine(root, "localA"), store, AppEnv.Paths, "MAY-A", TimeSpan.FromSeconds(10));
        var b = new Workspace(Path.Combine(root, "localB"), store, AppEnv.Paths, "MAY-B", TimeSpan.FromSeconds(10));
        Assert.True(a.Probe());
        a.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        var s = a.Login("admin", "Admin@2026");
        a.Auth.SaveUser(s, "kd1", "Kinh Doanh 1", Role.Sales, true, "Tam@123");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        a.Orders!.Apply(s, a.Orders.Preview(read), "erp.xlsx", _ => false);
        Assert.True(b.Probe());
        var sb = b.Login("kd1", "Tam@123");
        b.Auth!.ChangePassword(sb, "Tam@123", "Kd1@2026", "Kd1@2026"); b.Logout(); b.Login("kd1", "Kd1@2026");
        return (store, a, b, read);
    }

    private static void MakeCq(Workspace w, string key, string samples, bool approveAndPrint = false)
    {
        var r = w.GetOrder(key)!;
        r = w.EditQc(key, r.Version, q => { q.TemplateId = "carbon"; q.HardnessRequirement = "58-62"; q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" }; }, "t");
        r = w.SaveHardness(key, r.Version, new[] { samples }, h => h.CheckDate = "13/11/2026");
        w.ConfirmHardness(key, r.Version);
        var c = w.CreateCq(key);
        if (approveAndPrint)
        {
            // duyệt cần Excel; ở đây đánh dấu trực tiếp bản ghi để kiểm thống kê (luồng duyệt thật có test riêng)
            w.Repo!.Update<CqVersionRecord>(CqService.T, c.Id, "admin", x => { x.State = CqState.Approved; x.ApprovedBy = "admin"; x.ApprovedAt = DateTimeOffset.Now; });
            w.Cq!.MarkPrinted(w.Session!, c.Id, true);
        }
    }

    [Fact]
    public void Dashboard_counts_every_order_including_not_done_and_exports_valid_excel()
    {
        var (_, a, _, read) = Setup();
        var keys = read.Lines.Take(5).Select(l => l.Key).ToList();
        MakeCq(a, keys[0], "59", approveAndPrint: true);      // đạt, đã duyệt, đã in
        MakeCq(a, keys[1], "57");                            // không đạt, nháp
        MakeCq(a, keys[2], "60");                            // đạt, nháp
        var r3 = a.GetOrder(keys[3])!;                        // đã xác nhận đo nhưng CHƯA làm CQ
        r3 = a.EditQc(keys[3], r3.Version, q => q.HardnessRequirement = "58-62", "t");
        r3 = a.SaveHardness(keys[3], r3.Version, new[] { "61" }, h => h.CheckDate = "13/11/2026");
        a.ConfirmHardness(keys[3], r3.Version);
        var res = DashboardService.Compute(a.ListOrders(), a.NoteCounts(), new DashFilter(null, null, null));
        var t = res.Totals;
        Assert.Equal(read.Lines.Count, t.Total);                                     // mọi dòng đơn đều được ghi nhận
        Assert.Equal(3, t.WithCq); Assert.Equal(read.Lines.Count - 3, t.WithoutCq);
        Assert.Equal(3, t.Pass); Assert.Equal(1, t.Fail); Assert.Equal(read.Lines.Count - 4, t.NoResult);
        Assert.Equal(1, t.Approved); Assert.Equal(1, t.Printed); Assert.Equal(0, t.NotPrinted); Assert.Equal(2, res.DraftCq);
        var l3 = res.Lines.Single(l => l.Key == keys[3]);
        Assert.Equal(DashboardService.NoCq, l3.CqStatus); Assert.Equal(DashboardService.Pass, l3.Result); Assert.Equal("Kết quả đo đã xác nhận", l3.ResultSource);
        Assert.Equal(read.Lines.Count, res.ByMonth.Sum(m => m.Total));
        // lọc theo khoảng ngày + khách hàng
        var d0 = res.Lines.Where(l => l.Received is not null).Min(l => l.Received)!.Value;
        var one = DashboardService.Compute(a.ListOrders(), a.NoteCounts(), new DashFilter(d0, d0, null));
        Assert.All(one.Lines, l => Assert.Equal(d0, l.Received));
        var cust = res.Lines[0].Customer;
        Assert.All(DashboardService.Compute(a.ListOrders(), a.NoteCounts(), new DashFilter(null, null, cust[..6])).Lines, l => Assert.Contains(cust[..6].ToLowerInvariant(), l.Customer.ToLowerInvariant()));
        // Excel hợp lệ (OpenXML validator) và đủ dòng chi tiết, kể cả đơn chưa làm CQ
        var x = DashboardService.ExportXlsx(res, "Quản trị");
        var p = Path.Combine(TD.NewDir("rp_"), "baocao.xlsx"); File.WriteAllBytes(p, x);
        using (var doc = SpreadsheetDocument.Open(p, false))
        {
            var errs = new OpenXmlValidator().Validate(doc).Take(5).Select(e => e.Description).ToList();
            Assert.True(errs.Count == 0, string.Join("\n", errs));
        }
        var detail = XlsxReader.ReadSheet(p, "Chi tiết");
        Assert.Equal(read.Lines.Count + 1, detail.Count(r => r.Any(c => c?.Text is not null || c?.Number is not null)));
        Assert.Contains(detail, r => r.Any(c => c?.Text == DashboardService.NoCq));
        var sum = XlsxReader.ReadSheet(p, "Tổng hợp");
        Assert.Contains(sum, r => r[0]?.Text == "Không đạt" && r[1]?.Number == 1);
    }

    [Fact]
    public void Notes_by_qc_and_sales_never_conflict_and_follow_customer()
    {
        var (store, a, b, read) = Setup();
        var key = read.Lines[0].Key;
        var cust = read.Lines[0].Customer!;
        var other = read.Lines.First(l => l.Customer == cust && l.Key != key).Key;        // đơn khác cùng khách
        // Kinh doanh: KHÔNG sửa được dữ liệu QC nhưng ghi được lưu ý (kèm ảnh)
        Assert.Throws<PermissionException>(() => b.EditQc(key, b.GetOrder(key)!.Version, q => q.Size = "x", "t"));
        var img = File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg")));
        var nb = b.AddNote(key, "Khách yêu cầu chụp thêm ảnh mặt cắt, giao trước 20/11", true, new[] { (img, "jpg", "anh_khach.jpg") });
        var na = a.AddNote(key, "QC: đã đo lại mẫu 2", false, Array.Empty<(byte[], string, string)>());
        Assert.Equal(2, a.NotesFor(key).Count);
        Assert.Equal(sha(img), a.NotesFor(key).Single(n => n.Note.Id == nb.Id).Note.Images[0].Sha);
        Assert.NotNull(a.ImageBytes(nb.Images[0].Sha, "jpg"));                                 // máy khác xem được ảnh
        // đơn sau của cùng khách thấy lưu ý "nhắc lại", không thấy lưu ý chỉ của đơn kia
        var fut = a.NotesFor(other);
        Assert.Single(fut); Assert.True(fut[0].FromOtherOrder); Assert.Equal(nb.Id, fut[0].Note.Id);
        // không cho ghi rỗng; viewer/người khác không ẩn được lưu ý của người khác
        Assert.Throws<BusinessException>(() => b.AddNote(key, "  ", false, Array.Empty<(byte[], string, string)>()));
        Assert.Throws<PermissionException>(() => b.HideNote(na.Id));
        a.HideNote(nb.Id);                                                                      // admin ẩn được, lịch sử giữ
        Assert.Single(a.NotesFor(key)); Assert.Equal(2, a.NotesFor(key, includeHidden: true).Count);
        Assert.Equal(2, a.Store!.Versions(NoteService.T, nb.Id).Count);
        // offline: lưu ý vào hàng đợi, tự gửi khi có kết nối
        Directory.Move(store, store + "_mat"); b.Probe();
        var off = b.AddNote(key, "Ghi khi mất mạng", false, new[] { (img, "jpg", "a.jpg") });
        Assert.True(b.NotesFor(key).Single(n => n.Note.Id == off.Id).Pending);
        Directory.Move(store + "_mat", store);
        var st = b.Tick(); Assert.Equal(0, st.Pending);
        Assert.Contains(a.NotesFor(key), n => n.Note.Id == off.Id);
        static string sha(byte[] x) => FileStore.Sha256Hex(x);
    }

    [Fact]
    public void Offline_image_is_queued_with_blobs_and_pushed_before_record()
    {
        var (store, a, _, read) = Setup();
        var key = read.Lines[0].Key;
        a.ListOrders();
        var v0 = a.GetOrder(key)!.Version;
        Directory.Move(store, store + "_mat"); a.Probe();
        var bytes = File.ReadAllBytes(TD.P(Path.Combine("img", "mitu_goc_38.2.jpg")));
        var im = a.AddImage(key, v0, bytes, "jpg", "Hardness");
        Assert.Equal("38.2", im.OcrValue); Assert.NotNull(im.NormalizedSha);                    // xử lý trên máy, không cần kho
        Assert.NotNull(a.ImageBytes(im.BlobSha, "jpg"));                                        // xem được ngay khi offline
        var item = a.Outbox.Items().Single();
        // gốc + bản chuẩn hóa CQ + 3 bản của module xử lý ảnh (HD màu, in đen trắng, vùng số đo) — tất cả vào hàng đợi khi offline
        Assert.Equal(5, item.Blobs!.Count);
        Assert.NotNull(im.Vision); Assert.NotNull(im.Vision!.HdSha); Assert.NotNull(im.Vision.PrintSha); Assert.NotNull(im.Vision.RoiSha);
        a.ConfirmImage(key, a.GetOrder(key)!.Version, im.Id, "38.2");                          // gộp vào cùng nháp
        Assert.Single(a.Outbox.Items());
        Directory.Move(store + "_mat", store);
        var st = a.Tick();
        Assert.Equal(0, st.Pending);
        var o = a.Orders!.Get(key).Order!;
        Assert.Equal("38.2", o.Qc.Images.Single().ConfirmedValue);
        Assert.Equal(bytes.Length, a.Store!.GetBlob(im.BlobSha, "jpg").Length);                   // blob đã lên kho, đúng hash
        Assert.NotNull(a.Store.GetBlob(im.NormalizedSha!, "jpg"));
        Assert.NotNull(a.Store.GetBlob(im.Vision.HdSha!, "jpg")); Assert.NotNull(a.Store.GetBlob(im.Vision.PrintSha!, "png"));   // ảnh xử lý cũng lên kho sau khi có mạng
        Assert.NotNull(o.Qc.Images.Single().Vision);                                                                          // kết quả OCR / lịch sử đi cùng bản ghi
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(a.Outbox.Path_, "blobs")));          // dọn hàng đợi
    }

    [Fact]
    public void Order_list_refresh_reads_only_changed_records()
    {
        var (store, a, b, read) = Setup();
        var b2 = new Workspace(Path.Combine(Path.GetDirectoryName(store)!, "localC"), store, AppEnv.Paths, "MAY-C", TimeSpan.FromSeconds(10));
        b2.Probe();                                    // chỉ đọc danh sách: không cần đăng nhập
        var first = b2.ListOrders();
        Assert.True(b2.LastRefreshWasFull);
        Assert.Equal(read.Lines.Count, first.Count);
        b2.ListOrders();
        Assert.False(b2.LastRefreshWasFull); Assert.Equal(0, b2.LastRefreshReads);           // không đổi → không đọc lại gì
        var key = read.Lines[3].Key;
        a.EditQc(key, a.GetOrder(key)!.Version, q => q.PartNo = "MOI", "t");                  // máy khác sửa 1 đơn
        var list = b2.ListOrders();
        Assert.Equal(1, b2.LastRefreshReads);
        Assert.Equal("MOI", list.Single(r => r.Order.Key == key).Order.Qc.PartNo);
        // sự kiện của máy lệch đồng hồ (tên file "cũ" hơn) vẫn được xử lý
        var k2 = read.Lines[4].Key;
        a.EditQc(k2, a.GetOrder(k2)!.Version, q => q.PartNo = "LECH", "t");
        var evDir = Directory.EnumerateDirectories(Path.Combine(store, AppInfo.DataFolder, "events")).OrderBy(x => x).Last();
        var newest = Directory.EnumerateFiles(evDir).OrderBy(x => x).Last();
        File.Move(newest, Path.Combine(evDir, "0000000000000000001-MAY-LECH-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.Equal("LECH", b2.ListOrders().Single(r => r.Order.Key == k2).Order.Qc.PartNo);
        // làm mới đầy đủ vẫn đúng và chỉ đọc bản ghi có version mới
        var full = b2.ListOrders(fullRefresh: true);
        Assert.True(b2.LastRefreshWasFull); Assert.Equal(0, b2.LastRefreshReads);
        Assert.Equal(read.Lines.Count, full.Count);
    }
}

/// <summary>Tốc độ thật qua SMB (đường dẫn UNC THỬ NGHIỆM, không phải kho nghiệp vụ): 484 dòng ERP, 2 máy.</summary>
public class UncPerformanceTests
{
    [Fact]
    public void Unc_list_cold_and_incremental_timing()
    {
        var unc = Environment.GetEnvironmentVariable("CQNL_UNC_TEST");
        if (string.IsNullOrEmpty(unc) || !Directory.Exists(unc)) return;
        Assert.DoesNotContain("DU LIEU NHIET", unc, StringComparison.OrdinalIgnoreCase);        // không bao giờ chạy trên kho nghiệp vụ
        var store = Path.Combine(unc, "perf_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(store);
        var a = new Workspace(TD.NewDir("pa_"), store, AppEnv.Paths, "PERF-A", TimeSpan.FromSeconds(10));
        var b = new Workspace(TD.NewDir("pb_"), store, AppEnv.Paths, "PERF-B", TimeSpan.FromSeconds(10));
        Assert.True(a.Probe()); Assert.True(b.Probe());
        a.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        var s = a.Login("admin", "Admin@2026");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        a.Orders!.Apply(s, a.Orders.Preview(read), "erp.xlsx", _ => false);
        var import = sw.Elapsed; sw.Restart();
        var cold = b.ListOrders(); var tCold = sw.Elapsed; sw.Restart();
        var warm = b.ListOrders(); var tWarm = sw.Elapsed;
        var key = read.Lines[0].Key;
        a.EditQc(key, a.GetOrder(key)!.Version, q => q.PartNo = "P1", "t");
        sw.Restart(); b.ListOrders(); var tInc = sw.Elapsed;
        var report = $"UNC {read.Lines.Count} dong: import {import.TotalSeconds:0.0}s; danh sach lan dau {tCold.TotalSeconds:0.0}s; lan sau (khong doi) {tWarm.TotalSeconds:0.00}s; sau 1 thay doi {tInc.TotalSeconds:0.00}s (doc {b.LastRefreshReads} ban ghi)";
        File.WriteAllText(Path.Combine(unc, "perf_result.txt"), report);
        Assert.Equal(read.Lines.Count, cold.Count); Assert.Equal(read.Lines.Count, warm.Count);
        Assert.Equal(1, b.LastRefreshReads);
        Assert.True(tWarm < TimeSpan.FromSeconds(5), report);
        Assert.True(tInc < TimeSpan.FromSeconds(5), report);
    }
}
