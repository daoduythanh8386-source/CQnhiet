using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CqNl.Infrastructure.Storage;

namespace CqNl.Application;

/// <summary>Dấu "kho đã reset" (đặt trong kho): mỗi máy so với mã reset đã biết → bỏ bản sao cục bộ cũ (đơn, CQ, lưu ý, nháp).</summary>
public sealed record StoreResetMarker(string Id, DateTimeOffset At, string By, string Machine, string BackupDir);

public sealed record ResetResult(string BackupDir, int Records, int BlobsDeleted, int BlobsKept);

/// <summary>"Reset dữ liệu" (chỉ quản trị): xoá đơn gia công, số đo, ảnh, CQ, lưu ý, nhật ký, số báo cáo đã cấp, danh sách file ERP đã nhập.
/// GIỮ: tài khoản, cài đặt (chữ ký, con dấu, bảng mác, tên viết tắt khách, thư mục ERP / lưu CQ), template, bản phát hành.
/// Luôn SAO LƯU + kiểm tra toàn vẹn TRƯỚC khi xoá; sao lưu lỗi → không xoá gì. Chặn khi có người đang dùng ở máy khác.</summary>
public sealed partial class Workspace
{
    public const string ResetFileName = "RESET.json";
    /// <summary>Loại bản ghi bị xoá khi reset. erpfile: file ERP đã nhập → sau reset app nhập lại từ thư mục ERP (đơn mới, chưa có số đo).</summary>
    public static readonly string[] ResetTypes = { OrderService.T, CqService.T, NoteService.T, ErpFileType };
    private static readonly Regex ShaRx = new("[0-9a-f]{64}", RegexOptions.Compiled);

    public static StoreResetMarker? ReadResetMarker(string dataRoot)
    {
        try { var p = Path.Combine(dataRoot, ResetFileName); return File.Exists(p) ? JsonSerializer.Deserialize<StoreResetMarker>(File.ReadAllText(p, Encoding.UTF8)) : null; }
        catch { return null; }
    }

    /// <summary>Kiểm tra trước khi reset: quyền, người đang dùng ở máy khác, nháp chưa đồng bộ, số bản ghi sẽ xoá. Không thay đổi gì.</summary>
    public (int Records, IReadOnlyList<string> OtherSessions, int PendingDrafts) PlanReset()
    {
        RequireOnline("reset dữ liệu");
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin);
        var n = ResetTypes.Sum(t => Repo!.Ids(t).Count());
        var others = Auth!.OnlineSessions().Where(l => !(l.User == s.User.Username && l.Machine == Machine)).Select(l => $"{l.User} @ {l.Machine}").Distinct().ToList();
        return (n, others, Outbox.Items().Count(i => i.State == "pending"));
    }

    public ResetResult ResetData(string backupRoot, IProgress<string>? progress = null)
    {
        var (records, others, pending) = PlanReset();
        if (others.Count > 0) throw new BusinessException("Đang có người dùng ở máy khác: " + string.Join(", ", others) + ". Nhờ họ đăng xuất rồi reset lại.");
        if (pending > 0) throw new BusinessException($"Máy này còn {pending} nháp chưa đồng bộ — bấm \"Đồng bộ ngay\" trước.");
        var s = Session!;
        var full = Path.GetFullPath(backupRoot);
        if (full.StartsWith(Path.GetFullPath(DataRoot), StringComparison.OrdinalIgnoreCase)) throw new BusinessException("Thư mục sao lưu không được nằm trong kho");
        // 1. sao lưu + kiểm tra SHA-256 (lỗi → dừng, chưa xoá gì)
        progress?.Report("Đang sao lưu toàn bộ kho trước khi xoá…");
        var bk = BackupService.Backup(DataRoot, full);
        // 2. xoá bản ghi nghiệp vụ, xung đột, sự kiện, số báo cáo đã cấp, nhật ký
        progress?.Report("Đang xoá đơn, CQ, ảnh, lưu ý, nhật ký…");
        foreach (var t in ResetTypes)
        {
            DeleteDir(Path.Combine(DataRoot, "records", t));
            DeleteDir(Path.Combine(DataRoot, "conflicts", t));
        }
        DeleteChildren(Path.Combine(DataRoot, "events"));
        DeleteDir(Path.Combine(DataRoot, "counters", "reportno"));
        DeleteChildren(Path.Combine(DataRoot, "audit"));
        DeleteChildren(Path.Combine(DataRoot, "locks"), keep: d => Path.GetFileName(d).StartsWith("session-", StringComparison.OrdinalIgnoreCase));
        // 3. ảnh / file CQ (blob) không còn bản ghi nào dùng → xoá; blob của chữ ký, con dấu, bảng mác, template, bộ cài → giữ
        progress?.Report("Đang dọn ảnh và file CQ không còn dùng…");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(DataRoot, "records"), "*.json", SearchOption.AllDirectories))
            foreach (Match m in ShaRx.Matches(File.ReadAllText(f))) used.Add(m.Value);
        int del = 0, kept = 0;
        var blobs = Path.Combine(DataRoot, "blobs");
        if (Directory.Exists(blobs))
            foreach (var b in Directory.EnumerateFiles(blobs, "*", SearchOption.AllDirectories).ToList())
            {
                var sha = Path.GetFileNameWithoutExtension(b);
                if (used.Contains(sha)) { kept++; continue; }
                try { File.Delete(b); del++; } catch { kept++; }
            }
        // 4. dấu reset → mọi máy bỏ bản sao cục bộ cũ khi kết nối lại
        var mk = new StoreResetMarker(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, s.User.Username, Machine, bk);
        var p = Path.Combine(DataRoot, ResetFileName);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(mk), Encoding.UTF8); File.Move(p + ".tmp", p, overwrite: true);
        ApplyResetLocally();
        try { Audit!.Write(s.User.Username, "data.reset", "store", new { records, blobsDeleted = del, backup = bk }); } catch { }
        progress?.Report($"Đã reset: xoá {records} bản ghi, {del} file ảnh/CQ. Bản sao lưu: {bk}");
        return new ResetResult(bk, records, del, kept);
    }

    /// <summary>Kho đã reset (mã khác lần trước) → cất bản sao cục bộ + nháp cũ sang thư mục "truoc-reset-…" (không xoá hẳn) và đọc lại từ đầu.</summary>
    private void ApplyResetLocally() { lock (_indexLock) ApplyResetLocallyCore(); }
    private void ApplyResetLocallyCore()
    {
        var mk = ReadResetMarker(DataRoot);
        if (mk is null) return;
        var known = Cache.Get<string>("meta", "reset")?.Value;
        if (known == mk.Id) return;
        var archive = Path.Combine(_localDir, "truoc-reset-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        foreach (var t in new[] { OrderService.T, CqService.T, NoteService.T })
        {
            var d = Path.Combine(_localDir, "cache", t);
            if (Directory.Exists(d)) { Directory.CreateDirectory(archive); Directory.Move(d, Path.Combine(archive, "cache-" + t)); }
        }
        try { File.Delete(Path.Combine(_localDir, "cache", "meta", FileStore.SafeId("index") + ".json")); } catch { }
        var ob = Outbox.Path_;
        foreach (var f in Directory.EnumerateFiles(ob, "*.json").ToList())
        {
            Directory.CreateDirectory(Path.Combine(archive, "outbox"));
            File.Move(f, Path.Combine(archive, "outbox", Path.GetFileName(f)), overwrite: true);
        }
        Cache.Put("meta", "reset", mk.Id, 0, false);
    }

    private static void DeleteDir(string d) { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
    private static void DeleteChildren(string d, Func<string, bool>? keep = null)
    {
        if (!Directory.Exists(d)) return;
        foreach (var x in Directory.EnumerateDirectories(d).ToList()) if (keep?.Invoke(x) != true) Directory.Delete(x, recursive: true);
        foreach (var x in Directory.EnumerateFiles(d).ToList()) File.Delete(x);
    }
}
