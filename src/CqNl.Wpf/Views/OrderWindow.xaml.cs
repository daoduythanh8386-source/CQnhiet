using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CqNl.Application;
using CqNl.Domain;
using CqNl.Infrastructure.Excel;
using Microsoft.Win32;

namespace CqNl.Wpf.Views;

public sealed class ImageItem
{
    public string Id { get; init; } = "";
    public string KindText { get; init; } = "";
    public ImageSource? Thumb { get; init; }
    public string CropText { get; init; } = "";
    public Brush CropBrush { get; init; } = Brushes.Gray;
    public string OcrText { get; init; } = "";
    public string Value { get; set; } = "";
    public string ConfirmedText { get; init; } = "";
    public Brush ConfirmedBrush { get; init; } = Brushes.Gray;
    public bool OnCq { get; init; }
    public bool CanEdit { get; init; }
    public string Kind { get; init; } = "";
    public string OnCqText => OnCq ? "✔ in trên CQ" : "không in";
    public Visibility ValueVisibility => Kind == "Other" ? Visibility.Collapsed : Visibility.Visible;
    public string ValueLabel => Kind == "Hardness" ? "HRC" : "kg";
}

public sealed record CqItem(CqVersionRecord Rec, int Ver, string ReportNo, string ResultText, string StateText, string Created, string Approved, string PrintedText, string Notes);
public sealed record TemplateOption(string Id, string Name);

public partial class OrderWindow : Window
{
    private readonly string _key;
    private Workspace.OrderRow? _row;
    private bool _readOnly;
    private readonly DispatcherTimer _lockTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly List<TextBox> _samples = new();

    private static Workspace Ws => App.Ws;

    public OrderWindow(string key)
    {
        InitializeComponent();
        _key = key;
        FooterText.Text = AppInfo.Footer;
        try { var tl = Ws.TemplateList().Where(t => t.Active).ToList(); TemplateBox.ItemsSource = tl; ExportTemplateBox.ItemsSource = tl; } catch (Exception e) { Ui.Error(e); }
        for (var i = 0; i < HardnessEvaluator.MaxSamples; i++)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            dp.Children.Add(new TextBlock { Text = $"Mẫu {i + 1}", Width = 56, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            var tb = new TextBox { FontSize = 14, Padding = new Thickness(6, 3, 6, 3) }; tb.TextChanged += (_, _) => UpdateEval(); tb.LostFocus += (_, _) => AutoSaveHard();
            dp.Children.Add(tb); _samples.Add(tb); SampleBoxes.Items.Add(dp);
        }
        ReqBox.TextChanged += (_, _) => { if (HardReqBox.Text != ReqBox.Text) HardReqBox.Text = ReqBox.Text; UpdateEval(); };
        TolBox.TextChanged += (_, _) => UpdateEval();
        HardReqBox.TextChanged += (_, _) => { if (ReqBox.Text != HardReqBox.Text) ReqBox.Text = HardReqBox.Text; };
        HardReqBox.LostFocus += (_, _) => { if (_row is not null && !_readOnly && !_loading && N(HardReqBox.Text) != _row.Order.Qc.HardnessRequirement) SaveInfo(); };
        // "Tự động lưu khi nhập": rời ô → lưu kết quả đo nếu có thay đổi
        foreach (var c in new Control[] { UnitBox, AppearanceBox, CurvReqBox, CurvResBox, OtherBox, NoteBox }) c.LostFocus += (_, _) => AutoSaveHard();
        UnitBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(AutoSaveHard));
        CheckDateBox.SelectedDateChanged += (_, _) => AutoSaveHard();
        TemplateBox.SelectionChanged += (_, _) =>
        {
            var tid = TemplateBox.SelectedValue as string;
            ApplyTemplateVisibility(tid);
            if (_row is not null && !_loading && !_readOnly && tid is not null && tid != _manualTpl)
            {
                ApplyGrade(interactive: false, fromTemplate: true);      // THÔNG SỐ LĐ NHIỆT tự điền theo mác khi chọn template
                BuildManualPanel(tid);
            }
            if (_row is not null) ShowGradeInfo(_gradeInfoRow);
            if (!Equals(ExportTemplateBox.SelectedValue, TemplateBox.SelectedValue)) ExportTemplateBox.SelectedValue = TemplateBox.SelectedValue;
        };
        ExportTemplateBox.SelectionChanged += (_, _) => { if (!Equals(ExportTemplateBox.SelectedValue, TemplateBox.SelectedValue)) TemplateBox.SelectedValue = ExportTemplateBox.SelectedValue; };
        foreach (var b in new[] { HeatBox, RamBox, TolBox }) b.LostFocus += (_, _) => { if (_row is not null) ShowGradeInfo(_gradeInfoRow); };
        GradeBox.LostFocus += (_, _) => { if (GradeBox.Text.Trim() != _gradeApplied) ApplyGrade(interactive: true); };
        Loaded += async (_, _) => { InitExportPanel(); AcquireLock(); Load(); AutoFill(); await LoadRecent(); };
        Closed += (_, _) => { _lockTimer.Stop(); Ws.ReleaseOrder(_key); };
        _lockTimer.Tick += (_, _) => AcquireLock();
        _lockTimer.Start();
        PreviewKeyDown += OnKey;
    }

    private void AcquireLock()
    {
        var role = Ws.Session!.User.Role;
        var canEdit = role is Role.Admin or Role.Qc;
        string? other = null;
        if (canEdit) { try { other = Ws.LockOrder(_key); } catch (Exception e) { other = null; ShowBanner("Không kiểm tra được khóa đơn: " + e.Message); } }
        var wasRo = _readOnly;
        _readOnly = !canEdit || other is not null;
        if (other is not null) ShowBanner(other + " — bạn đang xem ở chế độ CHỈ ĐỌC. App tự mở quyền sửa sau khi người kia đóng đơn (hoặc nhấn F5).");
        else if (!Ws.Online) ShowBanner("OFFLINE — sửa dữ liệu QC sẽ lưu NHÁP trên máy này và tự đồng bộ khi có kết nối; không kiểm tra được người khác có đang sửa đơn này hay không. Thêm ảnh/tạo CQ cần kết nối kho chung.");
        else if (role == Role.Viewer) ShowBanner("Tài khoản chỉ xem.");
        else if (role == Role.Sales) ShowBanner("Tài khoản Kinh doanh: xem đơn, ảnh, kết quả, CQ và ghi LƯU Ý (tab Lưu ý). Không sửa dữ liệu QC.");
        else Banner.Visibility = Visibility.Collapsed;
        if (wasRo != _readOnly && IsLoaded) Load();
    }

    private void ShowBanner(string t) { BannerText.Text = t; Banner.Visibility = Visibility.Visible; }

    private bool _loading;
    private void Load()
    {
        _loading = true;
        try { LoadCore(); } finally { _loading = false; }
    }

    private void LoadCore()
    {
        Workspace.OrderRow? row = null;
        if (!Ui.Run(() => row = Ws.GetOrder(_key))) return;
        if (row is null) { Ui.Info("Không tìm thấy đơn (chưa có bản sao trên máy này?)"); Close(); return; }
        _row = row;
        var o = row.Order; var e = o.Erp; var q = o.Qc;
        var p = _key.Split('|');
        TitleText.Text = $"{p[0]}  ·  dòng {p.ElementAtOrDefault(1)}";
        SubText.Text = $"{q.CustomerOverride ?? e.GetValueOrDefault("Customer")} · {e.GetValueOrDefault("ProductName")} · phiên bản dữ liệu v{row.Version}" + (row.Draft ? " · CÓ NHÁP OFFLINE CHƯA ĐỒNG BỘ" : "");
        Title = $"Đơn {p[0]} / {p.ElementAtOrDefault(1)} — CQ Nhiệt Luyện";
        // ERP (chỉ đọc)
        DngcBox.Text = p[0];
        ProductBox.Text = string.Join(" — ", new[] { e.GetValueOrDefault("ProductCode"), e.GetValueOrDefault("ProductName") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        QtyBox.Text = e.GetValueOrDefault("Quantity") ?? ""; WeightBox.Text = e.GetValueOrDefault("WeightKg") ?? "";
        ReceivedBox.Text = DashboardService.ParseDate(e.GetValueOrDefault("ReceivedAt"))?.ToString("dd/MM/yyyy") ?? e.GetValueOrDefault("ReceivedAt") ?? "";
        ErpNoteBox.Text = e.GetValueOrDefault("Note") ?? "";
        FlagsText.Text = string.Join("\n", o.Flags.Select(f => "⚠ " + f));
        // QC info
        GradeBox.Text = q.SteelGrade ?? e.GetValueOrDefault("GradeFromErp") ?? "";
        TemplateBox.SelectedValue = q.TemplateId; ExportTemplateBox.SelectedValue = q.TemplateId;
        ReqBox.Text = q.HardnessRequirement ?? ""; HardReqBox.Text = ReqBox.Text; TolBox.Text = q.Tolerance ?? "";
        HeatBox.Text = string.Join("; ", q.QuenchTemps ?? new()); RamBox.Text = string.Join("; ", q.RamValues ?? new());
        _gradeApplied = GradeBox.Text.Trim(); ShowGradeInfo(q.SteelGradeRow);
        BuildManualPanel(q.TemplateId);
        var erpCust = e.GetValueOrDefault("Customer") ?? "";
        CustomerBox.Text = Ws.CustomerOnCq(o);
        CustomerBox.ToolTip = CustomerHint.Text = $"Tên ERP: {erpCust}" + (q.CustomerOverride is not null ? " · đang dùng tên sửa riêng cho đơn này" : SettingsService.AliasOf(Ws.CustomerAliases(), erpCust) is not null ? " · đang dùng tên viết tắt đã ghi nhớ" : "");
        SizeBox.Text = q.Size ?? ""; PartNoBox.Text = q.PartNo ?? ""; PartNameBox.Text = q.PartName ?? "";
        // hardness
        for (var i = 0; i < _samples.Count; i++) _samples[i].Text = q.Hardness.Samples.ElementAtOrDefault(i) ?? "";
        UnitBox.Text = q.Hardness.Unit;
        CheckDateBox.SelectedDate = DateTime.TryParseExact(q.Hardness.CheckDate, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cd) ? cd : null;
        AppearanceBox.Text = q.Hardness.Appearance ?? ""; CurvReqBox.Text = q.Hardness.CurvatureRequirement ?? ""; CurvResBox.Text = q.Hardness.CurvatureResult ?? "";
        OtherBox.Text = q.Hardness.Other ?? ""; NoteBox.Text = q.Hardness.Note ?? "";
        UpdateEval();
        _infoSnap = InfoSnap(); _hardSnap = HardSnap();
        LoadImages();
        LoadCqs();
        LoadNotes();
        foreach (var c in new FrameworkElement[] { InfoForm, HardForm, ConfirmBar, HardHead, ImgEditBar, AddHardBtn, CaptureBtn, CreateCqBtn, ApproveBtn, CreateCqManualBtn, PrintedBox, DeleteCqBtn })
            c.IsEnabled = !_readOnly;
        NoteForm.IsEnabled = Ws.Session!.User.Role is Role.Admin or Role.Qc or Role.Sales;
        ConfirmAllBtn.IsEnabled = !_readOnly;
        foreach (var b in new[] { ApproveBtn }) b.IsEnabled = !_readOnly && Ws.Online;
        UpdateState();
        ApplyTemplateVisibility(q.TemplateId);
    }

    // ---------------- Ảnh gần đây (Clipboard / Downloads / Desktop / Pictures) ----------------
    public sealed class RecentItem
    {
        public string Name { get; init; } = "";
        public string Sub { get; init; } = "";
        public string Tip { get; init; } = "";
        public BitmapSource? Thumb { get; init; }
        public double Opacity { get; set; } = 1;
        public string? Path { get; init; }
        public byte[]? Bytes { get; init; }
    }
    private readonly HashSet<string> _addedRecent = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> RecentFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return new[] { System.IO.Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), pics, System.IO.Path.Combine(pics, "Screenshots") }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task LoadRecent()
    {
        var items = new List<RecentItem>();
        if (ReadClipboard() is { Bytes: { } cbBytes })
        {
            BitmapSource? th = null;
            try { var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 160; bi.StreamSource = new MemoryStream(cbBytes); bi.EndInit(); bi.Freeze(); th = bi; } catch { }
            items.Add(new RecentItem { Name = "Clipboard", Sub = "ảnh vừa chụp/copy", Tip = "Ảnh trong Clipboard (Ctrl+V)", Thumb = th, Bytes = cbBytes });
        }
        var files = await Task.Run(() =>
        {
            var since = DateTime.Now.AddDays(-14);
            return RecentFolders().SelectMany(d => { try { return new DirectoryInfo(d).EnumerateFiles(); } catch { return Enumerable.Empty<FileInfo>(); } })
                .Where(f => f.Extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" && f.LastWriteTime >= since && f.Length < 25_000_000)
                .OrderByDescending(f => f.LastWriteTime).Take(30)
                .Select(f => (f.FullName, f.Name, f.LastWriteTime, f.Directory?.Name ?? "", Thumb: Thumbnail(f.FullName))).ToList();
        });
        foreach (var f in files)
            items.Add(new RecentItem
            {
                Name = f.Name, Sub = $"{f.Item4} · {f.LastWriteTime:dd/MM HH:mm}", Tip = f.FullName, Thumb = f.Thumb, Path = f.FullName,
                Opacity = _addedRecent.Contains(f.FullName) ? 0.35 : 1,
            });
        RecentList.ItemsSource = items;
    }

    private static BitmapSource? Thumbnail(string path)
    {
        try
        {
            var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 160; bi.UriSource = new Uri(path); bi.EndInit(); bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private async void RecentRefresh_Click(object sender, RoutedEventArgs e) => await LoadRecent();

    private async void Recent_Click(object sender, MouseButtonEventArgs e)
    {
        if (_readOnly || (e.OriginalSource as FrameworkElement)?.DataContext is not RecentItem it) return;
        if (it.Path is not null && _addedRecent.Contains(it.Path) && !Ui.Ask($"Ảnh {it.Name} đã thêm vào đơn này rồi. Thêm lần nữa?")) return;
        var bytes = it.Bytes ?? await File.ReadAllBytesAsync(it.Path!);
        var ext = it.Path is null ? "png" : System.IO.Path.GetExtension(it.Path).TrimStart('.').ToLowerInvariant();
        await AddBytes(bytes, ext == "jpeg" ? "jpg" : ext, DropKind, it.Name);
        if (it.Path is not null) { _addedRecent.Add(it.Path); it.Opacity = 0.35; }
        await LoadRecent();
    }

    // ---------------- Một nút: xác nhận → tạo CQ → PDF ----------------
    private string _infoSnap = "", _hardSnap = "";
    private string InfoSnap() => string.Join("|", GradeBox.Text, TemplateBox.SelectedValue, ReqBox.Text, TolBox.Text, HeatBox.Text, RamBox.Text, CustomerBox.Text, SizeBox.Text, PartNoBox.Text, PartNameBox.Text)
                                  + "|" + string.Join("|", _manualBoxes.Select(x => x.F.Key + "=" + x.Box.Text));
    private string HardSnap() => string.Join("|", SampleTexts().Select(x => x.Trim())) + "|" + string.Join("|", UnitBox.Text, CheckDateBox.SelectedDate?.ToString("dd/MM/yyyy"), AppearanceBox.Text, CurvReqBox.Text, CurvResBox.Text, OtherBox.Text, NoteBox.Text);

    private async void QuickBlank_Click(object sender, RoutedEventArgs e) => await Quick(CqExportKind.Blank);

    /// <summary>Lưu thông tin / kết quả đo đang sửa dở trước khi tạo hoặc xuất CQ.</summary>
    private bool SavePending()
    {
        if (_row is null || _readOnly) return false;
        if (InfoSnap() != _infoSnap && !SaveInfo()) return false;
        if (HardSnap() != _hardSnap)
        {
            if (CheckDateBox.SelectedDate is null) CheckDateBox.SelectedDate = DateTime.Today;
            if (!SaveHardness()) return false;
        }
        return true;
    }

    /// <summary>CQ còn khớp dữ liệu mới nhất của đơn (nháp hoặc đã phát hành) — null nếu phải tạo nháp mới.</summary>
    private CqVersionRecord? LiveCq()
    {
        var live = _row?.Cqs.Where(c => c.State != CqState.Deleted).OrderBy(c => c.Ver).LastOrDefault();
        return live is not null && live.OrderVersion == _row!.Version ? live : null;
    }

    /// <summary>Xác nhận lần cuối trước khi app xác nhận số đọc trên ảnh + kết quả đo và cấp số báo cáo.</summary>
    private bool AskBeforeNewCq(string what)
    {
        var q = _row!.Order.Qc;
        var pending = q.Images.Where(i => i.ConfirmedValue is null && i.OcrValue is not null && (i.Kind == "Hardness" || i.Kind == "Sample")).ToList();
        var vals = SampleTexts().Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var manual = _manualBoxes.Count > 0 ? "\nThông số nhập tay: " + string.Join(" · ", _manualBoxes.Select(x => $"{x.F.Label}: {(x.Box.Text.Trim().Length > 0 ? x.Box.Text.Trim() : "—")}")) + "\n" : "";
        var msg = $"{what}\n\nTemplate: {TplName(TemplateBox.SelectedValue as string ?? "")}\nKhách in trên CQ: {CustomerBox.Text}\nMác: {GradeBox.Text} · Yêu cầu: {ReqBox.Text} {TolBox.Text}\n"
                  + (HeatRow.Visibility == Visibility.Visible ? $"Tôi: {HeatBox.Text} · Ram: {RamBox.Text}\n" : "") + manual
                  + $"Mẫu 1–6: {(vals.Count == 0 ? "(chưa có)" : string.Join(" / ", vals))}\nKết quả: {EvalStatus.Text}\n"
                  + (pending.Count > 0 ? $"\nSố máy đọc trên {pending.Count} ảnh (độ cứng / số cân) sẽ được XÁC NHẬN: " + string.Join(", ", pending.Select(i => i.OcrValue)) + "\n" : "")
                  + "\nBạn đã kiểm tra các số trên ảnh? Bấm Có để xác nhận và cấp số báo cáo.";
        return Ui.Ask(msg);
    }

    private void WithSignBox_Click(object sender, RoutedEventArgs e)
    {
        _cfg = LocalConfig.Load(App.LocalDir); _cfg.ExcelWithSign = WithSignBox.IsChecked == true;
        try { _cfg.Save(App.LocalDir); } catch { }
    }

    /// <summary>"Tạo file CQ": xác nhận số đo, cấp số, tạo bản NHÁP và mở Excel để kiểm tra. Ô "Kèm dấu + chữ ký" bật → file Excel có sẵn
    /// con dấu + chữ ký (ảnh rời, kéo được trong Excel), lưu vào thư mục lưu CQ; tắt → file chưa dấu, chưa ký.</summary>
    private async void CreateDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!SavePending()) return;
        var withSign = WithSignBox.IsChecked == true;
        if (LiveCq() is { } cur)
        {
            if (Ui.Ask($"Đã có CQ số {cur.ReportNo} khớp dữ liệu mới nhất ({(cur.State == CqState.Draft ? "bản nháp" : "đã phát hành")}). Mở file Excel {(withSign ? "(kèm dấu + chữ ký) " : "")}để kiểm tra?"))
            {
                if (withSign) await OpenExcelWithSign(); else Open(cur, "xlsx");
            }
            return;
        }
        if (!AskBeforeNewCq(withSign ? "Tạo file CQ (Excel kèm dấu + chữ ký, CHƯA duyệt)." : "Tạo file CQ NHÁP (chưa có dấu, chữ ký).")) return;
        var r = await Ui.RunAsync(this, "Đang tạo bản nháp CQ…", () => Ws.PrepareCq(_key), CqHint);
        Load();
        if (r.Cq is null) return;
        ShowSaved($"Bản nháp CQ số {r.Cq.ReportNo}: " + string.Join(" → ", r.Steps) + (r.Cq.Warnings.Count > 0 ? " — " + string.Join("; ", r.Cq.Warnings) : ""));
        if (withSign) await OpenExcelWithSign(); else Open(r.Cq, "xlsx");
    }

    /// <summary>Excel CQ + con dấu + chữ ký (người ký đã nhớ / mặc định), vị trí mặc định của template — lưu vào thư mục lưu CQ và mở.</summary>
    private async Task OpenExcelWithSign()
    {
        var appr = ApproverBox.SelectedItem as SignerRecord; var chk = CheckerBox.SelectedItem as SignerRecord;
        var dest = _cfg.PdfRoot;
        var r = await Ui.RunAsync(this, "Đang tạo file Excel kèm dấu + chữ ký…", () => Ws.ExportCqExcel(_key, dest, appr?.Id, chk?.Id), CqHint);
        Load();
        if (r.Xlsx is null) return;
        Ui.OpenFile(r.Xlsx);
        ShowSaved($"CQ số {r.Cq.ReportNo}: " + string.Join(" → ", r.Steps) + ". Trong Excel: bấm vào con dấu / chữ ký rồi kéo để di chuyển, kéo góc để đổi cỡ.");
    }

    /// <summary>"Duyệt CQ": đưa bản nháp sang bản CHUẨN để duyệt (cửa sổ duyệt: người ký, kéo vị trí dấu / chữ ký, xem trước) →
    /// duyệt xong lưu PDF có dấu + chữ ký theo tên chuẩn và in / mở.</summary>
    private async void DuyetCq_Click(object sender, RoutedEventArgs e)
    {
        if (!SavePending()) return;
        if (!Ui.Run(() => Ws.RequireOnline("duyệt CQ"))) return;
        var appr = ApproverBox.SelectedItem as SignerRecord; var chk = CheckerBox.SelectedItem as SignerRecord;   // người ký đã nhớ — chọn lại trong cửa sổ duyệt
        var cq = LiveCq();
        if (cq is not null && !Ws.Cq!.CanApprove(cq.Id))
        {
            if (Ui.Ask($"CQ số {cq.ReportNo} đã duyệt (có dấu + chữ ký). Mở PDF đã duyệt?")) await SaveAndOutputSigned(cq, print: false);
            return;
        }
        if (cq is null)
        {
            if (!AskBeforeNewCq("Chưa có bản nháp khớp dữ liệu mới nhất → tạo bản nháp rồi mở bản chuẩn để duyệt.")) return;
            var r = await Ui.RunAsync(this, "Đang tạo bản nháp CQ…", () => Ws.PrepareCq(_key), CqHint);
            Load();
            if (r.Cq is null) return;
            cq = r.Cq;
        }
        SaveExportPrefs(CqExportKind.Signed);
        var w = new ApproveWindow(cq, appr?.Id, chk?.Id) { Owner = this };
        var ok = w.ShowDialog() == true;
        // nhớ người ký vừa chọn trong cửa sổ duyệt cho lần sau (và cho "Xuất Excel để chỉnh sửa")
        var signers = ApproverBox.ItemsSource as IEnumerable<SignerRecord>;
        if (w.ApproverBox.SelectedItem is SignerRecord wa) ApproverBox.SelectedItem = signers?.FirstOrDefault(x => x.Id == wa.Id);
        if (w.CheckerBox.SelectedItem is SignerRecord wc) CheckerBox.SelectedItem = signers?.FirstOrDefault(x => x.Id == wc.Id);
        SaveExportPrefs(CqExportKind.Signed);
        Load();
        if (!ok) return;
        var done = Ws.Cq!.ForOrder(_key).FirstOrDefault(c => c.Id == cq.Id);
        if (done is not null) await SaveAndOutputSigned(done, print: PrintAfterBox.IsChecked == true);
    }

    /// <summary>Lưu PDF bản duyệt (tên chuẩn, thư mục PDF) rồi in ra máy in hoặc mở.</summary>
    private async Task SaveAndOutputSigned(CqVersionRecord c, bool print)
    {
        var dest = _cfg.PdfRoot;                                               // file vào <thư mục lưu CQ>\<tháng-năm>\
        string? pdf = null;
        if (!Ui.Run(() => pdf = Ws.ExportCqNamed(c, _row!.Order, CqExportKind.Signed, dest)) || pdf is null) return;
        var printer = PrinterBox.SelectedItem as string;
        if (print && printer is not null)
        {
            var copies = int.TryParse(CopiesBox.Text, out var cp) && cp is > 0 and < 20 ? cp : 1;
            try
            {
                await CqPrinter.Print(File.ReadAllBytes(pdf), printer, copies, Path.GetFileNameWithoutExtension(pdf));
                if (!c.Printed) Ui.Run(() => Ws.Cq!.MarkPrinted(Ws.Session!, c.Id, true));
                Load();
                ShowSaved($"CQ số {c.ReportNo} đã duyệt — đã gửi {copies} bản ra máy in {printer}. File: {pdf}");
            }
            catch (Exception ex) { Ui.Info("Không in được: " + ex.Message + "\nFile PDF vẫn được lưu: " + pdf); Ui.OpenFile(pdf); }
            return;
        }
        Ui.OpenFile(pdf);
        ShowSaved($"CQ số {c.ReportNo} đã duyệt — PDF có dấu + chữ ký: {pdf}");
    }

    /// <summary>"Xuất Excel để chỉnh sửa": file Excel cùng tên với PDF, dấu + chữ ký là ảnh rời (kéo được). Không tự duyệt CQ.</summary>
    private async void ExportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (!SavePending()) return;
        var appr = ApproverBox.SelectedItem as SignerRecord; var chk = CheckerBox.SelectedItem as SignerRecord;
        if (appr is null || chk is null) { Ui.Info("Chưa có người ký mặc định. Bấm \"Duyệt CQ\" một lần để chọn Người duyệt / Người kiểm tra (app nhớ cho lần sau), hoặc quản trị đặt mặc định ở Cài đặt → Chữ ký và con dấu."); return; }
        if (LiveCq() is null && !AskBeforeNewCq("Xuất file EXCEL để chỉnh sửa (tạo bản nháp CQ nếu chưa có; KHÔNG duyệt CQ).")) return;
        SaveExportPrefs(CqExportKind.Signed);
        var dest = _cfg.PdfRoot;                                               // file vào <thư mục lưu CQ>\<tháng-năm>\
        var r = await Ui.RunAsync(this, "Đang xuất file Excel…", () => Ws.ExportCqExcel(_key, dest, appr.Id, chk.Id), CqHint);
        Load();
        if (r.Xlsx is null) return;
        Ui.OpenFile(r.Xlsx);
        ShowSaved($"CQ số {r.Cq.ReportNo}: " + string.Join(" → ", r.Steps) + ". Trong Excel: bấm vào con dấu / chữ ký rồi kéo để di chuyển, kéo góc để đổi cỡ.");
    }

    /// <summary>"Sửa trong Excel rồi in": mọi kiểu bản in — sửa tay nội dung trong Excel trước khi tạo PDF / in.</summary>
    private void EditPrint_Click(object sender, RoutedEventArgs e)
    {
        if (!SavePending()) return;
        if (!Ui.Run(() => Ws.RequireOnline("sửa CQ trong Excel"))) return;
        if (LiveCq() is null && !AskBeforeNewCq("Sửa CQ trong Excel rồi in (tạo bản nháp CQ nếu chưa có).")) return;
        var appr = ApproverBox.SelectedItem as SignerRecord; var chk = CheckerBox.SelectedItem as SignerRecord;
        var copies = int.TryParse(CopiesBox.Text, out var cp) && cp is > 0 and < 20 ? cp : 1;
        var w = new EditPrintWindow(_key, _cfg.PdfRoot, appr?.Id, chk?.Id, PrinterBox.SelectedItem as string, copies) { Owner = this };
        w.ShowDialog();
        if (w.Changed) Load();
    }

    /// <summary>Bản in KHÔNG dấu, KHÔNG chữ ký (ký tay) — cùng số báo cáo với bản duyệt.</summary>
    private async Task Quick(CqExportKind kind)
    {
        if (!SavePending()) return;
        var kindText = kind switch { CqExportKind.Signed => "CÓ DẤU + CHỮ KÝ", CqExportKind.StampOnly => "CÓ DẤU, CHƯA KÝ", _ => "KHÔNG DẤU, KHÔNG CHỮ KÝ (in ký tay)" };
        if (LiveCq() is null && !AskBeforeNewCq($"Bản in: {kindText}.")) return;
        var appr = ApproverBox.SelectedItem as SignerRecord; var chk = CheckerBox.SelectedItem as SignerRecord;
        if (kind == CqExportKind.Signed && (appr is null || chk is null)) { Ui.Info("Chưa có người ký mặc định — bấm \"Duyệt CQ\" để chọn."); return; }
        var printer = PrinterBox.SelectedItem as string; var doPrint = PrintAfterBox.IsChecked == true;
        if (doPrint && printer is null) { Ui.Info("Chọn máy in (hoặc bỏ tick \"In ra máy in\")."); return; }
        var copies = int.TryParse(CopiesBox.Text, out var cp) && cp is > 0 and < 20 ? cp : 1;
        SaveExportPrefs(kind);
        var dest = _cfg.PdfRoot;                                               // file vào <thư mục lưu CQ>\<tháng-năm>\
        var r = await Ui.RunAsync(this, "Đang tạo CQ và xuất PDF…", () => Ws.QuickCq(_key, kind, dest, appr?.Id, chk?.Id), CqHint);
        Load();
        if (r.Pdf is null) return;
        if (doPrint)
        {
            try
            {
                await CqPrinter.Print(File.ReadAllBytes(r.Pdf), printer!, copies, Path.GetFileNameWithoutExtension(r.Pdf));
                if (!r.Cq.Printed) Ui.Run(() => Ws.Cq!.MarkPrinted(Ws.Session!, r.Cq.Id, true));
                Load();
                ShowSaved($"CQ số {r.Cq.ReportNo} — {kindText}: đã gửi {copies} bản ra máy in {printer}. File: {r.Pdf}");
            }
            catch (Exception ex) { Ui.Info("Không in được: " + ex.Message + "\nFile PDF vẫn được lưu: " + r.Pdf); Ui.OpenFile(r.Pdf); }
            return;
        }
        Ui.OpenFile(r.Pdf);
        ShowSaved($"CQ số {r.Cq.ReportNo} — {kindText}: " + string.Join(" → ", r.Steps));
    }

    // ---------------- Tab Xuất CQ: người ký, máy in, thư mục PDF, xác nhận lần cuối ----------------
    private LocalConfig _cfg = new();
    public sealed record ReviewItem(string Label, string Value, Brush Brush);

    private void InitExportPanel()
    {
        _cfg = LocalConfig.Load(App.LocalDir);
        if (Ws.SyncSharedCqFolder(_cfg)) { try { _cfg.Save(App.LocalDir); } catch { } }      // thư mục lưu CQ chung do quản trị đặt
        List<SignerRecord> signers = new(); string? defA = null, defC = null;
        if (Ws.Online) Ui.Run(() => { var st = Ws.Settings!.Get().Value; signers = st.Signers.Where(x => x.Active).OrderBy(x => x.Name).ToList(); defA = st.DefaultApproverId; defC = st.DefaultCheckerId; });
        ApproverBox.ItemsSource = signers; CheckerBox.ItemsSource = signers;
        ApproverBox.SelectedItem = signers.FirstOrDefault(x => x.Id == (_cfg.LastApproverId ?? defA)) ?? signers.FirstOrDefault(x => x.Id == defA);
        CheckerBox.SelectedItem = signers.FirstOrDefault(x => x.Id == (_cfg.LastCheckerId ?? defC)) ?? signers.FirstOrDefault(x => x.Id == defC);
        var printers = CqPrinter.Printers();
        PrinterBox.ItemsSource = printers;
        PrinterBox.SelectedItem = printers.FirstOrDefault(x => x == _cfg.Printer) ?? printers.FirstOrDefault(x => x == CqPrinter.DefaultPrinter()) ?? printers.FirstOrDefault();
        PrintAfterBox.IsChecked = false;          // không tự in ngầm sau khi duyệt — in bằng nút "In ▾"
        WithSignBox.IsChecked = _cfg.ExcelWithSign;
        CopiesBox.Text = Math.Max(1, _cfg.Copies).ToString();
        ShowPdfFolder();
    }

    /// <summary>Đưa chữ ký / con dấu từ ngoài vào app ngay ở tab Xuất CQ (quản trị); xong chọn luôn người vừa thêm.</summary>
    private void AddSignature_Click(object sender, RoutedEventArgs e)
    {
        if (!Ws.Online) { Ui.Info("Cần kết nối kho chung để thêm chữ ký / con dấu."); return; }
        if (Ws.Session!.User.Role != Role.Admin) { Ui.Info("Chỉ tài khoản quản trị (admin) được thêm / thay chữ ký và con dấu (dùng chung mọi máy). Đăng nhập admin hoặc nhờ quản trị thêm ở Cài đặt → Chữ ký và con dấu."); return; }
        var w = new SignatureImportWindow { Owner = this };
        if (w.ShowDialog() != true) return;
        InitExportPanel();
        if (w.AddedSignerId is { } id && ApproverBox.ItemsSource is IEnumerable<SignerRecord> list)
        {
            var rec = list.FirstOrDefault(x => x.Id == id);
            if (w.AsApprover || (!w.AsChecker && ApproverBox.SelectedItem is null)) ApproverBox.SelectedItem = rec;
            if (w.AsChecker || (!w.AsApprover && CheckerBox.SelectedItem is null)) CheckerBox.SelectedItem = rec;
        }
        ShowSaved(w.StampChanged ? "Đã cập nhật con dấu (dùng chung mọi máy)." : "Đã thêm chữ ký (dùng chung mọi máy).");
    }

    private void SaveExportPrefs(CqExportKind kind)
    {
        _cfg = LocalConfig.Load(App.LocalDir);
        _cfg.LastApproverId = (ApproverBox.SelectedItem as SignerRecord)?.Id ?? _cfg.LastApproverId;
        _cfg.LastCheckerId = (CheckerBox.SelectedItem as SignerRecord)?.Id ?? _cfg.LastCheckerId;
        _cfg.Printer = PrinterBox.SelectedItem as string ?? _cfg.Printer;
        _cfg.PrintAfterExport = PrintAfterBox.IsChecked == true;
        _cfg.Copies = int.TryParse(CopiesBox.Text, out var c) && c > 0 ? c : 1;
        _cfg.ExportKind = kind.ToString();
        try { _cfg.Save(App.LocalDir); } catch { }
    }


    /// <summary>"Mở thư mục": thư mục lưu CQ (chứa các thư mục tháng-năm).</summary>
    private void OpenCqRoot_Click(object sender, RoutedEventArgs e)
    {
        _cfg = LocalConfig.Load(App.LocalDir);
        try { Directory.CreateDirectory(_cfg.PdfRoot); Ui.OpenFile(_cfg.PdfRoot); }
        catch (Exception ex) { Ui.Info("Không mở được thư mục lưu CQ: " + _cfg.PdfRoot + "\n" + ex.Message); }
    }

    private void PdfFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Chọn thư mục lưu CQ (app tự tạo thư mục con theo tháng-năm, vd 11-2026)" };
        if (Directory.Exists(_cfg.PdfRoot)) d.InitialDirectory = _cfg.PdfRoot;
        if (d.ShowDialog() != true) return;
        _cfg = LocalConfig.Load(App.LocalDir); _cfg.PdfFolder = d.FolderName;
        try { _cfg.Save(App.LocalDir); } catch { }
        ShowPdfFolder();
    }

    /// <summary>Thư mục lưu CQ đang dùng + quy tắc thư mục tháng / tên file.</summary>
    private void ShowPdfFolder()
    {
        PdfFolderText.Text = $"Lưu vào: {System.IO.Path.Combine(_cfg.PdfRoot, DateTime.Now.ToString("MM-yyyy"))}   ({_cfg.PdfRootSource} · đổi ở \"Xuất khác ▾\")";
        PdfFolderText.ToolTip = "Ví dụ: " + System.IO.Path.Combine(_cfg.PdfRoot, DateTime.Now.ToString("MM-yyyy"), $"{DateTime.Now:MM}-0216 - HTVG - SKD11 - 58-62.pdf");
    }

    private void OpenPdfFolder_Click(object sender, RoutedEventArgs e)
    {
        var c = (CqGrid.SelectedItem as CqItem)?.Rec ?? _row?.Cqs.Where(x => x.State != CqState.Deleted).OrderBy(x => x.Ver).LastOrDefault();
        var dir = c is not null ? Workspace.MonthFolder(_cfg.PdfRoot, c) : System.IO.Path.Combine(_cfg.PdfRoot, DateTime.Now.ToString("MM-yyyy"));
        Directory.CreateDirectory(dir);
        Ui.OpenFile(dir);
    }

    /// <summary>Mở đơn lần đầu: tự điền các ô còn trống (mác, yêu cầu độ cứng từ ERP; nhiệt độ tôi, ram, dung sai, template từ bảng mác
    /// hoặc đơn trước của cùng khách). Không ghi đè thứ QC đã nhập; đơn đã có CQ không tự sửa.</summary>
    private void AutoFill()
    {
        if (_readOnly || _row is null) return;
        AutoFillResult? r = null;
        if (!Ui.Run(() => r = Ws.AutoFillQc(_key)) || r is null) return;
        if (r.Filled.Count > 0) { _row = r.Row; Load(); }
        // chỉ hiện khi còn việc QC phải làm (phần tự điền đã thấy ngay trên các ô)
        var t = r.NeedsQc.Count > 0 ? "⚠ Cần QC nhập: " + string.Join(" · ", r.NeedsQc) : "";
        if (r.Filled.Count > 0) Quiet("Đã tự điền: " + string.Join(" · ", r.Filled));
        AutoText.Text = t; AutoBox.Visibility = t.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Ẩn các ô mà template của đơn không có chỗ in (vd. Mã QL / Tên chi tiết / Độ cong / Nhiệt độ tôi ở mẫu không dùng).</summary>
    private void ApplyTemplateVisibility(string? templateId)
    {
        var map = MapFor(templateId);
        bool Has(string f) => map is null || map.Fields.ContainsKey(f) && (map.Cell(f) is not null || map.Cells(f).Count > 0 || map.Ranges(f).Count > 0 || map.Texts(f).Count > 0);
        var rows = new (FrameworkElement El, bool Show, string Name)[]
        {
            (SizeRow, Has("Size"), "Kích thước"), (PartNoRow, Has("PartNo"), "Mã quản lý"), (PartNameRow, Has("PartName"), "Tên chi tiết"),
            (HeatRow, Has("QuenchTemp1") || Has("QuenchTemp2") || Has("DrawingTemperatures"), "Nhiệt độ tôi"),
            (RamRow, Has("DrawingRamTexts") || Has("RamCells"), "Ram"),
            (AppearanceRow, Has("AppearanceResult"), "Ngoại quan"), (CurvRow, Has("CurvatureRequirement") || Has("CurvatureResult"), "Độ cong"),
            (OtherRow, Has("OtherResult"), "Yêu cầu khác"),
        };
        foreach (var (el, show, _) in rows) el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var hidden = rows.Where(x => !x.Show).Select(x => x.Name).ToList();
        HiddenNote.Text = hidden.Count > 0 ? $"Template \"{(TemplateBox.SelectedItem as TemplateInfo)?.DisplayName ?? templateId}\" không in: {string.Join(", ", hidden)} — đã ẩn." : "";
    }

    private readonly Dictionary<string, TemplateMap?> _maps = new();
    /// <summary>Bản đồ template (bộ đệm theo cửa sổ). Offline → bản đồ cài kèm.</summary>
    private TemplateMap? MapFor(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (_maps.TryGetValue(id, out var m)) return m;
        try { m = Ws.Online ? Ws.Cq!.Templates.Resolve(id).Map : TemplateMap.Load(Ws.Paths.TemplateMap(id)); } catch { m = null; }
        return _maps[id] = m;
    }

    // ---------------- Thông số nhập tay theo template (Vinfast, Thấm Nitơ…) ----------------
    private readonly List<(ManualField F, TextBox Box)> _manualBoxes = new();
    private string? _manualTpl;

    /// <summary>Dựng biểu mẫu nhập tay cho template: mỗi thông số một ô; chưa nhập → hiện sẵn mặc định của mẫu (giá trị trên CQ gốc,
    /// hoặc tính từ yêu cầu / kết quả đo). Ô có * bắt buộc trước khi tạo CQ.</summary>
    private void BuildManualPanel(string? tplId)
    {
        _manualTpl = tplId;
        ManualPanel.Children.Clear(); _manualBoxes.Clear();
        var map = MapFor(tplId);
        if (map is null || map.Manual.Count == 0 || _row is null) { ManualBox.Visibility = Visibility.Collapsed; return; }
        ManualBox.Visibility = Visibility.Visible;
        ManualTitle.Text = $"Thông số nhập tay — mẫu CQ \"{map.DisplayName}\"";
        ManualHint.Text = "Điền các thông số in lên báo cáo theo mẫu này. Ô đang hiện giá trị mặc định của mẫu — sửa nếu khác; ô có * bắt buộc. Bấm \"Lưu thông tin\" (hoặc tạo CQ: app tự lưu).";
        var ev = ManualValues.Eval(_row.Order);
        var saved = _row.Order.Qc.Manual;
        foreach (var g in map.Manual.GroupBy(m => m.Group))
        {
            if (g.Key.Length > 0) ManualPanel.Children.Add(new TextBlock { Text = g.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2), Foreground = (Brush)FindResource("Muted") });
            var wrap = new WrapPanel();
            foreach (var mf in g)
            {
                var def = ManualValues.Default(mf, _row.Order, ev) ?? "";
                var tb = new TextBox { Width = 230, Text = saved is not null && saved.TryGetValue(mf.Key, out var v) ? v : def, Tag = def, IsEnabled = !_readOnly,
                                       ToolTip = (mf.Cell is not null ? $"Ô {mf.Cell}" : $"Chữ \"{mf.Text}\" trên lưu đồ") + (def.Length > 0 ? $" · mặc định: {def}" : "") };
                var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
                sp.Children.Add(new TextBlock { Text = mf.Label + (mf.Required ? " *" : ""), Style = (Style)FindResource("Label") });
                sp.Children.Add(tb);
                wrap.Children.Add(sp);
                _manualBoxes.Add((mf, tb));
            }
            ManualPanel.Children.Add(wrap);
        }
    }

    /// <summary>Giá trị nhập tay để lưu: trùng mặc định → không lưu (luôn theo mặc định mới nhất); khác → lưu; xoá trắng ô có mặc định → lưu rỗng.</summary>
    private Dictionary<string, string?> ManualEdits() => _manualBoxes.ToDictionary(x => x.F.Key, x =>
        x.Box.Text.Trim() == ((string?)x.Box.Tag ?? "").Trim() ? null : x.Box.Text.Trim());

    private void ConfirmAll_Click(object sender, RoutedEventArgs e)
    {
        if (_row is null || _readOnly) return;
        var imgs = _row.Order.Qc.Images.Where(i => i.ConfirmedValue is null && i.Kind is "Hardness" or "Sample").OrderBy(i => i.AddedOrder).ToList();
        var list = imgs.Select(i => $"• {(i.Kind == "Hardness" ? "Ảnh độ cứng" : "Ảnh cân")} {i.AddedOrder}: {i.OcrValue ?? "(không đọc được — nhập tay)"}").ToList();
        if (imgs.Count == 0 && _row.Order.Qc.Hardness.ConfirmedAt is not null) { Ui.Info("Mọi số đọc và kết quả đo đã được xác nhận."); return; }
        if (!Ui.Ask((list.Count > 0 ? "Xác nhận các số máy đọc trên ảnh:\n" + string.Join("\n", list) + "\n\n" : "")
                    + "Sau đó điền Mẫu 1–6 (xếp tăng dần, nếu đang trống) và xác nhận kết quả đo.\nBạn đã kiểm tra các số trên ảnh?")) return;
        ConfirmAllResult? r = null;
        if (!Ui.Run(() => r = Ws.ConfirmAllReadings(_key, _row.Version)) || r is null) { Load(); return; }
        _row = r.Row; Load();
        var msg = $"Đã xác nhận {r.Confirmed.Count} số đọc." + (r.SamplesFilled ? " Đã điền Mẫu 1–6." : "")
                  + (r.Skipped.Count > 0 ? "\nCần xử lý tay:\n" + string.Join("\n", r.Skipped) : "");
        var samplesOk = _row.Order.Qc.Hardness.Samples.Any(x => !string.IsNullOrWhiteSpace(x));
        if (samplesOk && _row.Order.Qc.Hardness.ConfirmedAt is null && _row.Order.Qc.Hardness.CheckDate is not null)
        {
            Evaluation? ev = null;
            if (Ui.Run(() => { var c = Ws.ConfirmHardness(_key, _row!.Version); _row = c.Row; ev = c.Ev; }))
            {
                Load();
                msg += $"\nKết quả đo: {(ev!.Status == EvaluationStatus.Pass ? "ĐẠT" : "KHÔNG ĐẠT")} — sang tab Xuất CQ để xuất bản in.";
            }
            else Load();
        }
        else if (!samplesOk) msg += "\nChưa có số đo để điền Mẫu 1–6 — gõ tay ở cột bên phải hoặc Nhập từ CSV/Excel.";
        Ui.Info(msg);
    }

    private void Reload_Click(object sender, RoutedEventArgs e) { AcquireLock(); Load(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Mọi sửa đổi đi qua đây: dùng version đang xem làm gốc → nếu máy khác đã sửa thì báo xung đột, không ghi đè.</summary>
    private bool Edit(Action<QcData> f, string what)
    {
        if (_row is null || _readOnly) return false;
        var ok = Ui.Run(() => _row = Ws.EditQc(_key, _row.Version, f, what));
        Load();
        return ok;
    }

    // ---------------- Thông tin ----------------
    private static List<string>? SplitList(string t)
    {
        var l = t.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return l.Count == 0 ? null : l;
    }
    private static string? N(string t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();

    private int? _pendingGradeRow;
    private void SaveInfo_Click(object sender, RoutedEventArgs e) => SaveInfo();

    private bool SaveInfo()
    {
        var req = N(ReqBox.Text);
        if (req is not null && HardnessRequirement.TryParse(req) is null) { Ui.Info("Yêu cầu độ cứng không hợp lệ. Dạng đúng: 58-62"); return false; }
        var (grade, tpl, tol, heat, ram) = (N(GradeBox.Text), TemplateBox.SelectedValue as string, N(TolBox.Text), SplitList(HeatBox.Text), SplitList(RamBox.Text));
        var (cust, size, pno, pname, row) = (N(CustomerBox.Text), N(SizeBox.Text), N(PartNoBox.Text), N(PartNameBox.Text), _pendingGradeRow);
        var manual = ManualEdits();
        var erpCust = _row?.Order.Erp.GetValueOrDefault("Customer");
        if (cust is not null && erpCust is not null && cust == Ws.CustomerOnCq(_row!.Order) && _row.Order.Qc.CustomerOverride is null) cust = null;   // không đổi
        else if (cust is not null && cust.Trim() == erpCust?.Trim()) cust = null;                                                         // = tên ERP
        else if (cust is not null && erpCust is not null && Ws.Online)
        {
            if (!Ui.Run(() => Ws.SetCustomerAlias(erpCust, cust))) return false;
            cust = null;                                                                                                                  // dùng tên viết tắt chung
        }
        var ok = Edit(q =>
        {
            if (q.SteelGrade != grade) q.SteelGradeRow = null;
            if (q.HardnessRequirement != req || q.Tolerance != tol) { q.Hardness.ConfirmedAt = null; q.Hardness.ConfirmedBy = null; }   // đổi yêu cầu/dung sai → xác nhận lại
            q.SteelGrade = grade; q.TemplateId = tpl; q.HardnessRequirement = req; q.Tolerance = tol; q.QuenchTemps = heat; q.RamValues = ram;
            q.CustomerOverride = cust; q.Size = size; q.PartNo = pno; q.PartName = pname;
            if (row is not null) q.SteelGradeRow = row;
            foreach (var (k, v) in manual)
            {
                if (v is null) q.Manual?.Remove(k);
                else (q.Manual ??= new())[k] = v;
            }
            if (q.Manual is { Count: 0 }) q.Manual = null;
        }, "info.save");
        if (ok) { _pendingGradeRow = null; ShowSaved("Đã lưu thông tin đơn"); }
        return ok;
    }

    private void ShowSaved(string msg) { if (Owner is MainWindow m) m.ShowStatus(Ws.Status(msg)); else Ui.Info(msg); }
    /// <summary>Thông báo nhẹ (thanh trạng thái cửa sổ chính) — không bật hộp thoại (tự lưu, tự điền).</summary>
    private void Quiet(string msg) { if (Owner is MainWindow m) m.ShowStatus(Ws.Status(msg)); }

    /// <summary>Mác thép → tra bảng mác: điền nhiệt độ tôi, ram, dung sai, template (cột ĐÁNH DẤU) đúng như file CQ 2026; thấm carbon → tôi 2 = 920℃.
    /// Mác có nhiều dòng KHÁC thông số → cho QC chọn dòng. Không có trong bảng mác → mở phần nhập tay.</summary>
    private string? _gradeApplied;
    private void ApplyGrade(bool interactive, bool fromTemplate = false)
    {
        if (_row is null || _readOnly) return;
        var grade = GradeBox.Text.Trim();
        _gradeApplied = grade;
        if (grade.Length == 0) return;
        CqNl.Infrastructure.Excel.SteelGradeTable? t = null;
        try { t = Ws.SteelTable(); } catch { }
        if (t is null) { GradeInfo.Visibility = Visibility.Visible; GradeInfo.Text = "⚠ Chưa có bảng mác thép (Cài đặt → Bảng mác thép) — nhập tay thông số ở mục dưới."; ParamExpander.IsExpanded = true; return; }
        var found = Workspace.FindGrade(t, grade);
        if (found.Count == 0) { GradeInfo.Visibility = Visibility.Visible; GradeInfo.Text = $"⚠ Mác \"{grade}\" không có trong bảng mác thép — nhập tay nhiệt độ tôi / ram / dung sai ở mục dưới."; ParamExpander.IsExpanded = true; return; }
        var g = found[0];
        var distinct = found.GroupBy(x => (string.Join("|", Workspace.HeatTexts(x)), string.Join("|", Workspace.RamTexts(x)), x.Tolerance, x.TemplateMark)).Count();
        if (distinct > 1)
        {
            if (!interactive) { GradeInfo.Visibility = Visibility.Visible; GradeInfo.Text = $"⚠ Mác \"{grade}\" có {distinct} dòng khác thông số trong bảng mác — sửa ô Mác rồi chọn dòng."; return; }
            var w = new PickGradeWindow(found) { Owner = this };
            if (w.ShowDialog() != true || w.Selected is null) return;
            g = w.Selected;
        }
        var heats = Workspace.HeatTexts(g); var rams = Workspace.RamTexts(g);
        // template: người làm vừa chọn → giữ; template đang chọn không phụ thuộc mác (Thấm Nitơ) hoặc cùng cột ĐÁNH DẤU (Vinfast = khuôn) → giữ;
        // còn lại theo cột ĐÁNH DẤU của bảng mác, rồi theo tên sản phẩm ERP
        var curTpl = TemplateBox.SelectedValue as string; var curMap = MapFor(curTpl);
        var tpl = fromTemplate || (curMap is not null && (curMap.SteelGradeMark is null || curMap.SteelGradeMark == g.TemplateMark)) ? curTpl
            : Workspace.TemplateFromMark(g.TemplateMark) ?? curTpl ?? Workspace.TemplateFromProduct(_row.Order.Erp.GetValueOrDefault("ProductName"), _row.Order.Erp.GetValueOrDefault("ProductCode"));
        var method = Workspace.Plain(_row.Order.Erp.GetValueOrDefault("ProcessMethod"));
        if (tpl == "carbon" && heats.Count >= 2 && (method.Contains("THAM CARBON") || method.Contains("THAMC"))) heats[1] = "920℃";
        HeatBox.Text = string.Join("; ", heats); RamBox.Text = string.Join("; ", rams); TolBox.Text = g.Tolerance?.Trim() ?? "";
        if (tpl is not null && !fromTemplate && !Equals(TemplateBox.SelectedValue, tpl)) TemplateBox.SelectedValue = tpl;
        _pendingGradeRow = g.SourceRow;
        ShowGradeInfo(g.SourceRow);
    }

    private int? _gradeInfoRow;
    /// <summary>Hiện thông số đang in lên CQ và ĐỐI CHIẾU với bảng mác thép: template (cột ĐÁNH DẤU), nhiệt độ tôi, ram, dung sai khác bảng mác → cảnh báo cam.</summary>
    private void ShowGradeInfo(int? row)
    {
        _gradeInfoRow = row;
        var text = (row is { } r ? $"Bảng mác dòng {r}: " : "Thông số: ")
            + $"tôi {(HeatBox.Text.Length > 0 ? HeatBox.Text : "—")} · ram {(RamBox.Text.Length > 0 ? RamBox.Text : "—")} · dung sai {(TolBox.Text.Length > 0 ? TolBox.Text : "—")}";
        var warn = new List<string>();
        try
        {
            var t = Ws.SteelTable(); var grade = GradeBox.Text.Trim();
            var found = Workspace.FindGrade(t, grade);
            var g = found.FirstOrDefault(x => x.SourceRow == row)
                    ?? (found.GroupBy(x => (string.Join("|", Workspace.HeatTexts(x)), string.Join("|", Workspace.RamTexts(x)), x.Tolerance, x.TemplateMark)).Count() == 1 ? found[0] : null);
            if (t is not null && grade.Length > 0 && found.Count == 0) warn.Add($"mác \"{grade}\" không có trong bảng mác thép — kiểm tra thông số nhập tay");
            if (g is not null)
            {
                var tplWant = Workspace.TemplateFromMark(g.TemplateMark);
                var tplNow = TemplateBox.SelectedValue as string;
                var mapNow = MapFor(tplNow);
                // template phụ thuộc mác (khuôn / gió / carbon / Vinfast): so cột ĐÁNH DẤU; template nhập tay (Thấm Nitơ) không so
                if (tplWant is not null && tplNow is not null && mapNow?.SteelGradeMark is int markNow && markNow != g.TemplateMark)
                    warn.Add($"bảng mác đánh dấu template \"{TplName(tplWant)}\" nhưng đang chọn \"{TplName(tplNow)}\"");
                var usesHeat = mapNow is null || mapNow.Texts("DrawingTemperatures").Count > 0 || mapNow.Cell("QuenchTemp1") is not null;
                var usesRam = mapNow is null || mapNow.Texts("DrawingRamTexts").Count > 0 || mapNow.Cells("RamCells").Count > 0;
                static string J(IEnumerable<string> v) => string.Join(";", v.Select(x => x.Replace(" ", "").Replace("°C", "℃").ToUpperInvariant()));
                var heatsNow = HeatBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var heatsWant = Workspace.HeatTexts(g).ToList();
                var method = Workspace.Plain(_row?.Order.Erp.GetValueOrDefault("ProcessMethod"));
                if ((tplNow ?? tplWant) == "carbon" && heatsWant.Count >= 2 && (method.Contains("THAM CARBON") || method.Contains("THAMC"))) heatsWant[1] = "920℃";
                if (usesHeat && J(heatsNow) != J(heatsWant)) warn.Add("nhiệt độ tôi khác bảng mác");
                if (usesRam && J(RamBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) != J(Workspace.RamTexts(g))) warn.Add("ram khác bảng mác");
                if ((TolBox.Text.Trim()) != (g.Tolerance?.Trim() ?? "")) warn.Add("dung sai khác bảng mác");
            }
        }
        catch { }
        ParamExpander.Header = "Thông số in trên CQ (tự lấy theo mác thép) — " + text;
        if (warn.Count > 0)
        {
            GradeInfo.Text = "⚠ Đối chiếu bảng mác: " + string.Join("; ", warn) + " — sửa ô Mác (tự lấy lại) hoặc kiểm tra mục thông số bên dưới.";
            GradeInfo.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
            GradeInfo.Visibility = Visibility.Visible;
        }
        else GradeInfo.Visibility = Visibility.Collapsed;
    }

    private string TplName(string id) =>
        (TemplateBox.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Select(o => (o.GetType().GetProperty("Id")?.GetValue(o) as string, o.GetType().GetProperty("DisplayName")?.GetValue(o) as string))
            .FirstOrDefault(x => x.Item1 == id).Item2 ?? id;

    // ---------------- Ảnh ----------------
    private static BitmapImage? ToBitmap(byte[]? b, int w = 480)
    {
        if (b is null) return null;
        try
        {
            var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = w; bi.StreamSource = new MemoryStream(b); bi.EndInit(); bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private void LoadImages()
    {
        var imgs = _row!.Order.Qc.Images.OrderBy(i => i.Kind == "Sample" ? 0 : 1).ThenBy(i => i.AddedOrder).ToList();
        var items = new List<ImageItem>();
        foreach (var im in imgs)
        {
            byte[]? b = null;
            try { b = im.NormalizedSha is not null ? Ws.ImageBytes(im.NormalizedSha, "jpg") : Ws.ImageBytes(im.BlobSha, im.Ext); } catch { }
            var isHard = im.Kind == "Hardness";
            items.Add(new ImageItem
            {
                Id = im.Id, KindText = isHard ? "Ảnh đo độ cứng" : im.Kind == "Sample" ? "Ảnh sản phẩm" : "Ảnh kim tương / khác",
                Thumb = ToBitmap(b), Value = im.ConfirmedValue ?? im.OcrValue ?? "",
                CropText = b is null ? "Chưa có ảnh trên máy này (cần kết nối kho chung)" : im.NeedsManualCrop ? "⚠ Không tự cắt được — bấm \"Cắt ảnh…\"" : im.NormalizedSha is not null ? "Đã cắt + làm sáng theo chuẩn" : "Ảnh gốc",
                CropBrush = im.NeedsManualCrop || b is null ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("Muted"),
                OcrText = (isHard ? "Máy đọc gợi ý (HRC): " : "Số cân đọc gợi ý (kg): ") + (im.OcrValue ?? "không đọc được — nhập tay") + (im.OcrValue is not null ? $" · độ tin cậy {im.OcrConfidence:0.0}" : ""),
                ConfirmedText = im.ConfirmedValue is not null ? $"✔ Đã xác nhận {im.ConfirmedValue} — {im.ConfirmedBy} {im.ConfirmedAt:dd/MM HH:mm}"
                    : ImageVisionService.BlocksAuto(im) ? "⚠ Cảnh báo OCR — bấm vào ảnh để đối chiếu và xác nhận riêng" : "Chưa xác nhận",
                ConfirmedBrush = im.ConfirmedValue is null ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("OkBrush"),
                OnCq = im.OnCq, CanEdit = !_readOnly, Kind = im.Kind,
            });
        }
        ImageList.ItemsSource = items;
        ImageList.SelectedItem = items.FirstOrDefault(x => x.Id == _selImgId);
        ShowSelectedImage();
        ImgEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var h = imgs.Count(i => i.Kind == "Hardness" && i.OnCq);
        ImgStatus.Text = imgs.Count == 0 ? "" : $"{imgs.Count(i => i.Kind == "Sample")} ảnh sản phẩm · {imgs.Count(i => i.Kind == "Hardness")} ảnh độ cứng ({h} in trên CQ, tối đa 8). Bấm vào ảnh để đổi loại / cắt / xoá.";
    }

    private async Task AddFiles(IEnumerable<string> files, string kind)
    {
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
            if (ext is not ("jpg" or "jpeg" or "png" or "bmp")) { Ui.Info("Bỏ qua file không phải ảnh: " + Path.GetFileName(f)); continue; }
            var bytes = await File.ReadAllBytesAsync(f);
            await AddBytes(bytes, ext == "jpeg" ? "jpg" : ext, kind, Path.GetFileName(f));
        }
    }

    private async Task AddBytes(byte[] bytes, string ext, string kind, string name)
    {
        if (_row is null || _readOnly) return;
        var ver = _row.Version;
        var im = await Ui.RunAsync(this, $"Đang xử lý ảnh {name}: dò vùng số đo, làm rõ (HD / in đen trắng), đọc số trên ảnh gốc và ảnh xử lý, kiểm tra sai lệch…", () => kind == "Auto" ? Ws.AddImageAuto(_key, ver, bytes, ext) : Ws.AddImage(_key, ver, bytes, ext, kind), ImgStatus);
        Load();
        if (im is not null && im.NeedsManualCrop) ShowSaved($"{name}: không tự cắt được — hãy bấm \"Cắt tay…\" trên ảnh đó.");
        else if (im is not null && ImageVisionService.BlocksAuto(im)) ShowSaved($"{name}: ⚠ {im.Vision!.Flags.FirstOrDefault()} — bấm vào ảnh để đối chiếu và xác nhận.");
    }

    private async void AddSample_Click(object sender, RoutedEventArgs e) => await Pick("Sample");
    private async void AddHard_Click(object sender, RoutedEventArgs e) => await Pick("Auto");
    private async Task Pick(string kind)
    {
        var d = new OpenFileDialog { Multiselect = true, Filter = "Ảnh (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp", Title = kind == "Sample" ? "Chọn ảnh xuất hàng" : "Chọn ảnh (ảnh máy đo + ảnh sản phẩm)" };
        if (d.ShowDialog() == true) await AddFiles(d.FileNames, kind);
    }

    private static string DropKind => "Auto";   // tự phân loại; đổi loại sau ở thanh "Loại ảnh"

    private async void Images_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddFiles(files, DropKind);
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; AcquireLock(); Load(); return; }
        // Ctrl+Alt+S: chụp màn hình (công cụ cắt màn hình của Windows) → ảnh chụp tự vào đơn
        if (key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) { e.Handled = true; StartCapture(launch: true); return; }
        // Ctrl+V: ảnh trong clipboard (chụp màn hình, Ctrl+C ảnh / file ảnh) → thêm vào đơn ở BẤT KỲ tab nào (trừ tab Lưu ý).
        // Đang gõ trong ô chữ mà clipboard có chữ → để ô chữ dán chữ như thường.
        if (key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !_readOnly && !ReferenceEquals(Tabs.SelectedItem, NotesTab))
        {
            if (Keyboard.FocusedElement is TextBox && SafeClip(Clipboard.ContainsText)) return;
            var clip = ReadClipboard();
            if (clip is null) return;
            e.Handled = true;
            await AddClip(clip);
        }
    }

    // ---------------- Clipboard: Ctrl+C / Ctrl+V / chụp màn hình ----------------
    public sealed record ClipImage(IReadOnlyList<string>? Files, byte[]? Bytes, string Format);

    private static bool SafeClip(Func<bool> f) { for (var i = 0; i < 5; i++) { try { return f(); } catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(60); } } return false; }

    private static bool IsImageFile(string f) => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp";

    /// <summary>Đọc ảnh trong clipboard theo mọi dạng thường gặp: file ảnh (Ctrl+C trong Explorer), "PNG" (Snipping Tool, trình duyệt),
    /// DIB (Zalo, Paint, PrtScn…), Bitmap. Clipboard đang bị app khác giữ → thử lại vài lần.</summary>
    public static ClipImage? ReadClipboard()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().Where(f => IsImageFile(f) && File.Exists(f)).ToList();
                    if (files.Count > 0) return new ClipImage(files, null, "FileDrop");
                }
                var data = Clipboard.GetDataObject();
                if (data is null) return null;
                if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png && png.Length > 8) return new ClipImage(null, png.ToArray(), "PNG");
                if (data.GetDataPresent(DataFormats.Dib) && data.GetData(DataFormats.Dib) is MemoryStream dib && DibToPng(dib.ToArray()) is { } p1) return new ClipImage(null, p1, "DIB");
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bmp) return new ClipImage(null, EncodePng(bmp), "Bitmap");
                return null;
            }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(80); }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        }
        return null;
    }

    private static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    /// <summary>CF_DIB (BITMAPINFO + điểm ảnh) → PNG: thêm tiêu đề file BMP 14 byte rồi giải mã.</summary>
    public static byte[]? DibToPng(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var hdr = BitConverter.ToInt32(dib, 0); var bits = BitConverter.ToInt16(dib, 14); var comp = BitConverter.ToInt32(dib, 16); var used = BitConverter.ToInt32(dib, 32);
        if (hdr < 40 || hdr > dib.Length) return null;
        var colors = used > 0 ? used : bits <= 8 ? 1 << bits : 0;
        var masks = hdr == 40 && comp == 3 ? 12 : 0;                    // BI_BITFIELDS: 3 mặt nạ màu sau BITMAPINFOHEADER
        var off = 14 + hdr + masks + colors * 4;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B'; file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2); BitConverter.GetBytes(off).CopyTo(file, 10);
        Buffer.BlockCopy(dib, 0, file, 14, dib.Length);
        try
        {
            var dec = new BmpBitmapDecoder(new MemoryStream(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource f = dec.Frames[0];
            if (f.Format == PixelFormats.Bgra32 || f.Format == PixelFormats.Pbgra32) f = new FormatConvertedBitmap(f, PixelFormats.Bgr32, null, 0);   // ảnh chụp màn hình: kênh alpha = 0 → bỏ
            return EncodePng(f);
        }
        catch { return null; }
    }

    /// <summary>Thêm ảnh từ clipboard vào đơn (tự nhận loại), chuyển sang tab Ảnh &amp; kết quả đo. Trả số ảnh đã thêm.</summary>
    public async Task<int> AddClip(ClipImage clip)
    {
        if (_row is null || _readOnly) return 0;
        Tabs.SelectedIndex = 1;
        var before = _row.Order.Qc.Images.Count;
        if (clip.Files is { Count: > 0 }) await AddFiles(clip.Files, DropKind);
        else if (clip.Bytes is not null) await AddBytes(clip.Bytes, "png", DropKind, clip.Format == "PNG" || clip.Format == "DIB" || clip.Format == "Bitmap" ? "Ảnh dán (Ctrl+V)" : "Ảnh dán");
        return (_row?.Order.Qc.Images.Count ?? before) - before;
    }

    /// <summary>Dán ảnh đang có trong clipboard (như Ctrl+V). Trả số ảnh đã thêm (0 = clipboard không có ảnh).</summary>
    public async Task<int> PasteFromClipboard() => ReadClipboard() is { } c ? await AddClip(c) : 0;

    private DateTime? _captureUntil;
    /// <summary>Chụp màn hình: mở công cụ cắt màn hình của Windows (Ctrl+Alt+S / nút "Chụp màn hình"); ảnh chụp vào clipboard trong 3 phút
    /// tới → tự thêm vào đơn. Dùng công cụ chụp khác (Zalo, Snipping Tool…) rồi Ctrl+V cũng được.</summary>
    public void StartCapture(bool launch)
    {
        if (_row is null || _readOnly) return;
        _captureUntil = DateTime.Now.AddMinutes(3);
        Tabs.SelectedIndex = 1;
        ImgStatus.Text = "Đang chờ ảnh chụp màn hình… chụp xong ảnh tự vào đơn (hoặc Ctrl+V).";
        if (!launch) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
        catch { Ui.Info("Không mở được công cụ chụp màn hình của Windows. Hãy chụp bằng Win+Shift+S (hoặc Zalo Ctrl+Alt+S) — ảnh chụp sẽ tự vào đơn, hoặc bấm Ctrl+V."); }
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => StartCapture(launch: true);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    private const int WmClipboardUpdate = 0x031D;
    private IntPtr _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        try { AddClipboardFormatListener(_hwnd); } catch { }
        Closed += (_, _) => { try { RemoveClipboardFormatListener(_hwnd); } catch { } };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OnClipboardChanged));
        return IntPtr.Zero;
    }

    /// <summary>Clipboard đổi: đang chờ ảnh chụp màn hình → tự thêm; luôn cập nhật ô "Clipboard" ở dải ẢNH GẦN ĐÂY.</summary>
    private async void OnClipboardChanged()
    {
        var clip = ReadClipboard();
        if (clip?.Bytes is not null && _captureUntil is { } until && DateTime.Now <= until && !_readOnly)
        {
            _captureUntil = null;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            await AddClip(clip);
        }
        RefreshClipboardItem(clip);
    }

    private void RefreshClipboardItem(ClipImage? clip)
    {
        var items = (RecentList.ItemsSource as List<RecentItem>)?.Where(x => x.Name != "Clipboard").ToList() ?? new List<RecentItem>();
        if (clip?.Bytes is { } b)
        {
            BitmapSource? th = null;
            try { var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 160; bi.StreamSource = new MemoryStream(b); bi.EndInit(); bi.Freeze(); th = bi; } catch { }
            items.Insert(0, new RecentItem { Name = "Clipboard", Sub = "ảnh vừa chụp/copy", Tip = "Ảnh trong Clipboard (Ctrl+V)", Thumb = th, Bytes = b });
        }
        RecentList.ItemsSource = items;
    }

    private InspectionImage? Img(object sender) => ((FrameworkElement)sender).Tag is string id ? _row?.Order.Qc.Images.FirstOrDefault(i => i.Id == id) : null;

    private void ConfirmImg_Click(object sender, RoutedEventArgs e)
    {
        var im = Img(sender); if (im is null || _row is null) return;
        var item = (ImageList.ItemsSource as List<ImageItem>)!.First(x => x.Id == im.Id);
        if (Ui.Run(() => _row = Ws.ConfirmImage(_key, _row.Version, im.Id, item.Value))) Load();
    }

    private void OnCq_Click(object sender, RoutedEventArgs e)
    {
        var im = Img(sender); if (im is null) return;
        var on = ((CheckBox)sender).IsChecked == true;
        Edit(q => { q.Images.First(i => i.Id == im.Id).OnCq = on; Workspace.ApplyAutoSamples(q); }, $"image.oncq {im.Id}={on}");
    }

    private void RemoveImg_Click(object sender, RoutedEventArgs e) => Remove_Img(sender);
    private void Remove_Img(object sender)
    {
        var im = Img(sender); if (im is null) return;
        if (!Ui.Ask("Xoá ảnh này khỏi đơn? (File ảnh gốc vẫn được giữ trong kho, có ghi nhật ký)")) return;
        Edit(q => { q.Images.RemoveAll(i => i.Id == im.Id); Workspace.ApplyAutoSamples(q); }, $"image.remove {im.Id} {im.BlobSha}");
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var im = Img(sender); if (im is null) return;
        if (!Ui.Ask("Bỏ cắt: dùng ẢNH GỐC (bỏ cắt và bỏ làm sáng) cho ảnh này?")) return;
        Edit(q => { var x = q.Images.First(i => i.Id == im.Id); x.NormalizedSha = null; x.Crop = null; x.NeedsManualCrop = false; }, $"image.restore {im.Id}");
    }

    private async void Crop_Click(object sender, RoutedEventArgs e)
    {
        var im = Img(sender); if (im is null || _row is null) return;
        byte[]? orig = null;
        if (!Ui.Run(() => orig = Ws.ImageBytes(im.BlobSha, im.Ext)) || orig is null) { Ui.Info("Cần kết nối kho chung để lấy ảnh gốc"); return; }
        var w = new CropWindow(orig, im.Crop) { Owner = this };
        if (w.ShowDialog() != true || w.Result is not { } r) return;
        var ver = _row.Version;
        await Ui.RunAsync(this, "", () => { Ws.CropImage(_key, ver, im.Id, r); return true; }, ImgStatus);
        Load();
    }

    // ---------------- Kết quả đo ----------------
    private List<string> SampleTexts() => _samples.Select(t => t.Text).ToList();

    private void UpdateEval()
    {
        if (_row is null) return;
        var probe = Json.From<OrderRecord>(Json.ToNode(_row.Order));
        probe.Qc.Hardness.Samples = SampleTexts();
        probe.Qc.HardnessRequirement = N(ReqBox.Text); probe.Qc.Tolerance = N(TolBox.Text);
        var (ev, err) = OrderService.TryEvaluate(probe);
        EvalStatus.Foreground = (Brush)FindResource("Muted");
        if (ev is null) { EvalStatus.Text = "LỖI NHẬP"; EvalStatus.Foreground = (Brush)FindResource("BadBrush"); EvalDetail.Text = err; }
        else
        {
            switch (ev.Status)
            {
                case EvaluationStatus.InvalidRequirement: EvalStatus.Text = "—"; EvalDetail.Text = "Chưa có yêu cầu độ cứng hợp lệ (nhập ở tab Thông tin đơn, dạng 58-62)."; break;
                case EvaluationStatus.NoData: EvalStatus.Text = "—"; EvalDetail.Text = $"Giới hạn chấp nhận [{ev.Low}; {ev.High}]. Chưa có điểm đo."; break;
                default:
                    var pass = ev.Status == EvaluationStatus.Pass;
                    EvalStatus.Text = pass ? "ĐẠT" : "KHÔNG ĐẠT";
                    EvalStatus.Foreground = pass ? (Brush)FindResource("OkBrush") : (Brush)FindResource("BadBrush");
                    EvalDetail.Text = $"Giới hạn chấp nhận [{ev.Low}; {ev.High}] (theo yêu cầu + dung sai)\nNhỏ nhất {ev.Min} · lớn nhất {ev.Max} · trung bình {ev.Average}"
                        + (ev.Failing is { Count: > 0 } f ? "\nĐiểm ngoài giới hạn: " + string.Join(", ", f) : "\nMọi điểm đo nằm trong giới hạn");
                    break;
            }
        }
        var h = _row.Order.Qc.Hardness;
        var dirty = !h.Samples.SequenceEqual(SampleTexts().Select(x => x.Trim()).Reverse().SkipWhile(x => x.Length == 0).Reverse());
        ConfirmState.Text = h.ConfirmedAt is { } at && !dirty ? $"✔ Đã xác nhận bởi {h.ConfirmedBy} lúc {at:dd/MM/yyyy HH:mm}" : dirty ? "Có thay đổi chưa lưu" : "Chưa xác nhận kết quả";
        ConfirmState.Foreground = h.ConfirmedAt is not null && !dirty ? (Brush)FindResource("OkBrush") : (Brush)FindResource("WarnBrush");
        AutoSampleText.Text = h.SamplesAuto && !dirty ? $"✔ Mẫu 1–6 đang lấy tự động từ ảnh đo (thêm/sửa/gỡ ảnh → tự cập nhật). Gõ tay để sửa."
            : dirty ? "Đang nhập… (tự lưu khi rời ô)" : "";
        EvalBadgeText.Text = EvalStatus.Text is "ĐẠT" or "KHÔNG ĐẠT" ? EvalStatus.Text : "—";
        EvalBadgeText.Foreground = EvalStatus.Foreground;
        EvalBadge.Background = new SolidColorBrush(EvalBadgeText.Text == "ĐẠT" ? Color.FromRgb(0xE6, 0xF4, 0xEA) : EvalBadgeText.Text == "KHÔNG ĐẠT" ? Color.FromRgb(0xFD, 0xEC, 0xEA) : Color.FromRgb(0xEE, 0xF2, 0xF8));
    }

    private void FillFromImages_Click(object sender, RoutedEventArgs e)
    {
        var vals = _row!.Order.Qc.Images.Where(i => i.Kind == "Hardness" && i.ConfirmedValue is not null).OrderBy(i => i.AddedOrder).Select(i => i.ConfirmedValue!).ToList();
        if (vals.Count == 0) { Ui.Info("Chưa có ảnh đo độ cứng nào được xác nhận số"); return; }
        if (vals.Count > _samples.Count) { Ui.Info($"Có {vals.Count} giá trị, tối đa {_samples.Count} mẫu — nhập tay các mẫu ghép nhiều điểm"); return; }
        if (SampleTexts().Any(t => t.Trim().Length > 0) && !Ui.Ask("Thay các ô mẫu đang có bằng giá trị đã xác nhận trên ảnh?")) return;
        for (var i = 0; i < _samples.Count; i++) _samples[i].Text = vals.ElementAtOrDefault(i) ?? "";
    }

    private bool SaveHardness() { var ok = SaveHardCore(); Load(); return ok; }

    private bool SaveHardCore()
    {
        if (_row is null || _readOnly) return false;
        var (unit, date, app, cr, cs, oth, note) = (N(UnitBox.Text) ?? "(HRC)", CheckDateBox.SelectedDate?.ToString("dd/MM/yyyy"), N(AppearanceBox.Text), N(CurvReqBox.Text), N(CurvResBox.Text), N(OtherBox.Text), N(NoteBox.Text));
        return Ui.Run(() => _row = Ws.SaveHardness(_key, _row.Version, SampleTexts(), h =>
        {
            h.Unit = unit; h.CheckDate = date; h.Appearance = app; h.CurvatureRequirement = cr; h.CurvatureResult = cs; h.Other = oth; h.Note = note;
        }));
    }

    /// <summary>"Tự động lưu khi nhập": rời ô (mẫu, đơn vị, ngày kiểm, ngoại quan, độ cong, yêu cầu khác, ghi chú) → lưu nếu có thay đổi.
    /// Không tải lại cả màn hình (giữ vị trí con trỏ); xung đột với máy khác → báo và tải lại.</summary>
    private bool _savingHard;
    private void AutoSaveHard()
    {
        if (_row is null || _readOnly || _savingHard || _loading || !IsLoaded || HardSnap() == _hardSnap) return;
        _savingHard = true;
        try
        {
            if (SaveHardCore()) { _hardSnap = HardSnap(); UpdateEval(); UpdateState(); Quiet("Đã tự lưu kết quả đo"); }
            else Load();
        }
        finally { _savingHard = false; }
    }

    private void ImportSamples_Click(object sender, RoutedEventArgs e)
    {
        if (_row is null || _readOnly) return;
        var d = new OpenFileDialog { Filter = "Điểm đo (*.csv;*.txt;*.xlsx)|*.csv;*.txt;*.xlsx|Tất cả|*.*", Title = "Nhập điểm đo độ cứng từ CSV/Excel (mỗi dòng = 1 mẫu)" };
        if (d.ShowDialog() != true) return;
        List<string>? vals = null;
        if (!Ui.Run(() => vals = Workspace.SamplesFromTable(d.FileName, _samples.Count)) || vals is null) return;
        var preview = string.Join("\n", vals.Select((v, i) => $"Mẫu {i + 1}: {v}"));
        if (!Ui.Ask((SampleTexts().Any(t => t.Trim().Length > 0) ? "Thay các ô Mẫu đang có bằng số đọc từ file?\n\n" : "Điền Mẫu từ file?\n\n") + preview)) return;
        for (var i = 0; i < _samples.Count; i++) _samples[i].Text = vals.ElementAtOrDefault(i) ?? "";
        if (CheckDateBox.SelectedDate is null) CheckDateBox.SelectedDate = DateTime.Today;
        AutoSaveHard();
    }

    // ---------------- Ảnh đang chọn: loại ảnh / khung ảnh CQ / cắt / bỏ cắt / xoá ----------------
    private string? _selImgId;
    private InspectionImage? SelImg() => _selImgId is null ? null : _row?.Order.Qc.Images.FirstOrDefault(i => i.Id == _selImgId);

    private void Card_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        var id = (string)((FrameworkElement)sender).Tag;
        if ((ImageList.ItemsSource as List<ImageItem>)?.FirstOrDefault(x => x.Id == id) is { } it && !ReferenceEquals(ImageList.SelectedItem, it)) ImageList.SelectedItem = it;
    }

    private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ImageList.SelectedItem is ImageItem it) _selImgId = it.Id;
        ShowSelectedImage();
    }

    private void ShowSelectedImage()
    {
        var im = SelImg();
        foreach (var c in new Control[] { ImgKindBox, ImgOnCqBox, ApplyImgBtn, CropImgBtn, RestoreImgBtn, RemoveImgBtn }) c.IsEnabled = im is not null && !_readOnly;
        foreach (var b in new[] { CropImgBtn, RestoreImgBtn, RemoveImgBtn }) b.Tag = im?.Id;
        if (im is null) { SelImgText.Text = "Chọn ảnh"; ImgKindBox.SelectedIndex = -1; ImgOnCqBox.IsChecked = false; return; }
        var shown = (ImageList.ItemsSource as List<ImageItem>) ?? new();
        SelImgText.Text = $"Ảnh {shown.FindIndex(i => i.Id == im.Id) + 1}/{shown.Count}";
        ImgKindBox.SelectedIndex = im.Kind switch { "Hardness" => 0, "Sample" => 1, _ => 2 };
        ImgOnCqBox.IsChecked = im.OnCq;
        RestoreImgBtn.IsEnabled = RestoreImgBtn.IsEnabled && (im.NormalizedSha is not null || im.Crop is not null);
    }

    private async void ApplyImg_Click(object sender, RoutedEventArgs e)
    {
        var im = SelImg(); if (im is null || _row is null || _readOnly) return;
        var kind = (ImgKindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? im.Kind;
        var on = ImgOnCqBox.IsChecked == true;
        if (kind == im.Kind && on == im.OnCq) { Quiet("Ảnh không thay đổi"); return; }
        if (kind != im.Kind)
        {
            var (ver, id) = (_row.Version, im.Id);
            var r = await Ui.RunAsync(this, "Đang đọc lại ảnh theo loại mới…", () => Ws.ChangeImageKind(_key, ver, id, kind), ImgStatus);
            if (r is null) { Load(); return; }
            _row = r;
        }
        if (on != im.OnCq) Edit(q => { q.Images.First(i => i.Id == im.Id).OnCq = on; Workspace.ApplyAutoSamples(q); }, $"image.oncq {im.Id}={on}");
        else Load();
        if (kind != im.Kind) ShowSaved($"Đã đổi thành {(kind == "Hardness" ? "ảnh đo độ cứng" : "ảnh sản phẩm")} — kiểm tra số đọc lại trên ảnh rồi bấm ✔");
        else Quiet(on ? "Ảnh sẽ in trên CQ" : "Ảnh không in trên CQ");
    }

    private void CropSel_Click(object sender, RoutedEventArgs e) => Crop_Click(sender, e);
    private void RestoreSel_Click(object sender, RoutedEventArgs e) => Restore_Click(sender, e);
    private void RemoveSel_Click(object sender, RoutedEventArgs e) { Remove_Img(sender); }

    private void SaveHard_Click(object sender, RoutedEventArgs e) { if (SaveHardness()) ShowSaved("Đã lưu kết quả đo"); }

    private void ConfirmHard_Click(object sender, RoutedEventArgs e)
    {
        if (CheckDateBox.SelectedDate is null) { Ui.Info("Chọn ngày kiểm tra"); return; }
        if (!SaveHardness()) return;
        Evaluation? ev = null;
        if (!Ui.Run(() => { var r = Ws.ConfirmHardness(_key, _row!.Version); _row = r.Row; ev = r.Ev; })) { Load(); return; }
        Load();
        Tabs.SelectedIndex = 2;                                                // bước tiếp theo: Xuất CQ
        ShowSaved($"Đã xác nhận kết quả: {(ev!.Status == EvaluationStatus.Pass ? "ĐẠT" : "KHÔNG ĐẠT")} — bấm \"Duyệt CQ\" để ra file CQ.");
    }

    // ---------------- CQ ----------------
    private void LoadCqs()
    {
        var items = _row!.Cqs.OrderByDescending(c => c.Ver).Select(c => new CqItem(c, c.Ver, c.ReportNo, c.Result == "Pass" ? "ĐẠT" : "KHÔNG ĐẠT",
            c.State switch { CqState.Approved => "Đã phát hành" + (c.Pdfs.Count > 0 ? ": " + string.Join(", ", c.Pdfs.Keys.Select(PdfKindText)) : ""), CqState.Deleted => "Đã xóa", _ => "Nháp" },
            $"{c.CreatedAt:dd/MM/yyyy HH:mm}", c.ApprovedAt is { } a ? $"{c.ApprovedBy} {a:dd/MM/yyyy HH:mm}" : "",
            c.Printed ? $"{c.PrintedBy} {c.PrintedAt:dd/MM HH:mm}" : "",
            string.Join("; ", c.Warnings.Concat(c.DeleteReason is null ? Array.Empty<string>() : new[] { $"Lý do xóa: {c.DeleteReason} ({c.DeletedBy} {c.DeletedAt:dd/MM HH:mm})" })))).ToList();
        CqGrid.ItemsSource = items;
        CqHint.Text = !Ws.Online ? "OFFLINE — tạo/duyệt/xóa CQ cần kết nối kho chung."
            : "";
        if (items.Count > 0 && CqGrid.SelectedItem is null) CqGrid.SelectedIndex = 0;
    }

    private CqItem? SelCq() { if (CqGrid.SelectedItem is CqItem c) return c; Ui.Info("Chọn một bản CQ trong danh sách"); return null; }

    private async void CreateCq_Click(object sender, RoutedEventArgs e) => await CreateCq(null);
    private async void CreateCqManual_Click(object sender, RoutedEventArgs e)
    {
        if (!SavePending()) return;
        var n = Ui.Prompt(this, "Số báo cáo nhập tay", "Nhập số báo cáo dạng NNN/MM (vd 015/11). Số trùng sẽ được cảnh báo:");
        if (n is null) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{3}/\d{2}$")) { Ui.Info("Số báo cáo phải dạng NNN/MM"); return; }
        await CreateCq(n);
    }

    private async Task CreateCq(string? no)
    {
        var c = await Ui.RunAsync(this, "", () => Ws.CreateCq(_key, no));
        Load();
        if (c is null) return;
        ShowSaved($"Đã tạo CQ số {c.ReportNo} (bản {c.Ver}, {(c.Result == "Pass" ? "ĐẠT" : "KHÔNG ĐẠT")})" + (c.Warnings.Count > 0 ? " — " + string.Join("; ", c.Warnings) : ""));
        if (Ui.Ask($"Đã tạo CQ số {c.ReportNo}. Mở file Excel để kiểm tra?")) Open(c, "xlsx");
    }

    private void Open(CqVersionRecord c, string kind)
    {
        string? p = null;
        if (Ui.Run(() => p = Ws.ExportCq(c, kind, Path.Combine(Path.GetTempPath(), "CQNhietLuyen")))) Ui.OpenFile(p!);
    }

    private void OpenXlsx_Click(object sender, RoutedEventArgs e)
    {
        if (SelCq() is not { } c) return;
        var k = new[] { "Signed", "StampOnly", "Blank" }.FirstOrDefault(c.Rec.Xlsxs.ContainsKey);
        Open(c.Rec, k is not null ? "xlsx:" + k : c.Rec.ApprovedXlsxSha is not null ? "xlsx-approved" : "xlsx");
    }
    private void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        if (SelCq() is not { } c) return;
        var k = new[] { "Signed", "StampOnly", "Blank" }.FirstOrDefault(c.Rec.Pdfs.ContainsKey);
        Open(c.Rec, k is null ? "pdf" : "pdf:" + k);
    }

    private static string PdfKindText(string k) => k switch { "Signed" => "dấu + ký", "StampOnly" => "có dấu chưa ký", "Blank" => "trắng ký tay", _ => k };

    private void SaveCopy_Click(object sender, RoutedEventArgs e)
    {
        if (SelCq() is not { } c) return;
        var approved = c.Rec.State == CqState.Approved;
        var d = new SaveFileDialog { Filter = approved ? "PDF (*.pdf)|*.pdf|Excel (*.xlsx)|*.xlsx" : "Excel (*.xlsx)|*.xlsx", FileName = $"CQ_{c.ReportNo.Replace('/', '-')}" };
        if (d.ShowDialog() != true) return;
        var kind = Path.GetExtension(d.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "pdf" : approved ? "xlsx-approved" : "xlsx";
        Ui.Run(() =>
        {
            var tmp = Ws.ExportCq(c.Rec, kind, Path.Combine(Path.GetTempPath(), "CQNhietLuyen"));
            File.Copy(tmp, d.FileName, overwrite: true);
        });
    }

    private void CqGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => PrintedBox.IsChecked = (CqGrid.SelectedItem as CqItem)?.Rec.Printed == true;

    private void PrintedBox_Click(object sender, RoutedEventArgs e)
    {
        Printed_Click(sender, e);
        PrintedBox.IsChecked = (CqGrid.SelectedItem as CqItem)?.Rec.Printed == true;
    }

    /// <summary>Trạng thái đơn ở góc trên: Chờ kiểm tra → Đã đo → Đã xác nhận → Đã tạo CQ → Đã duyệt → Đã in.</summary>
    private void UpdateState()
    {
        if (_row is null) return;
        var h = _row.Order.Qc.Hardness;
        var live = _row.Cqs.Where(c => c.State != CqState.Deleted).OrderBy(c => c.Ver).LastOrDefault();
        var (t, ok) = live is { Printed: true } ? ("Đã in", true) : live is { State: CqState.Approved } ? ("Đã duyệt", true) : live is not null ? ("Đã tạo CQ", true)
            : h.ConfirmedAt is not null ? ("Đã xác nhận kết quả", true) : h.Samples.Any(x => !string.IsNullOrWhiteSpace(x)) ? ("Đã đo", false) : ("Chờ kiểm tra", false);
        StateText.Text = t;
        StateText.Foreground = ok ? (Brush)FindResource("OkBrush") : (Brush)FindResource("WarnBrush");
    }

    private void Printed_Click(object sender, RoutedEventArgs e)
    {
        if (SelCq() is not { } c) return;
        if (c.Rec.State != CqState.Approved) { Ui.Info("Chỉ đánh dấu in cho CQ đã duyệt"); return; }
        var to = !c.Rec.Printed;
        if (!to && !Ui.Ask("Bỏ đánh dấu đã in?")) return;
        if (Ui.Run(() => { Ws.RequireOnline("đánh dấu in"); Ws.Cq!.MarkPrinted(Ws.Session!, c.Rec.Id, to); })) Load();
    }

    private void DeleteCq_Click(object sender, RoutedEventArgs e)
    {
        if (SelCq() is not { } c) return;
        if (c.Rec.State == CqState.Deleted) { Ui.Info("CQ này đã bị xóa"); return; }
        var reason = Ui.Prompt(this, "Xóa CQ", $"Xóa CQ số {c.ReportNo} (bản {c.Ver}, {c.StateText}).\nSố báo cáo sẽ KHÔNG được cấp lại. File vẫn lưu trong kho để tra cứu.\nNhập lý do xóa (bắt buộc):");
        if (reason is null) return;
        if (Ui.Run(() => { Ws.RequireOnline("xóa CQ"); Ws.Cq!.Delete(Ws.Session!, c.Rec.Id, reason); })) Load();
    }

    // ---------------- Xem ảnh lớn ----------------
    private void Thumb_Click(object sender, MouseButtonEventArgs e)
    {
        var im = Img(sender); if (im is null) return;
        OpenInspect(im);
    }

    /// <summary>Cửa sổ "Ảnh &amp; OCR": 3 chế độ ảnh, so sánh trước/sau, kết quả đọc số + cảnh báo, xác nhận / sửa, xử lý lại, tải xuống.</summary>
    private void OpenInspect(InspectionImage im)
    {
        var id = im.Id;
        InspectionImage? Current() => _row?.Order.Qc.Images.FirstOrDefault(i => i.Id == id);
        var w = new ImageInspectWindow(im, _key, $"{(im.Kind == "Hardness" ? "Ảnh đo độ cứng" : im.Kind == "Sample" ? "Ảnh sản phẩm" : "Ảnh khác")} · {TitleText.Text}", !_readOnly,
            confirm: val =>
            {
                if (_row is null) return null;
                return Ui.Run(() => _row = Ws.ConfirmImage(_key, _row.Version, id, val)) ? Current() : null;
            },
            reprocess: () =>
            {
                try { var r = Ws.ReprocessImage(_key, _row!.Version, id); _row = r; return r.Order.Qc.Images.FirstOrDefault(i => i.Id == id); }
                catch (Exception ex) { Dispatcher.Invoke(() => Ui.Error(ex)); return null; }
            }) { Owner = this };
        w.ShowDialog();
        if (w.Changed) Load();
    }

    // ---------------- Lưu ý (QC + Kinh doanh) ----------------
    public sealed record NoteThumb(BitmapImage? Image, string Key, string Name);
    public sealed record NoteItem(string Id, string Header, string Badges, string Text, List<NoteThumb> Thumbs, Brush Background, Visibility HideVisibility);
    private readonly List<(byte[] Bytes, string Ext, string Name)> _attach = new();
    private List<Workspace.NoteView> _notes = new();

    private void LoadNotes()
    {
        List<Workspace.NoteView> notes = new();
        if (!Ui.Run(() => notes = Ws.NotesFor(_key).ToList())) return;
        _notes = notes;
        var me = Ws.Session!.User;
        NotesList.ItemsSource = notes.Select(v =>
        {
            var n = v.Note;
            var badges = string.Join(" · ", new[]
            {
                v.FromOtherOrder ? $"Từ đơn trước {n.OrderKey.Replace("|", " / dòng ")}" : null,
                n.ForCustomer ? "Nhắc lại cho các đơn sau của khách" : null,
                v.Pending ? "Chưa đồng bộ lên kho chung" : null,
            }.Where(x => x is not null));
            var thumbs = n.Images.Select(i => new NoteThumb(ToBitmap(SafeBytes(i.Sha, i.Ext), 320), i.Sha + "." + i.Ext, i.Name)).ToList();
            return new NoteItem(n.Id, $"{(string.IsNullOrWhiteSpace(n.AuthorName) ? n.Author : n.AuthorName)} · {Ui.RoleText(n.AuthorRole)} · {n.At:dd/MM/yyyy HH:mm}",
                badges, n.Text, thumbs, v.FromOtherOrder ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE1)) : Brushes.White,
                (n.Author == me.Username || me.Role == Role.Admin) && !v.Pending && Ws.Online ? Visibility.Visible : Visibility.Collapsed);
        }).ToList();
        NotesEmpty.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportNotesText.Text = string.Join("\n", notes.Select(v => $"• {v.Note.Text.Trim()}  — {(string.IsNullOrWhiteSpace(v.Note.AuthorName) ? v.Note.Author : v.Note.AuthorName)}, {v.Note.At:dd/MM}" + (v.FromOtherOrder ? " (từ đơn trước của khách)" : "")));
        ExportNotesBox.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var own = notes.Count(n => !n.FromOtherOrder); var fut = notes.Where(n => n.FromOtherOrder).ToList();
        NotesTab.Header = notes.Count == 0 ? "Lưu ý" : $"Lưu ý ({notes.Count})";
        if (fut.Count > 0)
        {
            var first = fut[0].Note.Text.Replace("\r", " ").Replace("\n", " ");
            NoteBannerText.Text = $"⚠ LƯU Ý CỦA KHÁCH HÀNG (từ đơn trước): {(first.Length > 160 ? first[..160] + "…" : first)}" + (fut.Count > 1 ? $"  (+{fut.Count - 1} lưu ý khác)" : "") + " — bấm để xem";
            NoteBanner.Visibility = Visibility.Visible;
        }
        else NoteBanner.Visibility = Visibility.Collapsed;
    }

    private byte[]? SafeBytes(string sha, string ext) { try { return Ws.ImageBytes(sha, ext); } catch { return null; } }

    private void NoteBanner_Click(object sender, MouseButtonEventArgs e) => Tabs.SelectedItem = NotesTab;

    private void NoteThumb_Click(object sender, MouseButtonEventArgs e)
    {
        var key = (string)((FrameworkElement)sender).Tag; var dot = key.LastIndexOf('.');
        var b = SafeBytes(key[..dot], key[(dot + 1)..]);
        if (b is null) { Ui.Info("Ảnh chưa có trên máy này — cần kết nối kho chung"); return; }
        new ImageViewWindow("Ảnh lưu ý · " + TitleText.Text, b, null, null) { Owner = this }.Show();
    }

    private void RefreshAttach() => AttachList.ItemsSource = _attach.Select(a => new TextBlock { Text = $"• {a.Name} ({a.Bytes.Length / 1024} KB)", FontSize = 12 }).ToList();

    private void AttachNoteImage_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Filter = "Ảnh (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp", Title = "Chọn ảnh đính kèm lưu ý" };
        if (d.ShowDialog() != true) return;
        foreach (var f in d.FileNames)
        {
            var ext = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
            _attach.Add((File.ReadAllBytes(f), ext == "jpeg" ? "jpg" : ext, Path.GetFileName(f)));
        }
        RefreshAttach();
    }

    private void ClearAttach_Click(object sender, RoutedEventArgs e) { _attach.Clear(); RefreshAttach(); }

    private async void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        var (text, fc, imgs) = (NoteText.Text, ForCustomerBox.IsChecked == true, _attach.ToList());
        var n = await Ui.RunAsync(this, "", () => Ws.AddNote(_key, text, fc, imgs));
        if (n is null) return;
        NoteText.Clear(); ForCustomerBox.IsChecked = false; _attach.Clear(); RefreshAttach();
        LoadNotes();
        ShowSaved(Ws.Online ? "Đã lưu lưu ý" : "Đã lưu lưu ý trên máy này — sẽ tự gửi lên kho chung khi có kết nối");
    }

    private void HideNote_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((FrameworkElement)sender).Tag;
        if (!Ui.Ask("Ẩn lưu ý này? (Vẫn lưu trong lịch sử kho, có ghi nhật ký)")) return;
        if (Ui.Run(() => Ws.HideNote(id))) LoadNotes();
    }
}
