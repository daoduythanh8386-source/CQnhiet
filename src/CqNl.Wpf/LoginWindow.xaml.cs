using System.Windows;
using System.Windows.Media;
using CqNl.Application;

namespace CqNl.Wpf;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        FooterText.Text = AppInfo.Footer;
        VersionText.Text = "Phiên bản " + Workspace.VersionText(Workspace.CurrentVersion);
        Loaded += async (_, _) => await Check();
        Closed += (_, _) => { if (!_proceeded) System.Windows.Application.Current.Shutdown(); };
    }
    private bool _proceeded;


    private void Init_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Ask($"Khởi tạo kho dữ liệu MỚI tại:\n{App.Ws.StorePath}\n\nChỉ làm khi đây đúng là kho chung mới, chưa từng dùng. " +
                    "Nếu công ty đã có kho với tài khoản sẵn, hãy bấm Không và kiểm tra lại đường dẫn kho.\n\nTiếp tục?")) return;
        LoginPanel.Visibility = Visibility.Collapsed; FirstAdminPanel.Visibility = Visibility.Visible; NewNameBox.Focus();
    }

    private void CancelInit_Click(object sender, RoutedEventArgs e) { FirstAdminPanel.Visibility = Visibility.Collapsed; LoginPanel.Visibility = Visibility.Visible; }

    private async Task Check()
    {
        StatusText.Text = "Đang kiểm tra kho chung…"; StorePathText.Text = App.Ws.StorePath;
        StatusBox.Background = Brushes.WhiteSmoke;
        var ws = App.Ws;
        bool hasUser = false;
        var online = await Task.Run(() =>
        {
            if (!ws.Probe()) return false;
            var auth = ws.Auth ?? throw new InvalidOperationException("Kho chung vừa mất kết nối");
            hasUser = auth.HasAnyUser();
            return true;
        }).ContinueWith(t => { if (t.IsFaulted) { Dispatcher.Invoke(() => Ui.Error(t.Exception!.GetBaseException())); return false; } return t.Result; });
        StatusBox.Background = new SolidColorBrush(online ? Color.FromRgb(0xE6, 0xF4, 0xEA) : Color.FromRgb(0xFD, 0xEC, 0xEA));
        StatusText.Foreground = online ? (Brush)FindResource("OkBrush") : (Brush)FindResource("BadBrush");
        StatusText.Text = online ? "ONLINE — đã kết nối kho chung" : "OFFLINE — " + App.Ws.OfflineReason +
            "\nChỉ đăng nhập được bằng tài khoản đã từng đăng nhập online trên máy này; không kiểm tra được phiên trùng ở máy khác.";
        StorePathText.Text = "Kho: " + App.Ws.StorePath + (!App.Ws.StorePath.Equals(AppInfo.Expand(AppInfo.DefaultStore).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? "   (khác đường dẫn mặc định)" : "")
            + (AppInfo.IsSyncFolder(App.Ws.StorePath) ? "\nBản đồng bộ Synology Drive trên máy này — máy chủ ADMIN-PC tắt vẫn dùng được." : "")
            + (App.SwitchedFrom is { } from ? $"\nĐã tự chuyển từ kho mạng {from} sang bản đồng bộ trên máy này (cùng dữ liệu)." : "");
        // Không bao giờ tự mở form tạo quản trị khi cài/cài lại app: luôn đăng nhập bằng tài khoản có sẵn trên kho chung.
        // Kho TRỐNG + đang ở máy chứa kho → chỉ hiện nút "Khởi tạo quản trị cho kho mới…" (phải bấm và xác nhận).
        LoginPanel.Visibility = Visibility.Visible; FirstAdminPanel.Visibility = Visibility.Collapsed;
        var empty = online && !hasUser;
        var canInit = ws.IsStoreHost() && (!AppInfo.IsSyncFolder(ws.StorePath) || ws.AllowNewSyncStore);
        InitBtn.Visibility = empty && canInit ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            StatusText.Text = "ONLINE — nhưng kho chung CHƯA CÓ tài khoản nào.";
            StorePathText.Text += AppInfo.IsSyncFolder(ws.StorePath) && !canInit ? "\nKho đồng bộ chưa có tài khoản: chờ Synology Drive đồng bộ xong rồi bấm Thử lại (không tạo quản trị mới)."
                                 : ws.IsStoreHost() ? "\nĐây là máy chứa kho: quản trị có thể bấm \"Khởi tạo quản trị cho kho mới…\" (chỉ một lần)."
                                                   : "\nKiểm tra lại đường dẫn kho, hoặc liên hệ quản trị (admin) để được hỗ trợ.";
        }
        UserBox.Focus();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await Check();

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var u = UserBox.Text; var p = PassBox.Password;
        if (string.IsNullOrWhiteSpace(u) || p.Length == 0) { Ui.Info("Nhập tên đăng nhập và mật khẩu"); return; }
        var s = await Ui.RunAsync(this, "", () => App.Ws.Login(u, p));
        PassBox.Clear();
        if (s is null) return;
        if (s.MustChangePassword)
        {
            var cp = new ChangePasswordWindow(forced: true) { Owner = this };
            if (cp.ShowDialog() != true) { App.Ws.Logout(); return; }
        }
        var main = new MainWindow();
        System.Windows.Application.Current.MainWindow = main;
        main.Show();
        _proceeded = true; Close();
    }

    private async void CreateAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (NewPassBox.Password != NewPass2Box.Password) { Ui.Info("Hai lần nhập mật khẩu không khớp"); return; }
        var (u, n, p, p2) = (NewUserBox.Text.Trim(), NewNameBox.Text.Trim(), NewPassBox.Password, NewPass2Box.Password);
        if (u.Length == 0 || n.Length == 0) { Ui.Info("Nhập tên đăng nhập và họ tên"); return; }
        var ok = await Ui.RunAsync(this, "", () => { App.Ws.InitFirstAdmin(u, n, p, p2); return true; });
        NewPassBox.Clear(); NewPass2Box.Clear();
        if (!ok) return;
        Ui.Info("Đã tạo tài khoản quản trị. Hãy đăng nhập.");
        UserBox.Text = u;
        await Check();
    }

    private async void ChangeStore_Click(object sender, RoutedEventArgs e)
    {
        var p = Ui.Prompt(this, "Đường dẫn kho chung", "Thư mục DU LIEU NHIET do Synology Drive đồng bộ về máy này. Mặc định: " + AppInfo.DefaultStore +
                                 " (= " + AppInfo.Expand(AppInfo.DefaultStore) + ")\nHoặc kho mạng cũ " + AppInfo.LegacyStore + " (ADMIN-PC phải bật).\nChỉ đổi khi quản trị yêu cầu.", App.Ws.StorePath);
        if (p is null || p == App.Ws.StorePath) return;
        if (!Ui.Ask($"Dùng kho:\n{p}\n\nMáy này sẽ đọc/ghi dữ liệu nghiệp vụ tại đây. Tiếp tục?")) return;
        var cfg = LocalConfig.Load(App.LocalDir); cfg.StorePath = p; cfg.Save(App.LocalDir);
        App.Rebuild(p);
        await Check();
    }
}
