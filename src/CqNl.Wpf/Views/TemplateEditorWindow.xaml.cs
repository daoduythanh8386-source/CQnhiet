using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using CqNl.Application;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Pdf;

namespace CqNl.Wpf.Views;

/// <summary>Thêm template mới (tự dò theo mẫu có sẵn hoặc làm tay) / sửa bản đồ ô. Kiểm tra + xem thử PDF trước khi lưu.</summary>
public partial class TemplateEditorWindow : Window
{
    public sealed class FieldRow : INotifyPropertyChanged
    {
        private string _value = "", _check = "", _cellText = "";
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public FieldKind Kind { get; init; }
        public string Hint { get; init; } = "";
        public string Value { get => _value; set { _value = value; On(nameof(Value)); } }
        public string CellText { get => _cellText; set { _cellText = value; On(nameof(CellText)); } }
        public string Check { get => _check; set { _check = value; On(nameof(Check)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string n) => PropertyChanged?.Invoke(this, new(n));
    }

    private static Workspace Ws => App.Ws;
    private readonly byte[] _xlsx;
    private readonly string _fileName;
    private readonly TemplateInfo? _existing;
    private JsonObject _json;
    private TemplateInspector _insp;
    private readonly List<FieldRow> _rows;
    private string? _basedOn;

    /// <summary>Thêm template mới.</summary>
    public TemplateEditorWindow(byte[] xlsx, string fileName) : this(xlsx, fileName, null, null) { }

    /// <summary>Sửa bản đồ ô của template có sẵn.</summary>
    public TemplateEditorWindow(TemplateInfo t, byte[] xlsx, TemplateMap map) : this(xlsx, t.FileName, t, map) { }

    private TemplateEditorWindow(byte[] xlsx, string fileName, TemplateInfo? existing, TemplateMap? map)
    {
        InitializeComponent();
        _xlsx = xlsx; _fileName = fileName; _existing = existing;
        _insp = new TemplateInspector(xlsx, map?.Sheet);
        SheetBox.ItemsSource = _insp.Sheets; SheetBox.SelectedItem = _insp.Sheet;
        _rows = TemplateFields.All.Select(f => new FieldRow { Key = f.Key, Label = f.Label + (f.Required ? " *" : ""), Kind = f.Kind, Hint = f.Hint }).ToList();
        Fields.ItemsSource = _rows;
        if (existing is null)
        {
            Head.Text = "Thêm template mới — " + fileName;
            NameBox.Text = Path.GetFileNameWithoutExtension(fileName);
            var bases = Ws.Cq!.Templates.List().ToList();
            BaseBox.ItemsSource = bases; BaseBox.SelectedIndex = 0;
            _json = EmptyJson();
            Loaded += (_, _) => Auto_Click(this, new RoutedEventArgs());
        }
        else
        {
            Head.Text = "Sửa bản đồ ô — " + existing.DisplayName;
            NameBox.Text = existing.DisplayName; NameBox.IsReadOnly = true;
            AutoPanel.Visibility = Visibility.Collapsed;
            _json = JsonNode.Parse(map!.Json)!.AsObject();
            FromJson();
            Loaded += (_, _) => Ui.Run(() => Check(show: false));
        }
    }

    private JsonObject EmptyJson() => new() { ["templateId"] = "new", ["displayName"] = NameBox.Text, ["fileName"] = _fileName, ["sheet"] = _insp.Sheet, ["fields"] = new JsonObject() };

    // ----- JSON <-> bảng -----
    private void FromJson()
    {
        var map = TemplateMap.Parse(_json.ToJsonString());
        foreach (var r in _rows)
        {
            r.Value = r.Kind switch
            {
                FieldKind.Cell => map.Cell(r.Key) ?? "",
                FieldKind.Cells => string.Join(", ", map.Cells(r.Key)),
                FieldKind.Range => map.Ranges(r.Key).FirstOrDefault() ?? "",
                FieldKind.Ranges => string.Join(", ", map.Ranges(r.Key)),
                _ => string.Join(", ", map.Texts(r.Key)),
            };
        }
        RefreshCellTexts();
    }

    private static List<string> Split(string v) => v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private string ToJson()
    {
        var root = (JsonObject)_json.DeepClone();
        root["sheet"] = SheetBox.SelectedItem as string ?? _insp.Sheet;
        root["displayName"] = NameBox.Text.Trim();
        root["fileName"] = _fileName;
        var fields = root["fields"] as JsonObject ?? new JsonObject();
        root["fields"] = fields;
        foreach (var r in _rows)
        {
            var v = (r.Value ?? "").Trim().ToUpperInvariant().Replace("$", "");
            if (r.Kind == FieldKind.Texts) v = (r.Value ?? "").Trim();
            var obj = fields[r.Key] as JsonObject ?? new JsonObject();
            foreach (var k in new[] { "cell", "cells", "range", "ranges", "texts" }) obj.Remove(k);
            if (v.Length == 0 && r.Kind != FieldKind.Texts) { fields.Remove(r.Key); continue; }
            switch (r.Kind)
            {
                case FieldKind.Cell: obj["cell"] = v; break;
                case FieldKind.Cells: obj["cells"] = new JsonArray(Split(v).Select(x => (JsonNode)x).ToArray()); break;
                case FieldKind.Range: obj["range"] = v; break;
                case FieldKind.Ranges: obj["ranges"] = new JsonArray(Split(v).Select(x => (JsonNode)x).ToArray()); break;
                default: obj["texts"] = new JsonArray(Split(v).Select(x => (JsonNode)x).ToArray()); break;
            }
            if (r.Key is "PartNo" or "PartName" && TemplateInspector.TryCell(v, out var pc)) obj["unhideRowWhenFilled"] = pc.Row;
            if (r.Key == "HardnessImageFrames" && obj["maxImages"] is null) obj["maxImages"] = 8;
            fields[r.Key] = obj;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private void RefreshCellTexts()
    {
        foreach (var r in _rows)
        {
            var first = Split(r.Value).FirstOrDefault() ?? "";
            if (r.Kind is FieldKind.Texts) { r.CellText = _insp.DrawingTexts.Count == 0 ? "(không có hình vẽ)" : "Chữ trong hình: " + string.Join(", ", _insp.DrawingTexts.Where(t => t.Trim().Length > 0).Take(12)); continue; }
            if (r.Kind is FieldKind.Range or FieldKind.Ranges) { r.CellText = TemplateInspector.TryRange(first, out var rg) ? $"vùng {rg.C2 - rg.C1 + 1} cột × {rg.R2 - rg.R1 + 1} dòng" : ""; continue; }
            r.CellText = first.Length == 0 ? "" : _insp.TextAt(first) is { } t ? $"\"{t}\"" : "(ô trống)";
            var lab = _json["fields"]?[r.Key]?["label"];
            if (lab is JsonArray { Count: 2 } la && la[0]?.ToString() is { } lc) r.CellText += $"   · nhãn {lc}: \"{_insp.TextAt(lc)}\"";
        }
    }

    private void Sheet_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SheetBox.SelectedItem is not string sh || sh == _insp.Sheet) return;
        Ui.Run(() => { _insp = new TemplateInspector(_xlsx, sh); RefreshCellTexts(); });
    }

    private void Fields_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) => Dispatcher.InvokeAsync(RefreshCellTexts);

    // ----- tự dò / làm tay -----
    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (BaseBox.SelectedItem is not TemplateInfo b) return;
        Ui.Run(() =>
        {
            var (bm, _) = Ws.Cq!.Templates.Resolve(b.Id);
            var (json, report) = _insp.AutoMap(bm, "new", NameBox.Text.Trim(), _fileName);
            _json = JsonNode.Parse(json)!.AsObject(); _basedOn = b.Id;
            FromJson();
            Report.Text = $"Tự dò theo mẫu \"{b.DisplayName}\":\n" + string.Join("\n", report);
            Check(show: false);
        });
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Ask("Xoá toàn bộ ô đã điền để nhập tay từ đầu?")) return;
        _json = EmptyJson(); _basedOn = null; FromJson();
        foreach (var r in _rows) r.Check = "";
        Report.Text = "Nhập tay: gõ địa chỉ ô (vd G6) hoặc vùng ô (vd B19:K23) cho từng trường; với ô gộp, ghi ô trên-trái của vùng gộp. Trường có dấu * là bắt buộc.";
    }

    // ----- kiểm tra / xem thử / lưu -----
    private List<TemplateIssue> Check(bool show)
    {
        var issues = TemplateCatalog.Check(_xlsx, ToJson());
        foreach (var r in _rows)
        {
            var mine = issues.Where(i => i.Field == r.Key).ToList();
            r.Check = mine.Count == 0 ? (r.Value.Trim().Length == 0 ? "" : "✔ hợp lệ") : string.Join("; ", mine.Select(i => (i.Error ? "✖ " : "⚠ ") + i.Message.Split(": ").Last()));
        }
        var errs = issues.Count(i => i.Error); var warns = issues.Count - errs;
        var general = issues.Where(i => _rows.All(r => r.Key != i.Field)).Select(i => (i.Error ? "✖ " : "⚠ ") + i.Message).ToList();
        Status.Text = errs == 0 ? $"Kiểm tra đạt" + (warns > 0 ? $" ({warns} cảnh báo)" : "") + " — có thể xem thử và lưu." : $"Còn {errs} lỗi, {warns} cảnh báo.";
        Status.Foreground = (System.Windows.Media.Brush)FindResource(errs == 0 ? "OkBrush" : "BadBrush");
        if (general.Count > 0) Report.Text = string.Join("\n", general) + "\n\n" + Report.Text;
        if (show && errs > 0) Ui.Info("Bản đồ còn lỗi — xem cột \"Kiểm tra\".");
        return issues;
    }

    private void Check_Click(object sender, RoutedEventArgs e) => Ui.Run(() => Check(show: true));

    /// <summary>Dựng CQ bằng dữ liệu mẫu và xuất PDF bằng Excel (kiểm A4, 1 trang). Trả về đường dẫn PDF.</summary>
    public static string RenderSample(byte[] xlsx, TemplateMap map)
    {
        var bytes = CqWriter.Build(xlsx, map, TemplateCatalog.SampleData(map));
        var dir = Path.Combine(Path.GetTempPath(), "CQNhietLuyen"); Directory.CreateDirectory(dir);
        var stem = $"XEM_THU_TEMPLATE_{DateTime.Now:HHmmss}";
        var xp = Path.Combine(dir, stem + ".xlsx"); var pp = Path.Combine(dir, stem + ".pdf");
        File.WriteAllBytes(xp, bytes);
        ExcelPdfExporter.Export(xp, pp);
        return pp;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        var json = ToJson();
        if (Check(show: true).Any(i => i.Error)) return;
        var pdf = await Ui.RunAsync(this, "Đang dựng CQ mẫu bằng Excel…", () => RenderSample(_xlsx, TemplateMap.Parse(json)), Status);
        if (pdf is not null) { Status.Text = "Đã mở bản xem thử (dữ liệu mẫu)."; Ui.OpenFile(pdf); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { Ui.Info("Nhập tên template"); return; }
        var json = ToJson();
        if (Check(show: true).Any(i => i.Error)) return;
        if (Ui.Run(() =>
        {
            if (_existing is null) Ws.Cq!.Templates.AddCustom(Ws.Session!, NameBox.Text, _fileName, _xlsx, json, _basedOn);
            else Ws.Cq!.Templates.SaveMap(Ws.Session!, _existing.Id, json);
        }))
        {
            Ui.Info(_existing is null ? $"Đã thêm template \"{NameBox.Text.Trim()}\" — chọn được ở tab Thông tin đơn." : "Đã lưu bản đồ ô.");
            DialogResult = true;
        }
    }
}
