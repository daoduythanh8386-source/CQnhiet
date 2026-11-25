using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CqNl.Wpf.Views;

/// <summary>Xem ảnh cỡ lớn (QC + Kinh doanh): ảnh đưa lên CQ (đã cắt/làm sáng) và ảnh gốc; lưu bản sao ra máy.</summary>
public partial class ImageViewWindow : Window
{
    private readonly byte[]? _main, _orig;
    private byte[]? _current;

    public ImageViewWindow(string title, byte[]? main, byte[]? original, string? value)
    {
        InitializeComponent();
        Title = title; _main = main; _orig = original;
        Info.Text = value is null ? "" : "Giá trị: " + value;
        ShowNormBtn.Visibility = main is not null && original is not null ? Visibility.Visible : Visibility.Collapsed;
        ShowOrigBtn.Visibility = ShowNormBtn.Visibility;
        Display(main ?? original);
    }

    private void Display(byte[]? b)
    {
        _current = b; if (b is null) return;
        var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(b); bi.EndInit(); bi.Freeze();
        Img.Source = bi;
    }

    private void ShowNorm_Click(object sender, RoutedEventArgs e) => Display(_main);
    private void ShowOrig_Click(object sender, RoutedEventArgs e) => Display(_orig);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var png = _current.Length > 4 && _current[0] == 0x89 && _current[1] == 0x50;
        var d = new SaveFileDialog { Filter = png ? "PNG (*.png)|*.png" : "JPEG (*.jpg)|*.jpg", FileName = "anh" + (png ? ".png" : ".jpg") };
        if (d.ShowDialog() == true) Ui.Run(() => File.WriteAllBytes(d.FileName, _current));
    }
}
