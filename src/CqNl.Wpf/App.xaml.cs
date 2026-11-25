using System.IO;
using System.Windows;
using System.Windows.Threading;
using CqNl.Application;

namespace CqNl.Wpf;

public partial class App : System.Windows.Application
{
    public static Workspace Ws { get; set; } = null!;
    public static string LocalDir { get; set; } = AppInfo.LocalDir;
    /// <summary>Lần mở này app đã tự chuyển kho từ đường dẫn mạng cũ sang bản đồng bộ trên máy (báo trên màn Đăng nhập).</summary>
    public static string? SwitchedFrom { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { Ui.Error(a.Exception); a.Handled = true; };
        // mọi cửa sổ: không cho to hơn vùng làm việc của màn hình (màn 1920×1080 phóng 125% chỉ còn ~1536×780) → không bị khuất đáy / mép
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Ui.FitToScreen((Window)s)));
        Ui.InstallVietnameseDatePicker();
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("vi-VN")));
        // --local <thư mục>: dữ liệu cục bộ riêng (kiểm thử). --store <đường dẫn>: chỉ dùng cho kiểm thử, ghi rõ trên thanh trạng thái.
        string? store = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--store") store = e.Args[i + 1];
            if (e.Args[i] == "--local") LocalDir = e.Args[i + 1];
        }
        var cfg = LocalConfig.Load(LocalDir);
        // đường dẫn kho nhập ở bộ cài (trang "Kho dữ liệu chung") → áp dụng khi mới cài / khi người cài đổi đường dẫn
        if (store is null && cfg.ApplyInstallerStore(LocalConfig.ReadInstallerStore(AppContext.BaseDirectory))) { try { cfg.Save(LocalDir); } catch { } }
        // máy cài trước 0.9.2 trỏ kho về \\ADMIN-PC: đã có bản đồng bộ Synology Drive của cùng kho trên máy này → dùng bản đó (ADMIN-PC tắt vẫn chạy)
        if (store is null && AppInfo.PreferLocalSyncCopy(AppInfo.Expand(cfg.StorePath)) is { } local)
        {
            SwitchedFrom = cfg.StorePath; cfg.StorePath = local; try { cfg.Save(LocalDir); } catch { }
        }
        var assets = Path.Combine(AppContext.BaseDirectory, "assets");
        Ws = new Workspace(LocalDir, store ?? cfg.StorePath, AppPaths.FromInstall(assets));
        StartLogin();
    }

    public static void StartLogin()
    {
        var w = new LoginWindow();
        Current.MainWindow = w;
        w.Show();
    }

    public static void Rebuild(string storePath)
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "assets");
        Ws = new Workspace(LocalDir, storePath, AppPaths.FromInstall(assets));
    }
}
