using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CqNl.Infrastructure.Imaging;

namespace CqNl.Wpf.Views;

public partial class CropWindow : Window
{
    private readonly int _w, _h;
    private double _scale = 1;
    private Point? _start;
    public System.Drawing.Rectangle? Result { get; private set; }

    public CropWindow(byte[] original, int[]? current)
    {
        InitializeComponent();
        // Hiển thị đúng hệ toạ độ mà bộ chuẩn hóa dùng (đã xoay theo EXIF) → khung chọn khớp ảnh thật.
        var rgb = ImageNormalizer.Load(original);
        _w = rgb.W; _h = rgb.H;
        var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(ImageNormalizer.SaveJpeg(rgb, 88)); bi.EndInit(); bi.Freeze();
        Img.Source = bi;
        Loaded += (_, _) =>
        {
            _scale = Math.Min(Host.ActualWidth / _w, Host.ActualHeight / _h);
            Cv.Width = Img.Width = _w * _scale; Cv.Height = Img.Height = _h * _scale;
            if (current is { Length: 4 }) Show(new Rect(current[0] * _scale, current[1] * _scale, current[2] * _scale, current[3] * _scale));
        };
    }

    private void Show(Rect r)
    {
        Canvas.SetLeft(Sel, r.X); Canvas.SetTop(Sel, r.Y); Sel.Width = r.Width; Sel.Height = r.Height; Sel.Visibility = Visibility.Visible;
        SelText.Text = $"Vùng chọn: {(int)(r.Width / _scale)} × {(int)(r.Height / _scale)} px";
    }

    private static Point Clamp(Point p, double w, double h) => new(Math.Clamp(p.X, 0, w), Math.Clamp(p.Y, 0, h));
    private void Cv_Down(object sender, MouseButtonEventArgs e) { _start = Clamp(e.GetPosition(Cv), Cv.Width, Cv.Height); Cv.CaptureMouse(); }
    private void Cv_Move(object sender, MouseEventArgs e)
    {
        if (_start is not { } s || e.LeftButton != MouseButtonState.Pressed) return;
        var p = Clamp(e.GetPosition(Cv), Cv.Width, Cv.Height);
        Show(new Rect(s, p));
    }
    private void Cv_Up(object sender, MouseButtonEventArgs e) { _start = null; Cv.ReleaseMouseCapture(); }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Sel.Visibility != Visibility.Visible || Sel.Width < 10 || Sel.Height < 10) { Ui.Info("Kéo chuột để chọn vùng cần giữ"); return; }
        var x = (int)Math.Round(Canvas.GetLeft(Sel) / _scale); var y = (int)Math.Round(Canvas.GetTop(Sel) / _scale);
        var w = (int)Math.Round(Sel.Width / _scale); var h = (int)Math.Round(Sel.Height / _scale);
        Result = new System.Drawing.Rectangle(x, y, Math.Min(w, _w - x), Math.Min(h, _h - y));
        DialogResult = true;
    }
}
