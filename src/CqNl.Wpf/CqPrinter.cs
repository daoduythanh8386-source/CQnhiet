using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CqNl.Wpf;

/// <summary>In CQ (PDF A4 một trang) thẳng ra máy in đã chọn: dựng trang PDF thành ảnh 300 dpi (Windows.Data.Pdf) rồi in khổ A4 bằng hàng đợi in của Windows.</summary>
public static class CqPrinter
{
    private static IEnumerable<PrintQueue> Queues()
    {
        var server = new LocalPrintServer();
        return server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
    }

    public static IReadOnlyList<string> Printers()
    {
        try { return Queues().Select(q => q.FullName).OrderBy(x => x).ToList(); } catch { return Array.Empty<string>(); }
    }

    public static string? DefaultPrinter()
    {
        try { return LocalPrintServer.GetDefaultPrintQueue()?.FullName; } catch { return null; }
    }

    public static async Task Print(byte[] pdf, string printer, int copies, string jobName)
    {
        var queue = Queues().FirstOrDefault(q => q.FullName == printer) ?? throw new InvalidOperationException("Không tìm thấy máy in: " + printer);
        var page = await PagePlacement.RenderAsync(pdf, 2480);                 // A4 @ 300 dpi
        const double w = 793.7, h = 1122.5;                                      // A4 theo đơn vị WPF (1/96 inch)
        var fp = new FixedPage { Width = w, Height = h, Background = Brushes.White };
        var img = new System.Windows.Controls.Image { Source = page, Width = w, Height = h, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        fp.Children.Add(img);
        fp.Measure(new Size(w, h)); fp.Arrange(new Rect(0, 0, w, h)); fp.UpdateLayout();
        var pc = new PageContent(); ((System.Windows.Markup.IAddChild)pc).AddChild(fp);
        var doc = new FixedDocument(); doc.DocumentPaginator.PageSize = new Size(w, h); doc.Pages.Add(pc);
        var ticket = queue.DefaultPrintTicket ?? new PrintTicket();
        ticket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
        ticket.PageOrientation = PageOrientation.Portrait;
        ticket.CopyCount = Math.Max(1, copies);
        queue.CurrentJobSettings.Description = jobName;
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        writer.Write(doc, ticket);
    }
}
