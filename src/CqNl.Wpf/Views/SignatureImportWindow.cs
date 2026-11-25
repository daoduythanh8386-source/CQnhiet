using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CqNl.Application;
using CqNl.Infrastructure.Imaging;
using Microsoft.Win32;

namespace CqNl.Wpf.Views;

/// <summary>Đưa CHỮ KÝ hoặc CON DẤU từ ngoài vào app (dùng chung mọi máy): chọn file ảnh, kéo-thả, hoặc dán (Ctrl+V — ảnh chụp,
/// ảnh Zalo, file ảnh đã copy). Xem trước bản đã xử lý (chữ ký: nét đậm trên nền giấy; con dấu: bỏ nền trắng → trong suốt) rồi lưu.
/// Chỉ tài khoản quản trị.</summary>
public sealed class SignatureImportWindow : Window
{
    private static Workspace Ws => App.Ws;
    private readonly RadioButton _sigRadio = new() { Content = "Chữ ký người ký", GroupName = "kind", IsChecked = true, Margin = new Thickness(0, 0, 18, 0) };
    private readonly RadioButton _stampRadio = new() { Content = "Con dấu công ty", GroupName = "kind" };
    private readonly ComboBox _name = new() { IsEditable = true, Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _defA = new() { Content = "Đặt làm Người xác nhận (Approved) mặc định", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _defC = new() { Content = "Đặt làm Người kiểm tra (Checker) mặc định", Margin = new Thickness(0, 2, 0, 0) };
    private readonly Image _orig = new() { Stretch = Stretch.Uniform };
    private readonly Image _proc = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _sigPanel = new();
    private byte[]? _bytes; private string _ext = "png";

    /// <summary>Id chữ ký vừa thêm (null nếu không thêm chữ ký).</summary>
    public string? AddedSignerId { get; private set; }
    public bool StampChanged { get; private set; }
    public bool AsApprover => _defA.IsChecked == true;
    public bool AsChecker => _defC.IsChecked == true;

    public SignatureImportWindow(bool stamp = false)
    {
        Title = "Thêm chữ ký / con dấu — CQ Nhiệt Luyện"; Width = 900; Height = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White; AllowDrop = true;
        var root = new DockPanel { Margin = new Thickness(16) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Đưa chữ ký / con dấu từ ngoài vào app", FontSize = 18, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 10),
            Text = "Chọn file ảnh (PNG, JPG, BMP), kéo-thả ảnh vào cửa sổ, hoặc copy ảnh (chụp màn hình, ảnh Zalo, file ảnh) rồi bấm Ctrl+V. "
                 + "Chữ ký: app làm nét đậm, nền giấy; bản chèn lên CQ là nền trong suốt. Con dấu: app tự bỏ nền trắng thành trong suốt. Dùng chung cho mọi máy; trùng tên = thay chữ ký cũ (vẫn lưu lịch sử)." });
        var kind = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        kind.Children.Add(_sigRadio); kind.Children.Add(_stampRadio);
        head.Children.Add(kind);
        _sigPanel.Children.Add(new TextBlock { Text = "Họ tên người ký", Foreground = Brushes.DimGray });
        _sigPanel.Children.Add(_name); _sigPanel.Children.Add(_defA); _sigPanel.Children.Add(_defC);
        head.Children.Add(_sigPanel);
        var tools = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        Button B(string t, RoutedEventHandler h, bool primary = false)
        {
            var b = new Button { Content = t, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
            if (primary && TryFindResource("Primary") is Style st) b.Style = st;
            b.Click += h; return b;
        }
        tools.Children.Add(B("Chọn file ảnh…", (_, _) => PickFile()));
        tools.Children.Add(B("Dán ảnh (Ctrl+V)", (_, _) => Paste()));
        head.Children.Add(tools);
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(B("Lưu vào app", (_, _) => { if (Save()) DialogResult = true; }, primary: true));
        btns.Children.Add(B("Đóng", (_, _) => Close()));
        DockPanel.SetDock(btns, Dock.Right); bottom.Children.Add(btns); bottom.Children.Add(_info);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement Box(string title, Image img, Brush bg)
        {
            var dp = new DockPanel();
            var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) }; DockPanel.SetDock(t, Dock.Top); dp.Children.Add(t);
            dp.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Background = bg, Child = img, Padding = new Thickness(8) });
            return dp;
        }
        var check = new DrawingBrush(new GeometryDrawing(Brushes.Gainsboro, null, new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } }))
        { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        var a = Box("Ảnh gốc", _orig, new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9)));
        var p = Box("Sau khi xử lý (sẽ in lên CQ — ô caro = trong suốt)", _proc, check);
        Grid.SetColumn(p, 2); grid.Children.Add(a); grid.Children.Add(p);
        root.Children.Add(grid);
        Content = root;
        try
        {
            if (Ws.Online)
            {
                var st = Ws.Settings!.Get().Value;
                _name.ItemsSource = st.Signers.Where(x => x.Active).Select(x => x.Name).OrderBy(x => x).ToList();
            }
        }
        catch { }
        _sigRadio.Checked += (_, _) => { _sigPanel.Visibility = Visibility.Visible; Preview(); };
        _stampRadio.Checked += (_, _) => { _sigPanel.Visibility = Visibility.Collapsed; Preview(); };
        if (stamp) _stampRadio.IsChecked = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) LoadFile(f[0]); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox) { e.Handled = true; Paste(); } };
        _info.Text = "Chưa có ảnh.";
    }

    public bool IsStamp => _stampRadio.IsChecked == true;
    public void SetKind(bool stamp) { if (stamp) _stampRadio.IsChecked = true; else _sigRadio.IsChecked = true; }
    public void SetSignerName(string name) => _name.Text = name;

    private void PickFile()
    {
        var d = new OpenFileDialog { Filter = "Ảnh (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", Title = IsStamp ? "Chọn ảnh con dấu" : "Chọn ảnh chữ ký" };
        if (d.ShowDialog(this) == true) LoadFile(d.FileName);
    }

    private void LoadFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext is not ("png" or "jpg" or "jpeg" or "bmp")) { Ui.Info("Chọn file ảnh PNG, JPG hoặc BMP."); return; }
        try { LoadImage(File.ReadAllBytes(path), ext == "jpeg" ? "jpg" : ext); } catch (Exception e) { Ui.Error(e); }
    }

    private void Paste()
    {
        var c = OrderWindow.ReadClipboard();
        if (c is null) { Ui.Info("Clipboard chưa có ảnh. Copy ảnh chữ ký / con dấu (hoặc chụp màn hình) rồi bấm Ctrl+V."); return; }
        if (c.Files is { Count: > 0 }) LoadFile(c.Files[0]); else if (c.Bytes is not null) LoadImage(c.Bytes, "png");
    }

    /// <summary>Nạp ảnh (bytes) và xem trước bản xử lý.</summary>
    public void LoadImage(byte[] bytes, string ext)
    {
        try { CqImages.Size(bytes); } catch { Ui.Info("File không phải ảnh hợp lệ."); return; }
        _bytes = bytes; _ext = ext;
        _orig.Source = Bmp(bytes);
        Preview();
    }

    private void Preview()
    {
        if (_bytes is null) return;
        try
        {
            var p = IsStamp ? SettingsService.PrepareStamp(_bytes) : CqImages.Signature(_bytes, transparent: true);
            _proc.Source = Bmp(p);
            var (w, h) = CqImages.Size(p);
            _info.Text = (IsStamp ? "Con dấu" : "Chữ ký") + $" sau xử lý: {w}×{h} px. Kiểm tra nét rõ, nền trong suốt rồi bấm \"Lưu vào app\".";
        }
        catch (Exception e) { _proc.Source = null; _info.Text = "Không xử lý được ảnh: " + Ui.Describe(e); }
    }

    private static BitmapImage? Bmp(byte[] b)
    {
        try { var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 700; bi.StreamSource = new MemoryStream(b); bi.EndInit(); bi.Freeze(); return bi; }
        catch { return null; }
    }

    /// <summary>Lưu vào kho chung (chỉ quản trị). Trả true nếu đã lưu.</summary>
    public bool Save()
    {
        if (_bytes is null) { Ui.Info("Chưa có ảnh — chọn file, kéo-thả hoặc Ctrl+V."); return false; }
        if (Ws.Session?.User.Role != Role.Admin) { Ui.Info("Chỉ tài khoản quản trị (admin) được thêm / thay chữ ký và con dấu (dùng chung mọi máy)."); return false; }
        var name = (_name.Text ?? "").Trim();
        if (!IsStamp && name.Length < 2) { Ui.Info("Nhập họ tên người ký."); return false; }
        var (bytes, ext, stamp, defA, defC) = (_bytes, _ext, IsStamp, _defA.IsChecked == true, _defC.IsChecked == true);
        return Ui.Run(() =>
        {
            var s = Ws.Session!;
            if (stamp) { Ws.Settings!.SetStamp(s, bytes); StampChanged = true; return; }
            var rec = Ws.Settings!.AddSigner(s, name, bytes, ext);
            AddedSignerId = rec.Id;
            if (defA || defC)
            {
                var st = Ws.Settings.Get().Value;
                Ws.Settings.SetDefaults(s, defA ? rec.Id : st.DefaultApproverId, defC ? rec.Id : st.DefaultCheckerId);
            }
        });
    }
}
