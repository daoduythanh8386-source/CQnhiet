using CqNl.Application;
using CqNl.Domain;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Pdf;
using Xunit;

namespace CqNl.Tests;

/// <summary>Yêu cầu 16/11 tối: mẫu CQ Vinfast + Thấm Nitơ (tách từ CQ-2026.xlsx) có biểu mẫu nhập tay; thông số lò tự điền theo mác;
/// "Tạo file CQ nháp" → "Duyệt CQ"; xuất Excel không tự duyệt; ảnh sản phẩm tự nhận loại + số cân in trên CQ.</summary>
public class TemplatesManualTests
{
    private static (TemplateMap Map, byte[] Xlsx) Tpl(string id) => (TemplateMap.Load(TD.P(Path.Combine("maps", id + ".json"))), File.ReadAllBytes(TD.P(id + ".xlsx")));

    private static (string root, Workspace ws, ErpReadResult read) Setup()
    {
        var root = TD.NewDir("tm_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var ws = new Workspace(Path.Combine(root, "local"), store, AppEnv.Paths, "MAY-T", TimeSpan.FromSeconds(10));
        Assert.True(ws.Probe());
        ws.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026"); ws.Login("admin", "Admin@2026");
        ws.Settings!.SetSteelTable(ws.Session!, File.ReadAllBytes(TD.P("steel_grades.xlsx")), "steel_grades.xlsx");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        ws.Orders!.Apply(ws.Session!, ws.Orders.Preview(read), "erp.xlsx", _ => false);
        return (root, ws, read);
    }

    [Theory]
    [InlineData("vinfast", "Vinfast")]
    [InlineData("nito", "THẤM NITO ")]
    public void New_templates_from_cq2026_pass_template_check_and_carry_no_old_order_data(string id, string sheet)
    {
        var (map, xlsx) = Tpl(id);
        Assert.Equal(sheet, map.Sheet);
        var issues = TemplateCatalog.Check(xlsx, map.Json);
        Assert.True(issues.All(i => !i.Error), string.Join(" | ", issues.Select(i => i.Message)));
        var insp = new TemplateInspector(xlsx, sheet);
        Assert.Equal(new[] { sheet }, insp.Sheets);                                            // 1 sheet, không mang sổ theo dõi đơn hàng
        Assert.NotNull(insp.PrintArea);
        foreach (var c in new[] { "G6", "G7" }) Assert.True(string.IsNullOrWhiteSpace(insp.TextAt(c)), $"{c} còn dữ liệu đơn cũ: {insp.TextAt(c)}");
        Assert.DoesNotContain(insp.Cells.Values, x => (x.Text ?? "").Contains("VLOOKUP") || (x.Text ?? "").Contains("THEO DÕI"));
        Assert.NotEmpty(map.Manual);
    }

    [Fact]
    public void Vinfast_drawing_gets_steel_temperatures_in_right_places_and_manual_hold_times_and_ram_temperatures()
    {
        var (map, xlsx) = Tpl("vinfast");
        Assert.Equal(1, map.SteelGradeMark);
        var d = TemplateCatalog.SampleData(map);
        d.QuenchTemps = new() { "651℃", "852℃", "1033℃" };                                   // tôi 1, 2, 3 theo bảng mác
        d.RamValues = new() { "201p", "242p", "203p" };
        d.Manual["VfHold1"] = "3H"; d.Manual["VfHold2"] = "5H"; d.Manual["VfHold3"] = "1.5H";
        d.Manual["VfRamT1"] = "560℃"; d.Manual["VfRamT2"] = "610℃"; d.Manual["VfRamT3"] = "540℃";
        var outx = CqWriter.Build(xlsx, map, d);
        var t = new TemplateInspector(outx, map.Sheet).DrawingTexts.Where(x => x.Trim().Length > 0).ToList();
        // hình gốc theo thứ tự: 1030, 650, 850, 4H, 4H, 2H, 210p, 240p, 210p, 550, 600, 550
        Assert.Equal(new[] { "1033℃", "651℃", "852℃", "3H", "5H", "1.5H", "201p", "242p", "203p", "560℃", "610℃", "540℃" },
            t.Where(x => x.EndsWith("℃") || x.EndsWith("H") || x.EndsWith("p")).ToArray());
        // để trống thông số nhập tay → giữ giá trị in sẵn của mẫu
        var d2 = TemplateCatalog.SampleData(map); d2.QuenchTemps = new() { "650℃", "850℃", "1030℃" }; d2.RamValues = new() { "210p", "240p", "210p" };
        foreach (var k in d2.Manual.Keys.ToList()) d2.Manual[k] = null;
        var t2 = new TemplateInspector(CqWriter.Build(xlsx, map, d2), map.Sheet).DrawingTexts;
        Assert.Equal(2, t2.Count(x => x == "4H")); Assert.Contains("2H", t2); Assert.Equal(2, t2.Count(x => x == "550℃"));
    }

    [Fact]
    public void Nitriding_manual_values_go_to_requirement_and_result_cells_and_heat_chart()
    {
        var (map, xlsx) = Tpl("nito");
        Assert.Null(map.SteelGradeMark);
        var d = TemplateCatalog.SampleData(map);
        d.Manual["NitPreTemp"] = "420℃"; d.Manual["NitTemp"] = "530℃"; d.Manual["NitPreTime"] = "3H"; d.Manual["NitTime"] = "24H";
        d.Manual["NitDepthReq"] = "0.08-0.12mm"; d.Manual["NitDepthRes"] = "0.1mm (100µm)";
        d.Manual["NitCoatReq"] = null; d.Manual["NitCoatRes"] = "0.02mm (20µm)";
        d.Manual["NitSurfReq"] = "≈43-47 HRC"; d.Manual["NitSurfRes"] = "446-460 HV ≈ 45.5-47.0 HRC";
        var i = new TemplateInspector(CqWriter.Build(xlsx, map, d), map.Sheet);
        Assert.Equal("1. Chiều sâu lớp thấm: 0.08-0.12mm", i.TextAt("A26"));
        Assert.Equal("0.1mm (100µm)", i.TextAt("J26"));
        Assert.Equal("2. Độ dày lớp phủ:", i.TextAt("A28")?.TrimEnd());                         // không nhập → giữ nhãn
        Assert.Equal("3. Độ cứng bề mặt: ≈43-47 HRC", i.TextAt("A30"));
        Assert.Equal("446-460 HV ≈ 45.5-47.0 HRC", i.TextAt("J30"));
        Assert.Equal("CÔNG TY MẪU", i.TextAt("G6"));
        foreach (var x in new[] { "420℃", "530℃", "3H", "24H" }) Assert.Contains(x, i.DrawingTexts);
        Assert.DoesNotContain("525℃", i.DrawingTexts);
        // bắt buộc: kết quả chiều sâu lớp thấm + độ cứng bề mặt
        d.Manual["NitDepthRes"] = " ";
        var ex = Assert.Throws<CqBuildException>(() => CqWriter.Build(xlsx, map, d));
        Assert.Contains("Chiều sâu lớp thấm — kết quả", ex.Message);
    }

    [Fact]
    public void Manual_defaults_use_requirement_and_measured_min_max()
    {
        var map = Tpl("nito").Map;
        var o = new OrderRecord { Key = "D|1", Erp = new() { ["HardnessRequirementErp"] = "43-47" } };
        o.Qc.Hardness.Samples = new() { "45.5", "47", "46.2" };
        var ev = ManualValues.Eval(o);
        Assert.Equal("≈43-47 HRC", ManualValues.Value(map.Manual.Single(m => m.Key == "NitSurfReq"), o, ev));
        Assert.Equal("≈45.5-47.0 HRC", ManualValues.Value(map.Manual.Single(m => m.Key == "NitSurfRes"), o, ev));
        Assert.Equal("525℃", ManualValues.Value(map.Manual.Single(m => m.Key == "NitTemp"), o, ev));
        Assert.Null(ManualValues.Value(map.Manual.Single(m => m.Key == "NitDepthRes"), o, ev));
        o.Qc.Manual = new() { ["NitTemp"] = "540℃", ["NitCoatRes"] = "" };
        Assert.Equal("540℃", ManualValues.Value(map.Manual.Single(m => m.Key == "NitTemp"), o, ev));
        Assert.Null(ManualValues.Value(map.Manual.Single(m => m.Key == "NitCoatRes"), o, ev));   // xoá trắng có chủ ý
        o.Qc.Hardness.Samples = new();
        Assert.Null(ManualValues.Value(map.Manual.Single(m => m.Key == "NitSurfRes"), o, ManualValues.Eval(o)));   // chưa đo → không in "≈- HRC"
    }

    [Fact]
    public void Template_is_chosen_from_customer_vinfast_and_process_nitriding_and_steel_parameters_still_fill()
    {
        var (_, ws, _) = Setup();
        var steel = ws.SteelTable(); var tpls = ws.TemplateList();
        Assert.Contains(tpls, t => t.Id == "vinfast" && t.Status == "Sẵn sàng");
        Assert.Contains(tpls, t => t.Id == "nito" && t.Status == "Sẵn sàng");
        var vf = new OrderRecord { Key = "V|1", Erp = new() { ["Customer"] = "CÔNG TY TNHH SẢN XUẤT VÀ KINH DOANH VINFAST", ["GradeFromErp"] = "H13", ["HardnessRequirementErp"] = "46-48", ["ProductName"] = "Thép hợp kim" } };
        var (a1, f1, _) = Workspace.PlanAutoFill(vf, steel, Array.Empty<OrderRecord>(), tpls);
        var q1 = new QcData(); a1!(q1);
        Assert.Equal("vinfast", q1.TemplateId);
        var h13 = Workspace.FindGrade(steel, "H13")[0];
        Assert.Equal(Workspace.HeatTexts(h13), q1.QuenchTemps);
        Assert.Equal(Workspace.RamTexts(h13), q1.RamValues);
        Assert.Contains(f1, x => x.Contains("Vinfast"));
        var nt = new OrderRecord { Key = "N|1", Erp = new() { ["Customer"] = "Cty A", ["GradeFromErp"] = "SKD61", ["HardnessRequirementErp"] = "43-47", ["ProcessMethod"] = "Thấm Nitơ" } };
        var (a2, _, _) = Workspace.PlanAutoFill(nt, steel, Array.Empty<OrderRecord>(), tpls);
        var q2 = new QcData(); a2!(q2);
        Assert.Equal("nito", q2.TemplateId);
    }

    [Fact]
    public void Product_photo_is_detected_by_scale_and_draft_confirms_weight_so_it_is_printed_on_cq()
    {
        var (_, ws, read) = Setup();
        Assert.Equal("Sample", ImageService.DetectKind(File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))), AppEnv.Paths));
        foreach (var f in new[] { "mitu1_47.3.png", "mitu_goc_38.2.jpg", "mitu_full2_58.4.png", "dyna1_47.3.png", "dyna2_47.4.png" })
            Assert.True(ImageService.DetectKind(File.ReadAllBytes(TD.P(Path.Combine("img", f))), AppEnv.Paths) == "Hardness", f);
        var key = read.Lines[0].Key;
        ws.EditQc(key, ws.GetOrder(key)!.Version, q => { q.HardnessRequirement = "40-50"; q.TemplateId = "carbon"; q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" }; }, "t");
        var prod = ws.AddImageAuto(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", "sanpham_goc.jpg"))), "jpg");
        Assert.Equal("Sample", prod.Kind);
        Assert.Equal("41.40", prod.OcrValue);
        Assert.NotNull(prod.NormalizedSha);                                                   // đã cắt + làm sáng
        ws.AddImageAuto(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", "mitu1_47.3.png"))), "png");
        var (cq, steps) = ws.PrepareCq(key);                                                  // "Tạo file CQ nháp"
        Assert.Equal(CqState.Draft, cq.State);
        Assert.Contains(steps, s => s.StartsWith("Xác nhận 2 số đọc"));
        Assert.Equal("41.40", ws.GetOrder(key)!.Order.Qc.Images.Single(i => i.Id == prod.Id).ConfirmedValue);
        Assert.DoesNotContain(cq.Warnings, w => w.Contains("số cân"));                        // số cân được ghi trên ảnh sản phẩm
        Assert.True(ws.Cq!.CanApprove(cq.Id));
        Assert.Equal(cq.Id, ws.PrepareCq(key).Cq.Id);                                         // gọi lại → dùng đúng bản nháp, không cấp số mới
        if (!ExcelPdfExporter.IsAvailable()) return;
        var (_, pdf) = ws.Cq.RenderDraft(ws.Session!, cq.Id);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e"));
        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "e2e", "app_nhap_anh_san_pham_so_can.pdf"), pdf);
    }

    [Fact]
    public void Draft_then_approve_flow_excel_export_does_not_approve_and_blank_print_still_allows_approval()
    {
        if (!ExcelPdfExporter.IsAvailable()) return;
        var sigs = TD.P("sig"); if (!Directory.Exists(sigs)) return;
        var (root, ws, read) = Setup();
        var s = ws.Session!;
        var ap = ws.Settings!.AddSigner(s, "Người duyệt", File.ReadAllBytes(Path.Combine(sigs, "approver.png")), "png");
        var ck = ws.Settings.AddSigner(s, "Đào Duy Thanh", File.ReadAllBytes(Path.Combine(sigs, "thanh_moi_goc.jpg")), "jpg");
        ws.Settings.SetStamp(s, File.ReadAllBytes(Path.Combine(sigs, "stamp.png")));
        var dest = Path.Combine(root, "pdf");
        foreach (var (tid, grade) in new[] { ("nito", "H13"), ("vinfast", "H13") })
        {
            var key = read.Lines[tid == "nito" ? 2 : 3].Key;
            var g = Workspace.FindGrade(ws.SteelTable(), grade)[0];
            ws.EditQc(key, ws.GetOrder(key)!.Version, q =>
            {
                q.SteelGrade = grade; q.TemplateId = tid; q.HardnessRequirement = tid == "nito" ? "43-47" : "46-48"; q.Tolerance = g.Tolerance;
                q.QuenchTemps = Workspace.HeatTexts(g); q.RamValues = Workspace.RamTexts(g);
                if (tid == "nito") q.Manual = new() { ["NitDepthReq"] = "0.08-0.12mm", ["NitDepthRes"] = "0.1mm (100µm)", ["NitCoatRes"] = "0.02mm (20µm)" };
            }, "t");
            ws.SaveHardness(key, ws.GetOrder(key)!.Version, tid == "nito" ? new[] { "45.5", "46.8", "47" } : new[] { "47", "47.5" }, h => h.CheckDate = "16/11/2026");
            foreach (var f in new[] { "sanpham_goc.jpg", "mitu1_47.3.png" })
                ws.AddImageAuto(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", f))), Path.GetExtension(f).TrimStart('.'));
            if (tid == "nito") ws.AddImage(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", "ref_sanpham_da_chinh.jpg"))), "jpg", "Other");
            // Xuất Excel: tạo nháp nếu chưa có, KHÔNG duyệt; có thêm dấu + 2 chữ ký (ảnh rời)
            var xl = ws.ExportCqExcel(key, dest, ap.Id, ck.Id);
            Assert.True(File.Exists(xl.Xlsx));
            var cq = ws.Cq!.ForOrder(key).Single();
            Assert.Equal(CqState.Draft, cq.State);
            // bản không dấu (ký tay) → CQ phát hành nhưng VẪN duyệt được bản có dấu + chữ ký, cùng số
            var blank = ws.QuickCq(key, CqExportKind.Blank, dest);
            Assert.True(ws.Cq.CanApprove(cq.Id));
            Assert.NotEmpty(ws.Cq.PreviewApproval(s, cq.Id, ap, ck, ws.Settings.Stamp()!, null));
            var approved = ws.Cq.Approve(s, cq.Id, ap, ck, ws.Settings.Stamp()!, null);
            Assert.False(ws.Cq.CanApprove(cq.Id));
            Assert.Equal(blank.Cq.ReportNo, approved.ReportNo);
            var pdf = ws.ExportCqNamed(approved, ws.GetOrder(key)!.Order, CqExportKind.Signed, dest);
            Assert.Equal(1, ExcelPdfExporter.Inspect(pdf).Pages);
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e"));
            File.Copy(pdf, Path.Combine(AppContext.BaseDirectory, "e2e", $"app_{tid}_duyet.pdf"), true);
            File.Copy(xl.Xlsx, Path.Combine(AppContext.BaseDirectory, "e2e", $"app_{tid}_excel.xlsx"), true);
            // sau khi duyệt: xuất Excel trả đúng file Excel bản duyệt
            var xl2 = ws.ExportCqExcel(key, Path.Combine(root, "x2"), ap.Id, ck.Id);
            Assert.Contains(xl2.Steps, x => x.Contains("bản đã duyệt"));
        }
    }

    /// <summary>Yêu cầu 23/11: sửa nội dung CQ trong Excel rồi mới xuất PDF. File sửa tay phải đúng CQ (còn số báo cáo); PDF lấy đúng
    /// file đã sửa; CQ ghi "sửa tay" + nhật ký; Kinh doanh không làm được.</summary>
    [Fact]
    public void Approve_from_hand_edited_excel_uses_edited_file_and_is_traced()
    {
        if (!ExcelPdfExporter.IsAvailable()) return;
        var sigs = TD.P("sig"); if (!Directory.Exists(sigs)) return;
        var (root, ws, read) = Setup();
        var s = ws.Session!;
        var ap = ws.Settings!.AddSigner(s, "Người duyệt", File.ReadAllBytes(Path.Combine(sigs, "approver.png")), "png");
        var ck = ws.Settings.AddSigner(s, "Đào Duy Thanh", File.ReadAllBytes(Path.Combine(sigs, "thanh_moi_goc.jpg")), "jpg");
        ws.Settings.SetStamp(s, File.ReadAllBytes(Path.Combine(sigs, "stamp.png")));
        var key = read.Lines[0].Key;
        ws.EditQc(key, ws.GetOrder(key)!.Version, q => { q.HardnessRequirement = "40-50"; q.TemplateId = "carbon"; q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" }; }, "t");
        ws.SaveHardness(key, ws.GetOrder(key)!.Version, new[] { "45", "46.5" }, h => h.CheckDate = "23/11/2026");
        var (cq, _) = ws.PrepareCq(key);
        var signed = ws.Cq!.SignedXlsx(s, cq.Id, ap, ck, ws.Settings.Stamp()!, null);
        // "sửa tay": đổi một chuỗi trong file (mô phỏng người dùng gõ lại nội dung trong Excel)
        var dngc = key.Split('|')[0];
        Assert.True(CqWriter.ContainsText(signed, dngc), "CQ phải có số ĐNGC " + dngc);
        var edited = EditText(signed, dngc, dngc + " (đã sửa tay)");
        Assert.True(CqWriter.ContainsText(edited, dngc + " (đã sửa tay)"));
        Assert.Throws<BusinessException>(() => ws.Cq.PdfFromEditedXlsx(s, cq.Id, EditText(edited, cq.ReportNo, "999/99")));   // nhầm file khác số
        Assert.Throws<BusinessException>(() => ws.Cq.PdfFromEditedXlsx(s, cq.Id, new byte[] { 1, 2, 3, 4 }));
        Assert.NotEmpty(ws.Cq.PdfFromEditedXlsx(s, cq.Id, edited));
        var done = ws.Cq.ApproveEdited(s, cq.Id, edited, ap, ck);
        Assert.Equal(CqState.Approved, done.State);
        Assert.Contains(done.Warnings, w => w.Contains("SỬA TAY"));
        Assert.Equal(CqNl.Infrastructure.Storage.FileStore.Sha256Hex(edited), done.Xlsxs["Signed"]);
        Assert.False(ws.Cq.CanApprove(cq.Id));
        Assert.Throws<BusinessException>(() => ws.Cq.ApproveEdited(s, cq.Id, edited, ap, ck));          // không duyệt lại
        var pdf = ws.ExportCqNamed(done, ws.GetOrder(key)!.Order, CqExportKind.Signed, Path.Combine(root, "pdf"));
        Assert.Equal(1, ExcelPdfExporter.Inspect(pdf).Pages);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e"));
        File.Copy(pdf, Path.Combine(AppContext.BaseDirectory, "e2e", "app_sua_tay_duyet.pdf"), true);
    }

    /// <summary>Yêu cầu 25/11: MỌI kiểu bản in (có dấu chưa ký, ký tay, có dấu + chữ ký) đều sửa tay được trong Excel trước khi in.
    /// Luồng như người dùng: bước 1 tạo file "- sua.xlsx" trong thư mục lưu CQ → sửa + lưu → bước 2 tạo PDF đúng file đó, lưu cùng CQ + lịch sử.</summary>
    [Fact]
    public void Every_print_kind_can_be_hand_edited_in_excel_before_pdf()
    {
        if (!ExcelPdfExporter.IsAvailable()) return;
        var sigs = TD.P("sig"); if (!Directory.Exists(sigs)) return;
        var (root, ws, read) = Setup();
        var s = ws.Session!;
        var ap = ws.Settings!.AddSigner(s, "Người duyệt", File.ReadAllBytes(Path.Combine(sigs, "approver.png")), "png");
        var ck = ws.Settings.AddSigner(s, "Đào Duy Thanh", File.ReadAllBytes(Path.Combine(sigs, "thanh_moi_goc.jpg")), "jpg");
        ws.Settings.SetStamp(s, File.ReadAllBytes(Path.Combine(sigs, "stamp.png")));
        var key = read.Lines[0].Key; var dngc = key.Split('|')[0];
        ws.EditQc(key, ws.GetOrder(key)!.Version, q => { q.HardnessRequirement = "40-50"; q.TemplateId = "carbon"; q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" }; }, "t");
        ws.SaveHardness(key, ws.GetOrder(key)!.Version, new[] { "45", "46.5" }, h => h.CheckDate = "25/11/2026");
        var save = Path.Combine(root, "File CQ");
        foreach (var kind in new[] { CqExportKind.StampOnly, CqExportKind.Blank, CqExportKind.Signed })
        {
            var (cq, x, _) = ws.EditableCqXlsx(key, kind, save, ap.Id, ck.Id);
            Assert.EndsWith(" - sua.xlsx", x);
            Assert.True(CqWriter.ContainsText(File.ReadAllBytes(x), cq.ReportNo));
            var mark = $"{dngc} (sửa tay {kind})";
            File.WriteAllBytes(x, EditText(File.ReadAllBytes(x), dngc, mark));                  // người dùng sửa + Ctrl+S
            Assert.Equal(x, ws.EditableCqXlsx(key, kind, save, ap.Id, ck.Id).Xlsx);              // mở lại: KHÔNG ghi đè phần đã sửa
            Assert.True(CqWriter.ContainsText(File.ReadAllBytes(x), mark));
            var (done, pdf) = ws.IssueFromEditedFile(cq.Id, kind, x, save, ap.Id, ck.Id);
            Assert.True(File.Exists(pdf)); Assert.Equal(1, ExcelPdfExporter.Inspect(pdf).Pages);
            Assert.Equal(CqNl.Infrastructure.Storage.FileStore.Sha256Hex(File.ReadAllBytes(x)), done.Xlsxs[kind.ToString()]);
            Assert.Contains(done.EditedPrints, e => e.Kind == kind.ToString() && e.By == "admin");
            Assert.Contains(done.Warnings, w => w.Contains("SỬA TAY"));
            Assert.Equal(cq.ReportNo, done.ReportNo);                                            // cùng một CQ, cùng số
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e"));
            File.Copy(pdf, Path.Combine(AppContext.BaseDirectory, "e2e", $"app_sua_tay_{kind}.pdf"), true);
        }
        // bản ký tay sửa lại lần 2 → thay PDF, bản trước còn trong lịch sử
        var c1 = ws.Cq!.ForOrder(key).Single(c => c.State != CqState.Deleted);
        var x2 = ws.EditableCqXlsx(key, CqExportKind.Blank, save, null, null).Xlsx;
        File.WriteAllBytes(x2, EditText(File.ReadAllBytes(x2), "(sửa tay Blank)", "(sửa tay lần 2)"));
        var (c2, _) = ws.IssueFromEditedFile(c1.Id, CqExportKind.Blank, x2, save, null, null);
        var hist = c2.EditedPrints.Where(e => e.Kind == "Blank").ToList();
        Assert.Equal(2, hist.Count);
        Assert.Equal(hist[0].PdfSha, hist[1].PreviousPdfSha);
        Assert.NotEqual(hist[0].PdfSha, c2.Pdfs["Blank"]);
        // bản duyệt (có dấu + chữ ký) đã phát hành là bất biến
        Assert.Throws<BusinessException>(() => ws.IssueFromEditedFile(c1.Id, CqExportKind.Signed, ws.EditableCqXlsx(key, CqExportKind.Signed, save, ap.Id, ck.Id).Xlsx, save, ap.Id, ck.Id));
        // file sai CQ (mất số báo cáo) bị từ chối
        File.WriteAllBytes(x2, EditText(File.ReadAllBytes(x2), c1.ReportNo, "999/99"));
        Assert.Throws<BusinessException>(() => ws.IssueFromEditedFile(c1.Id, CqExportKind.Blank, x2, save, null, null));
    }

    /// <summary>Thay chuỗi trong các phần XML của file xlsx (sharedStrings / sheet) — mô phỏng sửa nội dung trong Excel.</summary>
    private static byte[] EditText(byte[] xlsx, string from, string to)
    {
        var ms = new MemoryStream(); ms.Write(xlsx); ms.Position = 0;
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Update, true))
        {
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("xl/") && e.FullName.EndsWith(".xml")).ToList())
            {
                string t; using (var r = new StreamReader(e.Open())) t = r.ReadToEnd();
                if (!t.Contains(from)) continue;
                var name = e.FullName; e.Delete();
                var ne = zip.CreateEntry(name); using var w = new StreamWriter(ne.Open()); w.Write(t.Replace(from, to));
            }
        }
        return ms.ToArray();
    }
}
