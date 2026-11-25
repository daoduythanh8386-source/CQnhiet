using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Pdf;
using Xunit;

namespace CqNl.Tests;

/// <summary>Tên viết tắt khách hàng (sổ THEO DÕI ĐƠN HÀNG), Mẫu 1–6 tự lấy từ ảnh, ảnh tự nhận loại, một nút in CQ 3 kiểu.</summary>
public class QuickCqAliasTests
{
    private static (string root, Workspace ws, ErpReadResult read) Setup()
    {
        var root = TD.NewDir("qc_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var ws = new Workspace(Path.Combine(root, "local"), store, AppEnv.Paths, "MAY-Q", TimeSpan.FromSeconds(10));
        Assert.True(ws.Probe());
        ws.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026");
        ws.Login("admin", "Admin@2026");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        ws.Orders!.Apply(ws.Session!, ws.Orders.Preview(read), "erp.xlsx", _ => false);
        return (root, ws, read);
    }

    [Fact]
    public void Customer_short_names_from_tracking_sheet_are_printed_on_cq_and_shown_in_list()
    {
        var (_, ws, read) = Setup();
        var scan = ws.ScanTrackingAliases(TD.P("theo_doi_mau.xlsx"));
        Assert.Equal("THEO DÕI ĐƠN HÀNG", scan.Sheet);
        string? P(string erpStart) => scan.Proposals.FirstOrDefault(p => p.ErpName.StartsWith(erpStart))?.ShortName;
        Assert.Equal("HTVG", P("Công ty Cổ phần công nghệ cao HTVG"));                    // ghép theo số ĐNGC
        Assert.Equal("ORC", P("Công ty TNHH ORC Việt Nam"));
        Assert.Equal("Công nghiệp Hà Nội", P("Công ty cổ phần đầu tư thương mại công nghiệp Hà Nội"));
        Assert.Contains(scan.Proposals, p => p.ShortName == "Takara" && p.Source.Contains("nằm trong"));   // dò tên viết tắt trong tên ERP
        Assert.True(ws.ApplyAliasProposals(scan.Proposals) >= 4);
        Assert.DoesNotContain(ws.ScanTrackingAliases(TD.P("theo_doi_mau.xlsx")).Proposals, p => p.ShortName == "HTVG");   // đã có → không đề xuất lại
        var key = read.Lines.First(l => l.Customer!.StartsWith("Công ty TNHH ORC")).Key;
        Assert.Equal("ORC", ws.CustomerOnCq(ws.GetOrder(key)!.Order));
        // sửa tay ở đơn thắng tên viết tắt; QC sửa tên viết tắt chung
        ws.SetCustomerAlias("Công ty TNHH ORC Việt Nam", "ORC VN");
        Assert.Equal("ORC VN", ws.CustomerOnCq(ws.GetOrder(key)!.Order));
        Assert.Throws<BusinessException>(() => ws.ScanTrackingAliases(TD.P("erp.xlsx")));
    }

    [Fact]
    public void Samples_follow_images_automatically_until_qc_types_by_hand_and_kind_is_detected()
    {
        var (_, ws, read) = Setup();
        var key = read.Lines[0].Key;
        var r = ws.GetOrder(key)!;
        ws.EditQc(key, r.Version, q => { q.HardnessRequirement = "40-50"; q.TemplateId = "carbon"; }, "t");
        var prod = ws.AddImageAuto(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))), "jpg");
        Assert.Equal("Sample", prod.Kind);                                                     // ảnh sản phẩm → ảnh xuất hàng
        var h1 = ws.AddImageAuto(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", "mitu1_47.3.png"))), "png");
        Assert.Equal("Hardness", h1.Kind);
        ws.AddImageAuto(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", "mitu_goc_38.2.jpg"))), "jpg");
        var q1 = ws.GetOrder(key)!.Order.Qc;
        Assert.True(q1.Hardness.SamplesAuto);
        Assert.Equal(Workspace.DerivedSamples(q1), q1.Hardness.Samples);
        Assert.Equal(q1.Hardness.Samples.OrderBy(x => decimal.Parse(x, System.Globalization.CultureInfo.InvariantCulture)), q1.Hardness.Samples);   // tăng dần
        Assert.NotNull(q1.Hardness.CheckDate);
        // QC sửa số trên 1 ảnh → mẫu tự cập nhật
        var row = ws.ConfirmImage(key, ws.GetOrder(key)!.Version, h1.Id, "47.6");
        Assert.Contains("47.6", row.Order.Qc.Hardness.Samples);
        // QC gõ tay mẫu → thôi tự động
        row = ws.SaveHardness(key, row.Version, new[] { "45 46", "47" }, _ => { });
        Assert.False(row.Order.Qc.Hardness.SamplesAuto);
        row = ws.ConfirmImage(key, row.Version, h1.Id, "47.7");
        Assert.Equal(new[] { "45 46", "47" }, row.Order.Qc.Hardness.Samples);
    }

    [Fact]
    public void One_button_cq_blank_stamp_and_signed_share_one_report_number()
    {
        if (!ExcelPdfExporter.IsAvailable()) return;
        var sigs = TD.P("sig");
        if (!Directory.Exists(sigs)) return;
        var (root, ws, read) = Setup();
        var s = ws.Session!;
        var ap = ws.Settings!.AddSigner(s, "Người duyệt", File.ReadAllBytes(Path.Combine(sigs, "approver.png")), "png");
        var ck = ws.Settings.AddSigner(s, "Đào Duy Thanh", File.ReadAllBytes(Path.Combine(sigs, "thanh_moi_goc.jpg")), "jpg");
        ws.Settings.SetStamp(s, File.ReadAllBytes(Path.Combine(sigs, "stamp.png")));
        var key = read.Lines[0].Key;
        ws.EditQc(key, ws.GetOrder(key)!.Version, q => { q.HardnessRequirement = "40-50"; q.TemplateId = "carbon"; q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" }; }, "t");
        foreach (var f in new[] { "mitu1_47.3.png", "dyna2_47.4.png" })
            ws.AddImageAuto(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", f))), "png");
        var dest = Path.Combine(root, "pdf");
        Assert.Throws<BusinessException>(() => ws.QuickCq(key, CqExportKind.Signed, dest));   // chưa đặt người ký mặc định
        Assert.Empty(ws.Cq!.ForOrder(key));                                                    // lỗi → KHÔNG cấp số báo cáo
        var blank = ws.QuickCq(key, CqExportKind.Blank, dest);
        Assert.True(File.Exists(blank.Pdf));
        Assert.Equal(CqState.Approved, blank.Cq.State);
        Assert.Contains(blank.Steps, x => x.StartsWith("Tạo CQ số"));
        Assert.All(ws.GetOrder(key)!.Order.Qc.Images.Where(i => i.Kind == "Hardness"), i => Assert.NotNull(i.ConfirmedValue));
        var stamp = ws.QuickCq(key, CqExportKind.StampOnly, dest);
        ws.Settings.SetDefaults(s, ap.Id, ck.Id);
        var signed = ws.QuickCq(key, CqExportKind.Signed, dest);
        Assert.Equal(blank.Cq.ReportNo, stamp.Cq.ReportNo);
        Assert.Equal(blank.Cq.ReportNo, signed.Cq.ReportNo);
        Assert.Equal(3, signed.Cq.Pdfs.Count);
        Assert.Equal(3, signed.Cq.Xlsxs.Count);
        // Xuất Excel để chỉnh sửa: cùng số báo cáo, cùng tên với PDF; dấu + chữ ký là ẢNH RỜI (không khoá) → kéo di chuyển được trong Excel
        var xl = ws.ExportCqExcel(key, dest, ap.Id, ck.Id);
        Assert.Equal(signed.Cq.ReportNo, xl.Cq.ReportNo);
        Assert.Equal(Path.ChangeExtension(signed.Pdf, ".xlsx"), xl.Xlsx);
        Assert.Contains(xl.Steps, x => x.Contains("bản đã duyệt"));
        static (int Pics, string Xml) Drawing(string xlsx)
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(xlsx);
            var xml = string.Concat(z.Entries.Where(e => e.FullName.StartsWith("xl/drawings/drawing") || e.FullName.StartsWith("xl/worksheets/sheet"))
                .Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
            return (System.Text.RegularExpressions.Regex.Matches(xml, "<xdr:pic>").Count, xml);
        }
        var (picsSigned, xmlSigned) = Drawing(xl.Xlsx);
        var (picsBlank, _) = Drawing(ws.ExportCq(signed.Cq, "xlsx:Blank", Path.Combine(root, "xb")));
        Assert.True(picsSigned - picsBlank >= 3, $"thiếu ảnh dấu/chữ ký: {picsSigned} vs {picsBlank}");   // con dấu + Approved + Checker
        Assert.DoesNotContain("noMove", xmlSigned);
        Assert.DoesNotContain("sheetProtection", xmlSigned);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e"));
        File.Copy(xl.Xlsx, Path.Combine(AppContext.BaseDirectory, "e2e", "app_quick_excel.xlsx"), true);
        // tên file chuẩn: Tháng-4 số cuối ĐNGC - khách - vật liệu - độ cứng yêu cầu
        var o = ws.GetOrder(key)!.Order;
        var digits = new string(key.Split('|')[0].Where(char.IsDigit).ToArray());
        Assert.Matches(@"^\d{2}-\d{4} - .+ - .+ - 40-50\.pdf$", Path.GetFileName(signed.Pdf));
        Assert.StartsWith($"{signed.Cq.ReportNo.Split('/')[1]}-{digits[^4..]} - {ws.CustomerOnCq(o)} - ", Path.GetFileName(signed.Pdf));
        Assert.EndsWith("(ky tay).pdf", blank.Pdf);
        // lưu vào <thư mục lưu CQ>\<tháng-năm>\ (vd 11-2026)
        var month = $"{signed.Cq.ReportNo.Split('/')[1]}-{signed.Cq.CreatedAt.Year}";
        Assert.Equal(month, Workspace.MonthFolderName(signed.Cq));
        Assert.Matches(@"^\d{2}-\d{4}$", month);
        foreach (var f in new[] { blank.Pdf, stamp.Pdf, signed.Pdf }) Assert.Equal(Path.Combine(dest, month), Path.GetDirectoryName(f));
        Assert.Single(ws.Cq!.ForOrder(key));
        foreach (var p in new[] { blank.Pdf, stamp.Pdf, signed.Pdf })
        {
            var chk = ExcelPdfExporter.Inspect(p);
            Assert.Equal(1, chk.Pages);
        }
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e"));
        File.Copy(signed.Pdf, Path.Combine(AppContext.BaseDirectory, "e2e", "app_quick_signed.pdf"), true);
        File.Copy(stamp.Pdf, Path.Combine(AppContext.BaseDirectory, "e2e", "app_quick_stamp.pdf"), true);
        File.Copy(blank.Pdf, Path.Combine(AppContext.BaseDirectory, "e2e", "app_quick_blank.pdf"), true);
        // in lại cùng kiểu → không tạo thêm
        Assert.Equal(signed.Cq.Pdfs["Signed"], ws.QuickCq(key, CqExportKind.Signed, dest).Cq.Pdfs["Signed"]);
        // sửa đơn sau khi phát hành → báo phải xoá CQ cũ trước
        ws.EditQc(key, ws.GetOrder(key)!.Version, q => q.Size = "100x50", "t");
        var ex = Assert.Throws<BusinessException>(() => ws.QuickCq(key, CqExportKind.Blank, dest));
        Assert.Contains(blank.Cq.ReportNo, ex.Message);
    }

    [Fact]
    public void Erp_note_column_is_read_and_adding_it_does_not_flag_orders_with_cq()
    {
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        Assert.Contains(read.Lines, l => l.Note == "VC 2C");
        // đơn nhập từ bản cũ (chưa có "Ghi chú") + đã có CQ → nhập lại: điền Ghi chú, KHÔNG gắn cờ "ERP thay đổi sau CQ"
        var line = read.Lines.First(l => l.Note == "VC 2C");
        var current = new Dictionary<string, IReadOnlyDictionary<string, string?>> { [line.Key] = line.ErpFields().Where(kv => kv.Key != "Note").ToDictionary(kv => kv.Key, kv => kv.Value) };
        var plan = ErpImportPlanner.Plan(current, new[] { line });
        Assert.Single(plan.Update);
        Assert.Null(plan.Update[0].Changes["Note"].Old);
    }

    [Fact]
    public void Template_from_erp_product_and_method_and_carburizing_heat()
    {
        Assert.Equal("carbon", Workspace.TemplateFromProduct("Thép cacbon SKS3", "THEPCACBON_SKS3 SP"));
        Assert.Equal("gio", Workspace.TemplateFromProduct("Thép gió SKH51", null));
        Assert.Equal("khuon", Workspace.TemplateFromProduct("Thép hợp kim SKD11", null));
        Assert.Null(Workspace.TemplateFromProduct("Nhôm 6061", null));
        OrderRecord O(string product, string method, string grade) => new()
        {
            Key = "D|1", Erp = new() { ["Customer"] = "A", ["ProductName"] = product, ["ProcessMethod"] = method, ["GradeFromErp"] = grade, ["HardnessRequirementErp"] = "58-60" },
        };
        // không có bảng mác → template theo tên sản phẩm; yêu cầu độ cứng + mác theo ERP
        var (apply, filled, _) = Workspace.PlanAutoFill(O("Thép gió SKH51", "DV nhiệt luyện Tôi và Ram", "SKH51"), null, Array.Empty<OrderRecord>());
        var q = new QcData(); apply!(q);
        Assert.Equal("gio", q.TemplateId); Assert.Equal("SKH51", q.SteelGrade); Assert.Equal("58-60", q.HardnessRequirement);
        Assert.Contains(filled, f => f.Contains("tên sản phẩm"));
        // có bảng mác: thấm carbon → nhiệt độ tôi 2 = 920℃ (như ô H14 của mẫu THÉP CACBON trong CQ 2026)
        var steel = SteelGradeReader.Read(TD.P("steel_grades.xlsx"));
        var carbonGrade = steel.Grades.First(g => g.TemplateMark == 3 && Workspace.HeatTexts(g).Count >= 2 && steel.Find(g.Name).Count == 1).Name;
        var (a2, f2, _) = Workspace.PlanAutoFill(O("Thép cacbon " + carbonGrade, "DV nhiệt luyện Thấm Carbon - Thuê Ngoài", carbonGrade), steel, Array.Empty<OrderRecord>());
        var q2 = new QcData(); a2!(q2);
        Assert.Equal("carbon", q2.TemplateId);
        Assert.Equal("920℃", q2.QuenchTemps![1]);
        Assert.Contains(f2, f => f.Contains("thấm carbon"));
        var (a3, _, _) = Workspace.PlanAutoFill(O("Thép cacbon " + carbonGrade, "DV nhiệt luyện Tôi thể tích - Thuê Ngoài", carbonGrade), steel, Array.Empty<OrderRecord>());
        var q3 = new QcData(); a3!(q3);
        Assert.Equal(Workspace.HeatTexts(steel.Find(carbonGrade)[0]), q3.QuenchTemps);                       // tôi thể tích: giữ theo bảng mác
        // thấm nitơ + có template "Thấm nitơ" → chọn template đó
        var tpls = new[] { new TemplateInfo("tpl-n", "Thấm Nitơ", false, true, "n.xlsx", "", true, "") };
        var (a4, _, _) = Workspace.PlanAutoFill(O("Thép hợp kim SKD61", "DV nhiệt luyện Thấm Nitor", "SKD61"), steel, Array.Empty<OrderRecord>(), tpls);
        var q4 = new QcData(); a4!(q4);
        Assert.Equal("tpl-n", q4.TemplateId);
    }
}
