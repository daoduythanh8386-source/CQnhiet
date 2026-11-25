using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CqNl.Application;
using CqNl.Infrastructure.Excel;

namespace CqNl.Wpf.Views;

public partial class ApproveWindow : Window
{
    private readonly CqVersionRecord _cq;
    private readonly TemplateMap _map;
    private readonly (string Role, string Label)[] _roles = { ("Approver", "Chữ ký người duyệt"), ("Checker", "Chữ ký người kiểm tra"), ("Stamp", "Con dấu") };
    private readonly Dictionary<string, (RadioButton Pick, TextBox Cell, TextBox Dx, TextBox Dy, TextBox W)> _rows = new();
    private static Workspace Ws => App.Ws;

    /// <param name="approverId">Người xác nhận đã chọn ở tab Xuất CQ (null = mặc định).</param>
    /// <param name="checkerId">Người kiểm tra đã chọn ở tab Xuất CQ (null = mặc định).</param>
    public ApproveWindow(CqVersionRecord cq, string? approverId = null, string? checkerId = null)
    {
        InitializeComponent();
        _cq = cq;
        _map = Ws.Cq!.MapOf(cq);
        Head.Text = $"Duyệt CQ số {cq.ReportNo} · bản {cq.Ver} · {(cq.Result == "Pass" ? "ĐẠT" : "KHÔNG ĐẠT")}";
        SaveDefaultBtn.IsEnabled = Ws.Session!.User.Role == Role.Admin;
        var r = 1;
        foreach (var (role, label) in _roles)
        {
            var pick = new RadioButton { Content = label, GroupName = "role", VerticalAlignment = VerticalAlignment.Center, IsChecked = r == 3 };
            TextBox T() => new() { Margin = new Thickness(0, 2, 6, 2) };
            var row = (pick, T(), T(), T(), T());
            _rows[role] = row;
            void Add(UIElement el, int c) { Grid.SetRow(el, r); Grid.SetColumn(el, c); PlaceGrid.Children.Add(el); }
            Add(pick, 0); Add(row.Item2, 1); Add(row.Item3, 2); Add(row.Item4, 3); Add(row.Item5, 4);
            r++;
        }
        var st = Ws.Settings!.Get().Value;
        var signers = st.Signers.Where(x => x.Active).OrderBy(x => x.Name).ToList();
        ApproverBox.ItemsSource = signers; CheckerBox.ItemsSource = signers;
        ApproverBox.SelectedItem = signers.FirstOrDefault(x => x.Id == (approverId ?? st.DefaultApproverId)) ?? signers.FirstOrDefault(x => x.Id == st.DefaultApproverId);
        CheckerBox.SelectedItem = signers.FirstOrDefault(x => x.Id == (checkerId ?? st.DefaultCheckerId)) ?? signers.FirstOrDefault(x => x.Id == st.DefaultCheckerId);
        Fill(st.Placements.GetValueOrDefault(cq.TemplateId) ?? Defaults());
        foreach (var r2 in _rows.Values) foreach (var tb in new[] { r2.Cell, r2.Dx, r2.Dy, r2.W }) tb.TextChanged += (_, _) => PositionAll();
        ApproverBox.SelectionChanged += (_, _) => UpdateOverlayImages(); CheckerBox.SelectionChanged += (_, _) => UpdateOverlayImages();
        OverCanvas.Background = Brushes.Transparent;
        OverCanvas.MouseMove += Canvas_MouseMove; OverCanvas.MouseLeftButtonUp += Canvas_MouseUp;
        Loaded += async (_, _) => await LoadPage();
        Status.Text = signers.Count == 0 ? "Chưa có chữ ký nào trong thư viện — quản trị thêm ở Cài đặt → Chữ ký & con dấu." :
            st.StampSha is null ? "Chưa có con dấu — quản trị thêm ở Cài đặt → Chữ ký & con dấu." :
            "Bấm \"Xem trước PDF\" để kiểm tra vị trí trước khi duyệt. Sau khi duyệt, CQ không sửa được.";
    }

    private List<CqOverlayPlacement> Defaults()
    {
        string First(string field) => _map.Ranges(field).First().Split(':')[0];
        return new()
        {
            new("Approver", First("ApproverSignatureFrame"), 4, 1, 36),
            new("Checker", First("CheckerSignatureFrame"), 4, 1, 36),
            new("Stamp", First("ApproverSignatureFrame"), -12, -3, 34),
        };
    }

    private void Fill(List<CqOverlayPlacement> ps)
    {
        foreach (var p in ps)
        {
            if (!_rows.TryGetValue(p.Role, out var r)) continue;
            r.Cell.Text = p.Cell; r.Dx.Text = F(p.DxMm); r.Dy.Text = F(p.DyMm); r.W.Text = F(p.WidthMm);
        }
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static double P(string t, string what)
    {
        if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new BusinessException($"{what}: \"{t}\" không phải số");
        return v;
    }

    private List<CqOverlayPlacement> Read()
    {
        var list = new List<CqOverlayPlacement>();
        foreach (var (role, label) in _roles)
        {
            var r = _rows[role];
            var cell = r.Cell.Text.Trim().ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(cell, "^[A-Z]{1,2}[0-9]{1,3}$")) throw new BusinessException($"{label}: ô neo \"{cell}\" không hợp lệ (vd N34)");
            var w = P(r.W.Text, label);
            if (w < 8 || w > 120) throw new BusinessException($"{label}: bề rộng {w} mm ngoài khoảng 8–120 mm");
            list.Add(new CqOverlayPlacement(role, cell, P(r.Dx.Text, label), P(r.Dy.Text, label), w));
        }
        return list;
    }

    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        var sel = _rows.Values.FirstOrDefault(r => r.Pick.IsChecked == true);
        if (sel.Pick is null) return;
        var step = StepBox.SelectedIndex switch { 0 => 1.0, 2 => 5.0, _ => 2.0 };
        Ui.Run(() =>
        {
            switch ((string)((Button)sender).Tag)
            {
                case "L": sel.Dx.Text = F(P(sel.Dx.Text, "Lệch ngang") - step); break;
                case "R": sel.Dx.Text = F(P(sel.Dx.Text, "Lệch ngang") + step); break;
                case "U": sel.Dy.Text = F(P(sel.Dy.Text, "Lệch dọc") - step); break;
                case "D": sel.Dy.Text = F(P(sel.Dy.Text, "Lệch dọc") + step); break;
                case "-": sel.W.Text = F(Math.Max(8, P(sel.W.Text, "Rộng") - step)); break;
                case "+": sel.W.Text = F(Math.Min(120, P(sel.W.Text, "Rộng") + step)); break;
            }
        });
    }

    private void AddSignature_Click(object sender, RoutedEventArgs e)
    {
        if (Ws.Session!.User.Role != Role.Admin) { Ui.Info("Chỉ tài khoản quản trị (admin) được thêm / thay chữ ký và con dấu."); return; }
        var w = new SignatureImportWindow { Owner = this };
        if (w.ShowDialog() != true) return;
        var st = Ws.Settings!.Get().Value;
        var signers = st.Signers.Where(x => x.Active).OrderBy(x => x.Name).ToList();
        var (a, c) = (ApproverBox.SelectedItem as SignerRecord, CheckerBox.SelectedItem as SignerRecord);
        ApproverBox.ItemsSource = signers; CheckerBox.ItemsSource = signers;
        ApproverBox.SelectedItem = signers.FirstOrDefault(x => x.Id == (w.AsApprover ? w.AddedSignerId : a?.Id)) ?? signers.FirstOrDefault(x => x.Name == a?.Name);
        CheckerBox.SelectedItem = signers.FirstOrDefault(x => x.Id == (w.AsChecker ? w.AddedSignerId : c?.Id)) ?? signers.FirstOrDefault(x => x.Name == c?.Name);
        UpdateOverlayImages();
        Status.Text = w.StampChanged ? "Đã cập nhật con dấu." : "Đã thêm chữ ký.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => Fill(Ws.Settings!.Get().Value.Placements.GetValueOrDefault(_cq.TemplateId) ?? Defaults());

    private void SaveDefault_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.Run(() => Ws.Settings!.SetPlacements(Ws.Session!, _cq.TemplateId, Read()))) Status.Text = "Đã lưu vị trí mặc định cho template " + _cq.TemplateId;
    }

    private (SignerRecord A, SignerRecord C, byte[] Stamp, List<CqOverlayPlacement> P) Inputs()
    {
        if (ApproverBox.SelectedItem is not SignerRecord a || CheckerBox.SelectedItem is not SignerRecord c) throw new BusinessException("Chọn người duyệt và người kiểm tra");
        var stamp = Ws.Settings!.Stamp() ?? throw new BusinessException("Chưa có con dấu — quản trị thêm ở Cài đặt");
        return (a, c, stamp, Read());
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) { if (await PreviewAsync() is { } pdf) Ui.OpenFile(pdf); }

    /// <summary>Dựng PDF xem trước (dấu + chữ ký ở vị trí đang chỉnh). Lựa chọn trên giao diện được đọc TRƯỚC khi chạy nền
    /// (control WPF chỉ đọc được trên luồng giao diện — lỗi gặp ở bản 0.7.1 ngày 16/11).</summary>
    public async Task<string?> PreviewAsync()
    {
        (SignerRecord A, SignerRecord C, byte[] Stamp, List<CqOverlayPlacement> P) i = default;
        if (!Ui.Run(() => i = Inputs())) return null;
        var (id, no) = (_cq.Id, _cq.ReportNo);
        return await Ui.RunAsync(this, "Đang dựng bản xem trước bằng Excel…", () =>
        {
            var bytes = Ws.Cq!.PreviewApproval(Ws.Session!, id, i.A, i.C, i.Stamp, i.P);
            var d = Path.Combine(Path.GetTempPath(), "CQNhietLuyen"); Directory.CreateDirectory(d);
            var p = Path.Combine(d, $"XEM_TRUOC_{no.Replace('/', '-')}_{DateTime.Now:HHmmss}.pdf");
            File.WriteAllBytes(p, bytes);
            return p;
        }, Status);
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Ask($"Duyệt CQ số {_cq.ReportNo}? Sau khi duyệt, CQ là bản chính thức và KHÔNG sửa được.")) return;
        var c = await ApproveAsync();
        if (c is null) return;
        if (c.Warnings.Count > 0) Ui.Info($"Đã duyệt CQ số {c.ReportNo}.\nCảnh báo: " + string.Join("; ", c.Warnings));
        DialogResult = true;
    }

    /// <summary>Duyệt: chèn dấu + chữ ký theo vị trí đang chỉnh, xuất PDF. Trả CQ đã duyệt (null = lỗi, đã báo).</summary>
    public async Task<CqVersionRecord?> ApproveAsync()
    {
        (SignerRecord A, SignerRecord C, byte[] Stamp, List<CqOverlayPlacement> P) i = default;
        if (!Ui.Run(() => i = Inputs())) return null;
        var id = _cq.Id;
        return await Ui.RunAsync(this, "Đang chèn dấu, chữ ký và xuất PDF bằng Excel…", () => Ws.Cq!.Approve(Ws.Session!, id, i.A, i.C, i.Stamp, i.P), Status);
    }

    // ================= Sửa tay trong Excel rồi xuất PDF =================
    private string? _editPath;

    private async void EditInExcel_Click(object sender, RoutedEventArgs e)
    {
        (SignerRecord A, SignerRecord C, byte[] Stamp, List<CqOverlayPlacement> P) i = default;
        if (!Ui.Run(() => i = Inputs())) return;
        var d = Path.Combine(Path.GetTempPath(), "CQNhietLuyen", "sua_tay"); Directory.CreateDirectory(d);
        var p = Path.Combine(d, $"CQ_{_cq.ReportNo.Replace('/', '-')}_ban_{_cq.Ver}_SUA.xlsx");
        if (File.Exists(p) && Ui.Ask("Đã có file Excel đang sửa dở của CQ này.\nCó = mở lại file đó (giữ phần đã sửa)\nKhông = tạo lại từ bản nháp với dấu, chữ ký ở vị trí đang chọn"))
        { _editPath = p; Ui.OpenFile(p); AfterEditOpen(); return; }
        var id = _cq.Id;
        var bytes = await Ui.RunAsync(this, "Đang tạo file Excel có dấu + chữ ký…", () => Ws.Cq!.SignedXlsx(Ws.Session!, id, i.A, i.C, i.Stamp, i.P), Status);
        if (bytes is null) return;
        try { File.WriteAllBytes(p, bytes); }
        catch (IOException) { Ui.Info("File đang mở trong Excel — đóng file đó rồi bấm lại."); return; }
        _editPath = p; Ui.OpenFile(p); AfterEditOpen();
    }

    private void AfterEditOpen()
    {
        EditedPreviewBtn.IsEnabled = EditedApproveBtn.IsEnabled = true;
        EditStatus.Text = "Đang sửa: " + _editPath + "\nSửa xong: Lưu (Ctrl+S) rồi đóng Excel, sau đó bấm \"Xem PDF từ file đã sửa\" / \"Duyệt từ file đã sửa\".";
    }

    /// <summary>Đọc file Excel đã sửa — chỉ khi đã ĐÓNG trong Excel (đang mở thì có thể chưa lưu phần sửa).</summary>
    private byte[]? ReadEdited()
    {
        if (_editPath is null || !File.Exists(_editPath)) { Ui.Info("Chưa có file đang sửa — bấm \"Sửa trong Excel…\" trước."); return null; }
        try
        {
            using var f = new FileStream(_editPath, FileMode.Open, FileAccess.Read, FileShare.None);
            using var ms = new MemoryStream(); f.CopyTo(ms); return ms.ToArray();
        }
        catch (IOException) { Ui.Info("File vẫn đang mở trong Excel.\nBấm Lưu (Ctrl+S) trong Excel rồi ĐÓNG file, sau đó bấm lại."); return null; }
    }

    private async void EditedPreview_Click(object sender, RoutedEventArgs e)
    {
        var x = ReadEdited(); if (x is null) return;
        var (id, no) = (_cq.Id, _cq.ReportNo);
        var p = await Ui.RunAsync(this, "Đang xuất PDF từ file Excel đã sửa…", () =>
        {
            var pdf = Ws.Cq!.PdfFromEditedXlsx(Ws.Session!, id, x);
            var d = Path.Combine(Path.GetTempPath(), "CQNhietLuyen"); Directory.CreateDirectory(d);
            var f = Path.Combine(d, $"XEM_TRUOC_SUA_{no.Replace('/', '-')}_{DateTime.Now:HHmmss}.pdf");
            File.WriteAllBytes(f, pdf); return f;
        }, Status);
        if (p is not null) Ui.OpenFile(p);
    }

    private async void EditedApprove_Click(object sender, RoutedEventArgs e)
    {
        if (ApproverBox.SelectedItem is not SignerRecord a || CheckerBox.SelectedItem is not SignerRecord c) { Ui.Info("Chọn người duyệt và người kiểm tra"); return; }
        var x = ReadEdited(); if (x is null) return;
        if (!Ui.Ask($"Duyệt CQ số {_cq.ReportNo} TỪ FILE EXCEL ĐÃ SỬA TAY?\nPDF sẽ in đúng như file Excel đó; CQ ghi chú \"sửa tay\" trong lịch sử. Sau khi duyệt KHÔNG sửa được.")) return;
        var id = _cq.Id;
        var r = await Ui.RunAsync(this, "Đang xuất PDF từ file Excel đã sửa và duyệt…", () => Ws.Cq!.ApproveEdited(Ws.Session!, id, x, a, c), Status);
        if (r is null) return;
        DialogResult = true;
    }

    // ================= Kéo-thả trên ảnh trang CQ =================
    private byte[]? _xlsx;
    private PageMap? _page;
    private readonly Dictionary<string, (Border Box, Image Img, double Aspect)> _ov = new();
    private readonly Dictionary<string, (double X, double Y)> _cellOrigin = new();
    private (string Role, Point Start, double Left, double Top, double Width, bool Resize)? _drag;
    private bool _writing;

    private async Task LoadPage()
    {
        // 2 lần dựng bằng Excel: (1) trang SẠCH để hiển thị; (2) trang có ô đánh dấu MỖI DÒNG để đo vị trí từng dòng khi in thật
        // (Excel in chiều cao dòng theo tỉ lệ khác bề rộng cột → suy toạ độ dọc từ tỉ lệ ngang làm dấu lệch tới ~1 cm ở xa ô neo).
        var id = _cq.Id; var map = _map;
        var res = await Ui.RunAsync(this, "Đang dựng ảnh trang CQ bằng Excel (2 lần: trang sạch + hiệu chỉnh từng dòng)…", () =>
        {
            var clean = Ws.Cq!.RenderDraft(Ws.Session!, id);
            var (fc, rows) = CqWriter.AreaRows(clean.Xlsx, map);
            var marked = Ws.Cq!.RenderDraft(Ws.Session!, id, PagePlacement.RowMarkers(fc, rows));
            return (clean.Xlsx, clean.Pdf, Marked: marked.Pdf, Fc: fc, Rows: rows, Cols: CqWriter.AreaCols(clean.Xlsx, map));
        }, PageStatus);
        if (res.Xlsx is null) { PageStatus.Text = "Không dựng được ảnh trang — vẫn chỉnh được bằng số và \"Xem trước PDF\"."; return; }
        try
        {
            var width = (uint)Math.Clamp(PageScroll.ActualWidth - 40, 600, 1100);
            var bmp = await PagePlacement.RenderAsync(res.Pdf, width);
            var mbmp = await PagePlacement.RenderAsync(res.Marked, width);
            _xlsx = res.Xlsx;
            var pm = PagePlacement.Calibrate(mbmp, CqWriter.Geometry(res.Xlsx, _map), CqWriter.CellOriginEmu(res.Xlsx, _map, res.Fc + res.Rows[0].Row).X, res.Rows, res.Cols);
            _page = pm;
            var clean = bmp;
            PageImg.Source = clean; PageImg.Width = bmp.PixelWidth; PageImg.Height = bmp.PixelHeight;
            OverCanvas.Width = bmp.PixelWidth; OverCanvas.Height = bmp.PixelHeight;
            foreach (var (role, label) in _roles)
            {
                var img = new Image { Stretch = Stretch.Fill, Opacity = 0.9, IsHitTestVisible = false };
                var grip = new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Fill = (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
                var g = new Grid(); g.Children.Add(img); g.Children.Add(grip);
                var box = new Border { BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(1.5), Background = new SolidColorBrush(Color.FromArgb(18, 31, 95, 168)),
                                       Child = g, Cursor = Cursors.SizeAll, ToolTip = label + " — kéo để di chuyển, kéo góc dưới-phải để đổi cỡ", Tag = role };
                box.MouseLeftButtonDown += Box_MouseDown;
                OverCanvas.Children.Add(box);
                _ov[role] = (box, img, 1);
            }
            UpdateOverlayImages();
            FitPage();
            PageScroll.SizeChanged -= PageScroll_SizeChanged; PageScroll.SizeChanged += PageScroll_SizeChanged;
            PageScroll.UpdateLayout();
            if (_ov.TryGetValue("Approver", out var ap)) PageScroll.ScrollToVerticalOffset(Math.Max(0, Canvas.GetTop(ap.Box) * _fit - PageScroll.ViewportHeight * 0.55));
            PageStatus.Text = "Kéo dấu / chữ ký tới vị trí mong muốn. Khung xanh là vị trí sẽ in. Bấm \"Xem trước PDF\" để xem bản Excel thật trước khi duyệt.";
        }
        catch (Exception e) { PageStatus.Text = "Không hiển thị được trang để kéo thả (" + e.Message + ") — vẫn chỉnh được bằng số."; }
    }

    /// <summary>Thu trang cho vừa bề ngang khung xem (không phải cuộn ngang). Toạ độ kéo thả vẫn theo ảnh gốc (LayoutTransform).</summary>
    private double _fit = 1;
    private void FitPage()
    {
        if (PageImg.Width is not > 0 || PageScroll.ActualWidth <= 0) return;
        _fit = Math.Min(1, Math.Max(0.3, (PageScroll.ActualWidth - 40) / PageImg.Width));
        PageHost.LayoutTransform = new ScaleTransform(_fit, _fit);
    }
    private void PageScroll_SizeChanged(object sender, SizeChangedEventArgs e) => FitPage();

    private static BitmapImage Bmp(byte[] b)
    {
        var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(b); bi.EndInit(); bi.Freeze(); return bi;
    }

    private void UpdateOverlayImages()
    {
        if (_ov.Count == 0) return;
        void SetImg(string role, byte[]? b)
        {
            if (b is null || !_ov.TryGetValue(role, out var o)) return;
            var bi = Bmp(b); o.Img.Source = bi;
            _ov[role] = (o.Box, o.Img, bi.PixelHeight / (double)bi.PixelWidth);
        }
        try
        {
            if (ApproverBox.SelectedItem is SignerRecord a) SetImg("Approver", Ws.ImageBytes(a.TransparentSha, "png"));
            if (CheckerBox.SelectedItem is SignerRecord c) SetImg("Checker", Ws.ImageBytes(c.TransparentSha, "png"));
            SetImg("Stamp", Ws.Settings!.Stamp());
        }
        catch { }
        PositionAll();
    }

    private (double X, double Y) Origin(string cell)
    {
        if (!_cellOrigin.TryGetValue(cell, out var o)) _cellOrigin[cell] = o = CqWriter.CellOriginEmu(_xlsx!, _map, cell);
        return o;
    }

    private void PositionAll()
    {
        if (_page is null || _xlsx is null || _writing) return;
        foreach (var (role, _) in _roles)
        {
            if (!_ov.TryGetValue(role, out var o)) continue;
            var r = _rows[role];
            if (!double.TryParse(r.Dx.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var dx) ||
                !double.TryParse(r.Dy.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var dy) ||
                !double.TryParse(r.W.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var wmm) || wmm <= 0) continue;
            (double X, double Y) org;
            try { org = Origin(r.Cell.Text.Trim().ToUpperInvariant()); } catch { continue; }
            var wEmu = wmm * 36000;
            Canvas.SetLeft(o.Box, _page.X(org.X + dx * 36000)); Canvas.SetTop(o.Box, _page.Y(org.Y + dy * 36000));
            o.Box.Width = wEmu * _page.DrawPxPerEmu; o.Box.Height = wEmu * o.Aspect * _page.DrawPxPerEmu;
        }
    }

    private void Box_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var box = (Border)sender; var role = (string)box.Tag;
        var p = e.GetPosition(OverCanvas);
        double left = Canvas.GetLeft(box), top = Canvas.GetTop(box);
        var resize = p.X > left + box.Width - 14 && p.Y > top + box.Height - 14;
        _drag = (role, p, left, top, box.Width, resize);
        _rows[role].Pick.IsChecked = true;
        OverCanvas.CaptureMouse(); e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
        var o = _ov[d.Role]; var p = e.GetPosition(OverCanvas);
        if (d.Resize) { var w = Math.Max(12, d.Width + (p.X - d.Start.X)); o.Box.Width = w; o.Box.Height = w * o.Aspect; }
        else { Canvas.SetLeft(o.Box, d.Left + p.X - d.Start.X); Canvas.SetTop(o.Box, d.Top + p.Y - d.Start.Y); }
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not { } d || _page is null) return;
        _drag = null; OverCanvas.ReleaseMouseCapture();
        var o = _ov[d.Role]; var r = _rows[d.Role];
        try
        {
            static double R(double v) => Math.Round(v * 2, MidpointRounding.AwayFromZero) / 2;     // làm tròn 0.5 mm
            static double Fl(double v) => Math.Floor(v * 2) / 2;                                    // làm tròn XUỐNG 0.5 mm (không vượt ra ngoài ô)
            // NEO LẠI vào đúng ô nằm dưới góc trên-trái của dấu / chữ ký (lệch chỉ trong 1 ô): Excel đặt ảnh theo ô neo, lệch nhỏ
            // → vị trí in không phụ thuộc chiều cao các dòng ở xa (trước đây neo cố định N34, dấu kéo lên đầu trang lệch tới ~1 cm).
            var (cell, offX, offY) = CqWriter.CellAt(_xlsx!, _map, _page.EmuX(Canvas.GetLeft(o.Box)), _page.EmuY(Canvas.GetTop(o.Box)));
            _writing = true;
            r.Cell.Text = cell;
            r.Dx.Text = F(Fl(offX / 36000));
            r.Dy.Text = F(Fl(offY / 36000));
            r.W.Text = F(Math.Clamp(R(o.Box.Width / _page.DrawPxPerEmu / 36000), 8, 120));
        }
        catch (Exception ex) { Ui.Error(ex); }
        finally { _writing = false; PositionAll(); }
    }
}
