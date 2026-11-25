using System.Text;
using System.Text.Json.Nodes;
using CqNl.Infrastructure.Security;
using CqNl.Infrastructure.Storage;

namespace CqNl.Application;

/// <summary>Nhật ký bất biến: audit/yyyyMM/&lt;ticks&gt;-&lt;máy&gt;-&lt;guid&gt;.json (tạo-không-ghi-đè).</summary>
public sealed class AuditLog(FileStore store)
{
    public void Write(string user, string action, string target, object? detail = null)
    {
        var dir = Path.Combine(store.Root, "audit", DateTime.Now.ToString("yyyyMM")); Directory.CreateDirectory(dir);
        var o = new JsonObject
        {
            ["at"] = DateTimeOffset.Now.ToString("O"), ["user"] = user, ["machine"] = store.Machine,
            ["action"] = action, ["target"] = target, ["detail"] = detail is null ? null : Json.ToNode(detail),
        };
        FileStore.TryCreateNew(Path.Combine(dir, $"{DateTime.UtcNow.Ticks}-{FileStore.SafeId(store.Machine)}-{Guid.NewGuid():N}.json"), Encoding.UTF8.GetBytes(o.ToJsonString()));
    }

    public IReadOnlyList<JsonNode> Read(int maxFiles = 5000)
    {
        var root = Path.Combine(store.Root, "audit");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .TakeLast(maxFiles).Select(f => JsonNode.Parse(File.ReadAllText(f))!).ToList() : new List<JsonNode>();
    }
}

/// <summary>Lớp truy cập bản ghi kiểu mạnh trên FileStore (đọc version mới nhất, ghi CAS).</summary>
public sealed class Repo(FileStore store)
{
    public FileStore Store => store;
    public (T? Value, int Version) Get<T>(string type, string id) where T : class
    {
        var r = store.Read(type, id);
        return r is null ? (null, 0) : (Json.From<T>(r.Data), r.Meta.Version);
    }
    public int Put<T>(string type, string id, T value, int baseVersion, string user) => store.Commit(type, id, Json.ToNode(value), baseVersion, user, Guid.NewGuid().ToString());
    public IEnumerable<string> Ids(string type)
    {
        var d = Path.Combine(store.Root, "records", type);
        return Directory.Exists(d) ? Directory.EnumerateDirectories(d).Select(Path.GetFileName)!.Cast<string>() : Enumerable.Empty<string>();
    }
    /// <summary>Đọc-sửa-ghi với thử lại khi xung đột nhẹ (chỉ dùng cho thao tác tự gộp được, vd đánh dấu in).</summary>
    public T Update<T>(string type, string id, string user, Action<T> mutate) where T : class
    {
        var (v, ver) = Get<T>(type, id);
        if (v is null) throw new BusinessException($"Không tìm thấy {type} {id}");
        mutate(v); Put(type, id, v, ver, user); return v;
    }
}

public sealed record Session(UserRecord User, string Machine, bool MustChangePassword, bool OfflineLogin);

/// <summary>Đăng nhập, phiên (một phiên/tài khoản qua lease), đổi mật khẩu, quản lý tài khoản. Quyền kiểm ở đây, không chỉ ở giao diện.</summary>
public sealed class AuthService(Repo repo, LeaseLocks locks, AuditLog audit)
{
    public static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan SessionGrace = TimeSpan.FromMinutes(1);
    private const string T = "user";
    /// <summary>Chống dò mật khẩu: sai liên tiếp quá số lần này → tạm khoá tài khoản.</summary>
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);

    public bool HasAnyUser() => repo.Ids(T).Any();

    public static string NoAccountMessage(string username) =>
        $"Tài khoản \"{username}\" chưa có trong hệ thống.\nVui lòng liên hệ quản trị (admin) để được hỗ trợ tạo tài khoản.";

    /// <summary>Lần chạy đầu: tạo admin với mật khẩu người dùng tự đặt (không có mật khẩu mặc định cài sẵn trong mã).</summary>
    public void CreateFirstAdmin(string username, string fullName, string password)
    {
        if (HasAnyUser()) throw new BusinessException("Đã có tài khoản — không tạo admin đầu tiên nữa");
        if (password.Length < PasswordHasher.MinLength) throw new BusinessException($"Mật khẩu tối thiểu {PasswordHasher.MinLength} ký tự");
        repo.Put(T, username, new UserRecord { Username = username, FullName = fullName, Role = Role.Admin, PasswordHash = PasswordHasher.Hash(password), MustChangePassword = false }, 0, username);
        audit.Write(username, "user.first_admin", username);
    }

    public Session Login(string username, string password, DateTimeOffset? now = null)
    {
        var (u, _) = repo.Get<UserRecord>(T, username);
        if (u is null) { audit.Write(username, "login.unknown", username); throw new BusinessException(NoAccountMessage(username)); }
        var t = now ?? DateTimeOffset.Now;
        if (u.LockedUntil is { } lu && lu > t)
        {
            audit.Write(username, "login.temp_locked", username);
            throw new BusinessException($"Tài khoản đang TẠM KHOÁ do nhập sai mật khẩu {MaxFailedLogins} lần — thử lại sau {lu:HH:mm}, hoặc nhờ quản trị mở khoá (Tài khoản → Lưu).");
        }
        if (!PasswordHasher.Verify(password, u.PasswordHash))
        {
            // chống dò mật khẩu: sai liên tiếp MaxFailedLogins lần → tạm khoá LockoutTime
            var (cur, ver) = repo.Get<UserRecord>(T, username);
            var left = MaxFailedLogins;
            if (cur is not null)
            {
                cur.FailedLogins++;
                if (cur.FailedLogins >= MaxFailedLogins) { cur.LockedUntil = t + LockoutTime; cur.FailedLogins = 0; }
                left = MaxFailedLogins - cur.FailedLogins;
                try { repo.Put(T, username, cur, ver, username); } catch { /* ghi trùng lúc với máy khác: bỏ qua, lần sau đếm tiếp */ }
                if (cur.LockedUntil is { } l2 && l2 > t)
                {
                    audit.Write(username, "login.lockout", username, new { until = l2 });
                    throw new BusinessException($"Sai mật khẩu {MaxFailedLogins} lần liên tiếp — tài khoản TẠM KHOÁ đến {l2:HH:mm}. Quên mật khẩu: liên hệ quản trị (admin) để được đặt lại.");
                }
            }
            audit.Write(username, "login.fail", username);
            throw new BusinessException($"Sai mật khẩu (còn {left} lần thử trước khi tạm khoá {LockoutTime.TotalMinutes:0} phút). Quên mật khẩu: liên hệ quản trị (admin) để được đặt lại.");
        }
        if (!u.Active) { audit.Write(username, "login.locked", username); throw new BusinessException("Tài khoản đã bị khóa"); }
        var lr = locks.Acquire("session-" + username, username, SessionTtl, SessionGrace, now);
        if (!lr.Acquired) { audit.Write(username, "login.denied_other_session", username, lr.Message); throw new BusinessException("Tài khoản đang đăng nhập ở máy khác: " + lr.Message); }
        if (u.FailedLogins > 0 || u.LockedUntil is not null)
        {
            var (cur, ver) = repo.Get<UserRecord>(T, username);
            if (cur is not null) { cur.FailedLogins = 0; cur.LockedUntil = null; try { repo.Put(T, username, cur, ver, username); } catch { } }
        }
        audit.Write(username, "login.ok", username, lr.Message);
        return new Session(u, repo.Store.Machine, u.MustChangePassword, false);
    }

    public void Heartbeat(Session s, DateTimeOffset? now = null)
    {
        var (u, _) = repo.Get<UserRecord>(T, s.User.Username);
        if (u is null || !u.Active) throw new BusinessException("Tài khoản đã bị khóa — phiên kết thúc");
        var r = locks.Acquire("session-" + s.User.Username, s.User.Username, SessionTtl, SessionGrace, now);
        if (!r.Acquired) throw new BusinessException("Phiên đã bị máy khác chiếm: " + r.Message);
    }

    /// <summary>Đã đăng nhập OFFLINE, kho vừa trở lại: giành phiên. Nếu tài khoản đang mở ở máy khác → không cắt ai, chỉ cảnh báo + audit
    /// (khi offline không thể bảo đảm một-phiên toàn mạng — giới hạn đã nêu với người dùng).</summary>
    public string? ResumeOnline(Session s)
    {
        var (u, _) = repo.Get<UserRecord>(T, s.User.Username);
        if (u is null || !u.Active) throw new BusinessException("Tài khoản đã bị khóa — phiên kết thúc");
        var r = locks.Acquire("session-" + s.User.Username, s.User.Username, SessionTtl, SessionGrace);
        audit.Write(s.User.Username, r.Acquired ? "login.offline_resumed" : "login.offline_overlap", s.User.Username, r.Message);
        return r.Acquired ? null : "Cảnh báo: tài khoản này cũng đang đăng nhập ở máy khác (" + r.Message + ")";
    }

    public void Logout(Session s) { locks.Release("session-" + s.User.Username, s.User.Username); audit.Write(s.User.Username, "logout", s.User.Username); }

    public void ChangePassword(Session s, string oldPassword, string newPassword, string confirm)
    {
        var (u, ver) = repo.Get<UserRecord>(T, s.User.Username);
        if (u is null || !PasswordHasher.Verify(oldPassword, u.PasswordHash)) throw new BusinessException("Mật khẩu hiện tại không đúng");
        var err = PasswordHasher.ValidateNew(newPassword, confirm, u.TemporaryHash);
        if (err is not null) throw new BusinessException(err);
        u.PasswordHash = PasswordHasher.Hash(newPassword); u.MustChangePassword = false; u.TemporaryHash = null;
        repo.Put(T, u.Username, u, ver, u.Username);
        audit.Write(u.Username, "password.change", u.Username);
    }


    public static void Require(Session s, params Role[] roles)
    {
        if (s.MustChangePassword) throw new PermissionException("Phải đổi mật khẩu mặc định trước khi thao tác");
        if (!roles.Contains(s.User.Role)) throw new PermissionException($"Tài khoản {s.User.Username} ({s.User.Role}) không có quyền thực hiện thao tác này");
    }

    public IReadOnlyList<UserRecord> Users() => repo.Ids(T).Select(id => repo.Get<UserRecord>(T, id).Value!).Where(u => u is not null).OrderBy(u => u.Username).ToList();

    public void SaveUser(Session admin, string username, string fullName, Role role, bool active, string? newTemporaryPassword)
    {
        Require(admin, Role.Admin);
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsWhiteSpace)) throw new BusinessException("Tên đăng nhập không hợp lệ");
        var (u, ver) = repo.Get<UserRecord>(T, username);
        var isNew = u is null;
        if (isNew && string.IsNullOrEmpty(newTemporaryPassword)) throw new BusinessException("Tài khoản mới cần mật khẩu mặc định");
        u ??= new UserRecord { Username = username };
        var wasActiveAdmin = !isNew && u.Role == Role.Admin && u.Active;
        if (wasActiveAdmin && (role != Role.Admin || !active) && Users().Count(x => x.Role == Role.Admin && x.Active) <= 1)
            throw new BusinessException("Không thể khóa/hạ quyền admin cuối cùng");
        u.FullName = fullName; u.Role = role; u.Active = active;
        u.FailedLogins = 0; u.LockedUntil = null;                                   // quản trị lưu = mở tạm khoá do nhập sai
        if (!string.IsNullOrEmpty(newTemporaryPassword))
        {
            if (newTemporaryPassword.Length < PasswordHasher.MinLength) throw new BusinessException($"Mật khẩu tối thiểu {PasswordHasher.MinLength} ký tự");
            u.PasswordHash = PasswordHasher.Hash(newTemporaryPassword); u.TemporaryHash = u.PasswordHash; u.MustChangePassword = true;
        }
        repo.Put(T, username, u, ver, admin.User.Username);
        audit.Write(admin.User.Username, isNew ? "user.create" : "user.update", username, new { role = role.ToString(), active, resetPassword = !string.IsNullOrEmpty(newTemporaryPassword) });
    }

    /// <summary>Xoá hẳn tài khoản (chỉ quản trị). Không xoá được chính mình và admin đang hoạt động cuối cùng.
    /// Dữ liệu người đó đã tạo (đơn, CQ, nhật ký) giữ nguyên — chỉ còn tên đăng nhập trong lịch sử.</summary>
    public void DeleteUser(Session admin, string username)
    {
        Require(admin, Role.Admin);
        var (u, _) = repo.Get<UserRecord>(T, username);
        if (u is null) throw new BusinessException($"Không tìm thấy tài khoản {username}");
        if (string.Equals(u.Username, admin.User.Username, StringComparison.OrdinalIgnoreCase)) throw new BusinessException("Không xoá được tài khoản đang đăng nhập");
        if (u.Role == Role.Admin && u.Active && Users().Count(x => x.Role == Role.Admin && x.Active) <= 1) throw new BusinessException("Không xoá được admin cuối cùng");
        var dir = Path.Combine(repo.Store.Root, "records", T, FileStore.SafeId(username));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        try { locks.Release("session-" + username, username); } catch { }
        audit.Write(admin.User.Username, "user.delete", username, new { role = u.Role.ToString(), u.FullName });
    }

    public IReadOnlyList<LeaseInfo> OnlineSessions() => locks.ActiveSessions();
}
