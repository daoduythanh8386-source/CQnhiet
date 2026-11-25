using System.Windows;
using System.Windows.Controls;

namespace CqNl.Wpf.Views;

public sealed record AuditRow(string At, string User, string Machine, string Action, string Target, string Detail, string Raw)
{
    public string Search => $"{User} {Action} {Target} {Detail}".ToLowerInvariant();
}

public partial class AuditView : UserControl
{
    private List<AuditRow> _all = new();
    private static readonly System.Text.Json.JsonSerializerOptions Rel = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public AuditView() { InitializeComponent(); Loaded += async (_, _) => await Reload(); }

    private async Task Reload()
    {
        if (!App.Ws.Online) { CountText.Text = "OFFLINE — nhật ký nằm trong kho chung"; return; }
        var rows = await Ui.RunAsync(this, "", () => App.Ws.Audit!.Read(3000).Select(n =>
        {
            var action = (string?)n["action"] ?? "";
            var target = (string?)n["target"] ?? "";
            return new AuditRow(DateTimeOffset.TryParse((string?)n["at"], out var t) ? t.ToString("dd/MM/yyyy HH:mm:ss") : "", (string?)n["user"] ?? "", (string?)n["machine"] ?? "",
                CqNl.Application.AuditText.Action(action), target.Replace("|", " / dòng "), CqNl.Application.AuditText.Detail(action, n["detail"]), n["detail"]?.ToJsonString(Rel) ?? "");
        }).Reverse().ToList());
        if (rows is null) return;
        _all = rows; Apply();
    }
    private void Apply()
    {
        var q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        var l = _all.Where(a => q.Length == 0 || a.Search.Contains(q)).ToList();
        Grid.ItemsSource = l; CountText.Text = $"{l.Count} / {_all.Count} dòng (3000 sự kiện gần nhất)";
    }
    private void Filter_Changed(object sender, EventArgs e) { if (IsLoaded) Apply(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload();
}
