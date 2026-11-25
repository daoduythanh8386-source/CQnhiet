using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CqNl.Application;
using Microsoft.Win32;

namespace CqNl.Wpf.Views;

/// <summary>Tổng quan: số đơn, đạt/không đạt, đã in/chưa in, đã làm/chưa làm CQ + thống kê theo tháng/khách; xuất Excel.</summary>
public partial class DashboardView : UserControl
{
    private List<Workspace.OrderRow> _rows = new();
    private Dictionary<string, int> _notes = new();
    private DashResult? _res;
    private string _listName = "Tất cả đơn";
    private Func<DashLine, bool> _listPred = _ => true;
    private bool _loading;

    public DashboardView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { _loading = true; PeriodBox.SelectedIndex = 4; _loading = false; await Reload(); };
    }

    private async Task Reload(bool full = false)
    {
        var data = await Ui.RunAsync(this, "", () => (rows: App.Ws.ListOrders(full).ToList(), notes: App.Ws.NoteCounts()));
        if (data.rows is null) return;
        _rows = data.rows; _notes = data.notes;
        Recompute();
    }

    private void Recompute()
    {
        if (_rows is null) return;
        _res = DashboardService.Compute(_rows, _notes, new DashFilter(FromBox.SelectedDate, ToBox.SelectedDate, CustomerBox.Text));
        var r = _res; var t = r.Totals;
        InfoText.Text = (App.Ws.Online ? "" : "OFFLINE — số liệu theo bản sao trên máy này · ") + $"Cập nhật {r.At:HH:mm:ss}";
        string Pct(int n, int d) => d == 0 ? "" : $"{100.0 * n / d:0.#}%";
        Tiles.Children.Clear();
        Tile("Tổng đơn (dòng)", t.Total, $"{r.DistinctDngc} phiếu ĐNGC", "Accent", _ => true);
        Tile("Đã làm CQ", t.WithCq, Pct(t.WithCq, t.Total), "Accent", l => l.HasCq);
        Tile("Chưa làm CQ", t.WithoutCq, Pct(t.WithoutCq, t.Total), "WarnBrush", l => !l.HasCq);
        Tile("Đạt", t.Pass, "Tỉ lệ đạt " + t.PassRate, "OkBrush", l => l.Result == DashboardService.Pass);
        Tile("Không đạt", t.Fail, Pct(t.Fail, t.Pass + t.Fail), "BadBrush", l => l.Result == DashboardService.Fail);
        Tile("Chưa có kết quả", t.NoResult, "chưa xác nhận đo", "Muted", l => l.Result == DashboardService.NoResult);
        Tile("CQ chờ duyệt", r.DraftCq, "CQ nháp", "WarnBrush", l => l.CqStatus == DashboardService.DraftCq);
        Tile("Đã duyệt", t.Approved, "", "Accent", l => l.CqStatus == DashboardService.ApprovedCq);
        Tile("Đã in", t.Printed, Pct(t.Printed, t.Approved) + " số đã duyệt", "OkBrush", l => l.PrintStatus == DashboardService.Printed);
        Tile("Đã duyệt, chưa in", t.NotPrinted, "", "WarnBrush", l => l.PrintStatus == DashboardService.NotPrinted);
        Tile("Có lưu ý", r.WithNotes, "QC / Kinh doanh", "Accent", l => l.Notes > 0);
        Tile("Cảnh báo ERP", r.WithFlags, "ERP đổi sau CQ…", "WarnBrush", l => l.Flags.Length > 0);
        MonthGrid.ItemsSource = r.ByMonth; CustomerGrid.ItemsSource = r.ByCustomer.Take(30).ToList();
        DrawBars(r.ByMonth.Take(12).Reverse().ToList());
        ShowList(_listName, _listPred);
    }

    private void Tile(string label, int value, string sub, string brushKey, Func<DashLine, bool> pred)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("Muted"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = value.ToString("N0"), FontSize = 28, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource(brushKey) });
        sp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Button { Content = sp, Style = (Style)Resources["Tile"], ToolTip = "Xem danh sách: " + label, BorderBrush = (Brush)FindResource(brushKey) };
        b.Click += (_, _) => ShowList(label, pred);
        Tiles.Children.Add(b);
    }

    private void DrawBars(List<DashGroup> months)
    {
        MonthBars.Items.Clear();
        var max = Math.Max(1, months.Select(m => m.Total).DefaultIfEmpty(1).Max());
        const double W = 360;
        foreach (var m in months)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), ToolTip = $"{m.Name}: {m.Total} đơn · đạt {m.Pass} · không đạt {m.Fail} · chưa có KQ {m.NoResult}" };
            var lbl = new TextBlock { Text = m.Name, Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(lbl, Dock.Left); row.Children.Add(lbl);
            var bars = new StackPanel { Orientation = Orientation.Horizontal };
            void Seg(int n, Brush br) { if (n > 0) bars.Children.Add(new System.Windows.Shapes.Rectangle { Width = Math.Max(2, W * n / max), Height = 16, Fill = br }); }
            Seg(m.Pass, (Brush)FindResource("OkBrush")); Seg(m.Fail, (Brush)FindResource("BadBrush")); Seg(m.NoResult, new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDE)));
            bars.Children.Add(new TextBlock { Text = " " + m.Total, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 });
            row.Children.Add(bars);
            MonthBars.Items.Add(row);
        }
        if (months.Count == 0) MonthBars.Items.Add(new TextBlock { Text = "Không có đơn trong khoảng thời gian này", Foreground = (Brush)FindResource("Muted") });
    }

    private void ShowList(string name, Func<DashLine, bool> pred)
    {
        _listName = name; _listPred = pred;
        if (_res is null) return;
        var l = _res.Lines.Where(pred).ToList();
        LineGrid.ItemsSource = l;
        ListTitle.Text = $"Danh sách: {name} — {l.Count} dòng (nhấp đúp để mở đơn)";
    }

    private void Period_Changed(object sender, SelectionChangedEventArgs e)
    {
        var today = DateTime.Today; var first = new DateTime(today.Year, today.Month, 1);
        (DateTime? f, DateTime? t) = PeriodBox.SelectedIndex switch
        {
            0 => (first, (DateTime?)null), 1 => (first.AddMonths(-1), first.AddDays(-1)), 2 => (first.AddMonths(-2), null),
            3 => (new DateTime(today.Year, 1, 1), null), 4 => ((DateTime?)null, (DateTime?)null), _ => (FromBox.SelectedDate, ToBox.SelectedDate),
        };
        _loading = true; FromBox.SelectedDate = f; ToBox.SelectedDate = t; _loading = false;
        if (IsLoaded && _res is not null) Recompute();
    }

    private void Date_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        _loading = true; PeriodBox.SelectedIndex = 5; _loading = false;
        Recompute();
    }

    private void Customer_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded && _res is not null) Recompute(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload(full: true);

    private void CustomerGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CustomerGrid.SelectedItem is DashGroup g) CustomerBox.Text = g.Name;
    }

    private async void Line_DoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelected();
    private async void Line_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await OpenSelected(); } }

    private async Task OpenSelected()
    {
        if (LineGrid.SelectedItem is not DashLine l) return;
        new OrderWindow(l.Key) { Owner = Window.GetWindow(this) }.ShowDialog();
        await Reload();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_res is null) return;
        var d = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = $"BaoCao_CQ_{DateTime.Now:yyyyMMdd_HHmm}.xlsx" };
        if (d.ShowDialog() != true) return;
        if (Ui.Run(() => File.WriteAllBytes(d.FileName, DashboardService.ExportXlsx(_res, App.Ws.Session!.User.FullName))))
        {
            if (App.Ws.Online) try { App.Ws.Audit!.Write(App.Ws.Session!.User.Username, "report.export", Path.GetFileName(d.FileName), new { lines = _res.Lines.Count }); } catch { }
            if (Ui.Ask($"Đã xuất báo cáo ({_res.Lines.Count} dòng đơn, gồm cả đơn chưa làm CQ). Mở file?")) Ui.OpenFile(d.FileName);
        }
    }
}
