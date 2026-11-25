using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CqNl.Application;

/// <summary>Một bản cài đặt đã phát hành lên kho chung (bộ cài lưu dạng blob theo mã kiểm SHA-256).</summary>
public sealed class ReleaseRecord
{
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha { get; set; } = "";
    public long Size { get; set; }
    public string Notes { get; set; } = "";
    public string PublishedBy { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public sealed partial class Workspace
{
    public const string ReleaseType = "release";

    /// <summary>Phiên bản đang chạy (x.y.z, lấy từ Directory.Build.props khi build).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(Workspace).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string VersionText(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    // ---------------- Khôi phục mật khẩu quản trị ----------------

    /// <summary>Máy đang chạy có phải máy CHỨA kho chung không (đường dẫn UNC trỏ về chính máy này, hoặc ổ đĩa cục bộ).</summary>
    public static bool IsStoreHost(string storePath, string machineName)
    {
        if (string.IsNullOrWhiteSpace(storePath)) return false;
        if (storePath.StartsWith(@"\\"))
        {
            var host = storePath.TrimStart('\\').Split('\\')[0];
            var dot = host.IndexOf('.'); if (dot > 0 && !char.IsDigit(host[0])) host = host[..dot];
            return host.Equals(machineName, StringComparison.OrdinalIgnoreCase) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";
        }
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(storePath));
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;   // ổ mạng ánh xạ (Network) không tính
        }
        catch { return false; }
    }

    public bool IsStoreHost() => IsStoreHost(StorePath, Environment.MachineName);

    /// <summary>Tạo quản trị đầu tiên — CHỈ khi kho chung chưa có tài khoản nào VÀ đang chạy trên máy chứa kho.
    /// Cài app ở máy khác / cài lại app không bao giờ tạo được quản trị mới (dùng tài khoản đã có trên kho chung).</summary>
    public void InitFirstAdmin(string username, string fullName, string password, string confirm)
    {
        RequireOnline("khởi tạo kho");
        if (Auth!.HasAnyUser()) throw new BusinessException("Kho chung đã có tài khoản — không tạo quản trị mới. Đăng nhập bằng tài khoản đã có.");
        if (AppInfo.IsSyncFolder(StorePath) && !AllowNewSyncStore)
            throw new PermissionException("Kho đồng bộ trên máy này chưa thấy tài khoản nào — thường do Synology Drive CHƯA ĐỒNG BỘ XONG. " +
                "Chờ đồng bộ xong rồi đăng nhập bằng tài khoản đã có; không tạo quản trị mới (tránh tách thành 2 kho). Cần hỗ trợ: liên hệ quản trị (admin).");
        if (!IsStoreHost())
            throw new PermissionException($"Kho chung chưa có tài khoản. Chỉ khởi tạo được trên máy chứa kho ({StorePath}). Liên hệ quản trị (admin) để được hỗ trợ.");
        if (password != confirm) throw new BusinessException("Hai lần nhập mật khẩu không khớp");
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsWhiteSpace)) throw new BusinessException("Tên đăng nhập không hợp lệ");
        if (string.IsNullOrWhiteSpace(fullName)) throw new BusinessException("Nhập họ tên");
        Auth.CreateFirstAdmin(username.Trim(), fullName.Trim(), password);
    }



    // ---------------- Cập nhật phần mềm qua kho chung ----------------

    /// <summary>Đọc phiên bản x.y.z từ bộ cài: thông tin phiên bản của file .exe, nếu không có thì từ tên file "...Setup-x.y.z...".</summary>
    public static Version? SetupVersion(string path)
    {
        try
        {
            var fv = FileVersionInfo.GetVersionInfo(path);
            var s = fv.ProductVersion ?? fv.FileVersion;
            if (!string.IsNullOrWhiteSpace(s) && Version.TryParse(Regex.Match(s, @"\d+\.\d+(\.\d+)?").Value, out var v1)) return new Version(v1.Major, v1.Minor, Math.Max(0, v1.Build));
        }
        catch { }
        var m = Regex.Match(Path.GetFileName(path), @"(\d+)\.(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>Quản trị phát hành bộ cài lên kho chung → mọi máy thấy thông báo cập nhật.</summary>
    public ReleaseRecord PublishRelease(string setupPath, string notes)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin);
        RequireOnline("phát hành bản cập nhật");
        var name = Path.GetFileName(setupPath);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !name.Contains("CQNhietLuyen", StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("Hãy chọn đúng file bộ cài CQNhietLuyen-Setup-x.y.z-x64.exe");
        var v = SetupVersion(setupPath) ?? throw new BusinessException("Không đọc được số phiên bản của bộ cài");
        var vt = VersionText(v);
        if (Repo!.Get<ReleaseRecord>(ReleaseType, vt).Value is not null) throw new BusinessException($"Phiên bản {vt} đã được phát hành trước đó");
        var bytes = File.ReadAllBytes(setupPath);
        if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new BusinessException("File không phải chương trình cài đặt Windows (.exe)");
        var sha = Store!.PutBlob(bytes, "exe");
        var rec = new ReleaseRecord { Version = vt, FileName = name, Sha = sha, Size = bytes.Length, Notes = notes.Trim(), PublishedBy = s.User.Username, At = DateTimeOffset.Now };
        Repo.Put(ReleaseType, vt, rec, 0, s.User.Username);
        Audit!.Write(s.User.Username, "release.publish", vt, new { rec.FileName, rec.Size, rec.Sha });
        return rec;
    }

    public IReadOnlyList<ReleaseRecord> Releases()
    {
        if (!Online) return Array.Empty<ReleaseRecord>();
        return Repo!.Ids(ReleaseType).Select(id => Repo.Get<ReleaseRecord>(ReleaseType, id).Value).Where(r => r is not null).Select(r => r!)
            .OrderByDescending(r => Version.TryParse(r.Version, out var v) ? v : new Version(0, 0)).ToList();
    }

    /// <summary>Bản mới hơn bản đang chạy (null = đang dùng bản mới nhất / không đọc được kho).</summary>
    public ReleaseRecord? NewerRelease(Version? current = null)
    {
        try
        {
            var cur = current ?? CurrentVersion;
            return Releases().FirstOrDefault(r => Version.TryParse(r.Version, out var v) && v > cur);
        }
        catch { return null; }
    }

    /// <summary>Chép bộ cài từ kho về máy, kiểm mã SHA-256 rồi trả đường dẫn để chạy.</summary>
    public string DownloadRelease(ReleaseRecord r)
    {
        RequireOnline("tải bản cập nhật");
        var bytes = Store!.GetBlob(r.Sha, "exe");     // GetBlob đã kiểm mã SHA-256
        var dir = Path.Combine(Path.GetTempPath(), "CQNhietLuyen_update"); Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, r.FileName);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>Tải bộ cài + ghi nhật ký "release.install" (trước khi chạy bộ cài và thoát ứng dụng).</summary>
    public string PrepareInstall(ReleaseRecord r)
    {
        var p = DownloadRelease(r);
        try { Audit?.Write(Session?.User.Username ?? "?", "release.install", r.Version, new { from = VersionText(CurrentVersion), to = r.Version, machine = Environment.MachineName }); } catch { }
        return p;
    }
}
