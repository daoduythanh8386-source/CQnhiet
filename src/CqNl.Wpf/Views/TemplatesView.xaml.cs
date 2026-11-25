using System.IO;
using System.Windows;
using System.Windows.Controls;
using CqNl.Application;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Pdf;
using Microsoft.Win32;

namespace CqNl.Wpf.Views;

/// <summary>Quản lý template CQ: mặc định (cài kèm) + thêm mới (kho chung); sửa bản đồ ô, xem thử PDF.</summary>
public partial class TemplatesView : UserControl
{
    private static Workspace Ws => App.Ws;
    public TemplatesView() { InitializeComponent(); Loaded += (_, _) => Reload(); }

    private void Reload()
    {
        if (!Ws.Online) { Grid.ItemsSource = Ws.TemplateList(); IsEnabled = false; return; }
        Ui.Run(() => Grid.ItemsSource = Ws.Cq!.Templates.List(includeInactive: true));
        Grid_SelectionChanged(this, null!);
    }

    private TemplateInfo? Sel() { if (Grid.SelectedItem is TemplateInfo t) return t; Ui.Info("Chọn một template trong danh sách"); return null; }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var t = Grid.SelectedItem as TemplateInfo;
        ResetBtn.IsEnabled = t is { BuiltIn: true, MapOverridden: true };
        RemoveBtn.IsEnabled = t is { BuiltIn: false, Active: true };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Template Excel (*.xlsx)|*.xlsx", Title = "Chọn file Excel template CQ mới" };
        if (d.ShowDialog() != true) return;
        byte[] bytes = Array.Empty<byte>();
        if (!Ui.Run(() => { bytes = File.ReadAllBytes(d.FileName); _ = new TemplateInspector(bytes); })) return;
        var w = new TemplateEditorWindow(bytes, Path.GetFileName(d.FileName)) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() == true) Reload();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Sel() is not { } t) return;
        if (!t.Active) { Ui.Info("Template đã ngừng dùng"); return; }
        (TemplateMap Map, byte[] Xlsx) r = default;
        if (!Ui.Run(() => r = Ws.Cq!.Templates.Resolve(t.Id))) return;
        var w = new TemplateEditorWindow(t, r.Xlsx, r.Map) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() == true) Reload();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (Sel() is not { } t) return;
        var pdf = await Ui.RunAsync(this, "", () => { var r = Ws.Cq!.Templates.Resolve(t.Id); return TemplateEditorWindow.RenderSample(r.Xlsx, r.Map); });
        if (pdf is not null) Ui.OpenFile(pdf);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Sel() is not { } t || !t.BuiltIn) return;
        if (!Ui.Ask($"Khôi phục bản đồ ô MẶC ĐỊNH cho \"{t.DisplayName}\"? (CQ đã tạo không bị ảnh hưởng)")) return;
        if (Ui.Run(() => Ws.Cq!.Templates.ResetBuiltIn(Ws.Session!, t.Id))) Reload();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Sel() is not { } t || t.BuiltIn) return;
        if (!Ui.Ask($"Ngừng dùng template \"{t.DisplayName}\"? File vẫn giữ trong kho; CQ đã tạo vẫn mở được.")) return;
        if (Ui.Run(() => Ws.Cq!.Templates.Deactivate(Ws.Session!, t.Id))) Reload();
    }
}
