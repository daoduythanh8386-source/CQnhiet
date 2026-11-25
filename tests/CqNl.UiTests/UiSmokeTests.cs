using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CqNl.Application;
using CqNl.Infrastructure.Erp;
using CqNl.Wpf;
using CqNl.Wpf.Views;
using Xunit;

namespace CqNl.UiTests;

/// <summary>
/// Kiểm thử giao diện thật (không mock): dựng kho tạm có dữ liệu thật (ERP, ảnh, kết quả, CQ), mở từng màn hình WPF,
/// chờ nạp dữ liệu, chụp ảnh màn hình ra file PNG và kiểm tra không có lỗi nào được báo trong lúc nạp.
/// </summary>
public class UiSmokeTests
{
    private static string P(string n) => Path.Combine(AppContext.BaseDirectory, "TestData", n);
    private static readonly string Out = Path.Combine(AppContext.BaseDirectory, "ui");

    [Fact]
    public void All_screens_load_real_data_and_render()
    {
        Exception? fail = null;
        var t = new Thread(() =>
        {
            try { Run(); } catch (Exception e) { fail = e; }
            finally { try { Directory.CreateDirectory(Out); File.WriteAllLines(Path.Combine(Out, "thong_bao.txt"), Ui.Messages); } catch { } }
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (fail is not null) throw new Exception("Lỗi giao diện: " + fail.Message, fail);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private static void Clip(Action a)
    {
        for (var i = 0; i < 20; i++) { try { a(); return; } catch (System.Runtime.InteropServices.ExternalException) { Pump(100); } }
        a();
    }

    /// <summary>Gọi hàm async của cửa sổ TRONG Dispatcher (có SynchronizationContext như khi người dùng bấm phím) rồi bơm thông điệp chờ xong.</summary>
    private static int Wait(Func<Task<int>> f) { var t = Dispatcher.CurrentDispatcher.InvokeAsync(f).Task.Unwrap(); var until = DateTime.Now.AddSeconds(40); while (!t.IsCompleted && DateTime.Now < until) Pump(100); Assert.True(t.IsCompleted, "dán ảnh quá 40 giây"); return t.Result; }

    private static T? WaitT<T>(Func<Task<T?>> f, int seconds)
    {
        var t = Dispatcher.CurrentDispatcher.InvokeAsync(f).Task.Unwrap();
        var until = DateTime.Now.AddSeconds(seconds);
        while (!t.IsCompleted && DateTime.Now < until) Pump(150);
        Assert.True(t.IsCompleted, $"quá {seconds} giây");
        return t.Result;
    }

    private static void PumpUntil(Func<bool> ok, int seconds) { var until = DateTime.Now.AddSeconds(seconds); while (!ok() && DateTime.Now < until) Pump(150); }

    /// <summary>Dán ảnh từ clipboard theo mọi dạng (PNG / DIB / Bitmap / file ảnh), không dán khi chỉ có chữ, Ctrl+Alt+S chờ ảnh chụp,
    /// và PHÍM THẬT Ctrl+V (ô chữ vẫn dán chữ; ngoài ô chữ dán ảnh vào đơn).</summary>
    private static void ClipboardAndKeys(Workspace ws, string key, OrderWindow ow)
    {
        int Imgs() => ws.GetOrder(key)!.Order.Qc.Images.Count;
        var png = File.ReadAllBytes(P(Path.Combine("img", "dyna2_47.4.png")));
        BitmapSource Src() => BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var log = new List<string>();
        // 1) "PNG" (Snipping Tool, trình duyệt)
        Clip(() => { var d = new DataObject(); d.SetData("PNG", new MemoryStream(png)); Clipboard.SetDataObject(d, true); });
        Assert.Equal("PNG", OrderWindow.ReadClipboard()?.Format);
        Assert.Equal(1, Wait(() => ow.PasteFromClipboard())); log.Add("PNG OK");
        // 2) DIB (Zalo, Paint, PrtScn)
        byte[] bmpFile; { var enc = new BmpBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(Src())); using var ms = new MemoryStream(); enc.Save(ms); bmpFile = ms.ToArray(); }
        var dib = bmpFile[14..];
        Assert.NotNull(OrderWindow.DibToPng(dib));
        Clip(() => { var d = new DataObject(); d.SetData(DataFormats.Dib, new MemoryStream(dib)); Clipboard.SetDataObject(d, true); });
        Assert.Equal("DIB", OrderWindow.ReadClipboard()?.Format);
        Assert.Equal(1, Wait(() => ow.PasteFromClipboard())); log.Add("DIB OK");
        // 3) Bitmap (Clipboard.SetImage — app WPF / .NET khác)
        Clip(() => Clipboard.SetImage(Src()));
        Assert.NotNull(OrderWindow.ReadClipboard());
        Assert.Equal(1, Wait(() => ow.PasteFromClipboard())); log.Add("Bitmap OK");
        // 4) file ảnh (Ctrl+C trong File Explorer / Zalo "copy file")
        var tf = Path.Combine(Path.GetTempPath(), "cqnl_clip_" + Guid.NewGuid().ToString("N") + ".png"); File.WriteAllBytes(tf, png);
        Clip(() => Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { tf }));
        Assert.Equal("FileDrop", OrderWindow.ReadClipboard()?.Format);
        Assert.Equal(1, Wait(() => ow.PasteFromClipboard())); log.Add("FileDrop OK");
        // 5) chỉ có chữ → không thêm ảnh
        Clip(() => Clipboard.SetText("58-62"));
        Assert.Null(OrderWindow.ReadClipboard());
        Assert.Equal(0, Wait(() => ow.PasteFromClipboard())); log.Add("Text: không thêm ảnh OK");
        // 6) Ctrl+Alt+S: chờ ảnh chụp màn hình → clipboard có ảnh mới → TỰ thêm vào đơn (không cần Ctrl+V)
        var n = Imgs();
        Dispatcher.CurrentDispatcher.Invoke(() => ow.StartCapture(launch: false));
        Clip(() => Clipboard.SetImage(Src()));
        PumpUntil(() => Imgs() == n + 1, 40);
        Assert.Equal(n + 1, Imgs()); log.Add("Chụp màn hình (chờ clipboard) OK");
        // 7) PHÍM THẬT Ctrl+V — chỉ gửi phím khi cửa sổ đơn đang ở trên cùng (tránh gõ vào ứng dụng khác)
        var hwnd = new System.Windows.Interop.WindowInteropHelper(ow).Handle;
        ow.Topmost = true; ow.Activate(); SetForegroundWindow(hwnd); Pump(800);
        if (GetForegroundWindow() == hwnd)
        {
            void CtrlV() { keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x56, 0, 0, UIntPtr.Zero); keybd_event(0x56, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero); }
            var tabs = (TabControl)ow.FindName("Tabs");
            tabs.SelectedIndex = 0; Pump(500);
            var cust = (TextBox)ow.FindName("CustomerBox"); var old = cust.Text;
            cust.Focus(); Keyboard.Focus(cust); cust.CaretIndex = cust.Text.Length; Pump(200);
            Clip(() => Clipboard.SetText(" QL-123"));
            var n2 = Imgs();
            CtrlV(); Pump(1200);
            Assert.EndsWith("QL-123", cust.Text);                                               // ô chữ: dán chữ như thường
            Assert.Equal(n2, Imgs());
            cust.Text = old; Pump(200);
            Keyboard.Focus(tabs); Pump(200);
            Clip(() => Clipboard.SetImage(Src()));
            CtrlV();
            PumpUntil(() => Imgs() == n2 + 1, 40);
            Assert.Equal(n2 + 1, Imgs());                                                       // ngoài ô chữ: ảnh vào đơn
            Assert.Equal(1, tabs.SelectedIndex);                                                // tự chuyển sang tab ảnh
            log.Add("Phím thật Ctrl+V: chữ vào ô chữ OK; ảnh vào đơn OK");
        }
        else log.Add("Phím thật Ctrl+V: BỎ QUA (Windows không cho cửa sổ test lên trên cùng)");
        ow.Topmost = false;
        try { Clipboard.Clear(); } catch { }
        Directory.CreateDirectory(Out);
        File.WriteAllLines(Path.Combine(Out, "clipboard_phim.txt"), log);
    }

    private static void Pump(int ms)
    {
        var f = new DispatcherFrame(); var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        tm.Tick += (_, _) => { tm.Stop(); f.Continue = false; }; tm.Start(); Dispatcher.PushFrame(f);
    }

    private static void Save(FrameworkElement el, string name)
    {
        el.UpdateLayout();
        var w = (int)Math.Max(1, el.ActualWidth); var h = (int)Math.Max(1, el.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h)); dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h)); }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Out);
        using var fs = File.Create(Path.Combine(Out, name + ".png")); enc.Save(fs);
    }

    private static void Shot(Window w, string name, int width, int height, int waitMs = 2500, Action<Window>? before = null, bool hideOnly = false)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = 20; w.Top = 20; w.ShowActivated = false;
        w.WindowState = WindowState.Normal; w.Width = width; w.SizeToContent = SizeToContent.Manual; w.Height = height;
        w.Show(); Pump(waitMs);
        before?.Invoke(w);
        Save((FrameworkElement)w.Content, name);
        if (hideOnly) w.Hide(); else w.Close();
    }

    private static void AssertNoErrors(string where)
    {
        lock (Ui.Messages)
        {
            var errs = Ui.Messages.Where(m => m.StartsWith("ERR")).ToList();
            Assert.True(errs.Count == 0, where + ": " + string.Join("\n", errs));
        }
    }

    private static void Run()
    {
        Ui.Silent = true;
        // KHÔNG tạo đối tượng App: WPF sẽ chạy App.OnStartup khi bơm thông điệp → mở kho THẬT theo cấu hình máy (lỗi đã gặp ở build v15–v17).
        // Chỉ dùng Application gốc + nạp bộ giao diện chung (Theme.xaml).
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/CQNhietLuyen;component/Theme.xaml") });
        Ui.InstallVietnameseDatePicker();
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("vi-VN")));
        var root = Path.Combine(Path.GetTempPath(), "cqnl_ui_" + Guid.NewGuid().ToString("N"));
        var store = Path.Combine(root, "kho"); Directory.CreateDirectory(store);
        var paths = new AppPaths(P(""), P("maps"), P("presets"), P("tessdata"));
        App.LocalDir = Path.Combine(root, "local");
        new LocalConfig { StorePath = store, PdfFolder = Path.Combine(root, "CQ luu") }.Save(App.LocalDir);       // không đụng thư mục CQ thật của máy build
        var ws = new Workspace(App.LocalDir, store, paths, "MAY-UI");
        App.Ws = ws;

        // Màn hình đăng nhập lần đầu (kho trống → tạo admin)
        Shot(new LoginWindow(), "01_lan_dau_tao_admin", 920, 640, hideOnly: true);
        Assert.Same(ws, App.Ws);
        AssertNoErrors("Đăng nhập lần đầu");

        // Dữ liệu thật
        Assert.True(ws.Probe());
        ws.Auth!.CreateFirstAdmin("admin", "Quản trị hệ thống", "Admin@2026");
        Shot(new LoginWindow(), "02_dang_nhap", 920, 560, hideOnly: true);
        AssertNoErrors("Khôi phục mật khẩu quản trị");
        var s = ws.Login("admin", "Admin@2026");
        ws.Settings!.SetSteelTable(s, File.ReadAllBytes(P("steel_grades.xlsx")), "steel_grades.xlsx");
        var sig = P("sig");
        if (Directory.Exists(sig))
        {
            var a = ws.Settings.AddSigner(s, "Trương Ngọc Hùng", File.ReadAllBytes(Path.Combine(sig, "approver.png")), "png");
            var c = ws.Settings.AddSigner(s, "Đào Duy Thanh", File.ReadAllBytes(Path.Combine(sig, "thanh_moi_goc.jpg")), "jpg");
            ws.Settings.SetStamp(s, File.ReadAllBytes(Path.Combine(sig, "stamp.png")));
            ws.Settings.SetDefaults(s, a.Id, c.Id);
        }
        var read = ErpReader.Read(P("erp.xlsx"));
        ws.Orders!.Apply(s, ws.Orders.Preview(read), "erp.xlsx", _ => false);
        var key = read.Lines[0].Key;
        var r = ws.GetOrder(key)!;
        r = ws.EditQc(key, r.Version, q =>
        {
            q.SteelGrade = "SKD11"; q.TemplateId = "carbon"; q.HardnessRequirement = "58-62"; q.Tolerance = "MIN-1";
            q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" };
        }, "ui test");
        ws.AddImage(key, r.Version, File.ReadAllBytes(P(Path.Combine("img", "sanpham_goc.jpg"))), "jpg", "Sample");
        foreach (var f in new[] { "mitu_goc_38.2.jpg", "dyna2_47.4.png", "mitu1_47.3.png" })
            ws.AddImage(key, ws.GetOrder(key)!.Version, File.ReadAllBytes(P(Path.Combine("img", f))), Path.GetExtension(f).TrimStart('.'), "Hardness");
        foreach (var im in ws.GetOrder(key)!.Order.Qc.Images.Where(i => i.Kind == "Hardness").Take(2))
            ws.ConfirmImage(key, ws.GetOrder(key)!.Version, im.Id, im.OcrValue ?? "47.4");
        r = ws.SaveHardness(key, ws.GetOrder(key)!.Version, new[] { "59.5 / 58.2", "57.4", "60" }, h => h.CheckDate = "13/11/2026");
        // một đơn khác có CQ để màn hình Lịch sử có dữ liệu
        var key2 = read.Lines[1].Key;
        var r2 = ws.EditQc(key2, ws.GetOrder(key2)!.Version, q =>
        {
            q.TemplateId = "carbon"; q.HardnessRequirement = "58-62"; q.QuenchTemps = new() { "650℃", "1030℃" }; q.RamValues = new() { "150p", "180p", "200p" };
        }, "ui test");
        r2 = ws.SaveHardness(key2, r2.Version, new[] { "59", "60" }, h => h.CheckDate = "13/11/2026");
        ws.ConfirmHardness(key2, r2.Version);
        var cq = ws.CreateCq(key2);
        // lưu ý: một lưu ý "nhắc ở đơn sau của khách" ghi ở đơn 2 → hiện băng cảnh báo ở đơn 1 (cùng khách)
        ws.AddNote(key2, "Khách Takara yêu cầu ảnh đo độ cứng rõ số, gửi kèm CQ qua email trước khi giao hàng.", true,
            new[] { (File.ReadAllBytes(P(Path.Combine("img", "sanpham_goc.jpg"))), "jpg", "anh_khach_gui.jpg") });
        ws.AddNote(key, "QC: mẫu 1 đo 2 điểm do bề mặt không phẳng.", false, Array.Empty<(byte[], string, string)>());

        // gộp 2 dòng cùng ĐNGC (nếu có) để danh sách đơn hiện dòng gộp
        var grp = read.Lines.Skip(2).GroupBy(l => l.Dngc).FirstOrDefault(g => g.Count() >= 2)?.Take(2).Select(l => l.Key).ToList();
        if (grp is not null)
        {
            foreach (var k in grp) ws.EditQc(k, ws.GetOrder(k)!.Version, q => { q.SteelGrade = "SKD11"; q.HardnessRequirement = "58-62"; }, "ui test");
            ws.MergeOrders(grp);
        }

        // Cửa sổ chính
        Assert.Same(ws, App.Ws);
        Assert.True(ws.Session is not null, "Session null trước khi mở cửa sổ chính; thông báo: " + string.Join(" | ", Ui.Messages));
        var main = new MainWindow();
        // cỡ màn hình thật của máy người dùng: 1920×1080 phóng 125% → ~1536×790
        Shot(main, "03_cua_so_chinh_don_gia_cong", 1536, 790, 6000, before: w =>
        {
            var ovm = (OrdersView)((ContentControl)w.FindName("Host")).Content;
            var gm = (DataGrid)ovm.FindName("Grid");
            PumpUntil(() => gm.ItemsSource is not null, 60); Pump(800);                  // 484 đơn thật: đợi nạp xong rồi mới đo / chụp
            Assert.True(gm.ItemsSource is not null, "Đơn gia công không nạp xong sau 60 giây");
            var used = gm.Columns.Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth);
            Assert.True(used <= gm.ActualWidth, $"Lưới Đơn gia công tràn ngang: cột {used:0} > lưới {gm.ActualWidth:0}");
            Assert.True(((IEnumerable<OrderItem>)gm.ItemsSource).Any(), "Đơn gia công chưa có dòng nào sau khi mở");
        }, hideOnly: true);
        main.SkipPrompt();
        AssertNoErrors("Cửa sổ chính");

        // "Kiểm tra máy này": kho đọc + ghi, Excel, thư mục lưu CQ, máy in… — mỗi mục chưa đạt có cách sửa
        {
            var mc = new MachineCheckWindow();
            Shot(mc, "25_kiem_tra_may", 900, 700, 1000, before: w =>
            {
                PumpUntil(() => mc.Items.Count > 0, 60); Pump(500);
                Assert.True(mc.Items.Count > 0, "Kiểm tra máy không xong sau 60 giây");
                Assert.Equal(CheckLevel.Ok, mc.Items.Single(i => i.Name.StartsWith("Kho chung")).Level);
                Assert.NotEqual(CheckLevel.Fail, mc.Items.Single(i => i.Name.StartsWith("Thư mục lưu file CQ")).Level);   // thư mục thử ghi được
                Assert.All(mc.Items.Where(i => i.Level != CheckLevel.Ok), i => Assert.False(string.IsNullOrWhiteSpace(i.Fix), i.Name));
                Directory.CreateDirectory(Out); File.WriteAllText(Path.Combine(Out, "kiem_tra_may.txt"),
                    string.Join(Environment.NewLine, mc.Items.Select(i => $"[{i.Level}] {i.Name}: {i.Detail}")));
            });
            AssertNoErrors("Kiểm tra máy");
        }

        // Các màn hình trong menu
        foreach (var (view, name) in new (Func<UserControl>, string)[]
                 {
                     (() => new DashboardView(), "04a_tong_quan"),
                     (() => new OrdersView(), "04_don_gia_cong"), (() => new CqHistoryView(), "05_lich_su_cq"), (() => new TemplatesView(), "06_template"),
                     (() => new AuditView(), "07_nhat_ky"), (() => new AccountsView(), "08_tai_khoan"), (() => new SettingsView(), "09_cai_dat"),
                 })
        {
            var host = new Window { Content = view(), Background = Brushes.White };
            Shot(host, name, 1276, 740);
            AssertNoErrors(name);
        }

        // Đơn gia công gọn: 1 nút ERP, chip theo bước làm việc (mặc định "Cần làm CQ"), 10 cột chính, không còn nút Gộp/Tách/Mở/Làm mới trên thanh
        {
            var ovw = new OrdersView();
            var host = new Window { Content = ovw, Background = Brushes.White, Width = 1280, Height = 760, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000 };
            host.Show(); Pump(2500);
            var chips = ((WrapPanel)ovw.FindName("Chips")).Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().ToList();
            Assert.StartsWith("Cần làm CQ", (string)chips[0].Content);
            Assert.True(chips[0].IsChecked);
            Assert.Equal(new[] { "Cần làm CQ", "Nháp – chờ duyệt", "Đã duyệt – chưa in", "Đã in", "Tất cả" },
                chips.Where(c => c.Visibility == Visibility.Visible).Select(c => ((string)c.Content).Split("  ")[0]).ToArray());
            foreach (var old in new[] { "ErpSyncBtn", "ImportBtn", "MergeBtn", "SplitBtn" }) Assert.Null(ovw.FindName(old));
            Assert.NotNull(ovw.FindName("ErpBtn"));
            var g = (DataGrid)ovw.FindName("Grid");
            Assert.Equal(10, g.Columns.Count(c => c.Visibility == Visibility.Visible));
            Assert.Equal(Visibility.Collapsed, ((Button)ovw.FindName("ClearBtn")).Visibility);
            ((ComboBox)ovw.FindName("DaysPick")).SelectedIndex = 3;                       // mọi ngày nhận
            chips[4].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(500);
            var all = ((IEnumerable<OrderItem>)g.ItemsSource).ToList();
            Assert.Equal(ws.ListOrders(false).Count, all.Count);
            Assert.Contains(all, i => i.Status is "Chưa đo" or "Đang đo" or "Đã đo" or "Nháp CQ" or "Đã duyệt" or "Đã in" or "Gộp");
            Assert.Contains(all, i => i.Icons.Contains('⧉'));                           // dòng gộp có biểu tượng
            // màu dòng (17/11): đã in → xanh #A9D08E, CQ nháp chưa duyệt → vàng #FFE699, chưa in → trắng
            Assert.All(all, i => Assert.Equal(i.Printed ? "printed" : i.HasCq && !i.Approved ? "draft" : "", i.RowColor));
            Save((FrameworkElement)host.Content, "04b_don_gia_cong_tat_ca");
            ((ComboBox)ovw.FindName("DaysPick")).SelectedIndex = 1;                       // trả lại mặc định 30 ngày cho máy test
            host.Close();
            AssertNoErrors("Đơn gia công gọn");
        }

        // Chi tiết đơn: 4 tab
        var ow = new OrderWindow(key);
        ow.WindowStartupLocation = WindowStartupLocation.Manual; ow.WindowState = WindowState.Normal; ow.Width = 1536; ow.Height = 790; ow.ShowActivated = false; ow.Show(); Pump(3500);
        var tabs = (TabControl)ow.FindName("Tabs");
        ((ListBox)ow.FindName("ImageList")).SelectedIndex = 1; Pump(300);                          // ảnh đang chọn → thanh loại ảnh / khung CQ / cắt / xoá
        string[] tabNames = { "10_don_thong_tin", "11_don_anh_va_ket_qua_do", "13_don_xuat_cq", "13b_don_luu_y" };
        for (var i = 0; i < tabNames.Length; i++)
        {
            tabs.SelectedIndex = i; Pump(1200); Save((FrameworkElement)ow.Content, tabNames[i]);
            if (i == 1)
            {
                // nút bước chính không được nằm dưới đáy cửa sổ (màn hình thấp 790)
                var owRoot = (FrameworkElement)ow.Content; var btn = (Button)ow.FindName("ConfirmHardBtn");
                var bottom = btn.TranslatePoint(new Point(0, btn.ActualHeight), owRoot).Y;
                Assert.True(btn.IsVisible && bottom <= owRoot.ActualHeight, $"Nút Xác nhận kết quả độ cứng bị khuất: đáy {bottom:0} > {owRoot.ActualHeight:0}");
            }
        }
        // Ảnh & OCR (module xử lý ảnh 21/11): 3 chế độ ảnh, kết quả đọc số gốc / xử lý, cảnh báo, lịch sử
        var insp = ws.GetOrder(key)!.Order.Qc.Images.First(i => i.Kind == "Hardness");
        Shot(new ImageInspectWindow(insp, key, "thử", true, _ => null, () => null), "11b_anh_va_ocr", 1536, 790, 2500);
        AssertNoErrors("Ảnh & OCR");
        // Ảnh: 4 ảnh có thumbnail; kết quả đo: đánh giá ĐẠT hiển thị
        var imgList = (ListBox)ow.FindName("ImageList");
        Assert.Equal(4, ((System.Collections.ICollection)imgList.ItemsSource).Count);
        Assert.All(((IEnumerable<ImageItem>)imgList.ItemsSource), x => Assert.NotNull(x.Thumb));
        Assert.Equal("ĐẠT", ((TextBlock)ow.FindName("EvalStatus")).Text);
        Assert.Equal(Visibility.Visible, ((Border)ow.FindName("NoteBanner")).Visibility);           // lưu ý từ đơn trước của khách
        Assert.Equal(2, ((System.Collections.ICollection)((ListBox)ow.FindName("NotesList")).ItemsSource).Count);
        // Xuất CQ gọn: 2 nút chính + "Xuất khác ▾" + "In ▾"; người ký chọn trong cửa sổ duyệt
        // Xuất CQ đúng ảnh mẫu 17/11: Tạo file CQ · Duyệt CQ (xuất PDF có dấu, chữ ký) · Đổi số báo cáo / Mở PDF · Mở thư mục PDF · Mở file · Mở thư mục · Lưu bản sao · Đã in · Xoá
        foreach (var old in new[] { "QuickExcelBtn", "QuickBlankBtn", "AddSignBtn", "MoreBtn", "PrintBtn" }) Assert.Null(ow.FindName(old));
        foreach (var need in new[] { "CreateCqBtn", "ApproveBtn", "CreateCqManualBtn", "PrintedBox", "DeleteCqBtn", "ExportTemplateBox" }) Assert.NotNull(ow.FindName(need));
        Assert.Equal("Tạo file CQ", ((Button)ow.FindName("CreateCqBtn")).Content);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)((FrameworkElement)ow.FindName("ApproverBox")).Parent).Visibility);
        Assert.False(((CheckBox)ow.FindName("PrintAfterBox")).IsChecked == true);
        tabs.SelectedIndex = 2; Pump(400);
        ClipboardAndKeys(ws, key, ow);
        ow.Close();
        AssertNoErrors("Chi tiết đơn");

        // Đơn mới (chưa nhập gì): mở → tự điền từ ERP + bảng mác
        var fresh = read.Lines.Skip(5).First(l => !string.IsNullOrWhiteSpace(l.GradeFromErp)).Key;
        Shot(new OrderWindow(fresh), "10b_don_moi_tu_dien", 1280, 820, 3500);
        Assert.False(string.IsNullOrEmpty(ws.GetOrder(fresh)!.Order.Qc.SteelGrade));
        AssertNoErrors("Tự điền đơn mới");

        // Mẫu CQ Thấm Nitơ / Vinfast: chọn template → biểu mẫu nhập tay hiện ra; thông số lò tự điền theo mác
        var nitKey = read.Lines.Select(l => l.Key).First(k => k != key && k != key2 && k != fresh && (grp is null || !grp.Contains(k)));
        ws.EditQc(nitKey, ws.GetOrder(nitKey)!.Version, q => { q.SteelGrade = "H13"; q.HardnessRequirement = "43-47"; q.TemplateId = "nito"; }, "ui test");
        var own = new OrderWindow(nitKey);
        own.WindowStartupLocation = WindowStartupLocation.Manual; own.WindowState = WindowState.Normal; own.Width = 1280; own.Height = 900; own.ShowActivated = false; own.Show(); Pump(3500);
        Assert.Equal(Visibility.Visible, ((Border)own.FindName("ManualBox")).Visibility);
        Assert.Contains("Thấm Nitơ", ((TextBlock)own.FindName("ManualTitle")).Text);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)own.FindName("HeatRow")).Visibility);   // mẫu nitơ không có lưu đồ tôi/ram
        Save((FrameworkElement)own.Content, "10c_don_mau_tham_nito");
        // người làm đổi sang Vinfast → biểu mẫu Vinfast + nhiệt độ tôi / ram theo bảng mác (H13)
        var tb = (ComboBox)own.FindName("TemplateBox");
        tb.SelectedValue = "vinfast"; Pump(800);
        Assert.Contains("Vinfast", ((TextBlock)own.FindName("ManualTitle")).Text);
        var h13 = Workspace.FindGrade(ws.SteelTable(), "H13")[0];
        Assert.Equal(string.Join("; ", Workspace.HeatTexts(h13)), ((TextBox)own.FindName("HeatBox")).Text);
        Assert.Equal(string.Join("; ", Workspace.RamTexts(h13)), ((TextBox)own.FindName("RamBox")).Text);
        Save((FrameworkElement)own.Content, "10d_don_mau_vinfast");
        own.Close();
        AssertNoErrors("Mẫu Nitơ / Vinfast");

        // Đơn có CQ: tab Xuất CQ + cửa sổ Duyệt
        var ow2 = new OrderWindow(key2);
        Shot(ow2, "14_don_co_cq", 1536, 790, 3000, before: w => { ((TabControl)w.FindName("Tabs")).SelectedIndex = 2; Pump(1200); });
        if (Directory.Exists(sig)) { Shot(new ApproveWindow(cq), "15_duyet_cq_keo_tha", 1536, 790, 40000, before: w =>
        {
            var ps = ((TextBlock)w.FindName("PageStatus")).Text;
            Assert.True(ps.StartsWith("Kéo dấu"), "Trang kéo-thả phải hiệu chỉnh được từng dòng: " + ps);
            var sc = (ScrollViewer)w.FindName("PageScroll");
            Assert.True(sc.ExtentWidth <= sc.ViewportWidth + 1, $"Trang CQ ở cửa sổ Duyệt phải cuộn ngang: {sc.ExtentWidth:0} > {sc.ViewportWidth:0}");
        }); AssertNoErrors("Duyệt CQ"); }

        // Đưa chữ ký / con dấu từ ngoài vào app (ảnh JPG con dấu nền trắng, ảnh chữ ký) — cửa sổ mở từ tab Xuất CQ / Cài đặt / Duyệt
        {
            byte[] Draw(bool stampImg)
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 244, 240)), null, new Rect(0, 0, 400, 260));
                    if (stampImg) { dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(200, 30, 40)), 12), new Point(200, 130), 110, 110); }
                    else
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open()) { c.BeginFigure(new Point(30, 180), false, false); c.BezierTo(new Point(120, 20), new Point(200, 240), new Point(260, 90), true, true); c.BezierTo(new Point(300, 30), new Point(330, 220), new Point(380, 120), true, true); }
                        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(20, 30, 120)), 6), g);
                    }
                }
                var rtb = new RenderTargetBitmap(400, 260, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv);
                var enc = new JpegBitmapEncoder { QualityLevel = 92 }; enc.Frames.Add(BitmapFrame.Create(rtb));
                using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
            }
            var before = ws.Settings!.Get().Value;
            var siw = new SignatureImportWindow(stamp: true);
            siw.LoadImage(Draw(true), "jpg");
            Shot(siw, "23_them_con_dau_tu_anh", 900, 640, 800);
            Assert.True(siw.Save());
            Assert.NotEqual(before.StampSha, ws.Settings.Get().Value.StampSha);
            Assert.True(CqNl.Infrastructure.Imaging.CqImages.HasTransparency(ws.Settings.Stamp()!));
            var siw2 = new SignatureImportWindow();
            siw2.SetSignerName("Nguyễn Văn Thử"); siw2.LoadImage(Draw(false), "jpg");
            Shot(siw2, "24_them_chu_ky_tu_anh", 900, 640, 800);
            Assert.True(siw2.Save());
            Assert.Contains(ws.Settings.Get().Value.Signers, x => x.Active && x.Name == "Nguyễn Văn Thử" && x.Id == siw2.AddedSignerId);
            AssertNoErrors("Thêm chữ ký / con dấu");
            if (Directory.Exists(sig)) ws.Settings.SetStamp(ws.Session!, File.ReadAllBytes(Path.Combine(sig, "stamp.png")));   // trả con dấu thật cho các bước sau
        }

        // Cửa sổ Duyệt: "Xem trước PDF" và "Duyệt" chạy nền KHÔNG được đọc control giao diện (lỗi bản 0.7.1: "different thread owns it")
        if (Directory.Exists(sig) && CqNl.Infrastructure.Pdf.ExcelPdfExporter.IsAvailable())
        {
            var aw = new ApproveWindow(cq);
            aw.WindowStartupLocation = WindowStartupLocation.Manual; aw.WindowState = WindowState.Normal; aw.ShowActivated = false; aw.Show(); Pump(1500);
            var prev = WaitT(() => aw.PreviewAsync(), 120);
            Assert.True(prev is not null && File.Exists(prev), "xem trước PDF: " + string.Join(" | ", Ui.Messages.Where(m => m.StartsWith("ERR"))));
            var done = WaitT(() => aw.ApproveAsync(), 120);
            Assert.NotNull(done);
            Assert.Equal(CqState.Approved, done!.State);
            aw.Close();
            AssertNoErrors("Duyệt (xem trước + duyệt)");
        }

        // Thêm template mới: tự dò bản đồ ô từ file gió; sửa bản đồ template mặc định
        Shot(new TemplateEditorWindow(File.ReadAllBytes(P("gio.xlsx")), "gio_moi.xlsx"), "21_template_moi_tu_do", 1280, 820, 4000);
        var ti = ws.TemplateList().First(t => t.Id == "khuon"); var (tm, tx) = ws.Cq!.Templates.Resolve("khuon");
        Shot(new TemplateEditorWindow(ti, tx, tm), "22_template_sua_ban_do", 1280, 820, 3000);
        AssertNoErrors("Template");

        // Import ERP xem trước + cắt ảnh
        var plan = ws.Orders.Preview(read);
        Shot(new ImportPreviewWindow(P("erp.xlsx"), read, plan), "16_import_xem_truoc", 980, 640);
        var orig = File.ReadAllBytes(P(Path.Combine("img", "sanpham_goc.jpg")));
        Shot(new CropWindow(orig, ws.GetOrder(key)!.Order.Qc.Images.First(i => i.Kind == "Sample").Crop), "17_cat_anh", 1000, 760);
        AssertNoErrors("Import/Cắt ảnh");

        // Tài khoản Kinh doanh: xem đơn + ảnh, ghi lưu ý; không sửa dữ liệu QC
        ws.Auth.SaveUser(ws.Session!, "kd1", "Nguyễn Kinh Doanh", Role.Sales, true, "Tam@123");
        ws.Logout(); var sk = ws.Login("kd1", "Tam@123"); ws.Auth.ChangePassword(sk, "Tam@123", "Kd1@2026", "Kd1@2026"); ws.Logout(); ws.Login("kd1", "Kd1@2026");
        Shot(new OrderWindow(key), "20_kinh_doanh_luu_y", 1280, 820, 2500, before: w =>
        {
            Assert.False(((FrameworkElement)w.FindName("InfoForm")).IsEnabled);
            Assert.True(((FrameworkElement)w.FindName("NoteForm")).IsEnabled);
            ((TabControl)w.FindName("Tabs")).SelectedIndex = 3; Pump(1000);
        });
        AssertNoErrors("Kinh doanh");
        ws.Logout(); ws.Login("admin", "Admin@2026");

        // OFFLINE: đổi tên kho → thanh trạng thái OFFLINE, danh sách từ bản sao, cảnh báo trong đơn
        Directory.Move(store, store + "_mat");
        ws.Probe();
        var main2 = new MainWindow();
        Shot(main2, "18_offline", 1366, 768, 3000, hideOnly: true);
        main2.SkipPrompt();
        Shot(new OrderWindow(key), "19_offline_chi_tiet_don", 1280, 820, 2500);
        Directory.Move(store + "_mat", store);
        AssertNoErrors("Offline");

    }
}
