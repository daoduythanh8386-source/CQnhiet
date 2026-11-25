using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CqNl.Infrastructure.Storage;

public sealed record LeaseInfo(string User, string Machine, DateTimeOffset At, DateTimeOffset Expires, bool Released);
public sealed record LockResult(bool Acquired, string? Message);

/// <summary>
/// Khóa lease theo THẾ HỆ: giành khóa = tạo locks/&lt;key&gt;/&lt;n+1&gt;.json (CreateNew — thư mục dùng chung chỉ 1 máy thắng). Chỉ máy giữ khóa
/// sửa file của mình (gia hạn / nhả); thu hồi = tạo thế hệ mới → an toàn cả khi kho là thư mục đồng bộ (Synology Drive): hai máy cùng tạo
/// một thế hệ khi chưa đồng bộ kịp → Synology đổi tên một bản ("Conflict", bị bỏ qua), mọi máy thấy cùng một người giữ.
/// Đọc được cả tên &lt;n&gt;~&lt;máy&gt;.json; cùng thế hệ nhiều bản → bản tạo sớm nhất giữ khóa. Giới hạn: kho đồng bộ thì khóa có độ trễ bằng độ trễ đồng bộ (đã nêu trong thiết kế).
/// </summary>
public sealed class LeaseLocks(FileStore store)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    private string Dir(string key) { var d = Path.Combine(store.Root, "locks", FileStore.SafeId(key)); Directory.CreateDirectory(d); return d; }

    private static int GenOf(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file); var k = n.IndexOf('~');
        return int.TryParse(k < 0 ? n : n[..k], out var g) ? g : -1;
    }

    private static LeaseInfo ReadLease(string file)
    {
        // File khóa vừa được máy khác tạo (CreateNew) có thể CHƯA ghi xong nội dung khi ta đọc → thử lại; vẫn dở thì coi là ĐANG BỊ GIỮ
        // (hạn tính từ lúc ghi file + TTL mặc định) → không ai chiếm được khóa đang được tạo.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var txt = File.ReadAllText(file);
                if (txt.Length > 0) return JsonSerializer.Deserialize<LeaseInfo>(txt)!;
            }
            catch (Exception e) when (e is JsonException or IOException) { }
            Thread.Sleep(20);
        }
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        return new LeaseInfo("?", "?", written, written + DefaultTtl, false);
    }

    /// <summary>Thế hệ mới nhất và người giữ (cùng thế hệ nhiều máy → bản tạo sớm nhất, rồi theo tên máy).</summary>
    public (int Gen, LeaseInfo? Info) Latest(string key) { var (g, i, _) = LatestFile(key); return (g, i); }

    private (int Gen, LeaseInfo? Info, string? File) LatestFile(string key)
    {
        var files = Directory.EnumerateFiles(Dir(key), "*.json").Select(f => (G: GenOf(f), F: f)).Where(x => x.G > 0).ToList();
        if (files.Count == 0) return (0, null, null);
        var gen = files.Max(x => x.G);
        var best = files.Where(x => x.G == gen).Select(x => (x.F, I: ReadLease(x.F)))
            .OrderBy(x => x.I.At.UtcTicks).ThenBy(x => x.I.Machine, StringComparer.Ordinal).First();
        return (gen, best.I, best.F);
    }

    public LockResult Acquire(string key, string user, TimeSpan ttl, TimeSpan grace, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.Now;
        var (gen, cur, file) = LatestFile(key);
        string? note = null;
        if (cur is not null && !cur.Released)
        {
            if (cur.User == user && cur.Machine == store.Machine && file is not null)
            {
                File.WriteAllText(file, JsonSerializer.Serialize(cur with { Expires = t + ttl }));
                return new LockResult(true, null);
            }
            if (t <= cur.Expires + grace) return new LockResult(false, $"Đang sửa bởi {cur.User} trên máy {cur.Machine}");
            note = $"Thu hồi khóa quá hạn của {cur.User}@{cur.Machine}";
        }
        var info = new LeaseInfo(user, store.Machine, t, t + ttl, false);
        var mine = Path.Combine(Dir(key), $"{gen + 1}.json");                    // tạo-không-ghi-đè: thư mục dùng chung chỉ 1 máy thắng
        if (!FileStore.TryCreateNew(mine, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info)))) return new LockResult(false, "Máy khác vừa giành khóa");
        // máy khác cũng vừa tạo thế hệ này (tên file khác) → chỉ bản THẮNG giữ khóa; thua thì gỡ file của chính mình
        var (g2, _, f2) = LatestFile(key);
        if (g2 != gen + 1 || !string.Equals(f2, mine, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(mine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new LockResult(false, "Máy khác vừa giành khóa");
        }
        return new LockResult(true, note);
    }

    public void Release(string key, string user)
    {
        var (_, cur, file) = LatestFile(key);
        if (cur is not null && file is not null && cur.User == user && cur.Machine == store.Machine && !cur.Released)
            File.WriteAllText(file, JsonSerializer.Serialize(cur with { Released = true }));
    }

    /// <summary>Thu hồi khóa bất kể ai giữ: tạo thế hệ MỚI đã nhả (không sửa file của máy khác).</summary>
    public void ForceRelease(string key)
    {
        var (gen, cur, _) = LatestFile(key);
        if (cur is not null && !cur.Released)
            FileStore.TryCreateNew(Path.Combine(Dir(key), $"{gen + 1}.json"),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new LeaseInfo("?", store.Machine, DateTimeOffset.Now, DateTimeOffset.Now, true))));
    }

    /// <summary>Danh sách phiên đang online (key bắt đầu "session-").</summary>
    public IReadOnlyList<LeaseInfo> ActiveSessions(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.Now;
        var root = Path.Combine(store.Root, "locks");
        return Directory.EnumerateDirectories(root, "session-*").Select(d => Latest(Path.GetFileName(d)).Info)
            .Where(i => i is not null && !i.Released && i.Expires >= t).Select(i => i!).ToList();
    }
}

public sealed record ReportClaim(string Token, string CqRef, string Machine, DateTimeOffset At);

/// <summary>Số báo cáo NNN/MM: giữ số bằng file tạo-không-ghi-đè counters/reportno/yyyy-MM/NNN.json (kèm mã giữ số ngẫu nhiên).
/// Không dùng lại số đã cấp. Kho đồng bộ (Synology Drive trên từng máy): hai máy có thể cùng tạo NNN.json trong lúc chưa đồng bộ kịp —
/// phần mềm đồng bộ giữ MỘT bản NNN.json cho mọi máy (bản kia bị đổi tên kiểu "Conflict"); CQ có mã giữ số không khớp bản còn lại
/// bị chặn khi duyệt và phải đổi số → không bao giờ phát hành 2 CQ trùng số.</summary>
public sealed class ReportNumberAllocator(FileStore store)
{
    private string MonthDir(int year, int month) { var d = Path.Combine(store.Root, "counters", "reportno", $"{year:0000}-{month:00}"); Directory.CreateDirectory(d); return d; }
    /// <summary>Số trong tên file NNN.json (bản đổi tên do xung đột đồng bộ như "012 (Conflict…).json" vẫn tính là ĐÃ DÙNG).</summary>
    private static int NumOf(string f) { var n = Path.GetFileNameWithoutExtension(f); return n.Length >= 3 && int.TryParse(n[..3], out var v) && (n.Length == 3 || !char.IsDigit(n[3])) ? v : 0; }

    /// <summary>Giữ số mới; trả về (chuỗi số báo cáo, mã giữ số — lưu vào CQ để kiểm trùng khi duyệt).</summary>
    public (string Text, string Token) ClaimWithToken(int year, int month, string cqRef)
    {
        var d = MonthDir(year, month);
        for (var attempt = 0; attempt < 2000; attempt++)
        {
            var used = Directory.EnumerateFiles(d, "*.json").Select(NumOf).Where(n => n > 0);
            var (text, n2) = Domain.ReportNumber.Next(used, month);
            var token = Guid.NewGuid().ToString("N");
            var body = new JsonObject { ["cq"] = cqRef, ["token"] = token, ["machine"] = store.Machine, ["at"] = DateTimeOffset.Now.ToString("O") }.ToJsonString();
            if (FileStore.TryCreateNew(Path.Combine(d, $"{n2:000}.json"), Encoding.UTF8.GetBytes(body))) return (text, token);
        }
        throw new IOException("Không cấp được số báo cáo (tranh chấp quá nhiều)");
    }

    public string Claim(int year, int month, string cqRef) => ClaimWithToken(year, month, cqRef).Text;

    public bool IsTaken(int year, int month, int n) => File.Exists(Path.Combine(store.Root, "counters", "reportno", $"{year:0000}-{month:00}", $"{n:000}.json"));

    /// <summary>Người đang giữ số n (nội dung file NNN.json hiện tại). null: chưa ai giữ / file đang ghi dở.</summary>
    public ReportClaim? Owner(int year, int month, int n)
    {
        var f = Path.Combine(store.Root, "counters", "reportno", $"{year:0000}-{month:00}", $"{n:000}.json");
        try
        {
            if (!File.Exists(f)) return null;
            var o = JsonNode.Parse(File.ReadAllText(f))!;
            return new ReportClaim((string?)o["token"] ?? "", (string?)o["cq"] ?? "", (string?)o["machine"] ?? "",
                DateTimeOffset.TryParse((string?)o["at"], out var at) ? at : DateTimeOffset.MinValue);
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
}

public sealed record OutboxBlob(string Sha, string Ext);
public sealed record OutboxItem(string OpId, long Ts, string Type, string Id, JsonNode Data, int Base, string User, string State, string? ConflictFile,
    IReadOnlyList<OutboxBlob>? Blobs = null);
public sealed record SyncResult(int Pushed, int Conflicts, int Pending, bool Offline, string? Error);

/// <summary>Hàng đợi cục bộ (%LocalAppData%) khi kho chung không truy cập được. Chỉ xóa mục sau khi đọc lại kho và khớp opId + dữ liệu.
/// Mục có thể kèm blob (ảnh): blob được đẩy và kiểm hash TRƯỚC bản ghi tham chiếu nó.</summary>
public sealed class Outbox(string path)
{
    private static readonly JsonSerializerOptions J = new();
    public string Path_ { get; } = Directory.CreateDirectory(path).FullName;
    private string BlobDir => Directory.CreateDirectory(Path.Combine(Path_, "blobs")).FullName;

    /// <summary>Lưu blob vào outbox (kiểm hash), trả về mô tả để gắn vào mục.</summary>
    public OutboxBlob StageBlob(byte[] bytes, string ext)
    {
        var sha = FileStore.Sha256Hex(bytes); ext = ext.TrimStart('.');
        var p = Path.Combine(BlobDir, $"{sha}.{ext}");
        if (!File.Exists(p) || FileStore.Sha256Hex(File.ReadAllBytes(p)) != sha) { File.WriteAllBytes(p + ".tmp", bytes); File.Move(p + ".tmp", p, overwrite: true); }
        return new OutboxBlob(sha, ext);
    }

    public byte[]? BlobBytes(string sha, string ext)
    {
        var p = Path.Combine(BlobDir, $"{sha}.{ext.TrimStart('.')}");
        if (!File.Exists(p)) return null;
        var b = File.ReadAllBytes(p); return FileStore.Sha256Hex(b) == sha ? b : null;
    }

    private static void Write(string f, OutboxItem item)
    {
        var tmp = f + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(item, J), Encoding.UTF8);
        File.Move(tmp, f, overwrite: true);
    }

    public string Add(string type, string id, JsonNode data, int baseVersion, string user, IReadOnlyList<OutboxBlob>? blobs = null)
    {
        var item = new OutboxItem(Guid.NewGuid().ToString(), DateTime.UtcNow.Ticks, type, id, data, baseVersion, user, "pending", null, blobs);
        Write(Path.Combine(Path_, $"{item.Ts:D20}-{item.OpId}.json"), item);
        return item.OpId;
    }

    /// <summary>Mục XUNG ĐỘT có sẵn (vd lần ghi bị mất do đồng bộ giữa các máy) — hiện ở cửa sổ Xung đột để người dùng chọn.</summary>
    public string AddConflict(string type, string id, JsonNode data, int baseVersion, string user, string? conflictFile)
    {
        var item = new OutboxItem(Guid.NewGuid().ToString(), DateTime.UtcNow.Ticks, type, id, data.DeepClone(), baseVersion, user, "conflict", conflictFile);
        Write(Path.Combine(Path_, $"{item.Ts:D20}-{item.OpId}.json"), item);
        return item.OpId;
    }

    /// <summary>Nháp offline cho cùng một bản ghi được GỘP vào mục đang chờ (giữ OpId + version gốc ban đầu), tránh tự gây xung đột
    /// giữa hai lần sửa liên tiếp của chính mình khi offline. Mục đã ở trạng thái xung đột thì không gộp — tạo mục mới.</summary>
    public string AddOrReplace(string type, string id, JsonNode data, int baseVersion, string user, IReadOnlyList<OutboxBlob>? blobs = null)
    {
        foreach (var f in Directory.EnumerateFiles(Path_, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var op = JsonSerializer.Deserialize<OutboxItem>(File.ReadAllText(f, Encoding.UTF8), J)!;
            if (op.State == "pending" && op.Type == type && op.Id == id && op.User == user)
            {
                var merged = (op.Blobs ?? Array.Empty<OutboxBlob>()).Concat(blobs ?? Array.Empty<OutboxBlob>()).Distinct().ToList();
                Write(f, op with { Data = data, Blobs = merged.Count == 0 ? null : merged });
                return op.OpId;
            }
        }
        return Add(type, id, data, baseVersion, user, blobs);
    }

    public void Remove(string opId)
    {
        foreach (var f in Directory.EnumerateFiles(Path_, $"*-{opId}.json")) File.Delete(f);
        CleanBlobs();
    }

    /// <summary>Xóa blob không còn mục nào tham chiếu.</summary>
    private void CleanBlobs()
    {
        var used = Items().SelectMany(i => i.Blobs ?? Array.Empty<OutboxBlob>()).Select(b => $"{b.Sha}.{b.Ext}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(BlobDir)) if (!used.Contains(Path.GetFileName(f)) && !f.EndsWith(".tmp")) try { File.Delete(f); } catch { }
    }

    public IReadOnlyList<OutboxItem> Items() => Directory.EnumerateFiles(Path_, "*.json").OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => JsonSerializer.Deserialize<OutboxItem>(File.ReadAllText(f, Encoding.UTF8), J)!).ToList();

    public SyncResult Sync(Func<FileStore?> openStore)
    {
        FileStore? store;
        try { store = openStore(); } catch (Exception e) { return new SyncResult(0, 0, Items().Count, true, e.Message); }
        if (store is null) return new SyncResult(0, 0, Items().Count, true, "Kho chung không truy cập được");
        int pushed = 0, conflicts = 0;
        foreach (var f in Directory.EnumerateFiles(Path_, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList())
        {
            var op = JsonSerializer.Deserialize<OutboxItem>(File.ReadAllText(f, Encoding.UTF8), J)!;
            if (op.State == "conflict") { conflicts++; continue; }
            try
            {
                foreach (var b in op.Blobs ?? Array.Empty<OutboxBlob>())          // blob trước, bản ghi sau
                {
                    var bytes = BlobBytes(b.Sha, b.Ext) ?? throw new IntegrityException($"Thiếu/hỏng ảnh {b.Sha[..12]} trong hàng đợi trên máy này");
                    if (store.PutBlob(bytes, b.Ext) != b.Sha) throw new IntegrityException("Ảnh đẩy lên kho sai mã kiểm");
                }
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var v = store.Commit(op.Type, op.Id, op.Data, op.Base, op.User, op.OpId);
                    var back = store.Read(op.Type, op.Id, v)!;
                    if (back.Meta.OpId != op.OpId) break;
                    if (FileStore.Canonical(back.Data) == FileStore.Canonical(op.Data)) { File.Delete(f); pushed++; break; }
                    // Lần trước đã ghi (cùng OpId) nhưng sau đó nháp được gộp thêm thay đổi → ghi phần còn lại thành version kế tiếp (OpId mới)
                    op = op with { Base = v, OpId = Guid.NewGuid().ToString() };
                    Write(f, op);
                }
            }
            catch (ConflictException e)
            {
                var cf = store.SaveConflict(op.Type, op.Id, JsonSerializer.SerializeToNode(op, J)!, e.Message);
                Write(f, op with { State = "conflict", ConflictFile = cf });
                conflicts++;
            }
            catch (IOException e) { return new SyncResult(pushed, conflicts, Items().Count, true, e.Message); }   // mất mạng giữa chừng → dừng, giữ thứ tự
        }
        CleanBlobs();
        return new SyncResult(pushed, conflicts, Items().Count, false, null);
    }
}
