using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CqNl.Infrastructure.Erp;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Security;
using CqNl.Infrastructure.Storage;
using Xunit;

namespace CqNl.Tests;

internal static class TD
{
    public static string P(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);
    public static string NewDir(string prefix) { var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
}

public class StoreTests
{
    [Fact]
    public void Cas_idempotent_conflict_integrity()
    {
        var root = TD.NewDir("kho_");
        var a = new FileStore(root, "MAY-A"); var b = new FileStore(root, "MAY-B");
        Assert.Equal(1, a.Commit("order", "DNGC/1|1", new JsonObject { ["x"] = 1 }, 0, "qc1", "op1"));
        Assert.Equal(1, a.Commit("order", "DNGC/1|1", new JsonObject { ["x"] = 1 }, 0, "qc1", "op1"));   // gửi lại → không tạo trùng
        Assert.Equal(2, b.Commit("order", "DNGC/1|1", new JsonObject { ["x"] = 2 }, 1, "qc2", "op2"));
        Assert.Throws<ConflictException>(() => a.Commit("order", "DNGC/1|1", new JsonObject { ["x"] = 3 }, 1, "qc1", "op3"));
        Assert.Equal(2, (int)a.Read("order", "DNGC/1|1")!.Data["x"]!);
        Assert.Equal(1, a.Commit("order", "idem", new JsonObject { ["a"] = 1 }, 0, "qc1", "OP-X"));
        b.Commit("order", "idem", new JsonObject { ["a"] = 2 }, 1, "qc2", "OP-Y");
        Assert.Equal(1, a.Commit("order", "idem", new JsonObject { ["a"] = 1 }, 0, "qc1", "OP-X"));       // đã áp dụng dù B ghi tiếp
        var f = Directory.GetFiles(Path.Combine(root, "records", "order"), "v000002.json", SearchOption.AllDirectories).First(p => p.Contains("DNGC"));
        File.WriteAllText(f, File.ReadAllText(f).Replace("\"x\":2", "\"x\":99"));
        Assert.Throws<IntegrityException>(() => a.Read("order", "DNGC/1|1"));
    }

    [Fact]
    public void Ten_writers_no_lost_update()
    {
        var root = TD.NewDir("kho_race_");
        int ok = 0, conflicts = 0;
        Parallel.For(0, 10, i =>
        {
            var s = new FileStore(root, $"MAY-{i}");
            for (var k = 0; k < 20; k++)
            {
                var vs = s.Versions("order", "race"); var bas = vs.Count == 0 ? 0 : vs[^1];
                try { s.Commit("order", "race", new JsonObject { ["by"] = i, ["base"] = bas }, bas, $"u{i}", Guid.NewGuid().ToString()); Interlocked.Increment(ref ok); }
                catch (ConflictException) { Interlocked.Increment(ref conflicts); }
            }
        });
        var st = new FileStore(root);
        var all = st.Versions("order", "race");
        Assert.Equal(Enumerable.Range(1, ok), all);
        foreach (var v in all) Assert.Equal(v - 1, (int)st.Read("order", "race", v)!.Data["base"]!);
        Assert.Equal(200, ok + conflicts);
    }

    [Fact]
    public void Report_numbers_parallel_unique()
    {
        var root = TD.NewDir("kho_rp_");
        var nums = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 10, i => { var al = new ReportNumberAllocator(new FileStore(root, $"M{i}")); for (var k = 0; k < 10; k++) nums.Add(al.Claim(2026, 11, $"cq{i}-{k}")); });
        Assert.Equal(100, nums.Distinct().Count());
        Assert.Equal("001/11", nums.Min()); Assert.Equal("100/11", nums.Max());
    }

    [Fact]
    public void Lease_lock_takeover_single_winner()
    {
        var root = TD.NewDir("kho_lock_");
        var a = new LeaseLocks(new FileStore(root, "MAY-A"));
        var t0 = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.FromHours(7));
        Assert.True(a.Acquire("order-X", "qc1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), t0).Acquired);
        var b = new LeaseLocks(new FileStore(root, "MAY-B"));
        var r = b.Acquire("order-X", "qc2", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), t0.AddMinutes(1));
        Assert.False(r.Acquired); Assert.Contains("qc1", r.Message);
        for (var round = 0; round < 20; round++)      // lặp nhiều vòng để bắt lỗi tranh chấp (đã từng có 2 máy cùng thắng)
        {
            var key = "order-R" + round;
            a.Acquire(key, "qc1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), t0);
            var wins = 0;
            Parallel.For(0, 10, i => { if (new LeaseLocks(new FileStore(root, $"R{i}")).Acquire(key, $"u{i}", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), t0.AddMinutes(30)).Acquired) Interlocked.Increment(ref wins); });
            Assert.Equal(1, wins);
        }
    }

    [Fact]
    public void Outbox_offline_then_online_with_conflict()
    {
        var root = TD.NewDir("kho_ob_");
        var a = new FileStore(root, "MAY-A"); var b = new FileStore(root, "MAY-B");
        var ob = new Outbox(TD.NewDir("outbox_"));
        ob.Add("order", "D2", new JsonObject { ["kq"] = "58.4" }, 0, "qc1");
        ob.Add("order", "D3", new JsonObject { ["kq"] = "47.3" }, 0, "qc1");
        ob.Add("order", "D4", new JsonObject { ["s"] = 1 }, 0, "qc1");
        ob.Add("order", "D4", new JsonObject { ["s"] = 2 }, 1, "qc1");
        var off = ob.Sync(() => null);
        Assert.True(off.Offline); Assert.Equal(4, off.Pending);
        b.Commit("order", "D3", new JsonObject { ["kq"] = "47.5" }, 0, "qc2", "x");
        var on = ob.Sync(() => a);
        Assert.Equal(3, on.Pushed); Assert.Equal(1, on.Conflicts);
        Assert.Equal("47.5", (string)a.Read("order", "D3")!.Data["kq"]!);
        Assert.Equal(2, (int)a.Read("order", "D4")!.Data["s"]!);
        Assert.True(File.Exists(ob.Items().Single().ConflictFile));
    }
}

public class ErpTests
{
    [Fact]
    public void Reads_both_formats_and_never_touches_qc_data()
    {
        var x = ErpReader.Read(TD.P("erp.xlsx")); var h = ErpReader.Read(TD.P("erp_html.xls"));
        Assert.Equal(484, x.Lines.Count); Assert.Empty(x.Rejected);
        Assert.Equal(17, h.Lines.Count);
        Assert.Single(x.Lines, l => l.Warnings.Any(w => w.Contains("Excel đổi thành ngày")));
        Assert.Equal(92, x.Lines.Count(l => l.HardnessRequirementErp is null));
        var current = x.Lines.ToDictionary(l => l.Key, l => l.ErpFields());
        var again = ErpImportPlanner.Plan(current, x.Lines);
        Assert.Equal(484, again.Same.Count); Assert.Empty(again.Add); Assert.Empty(again.Update);
        var plan = ErpImportPlanner.Plan(current, h.Lines);
        Assert.Equal(8, plan.Add.Count); Assert.Equal(9, plan.Same.Count); Assert.Empty(plan.Update);   // 9 dòng chồng lấn giống hệt giữa 2 định dạng
    }

    [Fact]
    public void Rejects_non_mf0015()
    {
        var f = Path.Combine(TD.NewDir("erp_"), "x.xls"); File.WriteAllText(f, "<html><table><tr><td>abc</td></tr></table></html>");
        Assert.Throws<InvalidDataException>(() => ErpReader.Read(f));
    }
}

public class SteelGradeTests
{
    [Fact]
    public void Reports_duplicates_and_missing()
    {
        var t = SteelGradeReader.Read(TD.P("steel_grades.xlsx"));
        Assert.Contains(t.Issues, i => i.Contains("M390") && i.Contains("KHÁC"));
        Assert.Contains(t.Issues, i => i.Contains("SCM420") && i.Contains("KHÁC"));
        Assert.Contains(t.Issues, i => i.Contains("W302") && i.Contains("cùng giá trị"));
        Assert.Contains(t.Issues, i => i.Contains("S136H") && i.Contains("thiếu dung sai"));
        Assert.Equal(2, t.Find("M390").Count);
        Assert.Single(t.Find("s-star"));            // ERP 'S-Star' ↔ bảng 'S STAR'
        var skh9 = t.Find("SKH9").Single();
        Assert.Equal(new[] { "650℃", "850℃", "1000℃", "1180℃" }, skh9.Heats);
        Assert.Equal(2, skh9.TemplateMark);
    }
}

public class SecurityTests
{
    [Fact]
    public void Hash_verify_and_rules()
    {
        var h = PasswordHasher.Hash("Mk@12345");
        Assert.True(PasswordHasher.Verify("Mk@12345", h)); Assert.False(PasswordHasher.Verify("sai", h));
        Assert.DoesNotContain("Mk@12345", h);
        Assert.NotNull(PasswordHasher.ValidateNew("12345", "12345", null));
        Assert.NotNull(PasswordHasher.ValidateNew("Mk@12345", "Mk@12345", h));
        Assert.Null(PasswordHasher.ValidateNew("MatKhauMoi1", "MatKhauMoi1", h));
    }
}

public class CqWriterTests
{
    private static byte[] Png(int w, int h)
    {   // PNG tối thiểu hợp lệ (1 màu) để kiểm tra bố cục — không phải dữ liệu nghiệp vụ
        using var bmp = new System.Drawing.Bitmap(w, h); using var g = System.Drawing.Graphics.FromImage(bmp); g.Clear(System.Drawing.Color.Gray);
        using var ms = new MemoryStream(); bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray();
    }

    [Theory]
    [InlineData("carbon", new[] { "650℃", "800℃" })]
    [InlineData("khuon", new[] { "650℃", "850℃", "1080℃" })]
    [InlineData("gio", new[] { "650℃", "850℃", "1000℃", "1180℃" })]
    public void Only_mapped_cells_and_added_images_change(string tid, string[] temps)
    {
        var map = TemplateMap.Load(TD.P(Path.Combine("maps", tid + ".json")));
        var tpl = File.ReadAllBytes(TD.P(map.FileName));
        var d = new CqData();
        d.Values["Customer"] = "Công ty TNHH Takara Tool & Die Hà Nội"; d.Values["Material"] = "SKD11"; d.Values["Dngc"] = "DNGC/HY3-26090216";
        d.Values["ReportNo"] = "001/11"; d.Values["ReceivedDate"] = "30/09/2026"; d.Values["CheckDate"] = "01/10/2026";
        d.Values["HardnessRequirement"] = "58-62"; d.Values["HardnessTolerance"] = "MIN -0.7"; d.Values["Remark"] = "Nhận xét/Remark: Chất lượng đạt";
        d.Numbers["Quantity"] = 2; d.Numbers["WeightKg"] = 1.4m;
        d.Samples.AddRange(new[] { "58.4", "59.0 / 59.2" });
        d.QuenchTemps = temps.ToList(); d.RamValues = new() { "150p", "180p", "200p" };
        d.SampleImages.Add(new CqImage(Png(800, 900), 800, 900, "SampleImage"));
        for (var i = 0; i < 5; i++) d.HardnessImages.Add(new CqImage(Png(600, 700), 600, 700, "HardnessImage"));
        d.Overlays.Add(new CqOverlay(new CqImage(Png(400, 300), 400, 300, "Stamp"), Cell: map.Cell("Remark"), DxMm: 5, DyMm: 2, WidthMm: 30));
        var outBytes = CqWriter.Build(tpl, map, d);

        using var za = new ZipArchive(new MemoryStream(tpl)); using var zb = new ZipArchive(new MemoryStream(outBytes));
        byte[] Read(ZipArchive z, string n) { using var s = z.GetEntry(n)!.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
        var allowed = new HashSet<string> { "xl/worksheets/sheet1.xml", "xl/drawings/drawing1.xml", "xl/drawings/_rels/drawing1.xml.rels", "[Content_Types].xml", "xl/styles.xml" };
        foreach (var e in za.Entries)
        {
            Assert.NotNull(zb.GetEntry(e.FullName));
            if (!allowed.Contains(e.FullName)) Assert.True(Read(za, e.FullName).SequenceEqual(Read(zb, e.FullName)), "Part bị đổi: " + e.FullName);
        }
        XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sa = XDocument.Load(new MemoryStream(Read(za, "xl/worksheets/sheet1.xml"))); var sb = XDocument.Load(new MemoryStream(Read(zb, "xl/worksheets/sheet1.xml")));
        var mapped = map.Fields.Values.SelectMany(f => f.TryGetProperty("cell", out var c) ? new[] { c.GetString()! } : f.TryGetProperty("cells", out var cs) ? cs.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>()).ToHashSet();
        var sampleCells = map.Cells("Samples").ToHashSet();
        // styles.xml: mọi font/xf cũ giữ nguyên; chỉ được thêm mới; mọi ô mẫu có chữ ĐẬM
        var sta = XDocument.Load(new MemoryStream(Read(za, "xl/styles.xml"))); var stb = XDocument.Load(new MemoryStream(Read(zb, "xl/styles.xml")));
        foreach (var part in new[] { "fonts", "cellXfs", "borders", "fills", "numFmts", "cellStyleXfs", "dxfs" })
        {
            var ea = sta.Root!.Element(S + part)?.Elements().ToList() ?? new(); var eb = stb.Root!.Element(S + part)?.Elements().ToList() ?? new();
            Assert.True(eb.Count >= ea.Count, "styles bị xóa phần tử: " + part);
            for (var i = 0; i < ea.Count; i++) Assert.True(XNode.DeepEquals(ea[i], eb[i]), $"styles.{part}[{i}] bị sửa");
        }
        var fontsB = stb.Root!.Element(S + "fonts")!.Elements().ToList(); var xfsB = stb.Root!.Element(S + "cellXfs")!.Elements().ToList();
        var sbDoc = XDocument.Load(new MemoryStream(Read(zb, "xl/worksheets/sheet1.xml")));
        foreach (var cell in sampleCells)
        {
            var el = sbDoc.Descendants(S + "c").First(c => (string)c.Attribute("r")! == cell);
            var font = fontsB[(int)xfsB[(int)el.Attribute("s")!].Attribute("fontId")!];
            Assert.True(font.Element(S + "b") is not null, $"Ô kết quả {cell} chưa in đậm");
        }
        var ca = sa.Descendants(S + "c").ToDictionary(c => (string)c.Attribute("r")!); var cb = sb.Descendants(S + "c").ToDictionary(c => (string)c.Attribute("r")!);
        foreach (var r in ca.Keys.Union(cb.Keys))
        {
            if (mapped.Contains(r))
            {
                if (ca.ContainsKey(r) && cb.ContainsKey(r) && !sampleCells.Contains(r)) Assert.Equal((string?)ca[r].Attribute("s"), (string?)cb[r].Attribute("s"));
                continue;
            }
            Assert.True(ca.ContainsKey(r) && cb.ContainsKey(r) && XNode.DeepEquals(ca[r], cb[r]), "Ô ngoài mapping bị đổi: " + r);
        }
        XElement Strip(XDocument x) { var e = new XElement(x.Root!); e.Element(S + "sheetData")!.Remove(); return e; }
        Assert.True(XNode.DeepEquals(Strip(sa), Strip(sb)), "Phần ngoài sheetData bị đổi (merge, pageSetup, header/footer…)");
        var text = string.Concat(XDocument.Load(new MemoryStream(Read(zb, "xl/drawings/drawing1.xml"))).Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value + "|"));
        if (tid != "carbon") Assert.Contains(temps[^1], text);
        if (tid != "gio") Assert.Contains("150p", text);
        XNamespace X = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        var anchorsA = XDocument.Load(new MemoryStream(Read(za, "xl/drawings/drawing1.xml"))).Root!.Elements().Count();
        var anchorsB = XDocument.Load(new MemoryStream(Read(zb, "xl/drawings/drawing1.xml"))).Root!.Elements().Count();
        Assert.Equal(anchorsA + 7, anchorsB);   // 1 ảnh xuất hàng + 5 ảnh độ cứng + 1 dấu
    }

    [Fact]
    public void Stage_count_mismatch_blocks()
    {
        var map = TemplateMap.Load(TD.P(Path.Combine("maps", "gio.json")));
        var d = new CqData { QuenchTemps = new() { "650℃", "800℃" } };
        Assert.Throws<CqBuildException>(() => CqWriter.Build(File.ReadAllBytes(TD.P(map.FileName)), map, d));
    }
}
