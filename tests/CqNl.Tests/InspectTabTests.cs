using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Imaging;
using Xunit;

namespace CqNl.Tests;

/// <summary>Yêu cầu 16/11: mác ghi ở "Ghi chú" ERP (H13ESR), tab ảnh + kết quả đo gộp (đổi loại ảnh, nhập điểm đo CSV/Excel),
/// ảnh độ cứng trên CQ không in số bên dưới.</summary>
public class InspectTabTests
{
    private static SteelGradeTable Steel() => SteelGradeReader.Read(TD.P("steel_grades.xlsx"));

    [Fact]
    public void Grade_written_in_erp_note_is_used_and_esr_suffix_uses_base_grade_row()
    {
        var steel = Steel();
        Assert.Equal("H13ESR", Workspace.GradeFromNote("H13ESR", steel));
        Assert.Equal("H13ESR", Workspace.GradeFromNote("Hàng gấp, mác h13esr, 2 chi tiết", steel));
        Assert.Null(Workspace.GradeFromNote("VTS-824", steel));
        Assert.Null(Workspace.GradeFromNote("", steel));
        Assert.Null(Workspace.GradeFromNote("H13ESR", null));
        var h13 = Workspace.FindGrade(steel, "H13");
        Assert.NotEmpty(h13);
        Assert.Equal(h13.Select(g => g.SourceRow), Workspace.FindGrade(steel, "H13ESR").Select(g => g.SourceRow));

        // đơn ERP: không có mác, ghi chú "H13ESR", sản phẩm thép hợp kim → mác H13ESR, thông số + template theo dòng H13
        var o = new OrderRecord
        {
            Key = "DNGC/HY3-26110055|231579",
            Erp = new() { ["Customer"] = "Công Ty TNHH Công Nghệ Khuôn Mẫu Sanhexing (Việt Nam)", ["ProductName"] = "Thép hợp kim", ["ProductCode"] = "THEPHOPKIM SP",
                          ["HardnessRequirementErp"] = "46-48", ["Note"] = "H13ESR" },
        };
        var (apply, filled, needs) = Workspace.PlanAutoFill(o, steel, Array.Empty<OrderRecord>());
        Assert.NotNull(apply);
        var q = new QcData(); apply!(q);
        var g = h13[0];
        Assert.Equal("H13ESR", q.SteelGrade);
        Assert.Equal("46-48", q.HardnessRequirement);
        Assert.Equal(Workspace.HeatTexts(g), q.QuenchTemps);
        Assert.Equal(Workspace.RamTexts(g), q.RamValues);
        Assert.Equal(g.Tolerance!.Trim(), q.Tolerance);
        Assert.Equal(Workspace.TemplateFromMark(g.TemplateMark), q.TemplateId);
        Assert.Contains(filled, f => f.Contains("ghi chú ERP"));
        Assert.DoesNotContain(needs, n => n.Contains("không có trong bảng mác"));
    }

    [Fact]
    public void Samples_are_imported_from_csv_one_row_per_sample()
    {
        var d = TD.NewDir("csv_");
        var a = Path.Combine(d, "a.csv");
        File.WriteAllLines(a, new[] { "STT,Điểm 1,Điểm 2,Điểm 3", "1,56.1,56.8,57.0", "2,57.2,57.4", "", "3,58" });
        Assert.Equal(new[] { "56.1 56.8 57.0", "57.2 57.4", "58" }, Workspace.SamplesFromTable(a));
        var b = Path.Combine(d, "b.csv");                                            // kiểu VN: ; và dấu phẩy thập phân
        File.WriteAllLines(b, new[] { "Mẫu;Giá trị", "1;47,4;47,3", "2;46,9" });
        Assert.Equal(new[] { "47.4 47.3", "46.9" }, Workspace.SamplesFromTable(b));
        var many = Path.Combine(d, "m.txt");
        File.WriteAllLines(many, Enumerable.Range(0, 9).Select(i => $"{50 + i}.5"));
        Assert.Equal(6, Workspace.SamplesFromTable(many).Count);                     // tối đa 6 mẫu
        var none = Path.Combine(d, "n.csv"); File.WriteAllText(none, "không có số đo");
        Assert.Throws<BusinessException>(() => Workspace.SamplesFromTable(none));
        Assert.Throws<BusinessException>(() => Workspace.SamplesFromTable(Path.Combine(d, "x.pdf")));
    }

    [Fact]
    public void Hardness_tile_on_cq_has_no_value_band_under_the_photo()
    {
        // ảnh vuông đỏ: không nhãn → phủ kín ô vuông (đáy vẫn đỏ); có nhãn → đáy là dải trắng ghi số
        byte[] img;
        using (var red = new System.Drawing.Bitmap(200, 200))
        {
            using (var g = System.Drawing.Graphics.FromImage(red)) g.Clear(System.Drawing.Color.Red);
            using var ms = new MemoryStream(); red.Save(ms, System.Drawing.Imaging.ImageFormat.Png); img = ms.ToArray();
        }
        var with = CqImages.Tile(img, "47.4 HRC", 1.0);
        var without = CqImages.Tile(img, null, 1.0);
        Assert.Equal(CqImages.Size(with), CqImages.Size(without));
        using var bw = new System.Drawing.Bitmap(new MemoryStream(with));
        using var bn = new System.Drawing.Bitmap(new MemoryStream(without));
        var pw = bw.GetPixel(20, bw.Height - 12); var pn = bn.GetPixel(20, bn.Height - 12);
        Assert.True(pw.R > 240 && pw.G > 240 && pw.B > 240, "bản có số: đáy là dải nhãn trắng");
        Assert.True(pn.R > 200 && pn.G < 60 && pn.B < 60, "bản không số: ảnh phủ tới đáy ô (không có dải số)");
        Assert.Equal("Đổi loại ảnh → ảnh đo độ cứng", AuditText.EditWhat("image.kind abc Hardness"));
        Assert.Equal("Đổi loại ảnh → ảnh sản phẩm", AuditText.EditWhat("image.kind abc Sample"));
    }

    [Fact]
    public void Changing_image_kind_rereads_photo_and_clears_old_confirmation()
    {
        var root = TD.NewDir("kind_"); var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var ws = new Workspace(Path.Combine(root, "local"), store, AppEnv.Paths, "MAY-K", TimeSpan.FromSeconds(10));
        Assert.True(ws.Probe());
        ws.Auth!.CreateFirstAdmin("admin", "Quản trị", "Admin@2026"); ws.Login("admin", "Admin@2026");
        var read = ErpReader.Read(TD.P("erp.xlsx"));
        ws.Orders!.Apply(ws.Session!, ws.Orders.Preview(read), "erp.xlsx", _ => false);
        var key = read.Lines[0].Key;
        // ảnh máy đo bị thêm nhầm thành ảnh sản phẩm
        var im = ws.AddImage(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(TD.P(Path.Combine("img", "dyna2_47.4.png"))), "png", "Sample");
        var r = ws.ConfirmImage(key, ws.GetOrder(key)!.Version, im.Id, "12");
        Assert.Throws<BusinessException>(() => ws.ChangeImageKind(key, r.Version, im.Id, "Xyz"));
        r = ws.ChangeImageKind(key, r.Version, im.Id, "Hardness");
        var x = r.Order.Qc.Images.Single(i => i.Id == im.Id);
        Assert.Equal("Hardness", x.Kind);
        Assert.Null(x.ConfirmedValue);                                   // số cân cũ không thành số HRC
        Assert.NotNull(x.OcrValue);                                      // đọc lại theo loại máy đo
        Assert.Equal("47.4", x.OcrValue);
        Assert.Equal(im.AddedOrder, x.AddedOrder);
        Assert.Equal(Workspace.DerivedSamples(r.Order.Qc), r.Order.Qc.Hardness.Samples);   // Mẫu 1–6 tự cập nhật theo ảnh
        Assert.Single(r.Order.Qc.Hardness.Samples);
        Assert.Equal(r.Version, ws.ChangeImageKind(key, r.Version, im.Id, "Hardness").Version);   // cùng loại → không ghi
        Assert.Contains(ws.Audit!.Read(), n => n["detail"] is System.Text.Json.Nodes.JsonValue v && v.ToString() == $"image.kind {im.Id} Hardness");
    }
}
