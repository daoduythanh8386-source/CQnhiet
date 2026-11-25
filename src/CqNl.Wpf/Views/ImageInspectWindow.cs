using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CqNl.Application;
using Microsoft.Win32;

namespace CqNl.Wpf.Views;

/// <summary>
/// "Ảnh &amp; OCR" — xem ảnh ở 3 chế độ (ảnh gốc bất biến / ảnh HD màu / ảnh tối ưu in đen trắng) + so sánh trước/sau, phóng to / thu nhỏ,
/// tải xuống; bảng kết quả đọc số (ảnh gốc vs ảnh xử lý, độ tin cậy từng ký tự), cảnh báo, vùng số đo phóng lớn, LOT / ngày / mã thiết bị,
/// ô sửa + "Xác nhận số này", "Xử lý lại", lịch sử xử lý. Quyền: mọi tài khoản xem / tải; chỉ Quản trị + QC xác nhận / sửa / xử lý lại.
/// </summary>
public sealed class ImageInspectWindow : Window
{
    private static Workspace Ws => App.Ws;
    private InspectionImage _im;
    private readonly string _orderKey;
    private readonly bool _canEdit;
    private readonly Func<string, InspectionImage?> _confirm;     // giá trị → ảnh sau khi xác nhận (null = lỗi)
    private readonly Func<InspectionImage?> _reprocess;           // xử lý lại → ảnh mới (null = lỗi)
    private readonly Dictionary<string, byte[]?> _cache = new();
    private string _mode = "hd";

    private readonly Grid _view = new();
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly Slider _zoomSlider = new() { Minimum = 0.1, Maximum = 4, Value = 1, Width = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 46, Margin = new Thickness(6, 0, 10, 0) };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xED, 0xF2)) };
    private readonly StackPanel _side = new();
    private readonly TextBox _value = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, MinWidth = 120 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly Dictionary<string, ToggleButton> _modeBtns = new();

    public bool Changed { get; private set; }

    public ImageInspectWindow(InspectionImage im, string orderKey, string title, bool canEdit, Func<string, InspectionImage?> confirm, Func<InspectionImage?> reprocess)
    {
        _im = im; _orderKey = orderKey; _canEdit = canEdit; _confirm = confirm; _reprocess = reprocess;
        Title = "Ảnh & OCR — " + title; Width = 1300; Height = 820; WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowState = WindowState.Maximized;
        Background = Brushes.White;
        var root = new Grid { Margin = new Thickness(10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });

        // ---- trái: thanh công cụ + ảnh ----
        var left = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (k, t) in new[] { ("orig", "Ảnh gốc"), ("hd", "Ảnh HD màu"), ("print", "In đen trắng"), ("compare", "So sánh trước / sau") })
        {
            var b = new ToggleButton { Content = t, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), Tag = k };
            if (TryFindResource("Chip") is Style st) b.Style = st;
            b.Click += (_, _) => SetMode(k); _modeBtns[k] = b; bar.Children.Add(b);
        }
        bar.Children.Add(new TextBlock { Text = "  Phóng to:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        bar.Children.Add(_zoomSlider); bar.Children.Add(_zoomText);
        bar.Children.Add(Btn("Vừa khung", (_, _) => Fit()));
        bar.Children.Add(Btn("Tải ảnh xuống…", (_, _) => Download()));
        DockPanel.SetDock(bar, Dock.Top); left.Children.Add(bar);
        var note = new TextBlock { Foreground = Brushes.DimGray, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "Ảnh gốc được lưu nguyên vẹn (không ghi đè). Ảnh HD / in đen trắng chỉ dùng khử nhiễu, cân bằng sáng, làm nét, nắn thẳng, phóng to nội suy — " +
                   "không vẽ lại số, không dùng AI sinh ảnh. Phóng to không phục hồi được chi tiết đã mất trên ảnh gốc." };
        DockPanel.SetDock(note, Dock.Bottom); left.Children.Add(note);
        _view.LayoutTransform = _zoom;
        _scroll.Content = _view; left.Children.Add(_scroll);
        _zoomSlider.ValueChanged += (_, e) => { _zoom.ScaleX = _zoom.ScaleY = e.NewValue; _zoomText.Text = $"{e.NewValue:P0}"; };
        Grid.SetColumn(left, 0); root.Children.Add(left);

        // ---- phải: kết quả OCR ----
        var sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _side };
        Grid.SetColumn(sideScroll, 2); root.Children.Add(sideScroll);
        Content = root;
        Loaded += (_, _) => { BuildSide(); SetMode(_im.Vision?.HdSha is not null ? "hd" : "orig"); };
    }

    private Button Btn(string t, RoutedEventHandler h, bool primary = false)
    {
        var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 0) };
        if (primary && TryFindResource("Primary") is Style st) b.Style = st;
        b.Click += h; return b;
    }

    private byte[]? Bytes(string mode)
    {
        if (_cache.TryGetValue(mode, out var c)) return c;
        byte[]? b = null;
        Ui.Run(() => b = mode switch
        {
            "orig" => Ws.ImageBytes(_im.BlobSha, _im.Ext),
            "hd" => _im.Vision?.HdSha is { } h ? Ws.ImageBytes(h, "jpg") : null,
            "print" => _im.Vision?.PrintSha is { } p ? Ws.ImageBytes(p, "png") : null,
            "roi" => _im.Vision?.RoiSha is { } r ? Ws.ImageBytes(r, "png") : null,
            _ => null,
        });
        return _cache[mode] = b;
    }

    private static BitmapImage? Bmp(byte[]? b)
    {
        if (b is null) return null;
        try { var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(b); bi.EndInit(); bi.Freeze(); return bi; }
        catch { return null; }
    }

    private FrameworkElement MakePanel(string caption, byte[]? b)
    {
        var sp = new StackPanel { Margin = new Thickness(4) };
        sp.Children.Add(new TextBlock { Text = caption, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var bmp = Bmp(b);
        if (bmp is null) sp.Children.Add(new TextBlock { Text = "Chưa có ảnh này trên máy (cần kết nối kho chung) hoặc ảnh cũ chưa xử lý — bấm \"Xử lý lại\".", Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 });
        else sp.Children.Add(new Image { Source = bmp, Stretch = Stretch.None });
        return sp;
    }

    private void SetMode(string m)
    {
        _mode = m;
        foreach (var (k, b) in _modeBtns) b.IsChecked = k == m;
        _view.Children.Clear(); _view.ColumnDefinitions.Clear();
        if (m == "compare")
        {
            _view.ColumnDefinitions.Add(new ColumnDefinition()); _view.ColumnDefinitions.Add(new ColumnDefinition());
            var a = MakePanel("TRƯỚC — ảnh gốc", Bytes("orig")); var b = MakePanel("SAU — ảnh HD màu", Bytes("hd"));
            Grid.SetColumn(b, 1); _view.Children.Add(a); _view.Children.Add(b);
        }
        else _view.Children.Add(MakePanel(m switch { "orig" => "Ảnh gốc (bất biến, mã " + _im.BlobSha[..12] + "…)", "hd" => "Ảnh HD màu", _ => "Ảnh tối ưu in đen trắng" }, Bytes(m)));
        Dispatcher.BeginInvoke(new Action(Fit), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Fit()
    {
        _view.UpdateLayout();
        var w = _view.DesiredSize.Width / Math.Max(0.01, _zoom.ScaleX); var h = _view.DesiredSize.Height / Math.Max(0.01, _zoom.ScaleY);
        if (w < 1 || h < 1 || _scroll.ViewportWidth < 1) return;
        _zoomSlider.Value = Math.Clamp(Math.Min((_scroll.ViewportWidth - 10) / w, (_scroll.ViewportHeight - 10) / h), 0.1, 4);
    }

    private void Download()
    {
        var mode = _mode == "compare" ? "hd" : _mode;
        var b = Bytes(mode);
        if (b is null) { Ui.Info("Chưa có ảnh này trên máy."); return; }
        var ext = mode == "orig" ? _im.Ext : mode == "hd" ? "jpg" : "png";
        var name = mode switch { "orig" => "anh_goc", "hd" => "anh_HD", _ => "anh_in_den_trang" };
        var d = new SaveFileDialog { FileName = $"{_orderKey.Replace('/', '-').Replace('|', '_')}_{name}.{ext}", Filter = $"Ảnh (*.{ext})|*.{ext}" };
        if (d.ShowDialog() != true) return;
        if (Ui.Run(() => File.WriteAllBytes(d.FileName, b))) Ws.LogImageExport(_orderKey, _im.Id, mode, d.FileName);
    }

    private static Brush ConfBrush(double c) => c >= 0.85 ? new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)) : c >= 0.6 ? new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)) : Brushes.Firebrick;

    private void H(string t) => _side.Children.Add(new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 12, 0, 4) });
    private void Line(string t, Brush? fg = null) => _side.Children.Add(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = fg ?? Brushes.Black, Margin = new Thickness(0, 1, 0, 1) });

    private void BuildSide()
    {
        _side.Children.Clear();
        // ô trạng thái / ô nhập số được dùng lại mỗi lần dựng → gỡ khỏi khung cũ trước (WPF: một phần tử chỉ có một cha)
        if (_status.Parent is Border ob) ob.Child = null;
        if (_value.Parent is Panel op) op.Children.Remove(_value);
        var v = _im.Vision;
        _side.Children.Add(new TextBlock { Text = "Kết quả đọc số", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var ok = v is { NeedsReview: false, Error: null };
        _status.Text = _im.ConfirmedValue is not null ? $"✔ Đã xác nhận: {_im.ConfirmedValue} — {_im.ConfirmedBy} {_im.ConfirmedAt:dd/MM/yyyy HH:mm}"
            : v is null ? "Ảnh cũ (trước bản 0.9) — chưa qua module xử lý. Bấm \"Xử lý lại\" để kiểm tra."
            : ok ? "✔ Ảnh gốc và ảnh xử lý đọc KHỚP — vẫn chờ người xác nhận trước khi đưa vào hồ sơ CQ."
            : "⚠ CẦN XÁC NHẬN: đối chiếu số trên ảnh rồi bấm \"Xác nhận số này\".";
        _status.Foreground = _im.ConfirmedValue is not null || ok ? new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)) : new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
        _side.Children.Add(new Border { Child = _status, Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFB)), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 6, 0, 0) });
        if (v is not null && v.Flags.Count > 0)
        {
            H("Cảnh báo");
            foreach (var f in v.Flags) Line("• " + f, f.StartsWith("Chất lượng") ? Brushes.DimGray : Brushes.Firebrick);
        }
        // vùng số đo phóng lớn
        var roi = Bmp(Bytes("roi"));
        if (roi is not null)
        {
            H($"Vùng số đo ({v?.MeasurementKind ?? "?"}) — phóng to nội suy");
            _side.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = new Image { Source = roi, Stretch = Stretch.Uniform, MaxHeight = 170 } });
        }
        // các lần đọc
        if (v is not null)
        {
            H("Các lần đọc (so sánh ảnh gốc / ảnh xử lý)");
            foreach (var p in v.Passes)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
                sp.Children.Add(new TextBlock { Text = $"{p.Source} · {p.Engine}", FontWeight = FontWeights.SemiBold, FontSize = 12 });
                sp.Children.Add(new TextBlock { Text = $"Số: {p.Value ?? "không đọc được"}{(p.Unit is null ? "" : " " + p.Unit)} · tin cậy {p.Confidence:P0}" + (string.IsNullOrWhiteSpace(p.Raw) ? "" : $" · chuỗi thô \"{p.Raw}\""), TextWrapping = TextWrapping.Wrap, Foreground = ConfBrush(p.Confidence) });
                if (p.Chars is { Count: > 0 })
                {
                    var wp = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
                    foreach (var c in p.Chars)
                        wp.Children.Add(new Border { Background = ConfBrush(c.Conf), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 2, 2), Padding = new Thickness(4, 1, 4, 1),
                            ToolTip = $"'{c.C}' tin cậy {c.Conf:P0}", Child = new TextBlock { Text = c.C, Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold } });
                    sp.Children.Add(wp);
                }
                _side.Children.Add(sp);
            }
            if (v.Lot is not null || v.Date is not null || v.DeviceCode is not null || v.Unit is not null)
            {
                H("Thông tin nhận dạng thêm (chỉ tham khảo)");
                if (v.Unit is not null) Line("Đơn vị: " + v.Unit);
                if (v.Lot is not null) Line("Mã LOT: " + v.Lot);
                if (v.Date is not null) Line("Ngày: " + v.Date);
                if (v.DeviceCode is not null) Line("Mã thiết bị: " + v.DeviceCode);
            }
        }
        // xác nhận / sửa
        if (_im.Kind is "Hardness" or "Sample")
        {
            H(_im.Kind == "Hardness" ? "Số độ cứng (HRC) đưa vào hồ sơ" : "Số cân (kg) in trên ảnh CQ");
            _value.Text = _im.ConfirmedValue ?? (v is { NeedsReview: false } ? _im.OcrValue : null) ?? "";
            _value.IsEnabled = _canEdit;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_value);
            var cb = Btn("Xác nhận số này", (_, _) => DoConfirm(), primary: true); cb.Margin = new Thickness(8, 0, 0, 0); cb.IsEnabled = _canEdit;
            row.Children.Add(cb);
            _side.Children.Add(row);
            if (!_canEdit) Line("Tài khoản này chỉ được xem — xác nhận / sửa số do QC hoặc Quản trị làm.", Brushes.DimGray);
        }
        var rp = Btn("Xử lý lại từ ảnh gốc", (_, _) => DoReprocess()); rp.IsEnabled = _canEdit; rp.Margin = new Thickness(0, 10, 0, 0); rp.HorizontalAlignment = HorizontalAlignment.Left;
        _side.Children.Add(rp);
        // chất lượng + các bước
        if (v is not null)
        {
            H("Chất lượng ảnh & các bước xử lý");
            Line($"Độ sáng {v.Brightness:P0} · độ nét {v.Sharpness:0} · nhiễu {v.Noise:0.0} · cháy sáng {v.Saturated:P1} · tương phản {v.Contrast:0}", Brushes.DimGray);
            if (v.Steps.Count > 0) Line("Ảnh HD: " + string.Join(" → ", v.Steps), Brushes.DimGray);
            if (v.ReadingSteps.Count > 0) Line("Bản đọc số: " + string.Join(" → ", v.ReadingSteps), Brushes.DimGray);
            if (v.Regions.Count > 0) Line("Vùng phát hiện: " + string.Join(", ", v.Regions.Select(r => $"{r.Kind} ({r.W}×{r.H})")), Brushes.DimGray);
            if (v.Error is not null) Line("Lỗi xử lý: " + v.Error, Brushes.Firebrick);
        }
        // nguồn gốc + lịch sử
        H("Hồ sơ ảnh");
        Line($"Mã hồ sơ: {_orderKey.Replace("|", " · ")}");
        Line($"Tải lên: {_im.AddedBy ?? "—"} {(_im.AddedAt is { } a ? a.ToString("dd/MM/yyyy HH:mm") : "")}");
        Line($"Ảnh gốc SHA-256: {_im.BlobSha}", Brushes.DimGray);
        if (_im.History.Count > 0)
        {
            H("Lịch sử xử lý");
            foreach (var h in _im.History.OrderByDescending(x => x.At)) Line($"{h.At:dd/MM HH:mm} · {h.By} · {h.Action}" + (h.Detail is null ? "" : " — " + h.Detail), Brushes.DimGray);
        }
    }

    private void DoConfirm()
    {
        var val = _value.Text.Trim();
        if (val.Length == 0) { Ui.Info("Nhập số đúng như trên màn hình máy đo."); return; }
        var v = _im.Vision;
        var read = v?.Passes.Take(2).Select(p => p.Value).Where(x => x is not null).Distinct().ToList() ?? new List<string?>();
        if (read.Count > 0 && !read.Contains(val.Replace(',', '.')) &&
            !Ui.Ask($"Số bạn nhập ({val}) KHÁC số máy đọc ({string.Join(" / ", read)}).\nBạn đã đối chiếu kỹ với ảnh gốc? Bấm Có để xác nhận số bạn nhập (ghi lịch sử).")) return;
        var r = _confirm(val);
        if (r is null) return;
        _im = r; Changed = true; _cache.Clear(); BuildSide(); SetMode(_mode);
    }

    private async void DoReprocess()
    {
        IsEnabled = false; Cursor = System.Windows.Input.Cursors.Wait;
        _status.Text = "Đang xử lý lại từ ảnh gốc…";
        try
        {
            var r = await Task.Run(() => _reprocess());      // xử lý nặng chạy nền — không treo giao diện
            if (r is not null) { _im = r; Changed = true; _cache.Clear(); }
        }
        finally { IsEnabled = true; Cursor = null; BuildSide(); SetMode(_mode); }
    }
}
