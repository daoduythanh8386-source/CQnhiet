using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CqNl.Domain;
using CqNl.Infrastructure.Excel;
using CqNl.Infrastructure.Security;
using CqNl.Infrastructure.Storage;

namespace CqNl.Application;

public static class AppInfo
{
    public const string Name = "CQ Nhiệt Luyện";
    public const string Footer = "CQ NHIỆT LUYỆN  |  © Đào Duy Thanh. Mọi quyền được bảo lưu.";
    /// <summary>Kho dữ liệu chung (25/11/2026): thư mục DU LIEU NHIET do Synology Drive đồng bộ về TỪNG MÁY (thư mục nhóm TQM HƯNG YÊN
    /// trên NAS edoc.oristar.vn) → máy nào cũng chạy độc lập, ADMIN-PC tắt vẫn dùng được (cần internet để đồng bộ).
    /// %USERPROFILE% được mở rộng theo tài khoản Windows của từng máy.</summary>
    public const string DefaultStore = @"%USERPROFILE%\SynologyDrive\DU LIEU NHIET";
    /// <summary>Kho cũ trên ổ cứng ADMIN-PC (qua mạng LAN) — vẫn dùng được (cùng dữ liệu); máy có bản đồng bộ cục bộ tự chuyển sang bản đó.</summary>
    public const string LegacyStore = @"\\ADMIN-PC\Users\Admin\SynologyDrive\DU LIEU NHIET";
    public static string Expand(string? path) => Environment.ExpandEnvironmentVariables(path ?? "");
    /// <summary>Thư mục do phần mềm đồng bộ (Synology Drive / OneDrive / Dropbox) giữ trên chính máy này.</summary>
    public static bool IsSyncFolder(string path) => !path.StartsWith(@"\\") &&
        new[] { "SynologyDrive", "OneDrive", "Dropbox", "Google Drive" }.Any(k => path.Contains(k, StringComparison.OrdinalIgnoreCase));
    /// <summary>Máy cài trước 0.9.2 trỏ kho UNC về ADMIN-PC; máy đã có bản đồng bộ Synology Drive của CÙNG kho (có CQNL\store.json)
    /// → dùng bản trên máy mình (ADMIN-PC tắt vẫn chạy). Trả về đường dẫn nên dùng (null: giữ nguyên).</summary>
    public static string? PreferLocalSyncCopy(string configured)
    {
        if (!configured.StartsWith(@"\\")) return null;
        var local = Expand(DefaultStore);
        try { return File.Exists(Path.Combine(local, DataFolder, "store.json")) ? DefaultStore : null; } catch { return null; }
    }
    public const string DataFolder = "CQNL";
    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CQNhietLuyen");
}

/// <summary>Cấu hình riêng từng máy (%LocalAppData%) — chỉ đường dẫn kho; mọi cấu hình nghiệp vụ nằm trong kho chung.</summary>
public sealed class LocalConfig
{
    public string StorePath { get; set; } = AppInfo.DefaultStore;
    /// <summary>Thư mục lưu CQ (PDF + Excel) chọn trên app; bên trong mỗi tháng một thư mục con "MM-yyyy". Trống = Documents\CQ Nhiet Luyen.</summary>
    public string? PdfFolder { get; set; }
    /// <summary>Bản nhớ của "thư mục lưu CQ chung" (cài đặt chung) — để vẫn dùng đúng thư mục khi tạm OFFLINE.</summary>
    public string? SharedCqFolder { get; set; }
    /// <summary>Đường dẫn kho người cài đã nhập ở trang "Kho dữ liệu chung" của bộ cài (lần áp dụng gần nhất) — đổi ở bộ cài thì áp dụng lại.</summary>
    public string? InstallerStore { get; set; }
    /// <summary>Áp dụng đường dẫn kho do bộ cài ghi (store.txt cạnh file chạy) nếu khác lần trước. Trả true nếu đổi.</summary>
    public bool ApplyInstallerStore(string? installerValue)
    {
        var v = installerValue?.Trim();
        if (string.IsNullOrWhiteSpace(v) || v == InstallerStore) return false;
        StorePath = v; InstallerStore = v;
        return true;
    }
    public static string? ReadInstallerStore(string appDir)
    {
        try
        {
            var p = Path.Combine(appDir, "store.txt");
            return File.Exists(p) ? File.ReadAllLines(p).Select(l => l.Trim().TrimStart('\uFEFF')).FirstOrDefault(l => l.Length > 0) : null;
        }
        catch { return null; }
    }
    public string? Printer { get; set; }
    public bool PrintAfterExport { get; set; }
    public int Copies { get; set; } = 1;
    public string? LastApproverId { get; set; }
    public string? LastCheckerId { get; set; }
    public string ExportKind { get; set; } = "Signed";
    /// <summary>"Tạo file CQ" (Excel) kèm con dấu + chữ ký — bật mặc định, tắt được ở tab Xuất CQ (nhớ theo máy).</summary>
    public bool ExcelWithSign { get; set; } = true;
    /// <summary>Thư mục lưu CQ: riêng máy này (nếu đã chọn) → thư mục chung (quản trị đặt) → Documents\CQ Nhiet Luyen.</summary>
    public string PdfRoot => !string.IsNullOrWhiteSpace(PdfFolder) ? PdfFolder!
        : !string.IsNullOrWhiteSpace(SharedCqFolder) ? SharedCqFolder!
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CQ Nhiet Luyen");
    public string PdfRootSource => !string.IsNullOrWhiteSpace(PdfFolder) ? "riêng máy này" : !string.IsNullOrWhiteSpace(SharedCqFolder) ? "thư mục chung" : "mặc định trên máy này";
    public static LocalConfig Load(string localDir)
    {
        var p = Path.Combine(localDir, "config.json");
        try { return File.Exists(p) ? JsonSerializer.Deserialize<LocalConfig>(File.ReadAllText(p)) ?? new() : new(); } catch { return new(); }
    }
    public void Save(string localDir) { Directory.CreateDirectory(localDir); File.WriteAllText(Path.Combine(localDir, "config.json"), JsonSerializer.Serialize(this)); }
}

public sealed record ConnectionStatus(bool Online, string? Reason, DateTimeOffset? LastSyncOk, int Pending, int Conflicts, string? Message);

/// <summary>Bản sao cục bộ để LÀM VIỆC KHI OFFLINE — chỉ là vùng tạm, dựng lại được từ kho; không phải kho độc lập.</summary>
public sealed class LocalCache(string dir)
{
    private string D(string sub) { var d = Path.Combine(dir, sub); Directory.CreateDirectory(d); return d; }
    private static string F(string id) => FileStore.SafeId(id) + ".json";

    public sealed record Cached<T>(T Value, int Version, bool Draft);

    public void Put<T>(string type, string id, T value, int version, bool draft)
    {
        var p = Path.Combine(D(type), F(id)); var tmp = p + ".tmp";
        File.WriteAllText(tmp, new JsonObject { ["v"] = version, ["draft"] = draft, ["data"] = Json.ToNode(value) }.ToJsonString(), Encoding.UTF8);
        File.Move(tmp, p, overwrite: true);
    }
    public Cached<T>? Get<T>(string type, string id)
    {
        var p = Path.Combine(D(type), F(id));
        if (!File.Exists(p)) return null;
        var n = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8))!;
        return new Cached<T>(Json.From<T>(n["data"]!), (int)n["v"]!, (bool)n["draft"]!);
    }
    public IEnumerable<Cached<T>> All<T>(string type) => Directory.EnumerateFiles(D(type), "*.json").Select(f =>
    {
        var n = JsonNode.Parse(File.ReadAllText(f, Encoding.UTF8))!; return new Cached<T>(Json.From<T>(n["data"]!), (int)n["v"]!, (bool)n["draft"]!);
    });
    public string BlobPath(string name) => Path.Combine(D("files"), name);
}

/// <summary>
/// Điểm vào cho giao diện: biết đang ONLINE (kho UNC) hay OFFLINE, chọn đường ghi phù hợp, không bao giờ âm thầm đổi sang thư mục khác.
/// OFFLINE: đăng nhập bằng mật khẩu đã xác minh online trước đó trên máy này; chỉ sửa dữ liệu QC của đơn (lưu nháp vào outbox);
/// các thao tác cần kho (cấp số, tạo/duyệt/xóa CQ, ảnh, import, tài khoản, cài đặt) bị chặn với thông báo rõ.
/// </summary>
public sealed partial class Workspace
{
    public string StorePath { get; }
    public string DataRoot => Path.Combine(StorePath, AppInfo.DataFolder);
    public AppPaths Paths { get; }
    public string Machine { get; }
    public Outbox Outbox { get; }
    public LocalCache Cache { get; }
    public Session? Session { get; private set; }

    public FileStore? Store { get; private set; }
    public Repo? Repo { get; private set; }
    public AuditLog? Audit { get; private set; }
    public AuthService? Auth { get; private set; }
    public OrderService? Orders { get; private set; }
    public CqService? Cq { get; private set; }
    public SettingsService? Settings { get; private set; }
    public ImageService? Images { get; private set; }
    public LeaseLocks? Locks { get; private set; }

    public bool Online => Store is not null;
    /// <summary>Cho phép tạo kho MỚI trong thư mục đồng bộ (chỉ khi thật sự lập kho đầu tiên; mặc định tắt).</summary>
    public bool AllowNewSyncStore { get; set; }
    public string? OfflineReason { get; private set; }
    public DateTimeOffset? LastSyncOk { get; private set; }
    private readonly string _localDir;
    private readonly TimeSpan _probeTimeout;

    public Workspace(string localDir, string storePath, AppPaths paths, string? machine = null, TimeSpan? probeTimeout = null)
    {
        _localDir = localDir; StorePath = AppInfo.Expand(storePath).TrimEnd('\\'); Paths = paths; Machine = machine ?? Environment.MachineName;
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(4);
        Outbox = new Outbox(Path.Combine(localDir, "outbox"));
        Cache = new LocalCache(Path.Combine(localDir, "cache"));
    }

    // ---------------- kết nối ----------------
    /// <summary>Kiểm tra kho có truy cập được không (có thời hạn — đường UNC chết có thể treo hàng chục giây).</summary>
    public bool Probe()
    {
        string? reason = null;
        bool result = false;
        Func<bool> probe = () =>
        {
            try
            {
                if (!Directory.Exists(StorePath))
                {
                    reason = AppInfo.IsSyncFolder(StorePath)
                        ? "Máy này chưa có thư mục kho đồng bộ: " + StorePath + " — cài / mở Synology Drive, đồng bộ thư mục nhóm TQM HƯNG YÊN (có DU LIEU NHIET) rồi mở lại app."
                        : "Không truy cập được thư mục kho chung: " + StorePath;
                    return false;
                }
                // kho ĐỒNG BỘ mà chưa thấy dữ liệu (máy mới, Synology Drive chưa tải xong) → KHÔNG tạo kho rỗng mới (tránh tách thành 2 kho)
                if (AppInfo.IsSyncFolder(StorePath) && !File.Exists(Path.Combine(DataRoot, "store.json")) && !AllowNewSyncStore)
                {
                    reason = "Kho trên máy này CHƯA ĐỒNG BỘ XONG (chưa thấy " + Path.Combine(DataRoot, "store.json") + "). Mở Synology Drive, chờ đồng bộ xong thư mục DU LIEU NHIET rồi bấm Thử lại.";
                    return false;
                }
                Directory.CreateDirectory(DataRoot);
                var probeFile = Path.Combine(DataRoot, "staging", $"probe-{FileStore.SafeId(Machine)}-{Guid.NewGuid():N}.tmp");
                Directory.CreateDirectory(Path.GetDirectoryName(probeFile)!);
                File.WriteAllText(probeFile, "ok"); File.Delete(probeFile);                  // kiểm cả quyền GHI
                return true;
            }
            catch (Exception e) { reason = "Kho chung lỗi: " + e.Message; return false; }
        };
        // Luồng RIÊNG (không dùng thread pool): thời hạn chỉ phản ánh tốc độ truy cập kho, không bị trễ vì pool đang bận
        // (lỗi gặp ở build v18: kiểm thử song song làm Task.Run chậm > 3 s dù thư mục là ổ cục bộ). Đường UNC treo → luồng nền tự bỏ.
        var th = new Thread(() => { result = probe(); }) { IsBackground = true, Name = "cqnl-probe" };
        th.Start();
        var ok = th.Join(_probeTimeout) && result;
        lock (_probeLock)
        {
            if (!ok)
            {
                OfflineReason = reason ?? $"Kho chung không phản hồi sau {_probeTimeout.TotalSeconds:0} giây";
                Store = null;     // Online = (Store != null) → tắt trước, rồi mới bỏ các dịch vụ
                Repo = null; Audit = null; Locks = null; Auth = null; Orders = null; Cq = null; Settings = null; Images = null;
                return false;
            }
            OfflineReason = null;
            if (Store is null || Auth is null)
            {
                // Dựng đủ mọi dịch vụ rồi mới gán Store (cờ Online) — luồng khác không bao giờ thấy trạng thái "Online nhưng thiếu dịch vụ".
                var store = new FileStore(DataRoot, Machine); var repo = new Repo(store); var audit = new AuditLog(store);
                var orders = new OrderService(repo, audit);
                Repo = repo; Audit = audit; Orders = orders; Auth = new AuthService(repo, new LeaseLocks(store), audit);
                Cq = new CqService(repo, audit, new ReportNumberAllocator(store), orders, Paths);
                Settings = new SettingsService(repo, audit) { ResolveFolder = ResolveFolder, TokenizeFolder = FolderToken }; Images = new ImageService(repo, orders, Paths); Locks = new LeaseLocks(store);
                Store = store;
            }
        }
        // quản trị đã reset dữ liệu kho → bỏ bản sao cục bộ + nháp cũ của máy này (cất sang thư mục "truoc-reset-…")
        try { ApplyResetLocally(); } catch { }
        lock (_probeLock)
        {
            return Online;
        }
    }
    private readonly object _probeLock = new();

    public ConnectionStatus Status(string? message = null)
    {
        var items = Outbox.Items();
        return new ConnectionStatus(Online, OfflineReason, LastSyncOk, items.Count(i => i.State == "pending"), items.Count(i => i.State == "conflict"), message);
    }

    /// <summary>Gọi định kỳ (30 s): kiểm kho, giữ phiên, tự đồng bộ nháp khi kho trở lại.</summary>
    public ConnectionStatus Tick()
    {
        var was = Online;
        string? msg = null;
        if (!Probe()) return Status(was ? "Mất kết nối kho chung — chuyển sang OFFLINE, dữ liệu QC lưu nháp trên máy này" : null);
        if (Session is { } s)
        {
            if (s.OfflineLogin)
            {
                var r = Auth!.ResumeOnline(s);
                msg = r; Session = s with { OfflineLogin = false };
            }
            else
            {
                try { Auth!.Heartbeat(s); }
                catch (BusinessException e) { msg = e.Message; Session = null; return Status(msg); }
            }
        }
        if (Outbox.Items().Any(i => i.State == "pending"))
        {
            var r = Sync();
            msg = (msg is null ? "" : msg + " · ") + $"Đã đồng bộ {r.Pushed} nháp" + (r.Conflicts > 0 ? $", {r.Conflicts} xung đột cần xử lý" : "");
        }
        else if (!was) msg = (msg is null ? "" : msg + " · ") + "Đã kết nối lại kho chung";
        return Status(msg);
    }

    public SyncResult Sync()
    {
        var r = Outbox.Sync(() => Probe() ? Store : null);
        if (!r.Offline) LastSyncOk = DateTimeOffset.Now;
        if (Online && Session is { } s) Audit!.Write(s.User.Username, "sync", Machine, new { r.Pushed, r.Conflicts, r.Pending });
        return r;
    }

    public void RequireOnline(string what)
    {
        if (!Online) throw new BusinessException($"OFFLINE — không thể {what}. Thao tác này cần kho chung ({OfflineReason}).");
    }

    // ---------------- đăng nhập ----------------
    private string UserCacheFile(string u) { var d = Path.Combine(_localDir, "users"); Directory.CreateDirectory(d); return Path.Combine(d, FileStore.SafeId(u.ToLowerInvariant()) + ".json"); }

    public Session Login(string username, string password)
    {
        username = username.Trim();
        if (Probe())
        {
            Session = Auth!.Login(username, password);
            var (u, _) = Repo!.Get<UserRecord>("user", username);
            File.WriteAllText(UserCacheFile(username), JsonSerializer.Serialize(u, Json.Opt));   // để đăng nhập khi offline (chỉ hash)
            return Session;
        }
        var f = UserCacheFile(username);
        if (!File.Exists(f)) throw new BusinessException($"OFFLINE ({OfflineReason}). Tài khoản này chưa từng đăng nhập online trên máy này nên không xác minh được.");
        var cu = JsonSerializer.Deserialize<UserRecord>(File.ReadAllText(f), Json.Opt)!;
        if (!PasswordHasher.Verify(password, cu.PasswordHash)) throw new BusinessException("Sai tên đăng nhập hoặc mật khẩu");
        if (!cu.Active) throw new BusinessException("Tài khoản đã bị khóa");
        if (cu.MustChangePassword) throw new BusinessException("Tài khoản phải đổi mật khẩu mặc định — cần kết nối kho chung");
        return Session = new Session(cu, Machine, false, true);
    }

    public void Logout()
    {
        if (Session is { } s && Online && !s.OfflineLogin) { try { Auth!.Logout(s); } catch { } }
        Session = null;
    }

    public void RefreshSession(Session s) => Session = s;

    // ---------------- chỉ mục cục bộ: đọc TĂNG DẦN theo events/ (không đọc lại toàn bộ kho mỗi lần) ----------------
    public sealed record OrderRow(OrderRecord Order, int Version, bool Draft, IReadOnlyList<CqVersionRecord> Cqs);
    public sealed class IndexState
    {
        public Dictionary<string, List<string>> Seen { get; set; } = new();   // ngày (thư mục events) → tên file đã xử lý
        public DateTimeOffset LastFull { get; set; }
    }
    private static readonly string[] IndexedTypes = { OrderService.T, CqService.T, NoteService.T };
    public static TimeSpan FullRescanEvery { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>Số bản ghi phải đọc ở lần làm mới gần nhất (kiểm thử hiệu năng / hiển thị).</summary>
    public int LastRefreshReads { get; private set; }
    public bool LastRefreshWasFull { get; private set; }
    private readonly object _indexLock = new();

    private bool HasPendingDraft(string type, string id) => Outbox.Items().Any(i => i.Type == type && i.Id == id && i.State == "pending");

    /// <summary>Cập nhật bản sao cục bộ từ kho. Lần đầu / mỗi 15 phút / khi yêu cầu: quét toàn bộ nhưng CHỈ đọc bản ghi có version mới
    /// (so số version bằng liệt kê thư mục). Giữa các lần đó: chỉ đọc bản ghi có sự kiện mới trong events/ (xử lý theo tên file đã thấy,
    /// không theo giờ máy → không lỡ sự kiện của máy lệch đồng hồ). Nháp offline chưa đồng bộ không bị ghi đè.</summary>
    public void RefreshIndex(bool full = false)
    {
        if (!Online) return;
        lock (_indexLock)
        {
            var store = Store!; var reads = 0;
            var st = Cache.Get<IndexState>("meta", "index")?.Value;
            var drafts = Outbox.Items().Where(i => i.State == "pending").Select(i => i.Type + "\u0001" + i.Id).ToHashSet();
            void Load(string type, string id, int knownVersion, bool reloadSameVersion = false)
            {
                var r = store.Read(type, id); reads++;
                if (r is null) return;
                var key = type switch { OrderService.T => r.Data["Key"]?.ToString(), NoteService.T => r.Data["Id"]?.ToString(), _ => r.Data["OrderKey"] + "|v" + r.Data["Ver"] } ?? id;
                if (drafts.Contains(type + "\u0001" + key)) return;
                // kho đồng bộ: bản thắng của CÙNG version có thể đổi khi bản của máy khác về sau → đọc lại khi có sự kiện
                if (reloadSameVersion ? knownVersion > r.Meta.Version : knownVersion >= r.Meta.Version) return;
                switch (type)
                {
                    case OrderService.T: Cache.Put(type, key, Json.From<OrderRecord>(r.Data), r.Meta.Version, false); break;
                    case CqService.T: Cache.Put(type, key, Json.From<CqVersionRecord>(r.Data), r.Meta.Version, false); break;
                    default: Cache.Put(type, key, Json.From<NoteRecord>(r.Data), r.Meta.Version, false); break;
                }
            }
            var eventsRoot = Path.Combine(DataRoot, "events");
            Dictionary<string, List<string>> Snapshot(IEnumerable<string> days) => days.ToDictionary(d => d,
                d => Directory.EnumerateFiles(Path.Combine(eventsRoot, d), "*.json").Select(Path.GetFileName).Select(x => x!).ToList());
            var allDays = Directory.Exists(eventsRoot) ? Directory.EnumerateDirectories(eventsRoot).Select(Path.GetFileName).Select(x => x!).OrderBy(x => x).ToList() : new List<string>();
            var full2 = full || st is null || DateTimeOffset.Now - st.LastFull > FullRescanEvery;
            if (full2)
            {
                var keep = allDays.TakeLast(3).ToList();
                var snap = Snapshot(keep);                                       // ghi nhận sự kiện TRƯỚC khi quét → sự kiện mới hơn sẽ được đọc lần sau
                var cachedVer = IndexedTypes.ToDictionary(t => t, t => new Dictionary<string, int>());
                foreach (var c in Cache.All<OrderRecord>(OrderService.T)) cachedVer[OrderService.T][FileStore.SafeId(c.Value.Key)] = c.Version;
                foreach (var c in Cache.All<CqVersionRecord>(CqService.T)) cachedVer[CqService.T][FileStore.SafeId(c.Value.Id)] = c.Version;
                foreach (var c in Cache.All<NoteRecord>(NoteService.T)) cachedVer[NoteService.T][FileStore.SafeId(c.Value.Id)] = c.Version;
                foreach (var t in IndexedTypes)
                    foreach (var id in Repo!.Ids(t))
                    {
                        var latest = store.Versions(t, id).DefaultIfEmpty(0).Max();
                        var known = cachedVer[t].GetValueOrDefault(id);
                        if (latest > known) Load(t, id, known);
                    }
                st = new IndexState { Seen = snap, LastFull = DateTimeOffset.Now };
                LastRefreshWasFull = true;
            }
            else
            {
                var from = st!.Seen.Keys.DefaultIfEmpty("00000000").Min()!;
                foreach (var d in allDays.Where(d => string.CompareOrdinal(d, from) >= 0))
                {
                    var seen = st.Seen.TryGetValue(d, out var l) ? l.ToHashSet() : new HashSet<string>();
                    var files = Directory.EnumerateFiles(Path.Combine(eventsRoot, d), "*.json").Select(Path.GetFileName).Select(x => x!).Where(f => !seen.Contains(f)).OrderBy(f => f).ToList();
                    foreach (var f in files)
                    {
                        try
                        {
                            var ev = JsonNode.Parse(File.ReadAllText(Path.Combine(eventsRoot, d, f)))!;
                            var (t, id) = ((string?)ev["type"], (string?)ev["id"]);
                            if (t is not null && id is not null && IndexedTypes.Contains(t))
                            {
                                var known = t switch
                                {
                                    OrderService.T => Cache.Get<OrderRecord>(t, id)?.Version ?? 0,
                                    CqService.T => Cache.Get<CqVersionRecord>(t, id)?.Version ?? 0,
                                    _ => Cache.Get<NoteRecord>(t, id)?.Version ?? 0,
                                };
                                Load(t, FileStore.SafeId(id), known, reloadSameVersion: true);
                                if (t == OrderService.T && EventMachine(f) != FileStore.SafeId(Machine)) CheckLostWrites(t, FileStore.SafeId(id));   // màn Xung đột hiển thị đơn
                            }
                        }
                        catch (Exception e) when (e is IOException or JsonException) { continue; }   // file sự kiện đang ghi dở → lần sau
                        seen.Add(f);
                    }
                    st.Seen[d] = seen.ToList();
                }
                foreach (var old in st.Seen.Keys.OrderBy(x => x).SkipLast(3).ToList()) st.Seen.Remove(old);
                LastRefreshWasFull = false;
            }
            Cache.Put("meta", "index", st, 0, false);
            LastRefreshReads = reads;
        }
    }

    /// <summary>Tên máy trong tên file sự kiện "&lt;ticks&gt;-&lt;máy&gt;-&lt;guid&gt;.json" (tên máy có thể chứa dấu "-").</summary>
    internal static string EventMachine(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file); int a = n.IndexOf('-'), b = n.LastIndexOf('-');
        return a >= 0 && b > a ? n[(a + 1)..b] : "";
    }

    /// <summary>Kho đồng bộ qua Synology Drive: lần ghi của MÁY NÀY bị bản của máy khác (ghi cùng lúc, chưa đồng bộ kịp) thay thế →
    /// đưa vào danh sách XUNG ĐỘT (giống nháp offline xung đột) để người dùng xem và chọn giữ bản nào. Mỗi lần ghi chỉ báo 1 lần.</summary>
    public int CheckLostWrites(string type, string id)
    {
        if (Store is not { } store || type != OrderService.T) return 0;
        var acked = Cache.Get<List<string>>("meta", "lost-acked")?.Value ?? new List<string>();
        var n = 0;
        IReadOnlyList<LostWrite> lost;
        try { lost = store.LostWrites(type, id, Machine); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        foreach (var l in lost)
        {
            if (acked.Contains(l.Mine.Meta.OpId)) continue;
            var key = type switch { OrderService.T => l.Mine.Data["Key"]?.ToString(), NoteService.T => l.Mine.Data["Id"]?.ToString(), _ => null } ?? id;
            var note = $"Đồng bộ: bản ghi lúc {l.Mine.Meta.At.ToLocalTime():dd/MM HH:mm:ss} trên máy này bị thay bởi bản của máy {l.Head.Meta.Machine} ({l.Head.Meta.User}, {l.Head.Meta.At.ToLocalTime():dd/MM HH:mm:ss})";
            var cf = store.SaveConflict(type, key, l.Mine.Data, note);
            Outbox.AddConflict(type, key, l.Mine.Data, l.Head.Meta.Version, l.Mine.Meta.User, cf);
            acked.Add(l.Mine.Meta.OpId); n++;
        }
        if (n > 0) Cache.Put("meta", "lost-acked", acked.TakeLast(2000).ToList(), 0, false);
        return n;
    }

    private Dictionary<string, List<CqVersionRecord>> CachedCqs() => Cache.All<CqVersionRecord>(CqService.T).Select(c => c.Value)
        .GroupBy(c => c.OrderKey).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ver).ToList());

    public IReadOnlyList<OrderRow> ListOrders(bool fullRefresh = false)
    {
        RefreshIndex(fullRefresh);
        var drafts = Outbox.Items().Where(i => i.Type == OrderService.T && i.State == "pending").Select(i => i.Id).ToHashSet();
        var cqs = CachedCqs();
        return Cache.All<OrderRecord>(OrderService.T).Select(c => new OrderRow(c.Value, c.Version, c.Draft || drafts.Contains(c.Value.Key),
            cqs.GetValueOrDefault(c.Value.Key) ?? new List<CqVersionRecord>())).ToList();
    }

    /// <summary>Đọc một đơn MỚI NHẤT từ kho (online) kèm CQ của đơn; offline → bản sao cục bộ.</summary>
    public OrderRow? GetOrder(string key)
    {
        var draft = HasPendingDraft(OrderService.T, key);
        if (Online && !draft)
        {
            var (o, v) = Orders!.Get(key);
            if (o is null) return null;
            Cache.Put(OrderService.T, key, o, v, false);
            var cqs = Cq!.ForOrder(key);
            foreach (var c in cqs) { var (_, cv) = Repo!.Get<CqVersionRecord>(CqService.T, c.Id); Cache.Put(CqService.T, c.Id, c, cv, false); }
            return new OrderRow(o, v, false, cqs);
        }
        var cached = Cache.Get<OrderRecord>(OrderService.T, key);
        if (cached is null) return null;
        return new OrderRow(cached.Value, cached.Version, cached.Draft || draft, CachedCqs().GetValueOrDefault(key) ?? new List<CqVersionRecord>());
    }

    /// <summary>Sửa phần QC. Online: ghi CAS thẳng vào kho. Offline (hoặc đơn đang có nháp chưa đồng bộ): ghi nháp vào outbox
    /// với version gốc — khi đồng bộ, nếu đơn đã bị máy khác sửa thì GIỮ CẢ HAI BẢN (xung đột), không ghi đè.
    /// blobs: ảnh kèm theo (đẩy lên kho trước bản ghi).</summary>
    public OrderRow EditQc(string key, int baseVersion, Action<QcData> edit, string what, IReadOnlyList<(byte[] Bytes, string Ext)>? blobs = null)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        var hasDraft = HasPendingDraft(OrderService.T, key);
        if (Online && !hasDraft)
        {
            foreach (var (b, e) in blobs ?? Array.Empty<(byte[], string)>()) Store!.PutBlob(b, e);
            Orders!.EditQc(s, key, baseVersion, edit, what);
            return GetOrder(key)!;
        }
        var c = Cache.Get<OrderRecord>(OrderService.T, key) ?? throw new BusinessException("Đơn chưa có bản sao trên máy này — cần kết nối kho chung");
        if (c.Version != baseVersion) throw new ConflictException("Đơn đã thay đổi — tải lại trước khi sửa");
        edit(c.Value.Qc);
        var staged = (blobs ?? Array.Empty<(byte[], string)>()).Select(x => { CacheBlob(x.Item1, x.Item2); return Outbox.StageBlob(x.Item1, x.Item2); }).ToList();
        Outbox.AddOrReplace(OrderService.T, key, Json.ToNode(c.Value), c.Version, s.User.Username, staged.Count == 0 ? null : staged);
        Cache.Put(OrderService.T, key, c.Value, c.Version, true);
        if (Online) Sync();
        return GetOrder(key)!;
    }

    private void CacheBlob(byte[] b, string ext) { try { File.WriteAllBytes(Cache.BlobPath(FileStore.Sha256Hex(b) + "." + ext.TrimStart('.')), b); } catch { } }

    // ---------------- khóa đơn (tư vấn; CAS version mới là chốt chặn cuối) ----------------
    public static readonly TimeSpan OrderLockTtl = TimeSpan.FromMinutes(5);
    /// <summary>null = giữ được khóa (hoặc offline: không kiểm được, trả về cảnh báo riêng); khác null = người khác đang sửa.</summary>
    public string? LockOrder(string key)
    {
        if (!Online || Session is null || Session.User.Role is not (Role.Admin or Role.Qc)) return null;   // chỉ người được sửa mới giữ khóa
        var r = Locks!.Acquire("order-" + key, Session.User.Username, OrderLockTtl, TimeSpan.FromMinutes(2));
        if (r.Acquired && r.Message is not null) Audit!.Write(Session.User.Username, "lock.takeover", key, r.Message);
        return r.Acquired ? null : r.Message;
    }
    public void ReleaseOrder(string key) { if (Online && Session is not null) try { Locks!.Release("order-" + key, Session.User.Username); } catch { } }

    // ---------------- ảnh (đọc qua bản đệm cục bộ / hàng đợi để xem được cả khi offline) ----------------
    public byte[]? ImageBytes(string sha, string ext)
    {
        var p = Cache.BlobPath(sha + "." + ext);
        if (File.Exists(p)) { var b = File.ReadAllBytes(p); if (FileStore.Sha256Hex(b) == sha) return b; }
        var q = Outbox.BlobBytes(sha, ext);
        if (q is not null) return q;
        if (!Online) return null;
        var bytes = Store!.GetBlob(sha, ext);
        try { File.WriteAllBytes(p, bytes); } catch { }
        return bytes;
    }

    /// <summary>Thêm ảnh: xử lý trên máy (chuẩn hóa + OCR gợi ý). Online → lưu kho; OFFLINE → ảnh + bản ghi vào hàng đợi, tự đẩy khi có kết nối.</summary>
    public InspectionImage AddImage(string key, int baseVersion, byte[] bytes, string ext, string kind)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        var (im, blobs) = ImageService.Process(bytes, ext, kind, Paths);
        ImageService.Stamp(im, s.User.Username);
        EditQc(key, baseVersion, q => { ImageService.Attach(q, im); ApplyAutoSamples(q); }, $"image.add {kind} {im.BlobSha[..12]}", blobs);
        return im;
    }

    public void CropImage(string key, int baseVersion, string imageId, System.Drawing.Rectangle crop)
    {
        var row = GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn");
        var im = row.Order.Qc.Images.FirstOrDefault(i => i.Id == imageId) ?? throw new BusinessException("Không tìm thấy ảnh");
        var orig = ImageBytes(im.BlobSha, im.Ext) ?? throw new BusinessException("Ảnh gốc chưa có trên máy này — cần kết nối kho chung");
        var jpg = ImageService.CropBytes(orig, im, crop, Paths);
        var sha = FileStore.Sha256Hex(jpg);
        EditQc(key, baseVersion, q => { var x = q.Images.First(i => i.Id == imageId); x.NormalizedSha = sha; x.Crop = new[] { crop.X, crop.Y, crop.Width, crop.Height }; x.NeedsManualCrop = false; },
            $"image.crop {imageId}", new[] { (jpg, "jpg") });
    }

    // ---------------- lưu ý đơn hàng (QC + Kinh doanh) ----------------
    public sealed record NoteView(NoteRecord Note, bool FromOtherOrder, bool Pending);

    /// <summary>Ghi lưu ý: bản ghi MỚI (không sửa chung) → không xung đột giữa nhiều người. Offline → hàng đợi.</summary>
    public NoteRecord AddNote(string orderKey, string text, bool forCustomer, IReadOnlyList<(byte[] Bytes, string Ext, string Name)> images)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc, Role.Sales);
        text = (text ?? "").Trim();
        if (text.Length == 0 && images.Count == 0) throw new BusinessException("Nhập nội dung lưu ý hoặc đính kèm ảnh");
        if (text.Length > 4000) throw new BusinessException("Lưu ý tối đa 4000 ký tự");
        if (images.Count > 10) throw new BusinessException("Tối đa 10 ảnh cho một lưu ý");
        var row = GetOrder(orderKey) ?? throw new BusinessException("Không tìm thấy đơn");
        var note = new NoteRecord
        {
            OrderKey = orderKey, Customer = row.Order.Qc.CustomerOverride ?? row.Order.Erp.GetValueOrDefault("Customer") ?? "", Text = text,
            Author = s.User.Username, AuthorName = s.User.FullName, AuthorRole = s.User.Role, At = DateTimeOffset.Now, Machine = Machine, ForCustomer = forCustomer,
        };
        var blobs = new List<(byte[], string)>();
        foreach (var (b, e, n) in images)
        {
            if (b.Length > 25 * 1024 * 1024) throw new BusinessException($"Ảnh {n} quá lớn (> 25 MB)");
            try { CqNl.Infrastructure.Imaging.CqImages.Size(b); } catch { throw new BusinessException($"{n} không phải file ảnh hợp lệ"); }
            var ext = e.TrimStart('.').ToLowerInvariant();
            note.Images.Add(new NoteImage { Sha = FileStore.Sha256Hex(b), Ext = ext, Name = n }); blobs.Add((b, ext));
        }
        foreach (var (b, e) in blobs) CacheBlob(b, e);
        if (Online)
        {
            foreach (var (b, e) in blobs) Store!.PutBlob(b, e);
            var v = Repo!.Put(NoteService.T, note.Id, note, 0, s.User.Username);
            Audit!.Write(s.User.Username, "note.add", orderKey, new { note.Id, note.ForCustomer, images = note.Images.Count });
            Cache.Put(NoteService.T, note.Id, note, v, false);
        }
        else
        {
            var staged = blobs.Select(x => Outbox.StageBlob(x.Item1, x.Item2)).ToList();
            Outbox.Add(NoteService.T, note.Id, Json.ToNode(note), 0, s.User.Username, staged.Count == 0 ? null : staged);
            Cache.Put(NoteService.T, note.Id, note, 0, true);
        }
        return note;
    }

    /// <summary>Lưu ý của đơn + lưu ý "nhắc ở đơn sau" của CÙNG khách hàng từ các đơn khác (mới nhất trước).</summary>
    public IReadOnlyList<NoteView> NotesFor(string orderKey, bool includeHidden = false)
    {
        if (Online) RefreshIndex();
        var row = Cache.Get<OrderRecord>(OrderService.T, orderKey)?.Value;
        var cust = Grade.Normalize(row?.Qc.CustomerOverride ?? row?.Erp.GetValueOrDefault("Customer"));
        var pend = Outbox.Items().Where(i => i.Type == NoteService.T && i.State == "pending").Select(i => i.Id).ToHashSet();
        return Cache.All<NoteRecord>(NoteService.T).Select(c => c.Value)
            .Where(n => includeHidden || !n.Hidden)
            .Where(n => n.OrderKey == orderKey || (n.ForCustomer && cust.Length > 0 && Grade.Normalize(n.Customer) == cust))
            .OrderByDescending(n => n.At).Select(n => new NoteView(n, n.OrderKey != orderKey, pend.Contains(n.Id))).ToList();
    }

    public Dictionary<string, int> NoteCounts() => Cache.All<NoteRecord>(NoteService.T).Select(c => c.Value).Where(n => !n.Hidden)
        .GroupBy(n => n.OrderKey).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Ẩn lưu ý (người viết hoặc admin) — giữ nguyên lịch sử trong kho, có nhật ký.</summary>
    public void HideNote(string noteId)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        RequireOnline("ẩn lưu ý");
        var (n, v) = Repo!.Get<NoteRecord>(NoteService.T, noteId);
        if (n is null) throw new BusinessException("Không tìm thấy lưu ý (có thể chưa đồng bộ)");
        if (n.Author != s.User.Username && s.User.Role != Role.Admin) throw new PermissionException("Chỉ người viết hoặc quản trị được ẩn lưu ý");
        n.Hidden = true; n.HiddenBy = s.User.Username; n.HiddenAt = DateTimeOffset.Now;
        var nv = Repo.Put(NoteService.T, noteId, n, v, s.User.Username);
        Cache.Put(NoteService.T, noteId, n, nv, false);
        Audit!.Write(s.User.Username, "note.hide", n.OrderKey, new { noteId });
    }

    public OrderRow ConfirmImage(string key, int baseVersion, string imageId, string value)
    {
        var v = ImageService.NormalizeValue(value);
        return EditQc(key, baseVersion, q => { ImageService.ApplyConfirm(q, imageId, v, Session!.User.Username); ApplyAutoSamples(q); }, $"image.confirm {imageId} = {v}");
    }

    /// <summary>Lưu kết quả đo. Đổi điểm đo sau khi đã xác nhận → BỎ xác nhận (phải xác nhận lại trước khi tạo CQ).</summary>
    public OrderRow SaveHardness(string key, int baseVersion, IReadOnlyList<string> samples, Action<HardnessData> other)
    {
        var clean = samples.Select(x => (x ?? "").Trim()).ToList();
        while (clean.Count > 0 && clean[^1].Length == 0) clean.RemoveAt(clean.Count - 1);
        if (clean.Count > CqNl.Domain.HardnessEvaluator.MaxSamples) throw new BusinessException("Tối đa 6 mẫu");
        foreach (var x in clean) try { CqNl.Domain.HardnessEvaluator.ParsePoints(x); } catch (FormatException e) { throw new BusinessException(e.Message); }
        return EditQc(key, baseVersion, q =>
        {
            if (!q.Hardness.Samples.SequenceEqual(clean)) { q.Hardness.ConfirmedAt = null; q.Hardness.ConfirmedBy = null; }
            q.Hardness.Samples = clean; q.Hardness.SamplesAuto = clean.Count > 0 && clean.SequenceEqual(DerivedSamples(q)); other(q.Hardness);
        }, "hardness.save");
    }

    public (OrderRow Row, CqNl.Domain.Evaluation Ev) ConfirmHardness(string key, int baseVersion)
    {
        var row = GetOrder(key) ?? throw new BusinessException("Không tìm thấy đơn");
        if (row.Version != baseVersion) throw new ConflictException("Đơn đã thay đổi — tải lại");
        var ev = OrderService.CheckConfirmable(row.Order, null);
        var r = EditQc(key, baseVersion, q => { q.Hardness.ConfirmedBy = Session!.User.Username; q.Hardness.ConfirmedAt = DateTimeOffset.Now; }, "hardness.confirm " + ev.Status);
        if (Online) Audit!.Write(Session!.User.Username, "hardness.confirm", key, new { status = ev.Status.ToString(), ev.Low, ev.High, offlineDraft = r.Draft });
        return (r, ev);
    }

    /// <summary>Tạo CQ: cần kho chung và đơn KHÔNG còn nháp chưa đồng bộ (không phát hành CQ từ dữ liệu chưa đối soát).</summary>
    public CqVersionRecord CreateCq(string key, string? reportNo = null)
    {
        RequireOnline("tạo CQ (cấp số báo cáo)");
        if (Outbox.Items().Any(i => i.Type == OrderService.T && i.Id == key))
            throw new BusinessException("Đơn còn nháp offline chưa đồng bộ hoặc đang xung đột — đồng bộ/xử lý xung đột trước khi tạo CQ");
        return Cq!.Create(Session!, key, reportNo);
    }

    // ---------------- file CQ ----------------
    /// <summary>Lấy file CQ (xlsx nháp / xlsx đã duyệt / pdf) từ kho ra thư mục chỉ định, tên dễ đọc. Kiểm hash khi đọc blob.</summary>
    public string ExportCq(CqVersionRecord c, string kind, string destDir)
    {
        RequireOnline("mở file CQ");
        var (sha, ext) = kind switch
        {
            "pdf" => (c.PdfSha ?? throw new BusinessException("CQ chưa duyệt — chưa có PDF"), "pdf"),
            _ when kind.StartsWith("xlsx:") => (c.Xlsxs.GetValueOrDefault(kind[5..]) ?? throw new BusinessException("CQ chưa có file Excel kiểu này"), "xlsx"),
            _ when kind.StartsWith("pdf:") => (c.Pdfs.GetValueOrDefault(kind[4..]) ?? throw new BusinessException("CQ chưa xuất bản in kiểu này"), "pdf"),
            "xlsx-approved" => (c.ApprovedXlsxSha ?? throw new BusinessException("CQ chưa duyệt"), "xlsx"),
            _ => (c.XlsxSha, "xlsx"),
        };
        Directory.CreateDirectory(destDir);
        var suffix = kind switch { "xlsx" => "_nhap", "pdf:StampOnly" or "xlsx:StampOnly" => "_co-dau-chua-ky", "pdf:Blank" or "xlsx:Blank" => "_ky-tay", "pdf:Signed" or "xlsx:Signed" => "_da-ky", _ => "" };
        var name = $"CQ_{c.ReportNo.Replace('/', '-')}_{FileStore.SafeId(c.OrderKey.Split('|')[0]).Replace('_', '-')}_v{c.Ver}{suffix}.{ext}";
        var p = Path.Combine(destDir, name);
        File.WriteAllBytes(p, Store!.GetBlob(sha, ext));
        return p;
    }

    // ---------------- xung đột ----------------
    public sealed record ConflictView(OutboxItem Item, OrderRecord? Mine, OrderRecord? Latest, int LatestVersion);

    public IReadOnlyList<ConflictView> Conflicts()
    {
        return Outbox.Items().Where(i => i.State == "conflict").Select(i =>
        {
            OrderRecord? latest = null; var lv = 0;
            if (Online && i.Type == OrderService.T) (latest, lv) = Orders!.Get(i.Id);
            return new ConflictView(i, i.Type == OrderService.T ? Json.From<OrderRecord>(i.Data) : null, latest, lv);
        }).ToList();
    }

    /// <summary>Người dùng chọn: giữ bản kho (bỏ nháp — nháp vẫn còn trong conflicts/ của kho) hoặc ghi bản của mình thành version MỚI
    /// (trên bản mới nhất, có audit). Không có lựa chọn tự động.</summary>
    public void ResolveConflict(string opId, bool keepMine)
    {
        var s = Session ?? throw new PermissionException("Chưa đăng nhập");
        AuthService.Require(s, Role.Admin, Role.Qc);
        RequireOnline("xử lý xung đột");
        var it = Outbox.Items().FirstOrDefault(i => i.OpId == opId) ?? throw new BusinessException("Không tìm thấy mục xung đột");
        if (keepMine)
        {
            var latest = Store!.Versions(it.Type, it.Id).DefaultIfEmpty(0).Max();
            Store.Commit(it.Type, it.Id, it.Data, latest, s.User.Username, Guid.NewGuid().ToString());
        }
        Outbox.Remove(opId);
        Audit!.Write(s.User.Username, keepMine ? "conflict.keep_mine" : "conflict.keep_store", it.Id, new { opId, it.ConflictFile });
        if (it.Type == OrderService.T) { var (o, v) = Orders!.Get(it.Id); if (o is not null) Cache.Put(OrderService.T, it.Id, o, v, false); }
    }

    // ---------------- bảng mác ----------------
    private SteelGradeTable? _steel; private string? _steelSha;
    public SteelGradeTable? SteelTable()
    {
        var local = Cache.BlobPath("steel_grades.xlsx");
        if (Online)
        {
            var sha = Settings!.Get().Value.SteelTableSha;
            if (sha is null) return null;
            if (sha != _steelSha || _steel is null)
            {
                File.WriteAllBytes(local, Store!.GetBlob(sha, "xlsx"));
                _steel = SteelGradeReader.Read(local); _steelSha = sha;
            }
            return _steel;
        }
        return _steel ??= File.Exists(local) ? SteelGradeReader.Read(local) : null;
    }

    /// <summary>Ánh xạ cột "ĐÁNH DẤU" của bảng mác sang template (OQ-05, mặc định tạm: 1=khuôn, 2=gió, 3=carbon).</summary>
    public static string? TemplateFromMark(int? mark) => mark switch { 1 => "khuon", 2 => "gio", 3 => "carbon", _ => null };

    /// <summary>Nhiệt độ tôi theo bảng mác: số trần (vd 650) hiển thị "650℃" như hình vẽ template; giữ nguyên nếu đã có đơn vị.</summary>
    public static List<string> HeatTexts(SteelGrade g) => g.Heats.Where(h => !string.IsNullOrWhiteSpace(h))
        .Select(h => decimal.TryParse(h!.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _) ? h.Trim() + "℃" : h.Trim()).ToList();
    public static List<string> RamTexts(SteelGrade g) => g.Rams.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.Trim()).ToList();
}
