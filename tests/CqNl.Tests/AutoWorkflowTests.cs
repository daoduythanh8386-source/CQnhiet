using System.Globalization;
using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Excel;
using Xunit;

namespace CqNl.Tests;

/// <summary>Quy trình tự động: ERP vào thư mục → tự nhập; mở đơn → tự điền; ảnh đo → xác nhận tất cả một lần.</summary>
public class AutoWorkflowTests
{
    private static (string root, Workspace ws, ErpReadResult read) Setup(bool steel = true)
    {
        var root = TD.NewDir("auto_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var ws = new Workspace(Path.Combine(root, "local"), store, AppEnv.Paths, "MAY-A", TimeSpan.FromSeconds(10));
        Assert.True(ws.Probe());
        ws.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        ws.Login("admin", "Admin@2026");
        if (steel) ws.Settings!.SetSteelTable(ws.Session!, File.ReadAllBytes(TD.P("steel_grades.xlsx")), "steel_grades.xlsx");
        return (root, ws, ErpReader.Read(TD.P("erp.xlsx")));
    }

    [Fact]
    public void Erp_file_dropped_in_folder_is_imported_automatically_once()
    {
        var (root, ws, read) = Setup(steel: false);
        Assert.Null(ws.AutoImportErp());                                         // chưa cài thư mục → không làm gì
        var folder = Path.Combine(root, "MF0015"); Directory.CreateDirectory(folder);
        ws.Settings!.SetErpFolder(ws.Session!, folder);
        Assert.Null(ws.AutoImportErp());                                         // thư mục trống
        File.Copy(TD.P("erp.xlsx"), Path.Combine(folder, "MF0015_16-11.xlsx"));
        File.WriteAllText(Path.Combine(folder, "loi.xlsx"), "không phải excel");
        var r = ws.AutoImportErp();
        Assert.NotNull(r);
        Assert.Equal(new[] { "MF0015_16-11.xlsx" }, r!.Files);
        Assert.Equal(read.Lines.Count, r.Added);
        Assert.Single(r.Errors);                                                 // file lỗi báo 1 lần
        Assert.Equal(read.Lines.Count, ws.ListOrders().Count);
        Assert.Null(ws.AutoImportErp());                                         // lần sau: không nhập lại, không báo lỗi lại
        // Sales không tự nhập
        ws.Auth!.SaveUser(ws.Session!, "kd", "KD", Role.Sales, true, "Tam@123");
        ws.Logout(); var sk = ws.Login("kd", "Tam@123"); ws.Auth.ChangePassword(sk, "Tam@123", "Kd@20266", "Kd@20266"); ws.Logout(); ws.Login("kd", "Kd@20266");
        File.Copy(TD.P("erp.xlsx"), Path.Combine(folder, "khac.xlsx"));
        File.AppendAllText(Path.Combine(folder, "khac.xlsx"), "");                // cùng nội dung → đã nhập
        Assert.Null(ws.AutoImportErp());
    }

    [Fact]
    public void Opening_order_fills_grade_requirement_temperatures_ram_template_without_overwriting_qc_values()
    {
        var (_, ws, read) = Setup();
        ws.Orders!.Apply(ws.Session!, ws.Orders.Preview(read), "erp.xlsx", _ => false);
        var steel = ws.SteelTable()!;
        // dòng ERP có mác trong bảng mác với đúng 1 bộ thông số, có nhiệt độ tôi + ram + đánh dấu template
        var line = read.Lines.First(l =>
        {
            var f = steel.Find(l.GradeFromErp);
            return f.Count > 0 && f.Select(g => (string.Join("|", Workspace.HeatTexts(g)), string.Join("|", Workspace.RamTexts(g)), g.Tolerance, g.TemplateMark)).Distinct().Count() == 1
                   && Workspace.HeatTexts(f[0]).Count > 0 && Workspace.RamTexts(f[0]).Count > 0 && Workspace.TemplateFromMark(f[0].TemplateMark) is not null
                   && CqNl.Domain.HardnessRequirement.TryParse(l.HardnessRequirementErp) is not null;
        });
        var g = steel.Find(line.GradeFromErp)[0];
        var r = ws.AutoFillQc(line.Key)!;
        var q = r.Row.Order.Qc;
        Assert.Equal(line.GradeFromErp!.Trim(), q.SteelGrade);
        Assert.Equal(line.HardnessRequirementErp!.Trim(), q.HardnessRequirement);
        Assert.Equal(Workspace.HeatTexts(g), q.QuenchTemps);
        Assert.Equal(Workspace.RamTexts(g), q.RamValues);
        Assert.Equal(Workspace.TemplateFromMark(g.TemplateMark), q.TemplateId);
        Assert.Equal(g.SourceRow, q.SteelGradeRow);
        Assert.NotEmpty(r.Filled);
        Assert.Contains(ws.Audit!.Read(), n => (string?)n["action"] == "order.edit" && (string?)n["detail"] == "auto.fill");
        // mở lại: không ghi gì nữa
        var v = r.Row.Version;
        Assert.Equal(v, ws.AutoFillQc(line.Key)!.Row.Version);
        // QC đã nhập tay → giữ nguyên
        var other = read.Lines.First(l => l.Key != line.Key);
        var ro = ws.GetOrder(other.Key)!;
        ws.EditQc(other.Key, ro.Version, x => { x.SteelGrade = line.GradeFromErp; x.QuenchTemps = new() { "999℃" }; x.TemplateId = "gio"; x.HardnessRequirement = "40-45"; }, "t");
        var r2 = ws.AutoFillQc(other.Key)!;
        Assert.Equal(new[] { "999℃" }, r2.Row.Order.Qc.QuenchTemps);
        Assert.Equal("gio", r2.Row.Order.Qc.TemplateId);
        Assert.Equal("40-45", r2.Row.Order.Qc.HardnessRequirement);
        Assert.Equal(Workspace.RamTexts(g), r2.Row.Order.Qc.RamValues);           // ô còn trống vẫn được điền
    }

    [Fact]
    public void Grade_not_in_table_uses_template_of_same_customer_previous_order_and_reports_what_qc_must_enter()
    {
        OrderRecord O(string key, string cust, string grade, string? tpl = null) => new()
        {
            Key = key, Erp = new() { ["Customer"] = cust, ["GradeFromErp"] = grade, ["HardnessRequirementErp"] = "58-60" }, Qc = new QcData { TemplateId = tpl, SteelGrade = tpl is null ? null : grade },
        };
        var hist = new[] { O("D1|1", "Cty A", "XYZ-9", "khuon"), O("D2|1", "Cty B", "XYZ-9", "gio") };
        var cur = O("D3|1", "Cty A", "XYZ-9");
        var (apply, filled, needs) = Workspace.PlanAutoFill(cur, null, hist);
        Assert.NotNull(apply);
        var q = new QcData(); apply!(q);
        Assert.Equal("khuon", q.TemplateId);                                     // theo đơn trước của CÙNG khách, cùng mác
        Assert.Contains(filled, f => f.Contains("đơn trước"));
        Assert.Contains(needs, n => n.Contains("Bảng mác"));
        var (_, _, needs2) = Workspace.PlanAutoFill(O("D4|1", "Cty C", "ABC"), null, hist);
        Assert.Contains(needs2, n => n.Contains("Chọn template"));
    }

    [Fact]
    public void Confirm_all_readings_fills_samples_ascending_and_today_as_check_date()
    {
        var (_, ws, read) = Setup();
        ws.Orders!.Apply(ws.Session!, ws.Orders.Preview(read), "erp.xlsx", _ => false);
        var key = read.Lines[0].Key;
        var r = ws.GetOrder(key)!;
        r = ws.EditQc(key, r.Version, x => { x.HardnessRequirement = "40-50"; x.TemplateId = "carbon"; }, "t");
        foreach (var f in new[] { "dyna2_47.4.png", "mitu1_47.3.png", "mitu_goc_38.2.jpg" })
            ws.AddImage(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", f))), Path.GetExtension(f).TrimStart('.'), "Hardness");
        r = ws.GetOrder(key)!;
        var readable = r.Order.Qc.Images.Where(i => i.OcrValue is not null).ToList();
        Assert.NotEmpty(readable);
        // QC sửa tay 1 ảnh trước → giữ giá trị QC, không bị ghi đè
        var first = r.Order.Qc.Images.OrderBy(i => i.AddedOrder).First();
        r = ws.ConfirmImage(key, r.Version, first.Id, "47.5");
        var res = ws.ConfirmAllReadings(key, r.Version);
        var q = res.Row.Order.Qc;
        Assert.Equal("47.5", q.Images.First(i => i.Id == first.Id).ConfirmedValue);
        var confirmed = q.Images.Where(i => i.ConfirmedValue is not null).Select(i => decimal.Parse(i.ConfirmedValue!, CultureInfo.InvariantCulture)).OrderBy(x => x)
            .Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList();
        Assert.True(res.SamplesFilled);
        Assert.Equal(confirmed, q.Hardness.Samples);                              // Mẫu 1–6 = số đã xác nhận, tăng dần
        Assert.Equal(DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), q.Hardness.CheckDate);
        Assert.Equal(q.Images.Count(i => i.ConfirmedValue is null), res.Skipped.Count);
        Assert.Equal("Xác nhận tất cả số đọc từ ảnh (2)", AuditText.EditWhat("image.confirm_all 2"));
        // xác nhận kết quả đo → tạo được CQ
        var (row2, ev) = ws.ConfirmHardness(key, res.Row.Version);
        Assert.NotNull(row2.Order.Qc.Hardness.ConfirmedAt);
        // lần 2: không còn gì để xác nhận, mẫu đã có → không đổi mẫu
        var again = ws.ConfirmAllReadings(key, row2.Version);
        Assert.Equal(confirmed, again.Row.Order.Qc.Hardness.Samples);
    }
}
