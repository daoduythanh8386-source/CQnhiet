using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CqNl.Application;
using Microsoft.Win32;

namespace CqNl.Wpf.Views;

public sealed class OrderItem
{
    public string Key { get; init; } = "";
    public string Dngc { get; init; } = "";
    public string Line { get; init; } = "";
    public DateTime? ReceivedDate { get; init; }
    public string Received => ReceivedDate?.ToString("dd/MM/yyyy") ?? "";
    public string Customer { get; init; } = "";
    public string CustomerFull { get; init; } = "";
    public string Product { get; init; } = "";
    public string Grade { get; init; } = "";
    public string Requirement { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Weight { get; init; } = "";
    public string Hardness { get; init; } = "";
    public string CqText { get; init; } = "";
    public string Result { get; init; } = "";
    public string Template { get; init; } = "";
    public string Group { get; init; } = "";
    public int Notes { get; init; }
    public string Flags { get; init; } = "";
    public bool Draft { get; init; }
    public bool HasCq { get; init; }
    public bool Approved { get; init; }
    public bool Printed { get; init; }
    public bool IsChild { get; init; }
    public string RowKind => IsChild ? "child" : Printed ? "printed" : Approved ? "approved" : HasCq ? "draft" : "";
    /// <summary>Màu dòng (yêu cầu 17/11): đã in CQ → xanh #A9D08E; CQ nháp chưa duyệt → vàng #FFE699; còn lại (chưa in) → trắng. Dòng gộp theo CQ của nhóm.</summary>
    public string RowColor => Printed ? "printed" : HasCq && !Approved ? "draft" : "";
    public string Search => $"{Dngc} {Line} {Customer} {CustomerFull} {Product} {Grade} {CqText}".ToLowerInvariant();
    // ---- cột gọn ----
    public string Status { get; init; } = "";
    public string StatusBg => Status switch { "Đã in" => "#DCFCE7", "Đã duyệt" => "#E7F6EC", "Nháp CQ" => "#E0ECFF", "Đã đo" => "#FFF4D6", "Đang đo" => "#FFF8E8", _ => "#EEF2F7" };
    public string StatusFg => Status switch { "Đã in" => "#14532D", "Đã duyệt" => "#166534", "Nháp CQ" => "#1E40AF", "Đã đo" => "#92400E", "Đang đo" => "#92400E", _ => "#475569" };
    public string DngcLine => Line.Length > 0 ? $"{Dngc} · {Line}" : Dngc;
    public string QtyKg => Weight.Length > 0 ? $"{Qty} · {Weight}" : Qty;
    public string Range { get; init; } = "";
    public string ResultText => Result.Length == 0 ? "—" : Range.Length > 0 ? $"{Result}  {Range}" : Result;
    public string ResultFg => Result == "ĐẠT" ? "#15803D" : Result.Length > 0 ? "#B91C1C" : "#94A3B8";
    public string CqNo { get; init; } = "";
    public string Icons => (Notes > 0 ? "✎ " : "") + (Flags.Length > 0 ? "⚠ " : "") + (Group.Length > 0 ? "⧉" : "");
    public string IconTip => string.Join("\n", new[] { Notes > 0 ? $"Có {Notes} lưu ý" : "", Flags, Group }.Where(x => x.Length > 0));
}

/// <summary>Danh sách đơn: chip trạng thái có số lượng, lọc từng cột kiểu Excel, chọn cột hiển thị (nhớ theo máy),
/// gộp/tách dòng đơn, đồng bộ ERP từ thư mục theo dõi.</summary>
public partial class OrdersView : UserControl
{
    private sealed record Col(string Key, string Header, double Width, bool Default = true, bool Star = false, bool Funnel = false, string? Bg = null, string? Fg = null, string? Tip = null);
    private static readonly Col[] Cols =
    {
        // gọn: 9 cột chính + biểu tượng; các cột chi tiết bật lại ở nút "Cột"
        // Khách hàng co giãn (*) theo bề ngang màn hình → cả lưới vừa khít, không cuộn ngang; tên dài xem ở tooltip
        new("Status", "Trạng thái", 92, Funnel: true, Bg: "StatusBg", Fg: "StatusFg"), new("Received", "Ngày nhận", 86), new("DngcLine", "ĐNGC · dòng", 196),
        new("Customer", "Khách hàng", 140, Funnel: true, Star: true), new("Grade", "Mác", 74, Funnel: true), new("Requirement", "Độ cứng YC", 92, Funnel: true),
        new("QtyKg", "SL · KL (kg)", 92), new("ResultText", "Kết quả đo", 140, Fg: "ResultFg"), new("CqNo", "Số CQ", 64), new("Icons", "", 46, Tip: "IconTip"),
        new("Product", "Tên sản phẩm", 190, Default: false), new("Template", "Template", 130, Default: false, Funnel: true),
        new("Hardness", "Đo độ cứng", 110, Default: false), new("Group", "Gộp", 130, Default: false), new("Flags", "Cảnh báo", 170, Default: false),
    };
    private sealed record ChipDef(string Name, Func<OrderItem, bool> Pred, bool HideWhenZero = false);
    // theo đúng thứ tự làm việc: cần làm → chờ duyệt → chờ in → đã in
    private static readonly ChipDef[] ChipDefs =
    {
        new("Cần làm CQ", i => !i.HasCq && !i.IsChild), new("Nháp – chờ duyệt", i => i.HasCq && !i.Approved && !i.IsChild),
        new("Đã duyệt – chưa in", i => i.Approved && !i.Printed && !i.IsChild), new("Đã in", i => i.Printed && !i.IsChild),
        new("Tất cả", _ => true), new("Nháp offline", i => i.Draft, HideWhenZero: true),
    };
    public static string ErpStatus { get; set; } = "ERP tự nhập 2 phút/lần từ thư mục theo dõi";

    public sealed class FilterValue : INotifyPropertyChanged
    {
        private bool _on;
        public string Text { get; init; } = "";
        public bool On { get => _on; set { _on = value; PropertyChanged?.Invoke(this, new(nameof(On))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private List<OrderItem> _all = new();
    private int _chip;
    private readonly Dictionary<string, HashSet<string>> _colFilters = new();
    private readonly Dictionary<string, DataGridColumn> _columns = new();
    private readonly Dictionary<string, Button> _funnels = new();
    private HashSet<string> _visible;
    private string? _filterCol;
    private List<FilterValue> _filterValues = new();
    private static string UiFile => Path.Combine(App.LocalDir, "ui-orders-v2.json");      // v2: bộ cột gọn (bỏ qua lựa chọn cột cũ)
    private static string DaysFile => Path.Combine(App.LocalDir, "ui-orders-days.txt");
    private int _days = 30;

    public OrdersView()
    {
        InitializeComponent();
        var role = App.Ws.Session!.User.Role;
        var canEdit = role is Role.Admin or Role.Qc;
        ErpBtn.IsEnabled = canEdit; MergeMenu.IsEnabled = canEdit; SplitMenu.IsEnabled = canEdit;
        try { if (File.Exists(DaysFile) && int.TryParse(File.ReadAllText(DaysFile).Trim(), out var d)) _days = d; } catch { }
        DaysPick.SelectedItem = DaysPick.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == _days.ToString()) ?? DaysPick.Items[1];
        ErpStatusText.Text = ErpStatus;
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.F5) { e.Handled = true; await Reload(full: true); } };
        _visible = LoadVisible();
        BuildColumns();
        for (var i = 0; i < ChipDefs.Length; i++)
        {
            var idx = i;
            var tb = new ToggleButton { Style = (Style)FindResource("Chip"), IsChecked = i == 0, Tag = i };
            tb.Click += (_, _) => { _chip = idx; foreach (ToggleButton c in Chips.Children) c.IsChecked = (int)c.Tag == idx; ApplyFilter(); };
            Chips.Children.Add(tb);
        }
        Loaded += async (_, _) => await Reload();
    }

    // ---------------- cột + lọc cột ----------------
    private HashSet<string> LoadVisible()
    {
        try { if (File.Exists(UiFile)) return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(UiFile)) ?? Defaults(); } catch { }
        return Defaults();
        static HashSet<string> Defaults() => Cols.Where(c => c.Default).Select(c => c.Key).ToHashSet();
    }

    private void SaveVisible() { try { Directory.CreateDirectory(App.LocalDir); File.WriteAllText(UiFile, JsonSerializer.Serialize(_visible)); } catch { } }

    private void BuildColumns()
    {
        foreach (var c in Cols)
        {
            object header = c.Header;
            if (c.Funnel)
            {
                var title = new TextBlock { Text = c.Header, VerticalAlignment = VerticalAlignment.Center };
                var funnel = new Button { Content = "\uE71C", Style = (Style)FindResource("IconButton"), Tag = c.Key, ToolTip = "Lọc theo " + c.Header,
                                          Background = Brushes.Transparent, BorderThickness = new Thickness(0), Effect = null,
                                          Width = 20, Height = 20, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), FontSize = 10 };
                funnel.Click += Funnel_Click;
                _funnels[c.Key] = funnel;
                var dp = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(funnel, Dock.Right); dp.Children.Add(funnel); dp.Children.Add(title);
                header = dp;
            }
            DataGridColumn col;
            if (c.Bg is not null || c.Fg is not null || c.Tip is not null)
            {
                // ô có màu (nhãn trạng thái / ĐẠT-KHÔNG ĐẠT) hoặc biểu tượng có chú thích
                var tb = new FrameworkElementFactory(typeof(TextBlock));
                tb.SetBinding(TextBlock.TextProperty, new Binding(c.Key));
                tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                if (c.Fg is not null) tb.SetBinding(TextBlock.ForegroundProperty, new Binding(c.Fg));
                if (c.Tip is not null) { tb.SetBinding(FrameworkElement.ToolTipProperty, new Binding(c.Tip)); tb.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09))); }
                FrameworkElementFactory root = tb;
                if (c.Bg is not null)
                {
                    tb.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); tb.SetValue(TextBlock.FontSizeProperty, 11.5);
                    root = new FrameworkElementFactory(typeof(Border));
                    root.SetBinding(Border.BackgroundProperty, new Binding(c.Bg));
                    root.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
                    root.SetValue(Border.PaddingProperty, new Thickness(9, 2, 9, 2));
                    root.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                    root.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                    root.AppendChild(tb);
                }
                else if (c.Fg is not null && c.Key == "ResultText") tb.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
                col = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = root }, SortMemberPath = c.Key, CanUserSort = true };
            }
            else
            {
                var tc = new DataGridTextColumn { Binding = new Binding(c.Key), SortMemberPath = c.Key == "Received" ? "ReceivedDate" : c.Key };
                if (c.Key is "Customer" or "DngcLine" or "Product")          // chữ dài bị cắt → rê chuột xem đủ
                {
                    var st = new Style(typeof(TextBlock));
                    st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                    st.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(c.Key == "Customer" ? "CustomerFull" : c.Key)));
                    tc.ElementStyle = st;
                }
                col = tc;
            }
            col.Header = header; col.MinWidth = c.Star ? c.Width : 40;
            col.Width = c.Star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(c.Width);
            col.Visibility = _visible.Contains(c.Key) ? Visibility.Visible : Visibility.Collapsed;
            _columns[c.Key] = col; Grid.Columns.Add(col);
        }
        UpdateColumnsButton();
    }

    private void UpdateColumnsButton() => ColumnsBtn.Content = "⚙ Cột ▾";

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ColumnsBtn, Placement = PlacementMode.Bottom };
        foreach (var c in Cols)
        {
            var mi = new MenuItem { Header = c.Header.Length > 0 ? c.Header : "Biểu tượng (lưu ý / cảnh báo / gộp)", IsCheckable = true, IsChecked = _visible.Contains(c.Key), StaysOpenOnClick = true };
            mi.Click += (_, _) =>
            {
                if (mi.IsChecked) _visible.Add(c.Key); else if (_visible.Count > 1) _visible.Remove(c.Key); else mi.IsChecked = true;
                _columns[c.Key].Visibility = _visible.Contains(c.Key) ? Visibility.Visible : Visibility.Collapsed;
                UpdateColumnsButton(); SaveVisible();
            };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Về mặc định" };
        reset.Click += (_, _) =>
        {
            _visible = Cols.Where(c => c.Default).Select(c => c.Key).ToHashSet();
            foreach (var c in Cols) _columns[c.Key].Visibility = _visible.Contains(c.Key) ? Visibility.Visible : Visibility.Collapsed;
            UpdateColumnsButton(); SaveVisible();
        };
        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    private static string Val(OrderItem i, string key) => key switch
    {
        "Dngc" => i.Dngc, "Line" => i.Line, "Received" => i.Received, "Customer" => i.Customer, "Product" => i.Product, "Grade" => i.Grade,
        "Requirement" => i.Requirement, "Qty" => i.Qty, "Weight" => i.Weight, "Hardness" => i.Hardness, "Result" => i.Result, "CqText" => i.CqText,
        "Template" => i.Template, "Group" => i.Group, "Notes" => i.Notes.ToString(), "Flags" => i.Flags, "Status" => i.Status, _ => "",
    };
    private static string Show(string v) => string.IsNullOrWhiteSpace(v) ? "(trống)" : v;

    private void Funnel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var b = (Button)sender; _filterCol = (string)b.Tag;
        var others = Filtered(except: _filterCol);
        var cur = _colFilters.GetValueOrDefault(_filterCol);
        _filterValues = others.Select(i => Show(Val(i, _filterCol))).Distinct().OrderBy(x => x).Take(1000)
            .Select(v => new FilterValue { Text = v, On = cur is null || cur.Contains(v) }).ToList();
        FilterTitle.Text = "Lọc: " + Cols.First(c => c.Key == _filterCol).Header;
        FilterSearch.Text = ""; FilterList.ItemsSource = _filterValues; FilterAll.IsChecked = _filterValues.All(v => v.On);
        FilterPopup.PlacementTarget = b; FilterPopup.IsOpen = true; FilterSearch.Focus();
    }

    private void FilterSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var q = FilterSearch.Text.Trim().ToLowerInvariant();
        FilterList.ItemsSource = q.Length == 0 ? _filterValues : _filterValues.Where(v => v.Text.ToLowerInvariant().Contains(q)).ToList();
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (FilterValue v in FilterList.ItemsSource) v.On = FilterAll.IsChecked == true;
    }

    private void FilterOk_Click(object sender, RoutedEventArgs e)
    {
        if (_filterCol is null) return;
        var visibleSet = ((IEnumerable<FilterValue>)FilterList.ItemsSource).ToHashSet();
        // ô tìm đang thu hẹp danh sách → chỉ giữ giá trị đang hiện và được chọn
        var on = _filterValues.Where(v => v.On && visibleSet.Contains(v)).Select(v => v.Text).ToHashSet();
        if (on.Count == _filterValues.Count) _colFilters.Remove(_filterCol); else _colFilters[_filterCol] = on;
        FilterPopup.IsOpen = false; ApplyFilter();
    }

    private void FilterClear_Click(object sender, RoutedEventArgs e)
    {
        if (_filterCol is not null) _colFilters.Remove(_filterCol);
        FilterPopup.IsOpen = false; ApplyFilter();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _colFilters.Clear(); SearchBox.Text = ""; _customer = null; CustomerPick.SelectedIndex = 0;
        foreach (ToggleButton c in Chips.Children) c.IsChecked = (int)c.Tag == 0;
        ApplyFilter();
    }

    // ---------------- dữ liệu ----------------
    public static OrderItem ToItem(Workspace.OrderRow r, int notes, IReadOnlyDictionary<string, Workspace.OrderRow> byKey, IReadOnlyDictionary<string, string> templateNames,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        var o = r.Order; var e = o.Erp; var parts = o.Key.Split('|');
        var isChild = o.Qc.MergeGroup is { } mg && mg != o.Key;
        var cqs = isChild && byKey.TryGetValue(o.Qc.MergeGroup!, out var pr) ? pr.Cqs : r.Cqs;
        var live = cqs.Where(c => c.State != CqState.Deleted).OrderBy(c => c.Ver).ToList();
        var last = live.LastOrDefault(); var approved = live.LastOrDefault(c => c.State == CqState.Approved);
        var cq = last is null ? "—" : $"{(approved ?? last).ReportNo} · {(approved is not null ? "Đã duyệt" : "Nháp")}{(approved?.Printed == true ? " · Đã in" : "")}";
        var hard = o.Qc.Hardness.ConfirmedAt is not null ? "Đã xác nhận" : o.Qc.Hardness.Samples.Any(x => !string.IsNullOrWhiteSpace(x)) ? "Đang nhập" : "—";
        var result = last is not null ? (last.Result == "Pass" ? "ĐẠT" : "KHÔNG ĐẠT")
            : o.Qc.Hardness.ConfirmedAt is not null && OrderService.TryEvaluate(o).Ev is { } ev ? ev.Status switch { CqNl.Domain.EvaluationStatus.Pass => "ĐẠT", CqNl.Domain.EvaluationStatus.Fail => "KHÔNG ĐẠT", _ => "" } : "";
        var group = isChild ? $"Gộp vào dòng {o.Qc.MergeGroup!.Split('|').LastOrDefault()}" : o.Qc.MergedChildren is { Count: > 0 } ch ? $"Nhóm {ch.Count + 1} dòng" : "";
        var flags = string.Join("; ", o.Flags.Select(f => f.Split(':')[0]).Distinct()) + (r.Draft ? (o.Flags.Count > 0 ? "; " : "") + "Nháp offline" : "");
        return new OrderItem
        {
            Key = o.Key, Dngc = parts[0], Line = parts.ElementAtOrDefault(1) ?? "", ReceivedDate = DashboardService.ParseDate(e.GetValueOrDefault("ReceivedAt")),
            Customer = o.Qc.CustomerOverride ?? SettingsService.AliasOf(aliases, e.GetValueOrDefault("Customer")) ?? e.GetValueOrDefault("Customer") ?? "",
            CustomerFull = e.GetValueOrDefault("Customer") ?? "", Product = e.GetValueOrDefault("ProductName") ?? "",
            Grade = o.Qc.SteelGrade ?? e.GetValueOrDefault("GradeFromErp") ?? "", Requirement = o.Qc.HardnessRequirement ?? e.GetValueOrDefault("HardnessRequirementErp") ?? "",
            Qty = e.GetValueOrDefault("Quantity") ?? "", Weight = e.GetValueOrDefault("WeightKg") ?? "", Hardness = hard, CqText = cq, Result = result,
            Template = o.Qc.TemplateId is { } t ? templateNames.GetValueOrDefault(t, t) : "", Group = group, Notes = notes, Flags = flags, Draft = r.Draft,
            HasCq = last is not null, Approved = approved is not null, Printed = approved?.Printed == true, IsChild = isChild,
            Status = isChild ? "Gộp" : approved?.Printed == true ? "Đã in" : approved is not null ? "Đã duyệt" : last is not null ? "Nháp CQ"
                     : o.Qc.Hardness.ConfirmedAt is not null ? "Đã đo" : hard == "Đang nhập" ? "Đang đo" : "Chưa đo",
            Range = RangeOf(o), CqNo = (approved ?? last)?.ReportNo ?? "",
        };
    }

    private static string RangeOf(OrderRecord o)
    {
        if (!o.Qc.Hardness.Samples.Any(x => !string.IsNullOrWhiteSpace(x))) return "";
        var ev = OrderService.TryEvaluate(o).Ev;
        if (ev?.Min is not { } mn || ev.Max is not { } mx) return "";
        return mn == mx ? $"{mn:0.#}" : $"{mn:0.#}–{mx:0.#}";
    }

    public async Task Reload(bool full = false)
    {
        var data = await Ui.RunAsync(this, "", () =>
        {
            var rows = App.Ws.ListOrders(full);
            var tpl = App.Ws.TemplateList().ToDictionary(t => t.Id, t => t.DisplayName);
            return (rows, notes: App.Ws.NoteCounts(), tpl, aliases: App.Ws.CustomerAliases());
        });
        if (data.rows is null) return;
        var byKey = data.rows.GroupBy(r => r.Order.Key).ToDictionary(g => g.Key, g => g.First());
        _all = data.rows.Select(r => ToItem(r, data.notes.GetValueOrDefault(r.Order.Key), byKey, data.tpl, data.aliases))
            .OrderByDescending(i => i.ReceivedDate ?? DateTime.MinValue).ThenByDescending(i => i.Dngc).ThenBy(i => i.Line).ToList();
        _suppressPick = true;
        var picks = new List<CustomerItem> { new(null, "— Tất cả khách hàng —") };
        picks.AddRange(_all.Where(i => i.Customer.Length > 0).GroupBy(i => i.Customer).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new CustomerItem(g.Key, $"{g.Key}  ({g.Count(x => !x.HasCq && !x.IsChild)} chưa làm / {g.Count()})")));
        CustomerPick.ItemsSource = picks;
        CustomerPick.SelectedItem = picks.FirstOrDefault(p => p.Name == _customer) ?? picks[0];
        if (CustomerPick.SelectedItem is CustomerItem { Name: null }) _customer = null;
        _suppressPick = false;
        ApplyFilter();
    }

    private IEnumerable<OrderItem> Filtered(string? except = null)
    {
        var q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        var chip = ChipDefs[_chip].Pred;
        return InRange().Where(i => q.Length == 0 || i.Search.Contains(q)).Where(chip).Where(i => _customer is null || i.Customer == _customer)
            .Where(i => _colFilters.Where(f => f.Key != except).All(f => f.Value.Contains(Show(Val(i, f.Key)))));
    }

    /// <summary>Đơn trong khoảng ngày nhận đang chọn (đơn chưa có ngày nhận luôn hiện).</summary>
    private IEnumerable<OrderItem> InRange()
    {
        if (_days <= 0) return _all;
        var from = DateTime.Today.AddDays(-_days);
        return _all.Where(i => i.ReceivedDate is null || i.ReceivedDate >= from);
    }

    private void DaysPick_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DaysPick.SelectedItem is not ComboBoxItem it || !int.TryParse((string)it.Tag, out var d)) return;
        _days = d;
        try { Directory.CreateDirectory(App.LocalDir); File.WriteAllText(DaysFile, d.ToString()); } catch { }
        if (IsLoaded) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var list = Filtered().ToList();
        Grid.ItemsSource = list;
        for (var i = 0; i < ChipDefs.Length; i++)
        {
            var n = InRange().Where(x => _customer is null || x.Customer == _customer).Count(ChipDefs[i].Pred);
            var tb = (ToggleButton)Chips.Children[i];
            tb.Content = $"{ChipDefs[i].Name}  {n}";
            tb.Visibility = ChipDefs[i].HideWhenZero && n == 0 && _chip != i ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var (k, b) in _funnels) b.Foreground = _colFilters.ContainsKey(k) ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        var active = _colFilters.Count + (SearchBox.Text.Length > 0 ? 1 : 0) + (_customer is null ? 0 : 1);
        ClearBtn.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = $"{list.Count} đơn" + (_colFilters.Count > 0 ? $" · lọc {_colFilters.Count} cột" : "") + (App.Ws.Online ? "" : " · OFFLINE: bản sao trên máy này");
        EmptyText.Text = _all.Count == 0 ? "Chưa có đơn nào.\nBấm \"Nhập ERP ▾\" để nhập file MF0015." : _chip == 0 && list.Count == 0 && active == 0 ? "Không còn đơn cần làm CQ trong khoảng ngày đang chọn. 👍" : "Không có đơn phù hợp bộ lọc.";
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed record CustomerItem(string? Name, string Text) { public string Label => Name ?? "Tất cả khách hàng"; public override string ToString() => Label; }
    private string? _customer; private bool _suppressPick;

    private void CustomerPick_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPick || !IsLoaded) return;
        _customer = (CustomerPick.SelectedItem as CustomerItem)?.Name;
        ApplyFilter();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) ApplyFilter();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload(full: true);
    private void Grid_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Open_Click(sender, e); } }
    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // chỉ mở khi nháy đúp vào một dòng (không phải tiêu đề cột / thanh cuộn)
        if (e.OriginalSource is DependencyObject d && FindRow(d) is null) return;
        Open_Click(sender, e);
        static DataGridRow? FindRow(DependencyObject? x) { while (x is not null && x is not DataGridRow) x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x); return x as DataGridRow; }
    }

    /// <summary>"Nhập ERP ▾": nhập ngay từ thư mục theo dõi, hoặc chọn một file ERP lẻ.</summary>
    private void Erp_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ErpBtn, Placement = PlacementMode.Bottom };
        var a = new MenuItem { Header = "Nhập ngay các file mới trong thư mục ERP theo dõi" }; a.Click += ErpSync_Click;
        var b = new MenuItem { Header = "Chọn một file ERP (MF0015)…" }; b.Click += Import_Click;
        menu.Items.Add(a); menu.Items.Add(b);
        menu.IsOpen = true;
    }

    public void SetErpStatus(string text) { ErpStatus = text; ErpStatusText.Text = text; }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not OrderItem it) return;
        new OrderWindow(it.Key) { Owner = Window.GetWindow(this) }.ShowDialog();
        await Reload();
        Grid.SelectedItem = (Grid.ItemsSource as List<OrderItem>)?.FirstOrDefault(x => x.Key == it.Key);
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        var keys = Grid.SelectedItems.OfType<OrderItem>().Select(i => i.Key).ToList();
        if (keys.Count < 2) { Ui.Info("Chọn từ 2 dòng trở lên (giữ phím Ctrl hoặc Shift khi bấm) cùng số ĐNGC, cùng mác, cùng yêu cầu độ cứng."); return; }
        if (!Ui.Ask($"Gộp {keys.Count} dòng thành MỘT CQ (số lượng và khối lượng cộng dồn, CQ tạo ở dòng đầu)?")) return;
        var parent = await Ui.RunAsync(this, "", () => App.Ws.MergeOrders(keys));
        if (parent is null) return;
        await Reload();
        Ui.Info($"Đã gộp {keys.Count} dòng. Tạo CQ ở dòng {parent.Split('|').LastOrDefault()}.");
    }

    private async void Split_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not OrderItem it || it.Group.Length == 0) { Ui.Info("Chọn một dòng thuộc nhóm gộp"); return; }
        if (!Ui.Ask("Tách nhóm gộp này về từng dòng riêng?")) return;
        if (await Ui.RunAsync(this, "", () => { App.Ws.SplitOrders(it.Key); return true; })) await Reload();
    }

    private async void ErpSync_Click(object sender, RoutedEventArgs e)
    {
        var scan = await Ui.RunAsync(this, "", () => App.Ws.ScanErpFolder());
        if (scan is null) return;
        if (scan.NewFiles.Count == 0) { Ui.Info($"Không có file ERP mới trong thư mục:\n{scan.Folder}"); return; }
        var w = new ImportPreviewWindow(scan) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() == true) await Reload();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Run(() => App.Ws.RequireOnline("import ERP"))) return;
        var dlg = new OpenFileDialog { Title = "Chọn file ERP MF0015", Filter = "File ERP (*.xlsx;*.xls)|*.xlsx;*.xls" };
        if (dlg.ShowDialog() != true) return;
        var path = dlg.FileName;
        var prev = await Ui.RunAsync(this, "", () =>
        {
            var read = CqNl.Infrastructure.Erp.ErpReader.Read(path);
            return (read, plan: App.Ws.Orders!.Preview(read));
        });
        if (prev.read is null) return;
        var w = new ImportPreviewWindow(path, prev.read, prev.plan) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() == true) await Reload();
    }
}
