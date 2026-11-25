using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CqNl.Application;

namespace CqNl.Wpf;

/// <summary>Cài bản cập nhật đã phát hành trên kho chung: đồng bộ nháp → tải bộ cài (kiểm SHA-256) → chạy bộ cài chế độ im lặng
/// (/RELAUNCH: cài xong tự mở lại ứng dụng) → đăng xuất và đóng ứng dụng để bộ cài thay file.</summary>
public static class Updater
{
    public static bool Run(Window? owner, ReleaseRecord r, bool ask)
    {
        var ws = App.Ws;
        var cur = Workspace.VersionText(Workspace.CurrentVersion);
        if (ask && !Ui.Ask($"Có bản cập nhật {r.Version} (máy này đang dùng {cur})." + (string.IsNullOrWhiteSpace(r.Notes) ? "" : "\n\nThay đổi: " + r.Notes) +
                           "\n\nCập nhật ngay? Ứng dụng sẽ đóng, cài bản mới (Windows có thể hỏi quyền quản trị) rồi tự mở lại. Dữ liệu giữ nguyên.")) return false;
        try
        {
            if (ws.Outbox.Items().Any(i => i.State == "pending")) { try { ws.Sync(); } catch { } }
            if (ws.Outbox.Items().Any(i => i.State == "pending") &&
                !Ui.Ask("Còn nháp chưa đồng bộ lên kho chung. Nháp vẫn được giữ trên máy này và tự đồng bộ sau khi cập nhật. Tiếp tục cập nhật?")) return false;
            var path = ws.PrepareInstall(r);
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH") { UseShellExecute = true });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { Ui.Info("Đã huỷ cập nhật (không cấp quyền quản trị)."); return false; }
        catch (Exception e) { Ui.Error(e); return false; }
        foreach (var w in System.Windows.Application.Current.Windows.OfType<MainWindow>()) w.SkipPrompt();
        try { ws.Logout(); } catch { }
        System.Windows.Application.Current.Shutdown();
        return true;
    }
}
