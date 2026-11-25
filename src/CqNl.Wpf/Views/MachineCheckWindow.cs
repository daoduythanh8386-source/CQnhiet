using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CqNl.Application;
using CqNl.Infrastructure.Pdf;

namespace CqNl.Wpf.Views;

/// <summary>"Kiểm tra máy này": máy có đủ điều kiện tạo CQ cùng kho chung với các máy khác không (kho, Excel, thư mục lưu CQ, ERP,
/// máy in, mẫu CQ, chữ ký / con dấu, phiên bản). Chỉ đọc — không sửa gì; mỗi mục LỖI / LƯU Ý có hướng dẫn cách sửa.</summary>
public sealed class MachineCheckWindow : Window
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _sum = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<CheckItem> _items = Array.Empty<CheckItem>();
    public IReadOnlyList<CheckItem> Items => _items;

    /// <summary>Chạy toàn bộ mục kiểm tra trên luồng nền (Excel mở thử rồi đóng).</summary>
    public static Task<IReadOnlyList<CheckItem>> RunAsync()
    {
        var cfg = LocalConfig.Load(App.LocalDir);
        var printers = CqPrinter.Printers();
        return Task.Run(() =>
        {
            var excel = ExcelPdfExporter.Probe();
            var r = App.Ws.CheckMachine(cfg, excel, printers);
            try { cfg.Save(App.LocalDir); } catch { }                      // nhớ thư mục lưu CQ chung cho lúc OFFLINE
            return r;
        });
    }

    public MachineCheckWindow()
    {
        Title = "Kiểm tra máy này — CQ Nhiệt Luyện"; Width = 900; Height = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(18) };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = $"Kiểm tra máy {Environment.MachineName}", FontSize = 20, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 8),
            Text = "Máy nào đạt hết các mục LỖI (✖) là tạo được CQ dùng chung kho với các máy khác. Mục LƯU Ý (⚠) vẫn làm được nhưng nên sửa. Kiểm tra chỉ đọc, không thay đổi dữ liệu." });
        head.Children.Add(_sum);
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        Button B(string t, RoutedEventHandler h, bool primary = false)
        {
            var b = new Button { Content = t, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
            if (primary && TryFindResource("Primary") is Style st) b.Style = st;
            b.Click += h; return b;
        }
        btns.Children.Add(B("Kiểm tra lại", async (_, _) => await Recheck(), primary: true));
        btns.Children.Add(B("Sao chép kết quả", (_, _) => { try { Clipboard.SetText(Text()); _sum.Text += "  (đã sao chép)"; } catch { } }));
        btns.Children.Add(B("Đóng", (_, _) => Close()));
        DockPanel.SetDock(btns, Dock.Bottom); root.Children.Add(btns);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += async (_, _) => await Recheck();
    }

    public async Task Recheck()
    {
        _sum.Text = "Đang kiểm tra… (mở thử Excel, ghi thử kho chung và thư mục lưu CQ)"; _sum.Foreground = Brushes.DimGray;
        _list.Children.Clear();
        try { _items = await RunAsync(); }
        catch (Exception e) { _sum.Text = "Không kiểm tra được: " + e.Message; _sum.Foreground = Brushes.Firebrick; return; }
        Render(_items);
    }

    private void Render(IReadOnlyList<CheckItem> items)
    {
        var fail = items.Count(i => i.Level == CheckLevel.Fail); var warn = items.Count(i => i.Level == CheckLevel.Warn);
        _sum.Text = fail > 0 ? $"✖ Máy này CHƯA sẵn sàng tạo CQ: {fail} lỗi, {warn} lưu ý." : warn > 0 ? $"✔ Máy này tạo được CQ — còn {warn} lưu ý nên sửa." : "✔ Máy này đạt tất cả các mục.";
        _sum.Foreground = fail > 0 ? Brushes.Firebrick : warn > 0 ? new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)) : new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
        foreach (var it in items.OrderBy(i => i.Level == CheckLevel.Fail ? 0 : i.Level == CheckLevel.Warn ? 1 : 2))
        {
            var (icon, fg, bg) = it.Level switch
            {
                CheckLevel.Fail => ("✖", Color.FromRgb(0xB9, 0x1C, 0x1C), Color.FromRgb(0xFE, 0xF2, 0xF2)),
                CheckLevel.Warn => ("⚠", Color.FromRgb(0xB4, 0x53, 0x09), Color.FromRgb(0xFF, 0xF8, 0xE8)),
                _ => ("✔", Color.FromRgb(0x15, 0x80, 0x3D), Color.FromRgb(0xF3, 0xFB, 0xF5)),
            };
            var row = new DockPanel();
            var ic = new TextBlock { Text = icon, Foreground = new SolidColorBrush(fg), FontSize = 16, FontWeight = FontWeights.Bold, Width = 28 };
            DockPanel.SetDock(ic, Dock.Left); row.Children.Add(ic);
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = it.Name, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = it.Detail, TextWrapping = TextWrapping.Wrap });
            if (it.Fix is not null && it.Level != CheckLevel.Ok)
                body.Children.Add(new TextBlock { Text = "Cách sửa: " + it.Fix, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(fg), Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(body);
            _list.Children.Add(new Border { Child = row, Background = new SolidColorBrush(bg), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6) });
        }
    }

    private string Text() => $"Kiểm tra máy {Environment.MachineName} — {DateTime.Now:dd/MM/yyyy HH:mm}\n"
        + string.Join("\n", _items.Select(i => $"[{(i.Level == CheckLevel.Fail ? "LỖI" : i.Level == CheckLevel.Warn ? "LƯU Ý" : "ĐẠT")}] {i.Name}: {i.Detail}" + (i.Fix is not null && i.Level != CheckLevel.Ok ? " → " + i.Fix : "")));
}
