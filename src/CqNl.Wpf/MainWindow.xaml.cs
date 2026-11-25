using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CqNl.Application;
using CqNl.Wpf.Views;

namespace CqNl.Wpf;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _ticking;
    private DateTime _lastErpCheck = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        FooterText.Text = AppInfo.Footer;
        var s = App.Ws.Session ?? throw new InvalidOperationException("Chưa đăng nhập — không mở được cửa sổ chính");
        UserName.Text = string.IsNullOrWhiteSpace(s.User.FullName) ? s.User.Username : s.User.FullName;
        UserRole.Text = $"{s.User.Username} · {Ui.RoleText(s.User.Role)}";
        var last = UserName.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        AvatarText.Text = string.IsNullOrEmpty(last) ? "?" : last[..1].ToUpperInvariant();
        StorePathText.Text = App.Ws.StorePath; LocalPathText.Text = App.LocalDir;
        var admin = s.User.Role == Role.Admin;
        // người không phải quản trị: ẩn hẳn các mục quản trị (không để nút mờ gây rối)
        foreach (var el in new FrameworkElement[] { AdminHead, NavUsers, NavSettings, NavTemplates }) el.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        _timer.Tick += async (_, _) => await TickAsync();
        Loaded += (_, _) => { _timer.Start(); ShowStatus(App.Ws.Status(s.OfflineLogin ? "Đăng nhập OFFLINE: không kiểm tra được tài khoản có đang mở ở máy khác hay không." : null)); Navigate(NavOrders); _ = CheckUpdateAsync(prompt: true); _ = StartupCheckAsync(); WarnSingleAdmin(); };
        Closing += (_, e) =>
        {
            if (App.Ws.Outbox.Items().Any(i => i.State == "pending") && !Ui.Ask("Còn nháp chưa đồng bộ lên kho chung. Nháp vẫn được giữ trên máy này và tự đồng bộ lần sau. Thoát?")) { e.Cancel = true; return; }
            _timer.Stop(); App.Ws.Logout(); System.Windows.Application.Current.Shutdown();
        };
    }

    /// <summary>App không còn "quên mật khẩu quản trị" → kho chỉ có 1 quản trị đang hoạt động thì nhắc tạo thêm 1 quản trị dự phòng.</summary>
    private void WarnSingleAdmin()
    {
        try
        {
            if (App.Ws.Session?.User.Role != Role.Admin || !App.Ws.Online) return;
            var n = App.Ws.Auth!.Users().Count(u => u.Role == Role.Admin && u.Active);
            if (n <= 1) ShowStatus(App.Ws.Status("Bảo mật: kho chỉ có 1 tài khoản quản trị. App không có chức năng khôi phục mật khẩu quản trị — hãy tạo thêm 1 tài khoản quản trị DỰ PHÒNG (Tài khoản & phiên), giữ mật khẩu ở nơi an toàn."));
        }
        catch { }
    }

    public async Task TickAsync()
    {
        if (_ticking) return; _ticking = true;
        try
        {
            var st = await Task.Run(() => App.Ws.Tick());
            ShowStatus(st);
            if (st.Online && DateTime.Now - _lastErpCheck > TimeSpan.FromMinutes(2)) await AutoErpAsync();
            if (st.Online && DateTime.Now - _lastUpdateCheck > TimeSpan.FromMinutes(30)) await CheckUpdateAsync(prompt: false);
            if (App.Ws.Session is null) { Ui.Info(st.Message ?? "Phiên đăng nhập đã kết thúc"); ForceLogout(); }
        }
        catch (Exception e) { Ui.Error(e); }
        finally { _ticking = false; }
    }

    /// <summary>Tự nhập file MF0015 mới trong thư mục ERP theo dõi (máy QC/Quản trị), báo kết quả và làm mới danh sách đơn.</summary>
    private async Task AutoErpAsync()
    {
        _lastErpCheck = DateTime.Now;
        ErpAutoResult? r = null; string? err = null;
        await Task.Run(() => { try { r = App.Ws.AutoImportErp(); } catch (Exception e) { err = e.Message; } });
        if (err is not null) { ShowStatus(App.Ws.Status("Tự nhập ERP lỗi: " + err)); return; }
        if (r is null) return;
        var msg = r.Files.Count > 0
            ? $"Đã tự nhập ERP: {string.Join(", ", r.Files)} → thêm {r.Added} dòng, cập nhật {r.Updated}" + (r.Conflicts > 0 ? $", {r.Conflicts} dòng cần xem (xung đột)" : "") + "."
            : "";
        if (r.Errors.Count > 0) msg += (msg.Length > 0 ? " " : "") + "File ERP không đọc được: " + string.Join("; ", r.Errors);
        ShowStatus(App.Ws.Status(msg));
        var erp = r.Files.Count > 0 ? $"ERP tự nhập lúc {DateTime.Now:HH:mm}: +{r.Added} dòng mới, {r.Updated} cập nhật" : $"ERP tự kiểm tra lúc {DateTime.Now:HH:mm} · không có file mới";
        Views.OrdersView.ErpStatus = erp;
        if (Host.Content is Views.OrdersView ov) { ov.SetErpStatus(erp); if (r.Files.Count > 0) await ov.Reload(); }
    }

    private DateTime _lastUpdateCheck = DateTime.MinValue;
    private async Task CheckUpdateAsync(bool prompt)
    {
        _lastUpdateCheck = DateTime.Now;
        var r = await Task.Run(() => App.Ws.NewerRelease());
        if (r is null) return;
        if (prompt && !Ui.Silent) Updater.Run(this, r, ask: true);
        else ShowStatus(App.Ws.Status($"Có bản cập nhật {r.Version} — vào Cài đặt → Cập nhật máy này, hoặc đăng xuất rồi mở lại ứng dụng để được hỏi cập nhật."));
    }

    public void ShowStatus(ConnectionStatus st)
    {
        Badge.Background = st.Online ? (Brush)FindResource("OkBrush") : (Brush)FindResource("BadBrush");
        StorePathText.Text = App.Ws.StorePath;
        BadgeText.Text = st.Online ? "ONLINE" : "OFFLINE";
        var last = st.LastSyncOk is { } t ? $" · Đồng bộ gần nhất {t:HH:mm:ss dd/MM}" : "";
        ConnText.Text = (st.Online ? "Đã kết nối kho chung" : st.Reason) + (st.Pending > 0 ? $" · {st.Pending} nháp chờ đồng bộ" : "") + last;
        ConnText.ToolTip = $"Kho chung: {App.Ws.StorePath}\nDữ liệu trên máy này: {App.LocalDir}\nNháp chờ: {st.Pending}";
        SyncBtn.Visibility = !st.Online || st.Pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConflictBtn.Content = $"Xung đột ({st.Conflicts})";
        ConflictBtn.Visibility = st.Conflicts > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrEmpty(st.Message)) { MessageText.Text = st.Message; MessageBar.Visibility = Visibility.Visible; }
    }

    private void HideMsg_Click(object sender, RoutedEventArgs e) => MessageBar.Visibility = Visibility.Collapsed;

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var r = await Ui.RunAsync(this, "", () => App.Ws.Sync());
        if (r is null) return;
        ShowStatus(App.Ws.Status(r.Offline ? "Không đồng bộ được: " + (r.Error ?? "kho chung không truy cập được") + $". Còn {r.Pending} nháp chờ."
            : $"Đồng bộ xong: đã đẩy {r.Pushed} nháp, {r.Conflicts} xung đột, còn {r.Pending} mục."));
    }

    private void Conflicts_Click(object sender, RoutedEventArgs e) { new ConflictsWindow { Owner = this }.ShowDialog(); ShowStatus(App.Ws.Status()); }

    private void Nav_Click(object sender, RoutedEventArgs e) => Navigate((ToggleButton)sender);

    private void Navigate(ToggleButton b)
    {
        foreach (var t in new[] { NavDash, NavOrders, NavCq, NavTemplates, NavAudit, NavUsers, NavSettings }) t.IsChecked = t == b;
        Host.Content = b.Name switch
        {
            nameof(NavDash) => new DashboardView(),
            nameof(NavOrders) => new OrdersView(),
            nameof(NavCq) => new CqHistoryView(),
            nameof(NavTemplates) => new TemplatesView(),
            nameof(NavAudit) => new AuditView(),
            nameof(NavUsers) => new AccountsView(),
            _ => (UserControl)new SettingsView(),
        };
    }

    /// <summary>Kiểm tra máy ngay sau khi đăng nhập (chạy nền): có mục LỖI → báo trên thanh thông báo, không đợi đến lúc duyệt CQ mới biết.</summary>
    private async Task StartupCheckAsync()
    {
        try
        {
            var items = await MachineCheckWindow.RunAsync();
            var fails = items.Where(i => i.Level == CheckLevel.Fail).ToList();
            CheckBtn.Content = fails.Count > 0 ? $"⚠ Kiểm tra máy này ({fails.Count} lỗi)" : "Kiểm tra máy này";
            if (fails.Count > 0)
                ShowStatus(App.Ws.Status("⚠ Máy này chưa sẵn sàng tạo CQ: " + string.Join("; ", fails.Select(f => f.Name)) + " — bấm \"Kiểm tra máy này\" (góc trên phải) để xem cách sửa."));
        }
        catch { /* kiểm tra lúc mở app không được chặn công việc */ }
    }

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        var w = new MachineCheckWindow { Owner = this };
        w.ShowDialog();
        if (w.Items.Count > 0) { var n = w.Items.Count(i => i.Level == CheckLevel.Fail); CheckBtn.Content = n > 0 ? $"⚠ Kiểm tra máy này ({n} lỗi)" : "Kiểm tra máy này"; }
    }

    private void ChangePass_Click(object sender, RoutedEventArgs e) => new ChangePasswordWindow(false) { Owner = this }.ShowDialog();

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Ask("Đăng xuất?")) return;
        ForceLogout();
    }

    private void ForceLogout()
    {
        _timer.Stop(); App.Ws.Logout();
        App.StartLogin();
        _skipClosePrompt = true; Close();
    }
    private bool _skipClosePrompt;
    public void SkipPrompt() { _timer.Stop(); _skipClosePrompt = true; }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (!_skipClosePrompt) base.OnClosing(e); }
}
