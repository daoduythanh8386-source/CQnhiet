using System.Windows;
using CqNl.Domain;

namespace CqNl.Wpf.Views;

public partial class PickGradeWindow : Window
{
    public sealed record Row(SteelGrade G)
    {
        public int SourceRow => G.SourceRow; public string Group => G.Group; public string Name => G.Name; public string? Tolerance => G.Tolerance; public int? TemplateMark => G.TemplateMark;
        public string HeatText => string.Join(" / ", G.Heats.Where(h => !string.IsNullOrWhiteSpace(h)));
        public string RamText => string.Join(" / ", G.Rams.Where(h => !string.IsNullOrWhiteSpace(h)));
    }
    public SteelGrade? Selected { get; private set; }

    public PickGradeWindow(IReadOnlyList<SteelGrade> grades)
    {
        InitializeComponent();
        Grid.ItemsSource = grades.Select(g => new Row(g)).ToList();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not Row r) { Ui.Info("Chọn một dòng"); return; }
        Selected = r.G; DialogResult = true;
    }
}
