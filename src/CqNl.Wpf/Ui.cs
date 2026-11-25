using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CqNl.Application;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Pdf;
using CqNl.Infrastructure.Storage;

namespace CqNl.Wpf;

/// <summary>Xử lý lỗi/thông báo thống nhất bằng tiếng Việt. Lỗi nghiệp vụ hiện nguyên văn; lỗi lạ hiện tóm tắt + ghi file nhật ký cục bộ.</summary>
public static class Ui
{
    public static string Describe(Exception e) => e switch
    {
        BusinessException or PermissionException or CqBuildException or PdfExportException => e.Message,
        ConflictException => "Dữ liệu đã được máy khác thay đổi: " + e.Message + "\nHãy tải lại đơn rồi làm lại thao tác (không ghi đè dữ liệu người khác).",
        IntegrityException => "Phát hiện file dữ liệu bị thay đổi ngoài ứng dụng: " + e.Message,
        AggregateException a when a.InnerException is not null => Describe(a.InnerException),
        _ => "Lỗi không mong muốn: " + e.Message,
    };

    /// <summary>Chế độ kiểm thử tự động: không mở hộp thoại, ghi lại thông báo để test kiểm tra.</summary>
    public static bool Silent { get; set; }
    public static List<string> Messages { get; } = new();

    public static void Error(Exception e)
    {
        if (Silent) { lock (Messages) Messages.Add("ERR " + e); return; }
        try
        {
            var d = System.IO.Path.Combine(App.LocalDir, "logs"); System.IO.Directory.CreateDirectory(d);
            System.IO.File.AppendAllText(System.IO.Path.Combine(d, $"loi-{DateTime.Now:yyyyMMdd}.log"), $"{DateTime.Now:O} {e}\n\n");
        }
        catch { }
        MessageBox.Show(Describe(e), AppInfo.Name, MessageBoxButton.OK, e is BusinessException or PermissionException ? MessageBoxImage.Warning : MessageBoxImage.Error);
    }

    public static void Info(string msg) { if (Silent) { lock (Messages) Messages.Add("INFO " + msg); return; } MessageBox.Show(msg, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information); }
    public static bool Ask(string msg) { if (Silent) { lock (Messages) Messages.Add("ASK " + msg); return true; } return MessageBox.Show(msg, AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes; }

    public static bool Run(Action a)
    {
        try { Mouse.OverrideCursor = Cursors.Wait; a(); return true; }
        catch (Exception e) { Mouse.OverrideCursor = null; Error(e); return false; }
        finally { Mouse.OverrideCursor = null; }
    }

    /// <summary>Thao tác lâu (OCR, Excel xuất PDF, import): chạy nền, khóa control gọi, hiện trạng thái đang xử lý.</summary>
    public static async Task<T?> RunAsync<T>(FrameworkElement owner, string busyText, Func<T> f, TextBlock? status = null)
    {
        var old = status?.Text;
        try
        {
            owner.IsEnabled = false; Mouse.OverrideCursor = Cursors.AppStarting;
            if (status is not null) status.Text = busyText;
            return await Task.Run(f);
        }
        catch (Exception e) { Mouse.OverrideCursor = null; Error(e); return default; }
        finally { owner.IsEnabled = true; Mouse.OverrideCursor = null; if (status is not null) status.Text = old ?? ""; }
    }

    /// <summary>Hộp nhập văn bản (vd lý do xóa) — bắt buộc không rỗng nếu required.</summary>
    public static string? Prompt(Window? owner, string title, string label, string initial = "", bool required = true)
    {
        var w = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner, ResizeMode = ResizeMode.NoResize };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var tb = new TextBox { Text = initial, AcceptsReturn = false };
        sp.Children.Add(tb);
        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Đồng ý", IsDefault = true, Style = (Style)System.Windows.Application.Current.FindResource("Primary") };
        var cancel = new Button { Content = "Hủy", IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (required && string.IsNullOrWhiteSpace(tb.Text)) { MessageBox.Show(w, "Không được để trống", title); return; }
            w.DialogResult = true;
        };
        bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);
        w.Content = sp; w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true ? tb.Text.Trim() : null;
    }

    public static void OpenFile(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Error(new BusinessException("Không mở được file: " + e.Message)); }
    }

    /// <summary>DatePicker của WPF hiện chữ gợi ý tiếng Anh "Select a date" và đặt lại mỗi khi đổi ngày → thay bằng tiếng Việt cho mọi DatePicker.</summary>
    public static void InstallVietnameseDatePicker()
    {
        static void Fix(DatePicker dp)
        {
            dp.Dispatcher.InvokeAsync(() =>
            {
                if (dp.Template?.FindName("PART_TextBox", dp) is System.Windows.Controls.Primitives.DatePickerTextBox tb)
                {
                    var wm = new TextBlock { Text = "Chọn ngày", Foreground = System.Windows.Media.Brushes.Gray };
                    typeof(System.Windows.Controls.Primitives.DatePickerTextBox).GetProperty("Watermark", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.SetValue(tb, wm);
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        EventManager.RegisterClassHandler(typeof(DatePicker), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is not DatePicker dp) return;
            dp.Language = System.Windows.Markup.XmlLanguage.GetLanguage("vi-VN");
            Fix(dp);
            dp.SelectedDateChanged += (_, _) => Fix(dp);
            dp.LostFocus += (_, _) => Fix(dp);
        }));
    }

    public static string RoleText(Role r) => r switch { Role.Admin => "Quản trị (admin)", Role.Qc => "QC", Role.Sales => "Kinh doanh", _ => "Chỉ xem (viewer)" };

    /// <summary>Thu cửa sổ (đang ở chế độ thường) vào trong vùng làm việc của màn hình và đặt lại vị trí cho khỏi tràn.</summary>
    public static void FitToScreen(Window w)
    {
        if (w.WindowState != WindowState.Normal) return;
        var wa = SystemParameters.WorkArea;
        if (w.ActualWidth > wa.Width) { w.Width = wa.Width; w.Left = wa.Left; }
        if (w.ActualHeight > wa.Height) { w.Height = wa.Height; w.Top = wa.Top; }
        if (w.Left + w.ActualWidth > wa.Right) w.Left = Math.Max(wa.Left, wa.Right - w.ActualWidth);
        if (w.Top + w.ActualHeight > wa.Bottom) w.Top = Math.Max(wa.Top, wa.Bottom - w.ActualHeight);
        if (w.Left < wa.Left) w.Left = wa.Left;
        if (w.Top < wa.Top) w.Top = wa.Top;
    }
}
