using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CqNl.Infrastructure.Pdf;

public sealed record PdfCheck(int Pages, double WidthPt, double HeightPt)
{
    public bool IsA4 => Math.Abs(WidthPt - 595.3) < 2 && Math.Abs(HeightPt - 841.9) < 2;
}

public sealed class PdfExportException(string message) : Exception(message);

/// <summary>Xuất PDF bằng Microsoft Excel (COM, late-binding — không cần PIA).
/// Bắt buộc MapPaperSize=False (lỗi #10: Excel tự đổi A4→Letter theo máy in), trả lại cài đặt cũ sau khi xuất.</summary>
public static class ExcelPdfExporter
{
    public static bool IsAvailable() => Type.GetTypeFromProgID("Excel.Application") is not null;

    /// <summary>Mở thử Excel (ẩn) đọc tên + phiên bản rồi đóng — phân biệt Microsoft Excel thật với phần mềm khác đăng ký thay (WPS…).</summary>
    public static (bool Ok, string Detail) Probe()
    {
        var t = Type.GetTypeFromProgID("Excel.Application");
        if (t is null) return (false, "Máy chưa cài Microsoft Excel.");
        dynamic? xl = null;
        try
        {
            xl = Activator.CreateInstance(t)!;
            string name = xl.Name; string ver = xl.Version;
            var ms = name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            return ms ? (true, $"{name} {ver}") : (false, $"Đang đăng ký Excel là \"{name} {ver}\" (không phải Microsoft Excel) — không bảo đảm xuất đúng mẫu CQ.");
        }
        catch (Exception e) { return (false, "Không mở được Excel: " + e.Message); }
        finally
        {
            if (xl is not null) { try { xl.Quit(); } catch { } try { Marshal.FinalReleaseComObject(xl); } catch { } }
        }
    }

    public static PdfCheck Export(string xlsxPath, string pdfPath, int expectedPages = 1)
    {
        var t = Type.GetTypeFromProgID("Excel.Application") ?? throw new PdfExportException("Máy chưa cài Microsoft Excel");
        dynamic xl = Activator.CreateInstance(t)!;
        bool? oldMap = null;
        try
        {
            xl.Visible = false; xl.DisplayAlerts = false; xl.ScreenUpdating = false;
            oldMap = (bool)xl.MapPaperSize; xl.MapPaperSize = false;
            dynamic wb = xl.Workbooks.Open(Path.GetFullPath(xlsxPath), 0, true);
            try { wb.ExportAsFixedFormat(0, Path.GetFullPath(pdfPath)); }
            finally { wb.Close(false); Marshal.FinalReleaseComObject(wb); }
        }
        finally
        {
            try { if (oldMap is not null) xl.MapPaperSize = oldMap.Value; } catch { /* trả lại cài đặt, không chặn */ }
            xl.Quit(); Marshal.FinalReleaseComObject(xl);
        }
        var chk = Inspect(pdfPath);
        if (chk.Pages != expectedPages) throw new PdfExportException($"PDF có {chk.Pages} trang (cần {expectedPages}) → nội dung tràn trang");
        if (!chk.IsA4) throw new PdfExportException($"PDF không phải khổ A4 ({chk.WidthPt:0}×{chk.HeightPt:0} pt)");
        return chk;
    }

    /// <summary>Đếm trang và đọc MediaBox trang đầu (đủ cho PDF do Excel tạo).</summary>
    public static PdfCheck Inspect(string pdfPath)
    {
        var s = Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
        var pages = Regex.Matches(s, @"/Type\s*/Page(?![s\w])").Count;
        var mb = Regex.Match(s, @"/MediaBox\s*\[\s*([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s*\]");
        double D(int i) => double.Parse(mb.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
        return mb.Success ? new PdfCheck(pages, D(3) - D(1), D(4) - D(2)) : new PdfCheck(pages, 0, 0);
    }
}
