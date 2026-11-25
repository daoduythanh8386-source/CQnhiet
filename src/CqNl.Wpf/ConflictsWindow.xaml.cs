using System.Text.Json.Nodes;
using System.Windows;
using CqNl.Application;

namespace CqNl.Wpf;

public partial class ConflictsWindow : Window
{
    public sealed record Entry(Workspace.ConflictView V) { public string Title => $"{V.Item.Id}\n{new DateTime(V.Item.Ts, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm} · {V.Item.User}"; }
    public sealed record DiffRow(string Field, string? Mine, string? Store);

    public ConflictsWindow() { InitializeComponent(); Loaded += (_, _) => Reload(); }

    private void Reload()
    {
        if (!App.Ws.Online) { Ui.Info("Cần kết nối kho chung để so sánh và xử lý xung đột"); }
        Ui.Run(() => List.ItemsSource = App.Ws.Conflicts().Select(c => new Entry(c)).ToList());
        if (List.Items.Count > 0) List.SelectedIndex = 0; else Diff.ItemsSource = null;
    }

    private static void Flatten(JsonNode? n, string prefix, Dictionary<string, string?> into)
    {
        switch (n)
        {
            case JsonObject o: foreach (var (k, v) in o) Flatten(v, prefix.Length == 0 ? k : prefix + "." + k, into); break;
            case JsonArray a: for (var i = 0; i < a.Count; i++) Flatten(a[i], $"{prefix}[{i}]", into); break;
            default: into[prefix] = n?.ToString(); break;
        }
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not Entry en) { Diff.ItemsSource = null; return; }
        var a = new Dictionary<string, string?>(); var b = new Dictionary<string, string?>();
        Flatten(en.V.Mine is null ? null : Json.ToNode(en.V.Mine), "", a);
        Flatten(en.V.Latest is null ? null : Json.ToNode(en.V.Latest), "", b);
        Diff.ItemsSource = a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k)).OrderBy(k => k)
            .Select(k => new DiffRow(k, a.GetValueOrDefault(k), b.GetValueOrDefault(k))).ToList();
    }

    private void Resolve(bool mine)
    {
        if (List.SelectedItem is not Entry en) return;
        var q = mine ? "Ghi bản nháp của bạn thành bản MỚI NHẤT trên kho? Bản trên kho hiện tại vẫn được giữ trong lịch sử phiên bản."
                     : "Bỏ nháp của bạn và dùng bản trên kho? Nháp vẫn được lưu trong thư mục conflicts của kho để tra cứu.";
        if (!Ui.Ask(q)) return;
        if (Ui.Run(() => App.Ws.ResolveConflict(en.V.Item.OpId, mine))) Reload();
    }

    private void KeepMine_Click(object sender, RoutedEventArgs e) => Resolve(true);
    private void KeepStore_Click(object sender, RoutedEventArgs e) => Resolve(false);
}
