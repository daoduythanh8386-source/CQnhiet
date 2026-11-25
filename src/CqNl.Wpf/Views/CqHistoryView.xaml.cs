using System.IO;
using System.Windows;
using System.Windows.Controls;
using CqNl.Application;

namespace CqNl.Wpf.Views;

public sealed record CqRow(CqVersionRecord Rec)
{
    public string ReportNo => Rec.ReportNo; public string OrderKey => Rec.OrderKey.Replace("|", " / dòng "); public int Ver => Rec.Ver; public string TemplateId => Rec.TemplateId;
    public string ResultText => Rec.Result == "Pass" ? "ĐẠT" : "KHÔNG ĐẠT";
    public string StateText => Rec.State switch { CqState.Approved => "Đã duyệt", CqState.Deleted => "Đã xóa", _ => "Nháp" };
    public string Created => $"{Rec.CreatedBy} {Rec.CreatedAt:dd/MM/yyyy HH:mm}";
    public string Approved => Rec.ApprovedAt is { } a ? $"{Rec.ApprovedBy} {a:dd/MM/yyyy HH:mm}" : "";
    public string Printed => Rec.Printed ? $"{Rec.PrintedBy} {Rec.PrintedAt:dd/MM HH:mm}" : "";
    public string Notes => Rec.State == CqState.Deleted ? $"Xóa bởi {Rec.DeletedBy} {Rec.DeletedAt:dd/MM/yyyy HH:mm}: {Rec.DeleteReason}" : string.Join("; ", Rec.Warnings);
    public string Search => $"{Rec.ReportNo} {Rec.OrderKey} {Rec.CreatedBy} {Rec.ApprovedBy}".ToLowerInvariant();
}

public partial class CqHistoryView : UserControl
{
    private List<CqRow> _all = new();
    public CqHistoryView() { InitializeComponent(); Loaded += async (_, _) => await Reload(); }

    private async Task Reload()
    {
        var list = await Ui.RunAsync(this, "", () => App.Ws.ListOrders().SelectMany(r => r.Cqs).ToList());
        if (list is null) return;
        _all = list.OrderByDescending(c => c.CreatedAt).Select(c => new CqRow(c)).ToList();
        Apply();
    }

    private void Apply()
    {
        var q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        var l = _all.Where(c => q.Length == 0 || c.Search.Contains(q)).Where(c => StateBox.SelectedIndex switch
        {
            1 => c.Rec.State == CqState.Draft, 2 => c.Rec.State == CqState.Approved, 3 => c.Rec.State == CqState.Approved && !c.Rec.Printed, 4 => c.Rec.State == CqState.Deleted, _ => true,
        }).ToList();
        Grid.ItemsSource = l; CountText.Text = $"{l.Count} / {_all.Count} bản CQ";
    }

    private void Filter_Changed(object sender, EventArgs e) { if (IsLoaded) Apply(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload();

    private async void OpenOrder_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not CqRow r) return;
        new OrderWindow(r.Rec.OrderKey) { Owner = Window.GetWindow(this) }.ShowDialog();
        await Reload();
    }

    private void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not CqRow r) return;
        string? p = null;
        if (Ui.Run(() => p = App.Ws.ExportCq(r.Rec, "pdf", Path.Combine(Path.GetTempPath(), "CQNhietLuyen")))) Ui.OpenFile(p!);
    }
}
