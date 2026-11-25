using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CqNl.Application;
using CqNl.Infrastructure.Pdf;

namespace CqNl.Wpf.Views;

/// <summary>"Sửa trong Excel rồi in" (yêu cầu 25/11): với MỌI kiểu bản in (có dấu + chữ ký / có dấu chưa ký / ký tay) — 1) mở file Excel của
/// bản in đó, sửa nội dung bất kỳ ô nào, lưu (Ctrl+S); 2) tạo PDF đúng như file đã sửa, lưu cùng CQ (có lịch sử) + thư mục lưu CQ; in nếu chọn.</summary>
public sealed class EditPrintWindow : Window
{
    private readonly string _key, _saveRoot; private readonly string? _approverId, _checkerId, _printer; private readonly int _copies;
    private readonly RadioButton _signed = new() { Content = "Có dấu + chữ ký (bản duyệt)", Margin = new Thickness(0, 2, 0, 2), IsChecked = true };
    private readonly RadioButton _stamp = new() { Content = "Có dấu, chưa ký", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _blank = new() { Content = "Không dấu, không chữ ký (in ký tay)", Margin = new Thickness(0, 2, 0, 2) };
    private readonly CheckBox _print = new() { Content = "In ra máy in sau khi tạo PDF", Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Brushes.DimGray };
    private readonly Button _step2;
    private string? _xlsx; private CqVersionRecord? _cq; private CqExportKind _kind;
    public bool Changed { get; private set; }

    public EditPrintWindow(string key, string saveRoot, string? approverId, string? checkerId, string? printer, int copies)
    {
        _key = key; _saveRoot = saveRoot; _approverId = approverId; _checkerId = checkerId; _printer = printer; _copies = copies;
        Title = "Sửa CQ trong Excel rồi in"; Width = 620; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White; ResizeMode = ResizeMode.NoResize;
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "Kiểu bản in", FontWeight = FontWeights.SemiBold });
        root.Children.Add(_signed); root.Children.Add(_stamp); root.Children.Add(_blank);
        root.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6), Foreground = Brushes.DimGray,
            Text = "Bước 1: mở file Excel của bản in, sửa nội dung bất kỳ ô nào (dấu / chữ ký là ảnh rời, kéo được), bấm Lưu (Ctrl+S) trong Excel.\n" +
                   "Bước 2: quay lại đây bấm \"Tạo PDF từ file đã sửa\" — PDF in đúng như file Excel đã lưu. Không cần đóng Excel.\n" +
                   "File phải còn số báo cáo của CQ. Bản có dấu + chữ ký đã duyệt thì không sửa lại được (bất biến)." });
        Button B(string t, RoutedEventHandler h, bool primary = false)
        {
            var b = new Button { Content = t, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
            if (primary && TryFindResource("Primary") is Style st) b.Style = st;
            b.Click += h; return b;
        }
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(B("1. Mở Excel để sửa", async (_, _) => await Step1(false), primary: true));
        row.Children.Add(B("Làm lại từ đầu", async (_, _) => { if (Ui.Ask("Tạo lại file Excel từ bản gốc? Phần đã sửa trong file hiện tại sẽ bị thay.")) await Step1(true); }));
        _step2 = B("2. Tạo PDF từ file đã sửa", async (_, _) => await Step2(), primary: true); _step2.IsEnabled = false;
        row.Children.Add(_step2);
        root.Children.Add(row);
        _print.IsEnabled = printer is not null;
        if (printer is not null) _print.Content = $"In ra máy in sau khi tạo PDF ({printer}, {copies} bản)";
        root.Children.Add(_print);
        root.Children.Add(_status);
        var close = B("Đóng", (_, _) => Close()); close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(close);
        Content = root;
        foreach (var r in new[] { _signed, _stamp, _blank }) r.Checked += (_, _) => { _xlsx = null; _step2.IsEnabled = false; _status.Text = ""; };
    }

    private CqExportKind Kind() => _stamp.IsChecked == true ? CqExportKind.StampOnly : _blank.IsChecked == true ? CqExportKind.Blank : CqExportKind.Signed;

    private async Task Step1(bool fresh)
    {
        _kind = Kind();
        if (_kind == CqExportKind.Signed && (_approverId is null || _checkerId is null))
        { Ui.Info("Chưa có người ký mặc định. Bấm \"Duyệt CQ\" một lần để chọn Người duyệt / Người kiểm tra, hoặc chọn kiểu in khác."); return; }
        var r = await Ui.RunAsync(this, "Đang tạo file Excel…", () => App.Ws.EditableCqXlsx(_key, _kind, _saveRoot, _approverId, _checkerId, fresh), _status);
        if (r.Xlsx is null) return;
        Changed = true; _cq = r.Cq; _xlsx = r.Xlsx; _step2.IsEnabled = true;
        Ui.OpenFile(r.Xlsx);
        _status.Text = $"CQ số {r.Cq.ReportNo}: " + string.Join(" → ", r.Steps) + $"\nĐang sửa: {r.Xlsx}\nSửa xong bấm Lưu trong Excel rồi bấm bước 2.";
    }

    private async Task Step2()
    {
        if (_xlsx is null || _cq is null) return;
        if (!File.Exists(_xlsx)) { Ui.Info("Không thấy file " + _xlsx + " — bấm lại bước 1."); return; }
        var cq = _cq; var kind = _kind; var x = _xlsx;
        var r = await Ui.RunAsync(this, "Đang tạo PDF từ file Excel đã sửa…", () => App.Ws.IssueFromEditedFile(cq.Id, kind, x, _saveRoot, _approverId, _checkerId), _status);
        if (r.Pdf is null) return;
        Changed = true;
        if (_print.IsChecked == true && _printer is not null)
        {
            try
            {
                await CqPrinter.Print(File.ReadAllBytes(r.Pdf), _printer, _copies, Path.GetFileNameWithoutExtension(r.Pdf));
                if (!r.Cq.Printed) Ui.Run(() => App.Ws.Cq!.MarkPrinted(App.Ws.Session!, r.Cq.Id, true));
                _status.Text = $"Đã tạo PDF từ file đã sửa và gửi {_copies} bản ra máy in {_printer}.\nPDF: {r.Pdf}";
                return;
            }
            catch (Exception ex) { Ui.Info("Không in được: " + ex.Message + "\nFile PDF vẫn được lưu: " + r.Pdf); }
        }
        Ui.OpenFile(r.Pdf);
        _status.Text = $"Đã tạo PDF từ file đã sửa (lưu cùng CQ số {r.Cq.ReportNo}, có ghi nhật ký).\nPDF: {r.Pdf}";
    }
}
