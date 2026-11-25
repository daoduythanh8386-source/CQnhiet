using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Excel;
using Xunit;

namespace CqNl.Tests;

/// <summary>Template thêm mới (tự dò/kiểm tra), bảng mác sai loại file, đồng bộ thư mục ERP, gộp/tách dòng đơn, nhật ký dễ đọc.</summary>
public class TemplateErpMergeTests
{
    private static (string root, string store, Workspace ws, ErpReadResult read) Setup()
    {
        var root = TD.NewDir("tem_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var ws = new Workspace(Path.Combine(root, "local"), store, AppEnv.Paths, "MAY-T", TimeSpan.FromSeconds(10));
        Assert.True(ws.Probe());
        ws.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        ws.Login("admin", "Admin@2026");
        return (root, store, ws, ErpReader.Read(TD.P("erp.xlsx")));
    }

    [Fact]
    public void Auto_map_from_carbon_finds_gio_layout()
    {
        var carbon = TemplateMap.Load(TD.P(Path.Combine("maps", "carbon.json")));
        var gioReal = TemplateMap.Load(TD.P(Path.Combine("maps", "gio.json")));
        var gioXlsx = File.ReadAllBytes(TD.P("gio.xlsx"));
        var insp = new TemplateInspector(gioXlsx);
        Assert.Equal(gioReal.Sheet, insp.Sheet);
        var (json, report) = insp.AutoMap(carbon, "tpl-x", "Thép gió (dò)", "gio.xlsx");
        var auto = TemplateMap.Parse(json);
        // các trường có nhãn rõ ràng phải trùng bản đồ làm tay của template gió (gió lệch 1 dòng so với carbon)
        foreach (var f in new[] { "Customer", "Material", "Size", "Quantity", "WeightKg", "ReceivedDate", "CheckDate", "Dngc", "ReportNo", "AppearanceResult", "Remark" })
            Assert.True(gioReal.Cell(f) == auto.Cell(f), $"{f}: thật {gioReal.Cell(f)} / dò {auto.Cell(f)}\n{string.Join("\n", report)}");
        Assert.Equal(gioReal.Cells("Samples"), auto.Cells("Samples"));
        Assert.Equal(gioReal.Cell("OtherResult"), auto.Cell("OtherResult"));                  // nhãn "Khác" lệch xa bị bỏ qua
        Assert.Null(auto.Cell("PartNo")); Assert.Null(auto.Cell("QuenchTemp1"));              // mẫu gió không có các mục này
        var autoErr = TemplateCatalog.Check(gioXlsx, json).Where(i => i.Error).ToList();
        Assert.True(autoErr.Count == 0, "Tự dò còn lỗi: " + string.Join("; ", autoErr.Select(e => e.Message)));
        Assert.Equal(gioReal.Ranges("ApproverSignatureFrame"), auto.Ranges("ApproverSignatureFrame"));
        Assert.Equal(gioReal.Ranges("HardnessImageFrames"), auto.Ranges("HardnessImageFrames"));
        // bản đồ làm tay của 3 template mặc định phải qua kiểm tra không lỗi
        foreach (var t in new[] { "carbon", "khuon", "gio" })
        {
            var m = TemplateMap.Load(TD.P(Path.Combine("maps", t + ".json")));
            var errs = TemplateCatalog.Check(File.ReadAllBytes(TD.P(m.FileName)), m.Json).Where(i => i.Error).ToList();
            Assert.True(errs.Count == 0, t + ": " + string.Join("; ", errs.Select(e => e.Message)));
        }
        // ô sai (giữa vùng gộp / ngoài vùng in) bị chặn
        var bad = json.Replace($"\"{gioReal.Cell("Customer")}\"", "\"H6\"").Replace($"\"{gioReal.Cell("Dngc")}\"", "\"Z99\"");
        var issues = TemplateCatalog.Check(gioXlsx, bad);
        Assert.Contains(issues, i => i.Error && i.Field == "Customer");
        Assert.Contains(issues, i => i.Error && i.Field == "Dngc");
    }

    [Fact]
    public void Custom_template_is_shared_used_for_cq_and_cq_keeps_its_map()
    {
        var (_, _, ws, read) = Setup();
        var s = ws.Session!;
        var carbon = TemplateMap.Load(TD.P(Path.Combine("maps", "carbon.json")));
        var xlsx = File.ReadAllBytes(TD.P("carbon.xlsx"));                      // "template mới" = bản sao file carbon
        var (json, _) = new TemplateInspector(xlsx).AutoMap(carbon, "x", "Carbon khách A", "carbon_A.xlsx");
        var rec = ws.Cq!.Templates.AddCustom(s, "Carbon khách A", "carbon_A.xlsx", xlsx, json, "carbon");
        Assert.Throws<BusinessException>(() => ws.Cq.Templates.AddCustom(s, "Carbon khách A", "a.xlsx", xlsx, json, null));   // trùng tên
        Assert.Contains(ws.TemplateList(), t => t.Id == rec.Id && !t.BuiltIn);
        var key = read.Lines[0].Key;
        ws.Orders!.Apply(s, ws.Orders.Preview(read), "erp.xlsx", _ => false);
        var r = ws.GetOrder(key)!;
        r = ws.EditQc(key, r.Version, q => { q.TemplateId = rec.Id; q.HardnessRequirement = "58-62"; q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" }; }, "t");
        r = ws.SaveHardness(key, r.Version, new[] { "59" }, h => h.CheckDate = "13/11/2026");
        ws.ConfirmHardness(key, r.Version);
        var cq = ws.CreateCq(key);
        Assert.Equal("Carbon khách A", cq.TemplateName);
        Assert.Contains("\"Customer\"", cq.TemplateMapJson);
        // sửa bản đồ template sau đó → CQ đã tạo vẫn dùng bản đồ cũ
        var edited = json.Replace("\"N34:R36\"", "\"N33:R35\"");
        ws.Cq.Templates.SaveMap(s, rec.Id, edited);
        Assert.Equal("N34:R36", ws.Cq.MapOf(ws.Cq.ForOrder(key).Single()).Ranges("ApproverSignatureFrame")[0]);
        Assert.Equal("N33:R35", ws.Cq.Templates.Resolve(rec.Id).Map.Ranges("ApproverSignatureFrame")[0]);
        // template mặc định: sửa bản đồ rồi khôi phục
        ws.Cq.Templates.SaveMap(s, "carbon", carbon.Json.Replace("\"N34:R36\"", "\"N33:R35\""));
        Assert.Contains(ws.TemplateList(), t => t.Id == "carbon" && t.MapOverridden);
        ws.Cq.Templates.ResetBuiltIn(s, "carbon");
        Assert.Equal("N34:R36", ws.Cq.Templates.Resolve("carbon").Map.Ranges("ApproverSignatureFrame")[0]);
        ws.Cq.Templates.Deactivate(s, rec.Id);
        Assert.DoesNotContain(ws.TemplateList(), t => t.Id == rec.Id);
        Assert.Throws<BusinessException>(() => ws.Cq.Templates.Deactivate(s, "carbon"));
    }

    [Fact]
    public void Steel_table_rejects_cq_template_file()
    {
        var (_, _, ws, _) = Setup();
        var ex = Assert.Throws<BusinessException>(() => ws.Settings!.SetSteelTable(ws.Session!, File.ReadAllBytes(TD.P("carbon.xlsx")), "carbon.xlsx"));
        Assert.Contains("không phải bảng mác", ex.Message);
        var t = ws.Settings!.SetSteelTable(ws.Session!, File.ReadAllBytes(TD.P("steel_grades.xlsx")), "steel_grades.xlsx");
        Assert.True(t.Grades.Count > 100);
    }

    [Fact]
    public void Erp_folder_sync_imports_only_new_files()
    {
        var (root, _, ws, read) = Setup();
        var folder = Path.Combine(root, "erp"); Directory.CreateDirectory(folder);
        Assert.Throws<BusinessException>(() => ws.ScanErpFolder());                       // chưa cài thư mục
        ws.Settings!.SetErpFolder(ws.Session!, folder);
        File.Copy(TD.P("erp.xlsx"), Path.Combine(folder, "MF0015_1.xlsx"));
        File.WriteAllText(Path.Combine(folder, "ghi_chu.xlsx"), "không phải excel");
        Assert.Equal(2, ws.CountNewErpFiles());
        var scan = ws.ScanErpFolder();
        Assert.Single(scan.NewFiles, f => f.Error is null);
        Assert.Single(scan.NewFiles, f => f.Error is not null);
        Assert.Equal(read.Lines.Count, scan.Plan.Add.Count);
        var (added, _, _) = ws.ApplyErpScan(scan);
        Assert.Equal(read.Lines.Count, added);
        File.Copy(Path.Combine(folder, "MF0015_1.xlsx"), Path.Combine(folder, "doi_ten.xlsx"));   // đổi tên/sao chép: KHÔNG nhập lại
        var again = ws.ScanErpFolder();
        Assert.DoesNotContain(again.NewFiles, f => f.Error is null);
        Assert.Empty(again.Plan.Add);
        Assert.Contains(ws.Audit!.Read(), n => (string?)n["action"] == "erp.import");
        Assert.StartsWith("Thêm", AuditText.Detail("erp.import", ws.Audit.Read().Last(n => (string?)n["action"] == "erp.import")["detail"]));
        Assert.Equal("Đăng nhập sai mật khẩu", AuditText.Action("login.fail"));
        Assert.Equal("Xác nhận số đọc từ ảnh: 47.4", AuditText.Detail("order.edit", System.Text.Json.Nodes.JsonValue.Create("image.confirm c4395b444a98 = 47.4")));
        Assert.Equal("Gộp vào dòng 216967", AuditText.EditWhat("merge.child DNGC/HY3-26090216|216967"));
    }

    [Fact]
    public void Merge_lines_one_cq_with_summed_quantity_then_split()
    {
        var (_, _, ws, read) = Setup();
        var s = ws.Session!;
        ws.Orders!.Apply(s, ws.Orders.Preview(read), "erp.xlsx", _ => false);
        var pair = read.Lines.GroupBy(l => l.Dngc).Select(g => g.ToList()).Select(g => g.Where(l => l.Quantity.HasValue).ToList()).First(g => g.Count >= 2).Take(2).ToList();
        var keys = pair.Select(l => l.Key).ToList();
        foreach (var k in keys)
        {
            var r = ws.GetOrder(k)!;
            ws.EditQc(k, r.Version, q => { q.SteelGrade = "SKD11"; q.HardnessRequirement = "58-62"; q.TemplateId = "carbon"; q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" }; }, "t");
        }
        var parent = ws.MergeOrders(keys);
        var child = keys.Single(k => k != parent);
        Assert.Equal(parent, ws.GetOrder(child)!.Order.Qc.MergeGroup);
        Assert.Throws<BusinessException>(() => ws.MergeOrders(keys));                          // đã thuộc nhóm
        var pr = ws.GetOrder(parent)!;
        pr = ws.SaveHardness(parent, pr.Version, new[] { "59" }, h => h.CheckDate = "13/11/2026");
        ws.ConfirmHardness(parent, pr.Version);
        var cr = ws.GetOrder(child)!;
        cr = ws.SaveHardness(child, cr.Version, new[] { "59" }, h => h.CheckDate = "13/11/2026");
        ws.ConfirmHardness(child, cr.Version);
        Assert.Throws<BusinessException>(() => ws.CreateCq(child));                              // CQ tạo ở dòng chính
        var cq = ws.CreateCq(parent);
        var xlsx = ws.Store!.GetBlob(cq.XlsxSha, "xlsx");
        var p = Path.Combine(TD.NewDir("mg_"), "cq.xlsx"); File.WriteAllBytes(p, xlsx);
        var map = ws.Cq!.MapOf(cq);
        Assert.True(TemplateInspector.TryCell(map.Cell("Quantity")!, out var qc));
        var sheet = XlsxReader.ReadSheet(p, map.Sheet);
        Assert.Equal((double)(pair[0].Quantity!.Value + pair[1].Quantity!.Value), sheet[qc.Row - 1][qc.Col - 1]!.Number);
        // dòng con được tính "đã có CQ" trên Tổng quan
        var dash = DashboardService.Compute(ws.ListOrders(), ws.NoteCounts(), new DashFilter(null, null, null));
        Assert.True(dash.Lines.Single(l => l.Key == child).HasCq);
        Assert.Throws<BusinessException>(() => ws.SplitOrders(child));                           // đã có CQ → không tách
        ws.Cq.Delete(s, cq.Id, "thử tách");
        ws.SplitOrders(child);
        Assert.Null(ws.GetOrder(child)!.Order.Qc.MergeGroup);
        Assert.Null(ws.GetOrder(parent)!.Order.Qc.MergedChildren);
    }
}
