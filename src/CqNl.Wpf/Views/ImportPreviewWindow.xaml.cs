using System.IO;
using System.Windows;
using CqNl.Infrastructure.Erp;

namespace CqNl.Wpf.Views;

public partial class ImportPreviewWindow : Window
{
    private readonly string _path; private readonly ImportPlan _plan;
    private readonly Func<(int Added, int Updated, int Conflicts)>? _apply;
    public sealed record FileRow(string Name, string Modified, int Lines, string State);
    public sealed record UpdRow(string Key, string Field, string? Old, string? New);
    public sealed record RejRow(int Row, string? Dngc, string Reason);

    /// <summary>Xem trước đồng bộ từ thư mục ERP theo dõi (nhiều file).</summary>
    public ImportPreviewWindow(CqNl.Application.ErpFolderScan scan) : this(scan.Folder, scan.Combined, scan.Plan)
    {
        _apply = () => App.Ws.ApplyErpScan(scan);
        Title = "Đồng bộ ERP — xem trước";
        FileText.Text = $"Thư mục ERP: {scan.Folder}";
        FilesGrid.ItemsSource = scan.NewFiles.Select(f => new FileRow(f.Name, f.Modified.ToString("dd/MM/yyyy HH:mm"), f.Lines, f.Error ?? "Mới — sẽ nhập")).ToList();
        TabFiles.Header = $"File mới ({scan.NewFiles.Count})"; TabFiles.Visibility = Visibility.Visible; TabFiles.IsSelected = true;
    }

    public ImportPreviewWindow(string path, ErpReadResult read, ImportPlan plan)
    {
        InitializeComponent();
        _path = path; _plan = plan;
        FileText.Text = Path.GetFileName(path);
        Summary.Text = $"Đọc {read.Lines.Count} dòng hợp lệ · Thêm mới {plan.Add.Count} · Cập nhật {plan.Update.Count} · Không đổi {plan.Same.Count} · Từ chối {read.Rejected.Count}";
        AddGrid.ItemsSource = plan.Add;
        UpdGrid.ItemsSource = plan.Update.SelectMany(u => u.Changes.Select(c => new UpdRow(u.Line.Key, c.Key, c.Value.Old, c.Value.New))).ToList();
        RejGrid.ItemsSource = read.Rejected.Select(r => new RejRow(r.Row, r.Dngc, r.Reason)).ToList();
        TabAdd.Header = $"Thêm mới ({plan.Add.Count})"; TabUpd.Header = $"Cập nhật ({plan.Update.Count})"; TabRej.Header = $"Từ chối ({read.Rejected.Count})";
        ApplyBtn.IsEnabled = plan.Add.Count + plan.Update.Count > 0;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var res = await Ui.RunAsync<(int Added, int Updated, int Conflicts)?>(this, "", () =>
        {
            App.Ws.RequireOnline("import ERP");
            return _apply is not null ? _apply() : App.Ws.Orders!.Apply(App.Ws.Session!, _plan, _path, App.Ws.Cq!.HasActiveCq);
        });
        if (res is not { } r) return;
        Ui.Info($"Đã import: thêm {r.Added}, cập nhật {r.Updated}" + (r.Conflicts > 0 ? $", {r.Conflicts} dòng xung đột (máy khác vừa sửa) — import lại để thử tiếp" : ""));
        DialogResult = true;
    }
}
