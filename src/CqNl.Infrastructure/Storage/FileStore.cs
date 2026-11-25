using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CqNl.Infrastructure.Storage;

public sealed class ConflictException(string message) : Exception(message);
public sealed class IntegrityException(string message) : Exception(message);

/// <summary>BaseSha: mã SHA-256 dữ liệu của bản gốc mà lần ghi này dựa trên (null: bản đầu / bản ghi trước 0.9.2) — dùng phát hiện
/// "ghi đè do đồng bộ chậm" khi kho được đồng bộ qua Synology Drive giữa nhiều máy.</summary>
public sealed record RecordMeta(int Version, string Sha256, string User, string Machine, DateTimeOffset At, string OpId, int Base, string? BaseSha = null);
public sealed record StoredRecord(RecordMeta Meta, JsonNode Data);

/// <summary>Một lần ghi của CHÍNH MÁY NÀY bị mất do đồng bộ (máy khác ghi cùng lúc, bản của máy kia được chọn): dữ liệu của mình + bản đang dùng.</summary>
public sealed record LostWrite(string Type, string Id, StoredRecord Mine, StoredRecord Head);

/// <summary>
/// Kho chung (ADR-001). Mỗi bản ghi là chuỗi version bất biến records/&lt;type&gt;/&lt;id&gt;/v000001.json.
/// Ghi = file tạm trong staging → kiểm hash → đổi tên vào đúng chỗ (không ghi đè). KHÔNG có file nào bị ghi đè.
/// 0.9.2 — CHẠY ĐƯỢC TRÊN THƯ MỤC ĐỒNG BỘ (Synology Drive trên từng máy, ADMIN-PC tắt vẫn dùng được):
///  • hai máy cùng tạo v00000N.json khi chưa đồng bộ kịp → Synology giữ cả hai (đổi tên một bản kiểu "v00000N … Conflict ….json");
///    mọi bản có tên bắt đầu "v00000N" đều là ứng viên của version N → chọn bản thắng XÁC ĐỊNH, mọi máy chọn giống nhau
///    (ghi sớm hơn thắng; trùng giờ → tên máy, mã thao tác); bản thua vẫn nằm nguyên trong kho;
///  • máy có lần ghi bị thua (hoặc bị máy khác ghi đè vì dựa trên bản cũ) phát hiện được qua <see cref="LostWrites"/> → báo người dùng xử lý.
/// Tương thích: kho cũ đọc như trước; tên dạng v000001~máy~mã.json (bản thử 0.9.2) cũng đọc được.
/// </summary>
public sealed class FileStore
{
    public string Root { get; }
    public string Machine { get; }
    private static readonly JsonSerializerOptions J = new() { WriteIndented = false };
    private static readonly System.Text.RegularExpressions.Regex VerName = new(@"^v(\d{6})(?:[~_ (\-].*)?\.json$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public FileStore(string root, string? machine = null, bool create = true)
    {
        Root = root; Machine = machine ?? Environment.MachineName;
        if (!create) return;
        foreach (var d in new[] { "records", "staging", "conflicts", "locks", "counters", "events", "audit" })
            Directory.CreateDirectory(Path.Combine(root, d));
        var marker = Path.Combine(root, "store.json");
        if (!File.Exists(marker)) TryCreateNew(marker, Encoding.UTF8.GetBytes("{\"format\":\"cqnl-store\",\"version\":1}"));
    }

    public static string SafeId(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id) sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == '/' || c == '\\' ? '_' : c);
        return sb.ToString();
    }

    private string Dir(string type, string id) => Path.Combine(Root, "records", type, SafeId(id));

    /// <summary>Mọi file version hợp lệ của bản ghi: (số version, đường dẫn). Bỏ qua file lạ (vd bản "Conflict" do phần mềm đồng bộ đổi tên).</summary>
    private List<(int V, string Path)> Files(string type, string id)
    {
        var d = Dir(type, id);
        if (!Directory.Exists(d)) return new();
        var list = new List<(int, string)>();
        foreach (var f in Directory.EnumerateFiles(d, "v*.json"))
        {
            var m = VerName.Match(Path.GetFileName(f));
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v) && v > 0) list.Add((v, f));
        }
        return list;
    }

    public IReadOnlyList<int> Versions(string type, string id) => Files(type, id).Select(x => x.V).Distinct().OrderBy(v => v).ToList();

    public static string Canonical(JsonNode data) => data.ToJsonString(J);
    public static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static StoredRecord? Load(string path, bool throwOnBad)
    {
        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!;
            var data = doc["data"]!;
            var meta = doc["meta"]!.Deserialize<RecordMeta>()!;
            if (Sha256Hex(Encoding.UTF8.GetBytes(Canonical(data))) != meta.Sha256)
                throw new IntegrityException($"Hỏng dữ liệu {path}: sai SHA-256");
            return new StoredRecord(meta, data);
        }
        catch (Exception e) when (!throwOnBad && e is IOException or UnauthorizedAccessException or JsonException or IntegrityException or NullReferenceException) { return null; }
    }

    /// <summary>Thứ tự chọn bản thắng khi cùng số version có nhiều bản: ghi sớm hơn → tên máy → mã thao tác (mọi máy chọn giống nhau).</summary>
    private static IOrderedEnumerable<StoredRecord> ByWinner(IEnumerable<StoredRecord> c) =>
        c.OrderBy(r => r.Meta.At.UtcTicks).ThenBy(r => r.Meta.Machine, StringComparer.Ordinal).ThenBy(r => r.Meta.OpId, StringComparer.Ordinal);

    /// <summary>Mọi bản (kể cả bản thua) của một số version.</summary>
    public IReadOnlyList<StoredRecord> Candidates(string type, string id, int version) =>
        Files(type, id).Where(x => x.V == version).Select(x => Load(x.Path, throwOnBad: false)).Where(r => r is not null).Select(r => r!).ToList();

    public StoredRecord? Read(string type, string id, int? version = null)
    {
        var files = Files(type, id);
        if (files.Count == 0) return null;
        var v = version ?? files.Max(x => x.V);
        var mine = files.Where(x => x.V == v).ToList();
        if (mine.Count == 0) return null;
        if (mine.Count == 1) return Load(mine[0].Path, throwOnBad: true);      // trường hợp thường: 1 bản — lỗi dữ liệu báo rõ
        return ByWinner(mine.Select(x => Load(x.Path, throwOnBad: false)).Where(r => r is not null).Select(r => r!)).FirstOrDefault()
               ?? throw new IntegrityException($"Hỏng dữ liệu {type}/{id} v{v}");
    }

    /// <summary>CAS: chỉ tạo v(base+1). Idempotent theo opId (quét mọi version &gt; base). Ghi xong mà bản khác cùng version (máy khác,
    /// đã đồng bộ về) thắng → gỡ bản vừa ghi và báo ConflictException như cũ.</summary>
    public int Commit(string type, string id, JsonNode data, int baseVersion, string user, string opId)
    {
        var files = Files(type, id);
        var latest = files.Count == 0 ? 0 : files.Max(x => x.V);
        if (latest > baseVersion)
        {
            foreach (var f in files.Where(x => x.V > baseVersion))
                if (Load(f.Path, throwOnBad: false)?.Meta.OpId == opId) return f.V;
            throw new ConflictException($"{type}/{id}: dựa trên v{baseVersion} nhưng kho đã ở v{latest}");
        }
        var canon = Canonical(data);
        var baseSha = baseVersion > 0 ? Read(type, id, baseVersion)?.Meta.Sha256 : null;
        var meta = new RecordMeta(baseVersion + 1, Sha256Hex(Encoding.UTF8.GetBytes(canon)), user, Machine, DateTimeOffset.Now, opId, baseVersion, baseSha);
        var doc = new JsonObject { ["meta"] = JsonSerializer.SerializeToNode(meta), ["data"] = JsonNode.Parse(canon) };
        var bytes = Encoding.UTF8.GetBytes(doc.ToJsonString(J));
        var tmp = WriteStaging(bytes);
        var d = Dir(type, id); Directory.CreateDirectory(d);
        // tên v000001.json tạo-không-ghi-đè: thư mục dùng chung (UNC) chỉ 1 máy thắng. Kho đồng bộ: 2 máy cùng tạo khi chưa đồng bộ kịp →
        // Synology đổi tên 1 bản ("v000001 … Conflict ….json") — bản đó vẫn được đọc là ứng viên của cùng version, chọn bản thắng xác định.
        var dst = Path.Combine(d, $"v{baseVersion + 1:000000}.json");
        try { File.Move(tmp, dst, overwrite: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            if (TargetAppeared(dst)) throw new ConflictException($"{type}/{id}: v{baseVersion + 1} đã có (ghi trùng thao tác)");
            throw;
        }
        // xác nhận sau khi publish: đọc lại bản THẮNG của version này
        var won = Read(type, id, baseVersion + 1);
        if (won?.Meta.OpId != opId)
        {
            TryDelete(dst);
            throw new ConflictException($"{type}/{id}: máy khác vừa ghi v{baseVersion + 1}");
        }
        AppendEvent(type, id, baseVersion + 1);
        return baseVersion + 1;
    }

    /// <summary>
    /// Lần ghi của máy <paramref name="machine"/> KHÔNG còn trong chuỗi bản đang dùng (bản mới nhất → bản gốc nó dựa trên → …):
    /// thua khi cùng version, hoặc bị máy khác ghi đè vì máy kia dựa trên bản cũ (đồng bộ chậm). Bản trùng nội dung với bản đang dùng
    /// (vd hai máy cùng nhập 1 file ERP) không tính. Chỉ xét các version &gt; <paramref name="sinceVersion"/>.
    /// </summary>
    public IReadOnlyList<LostWrite> LostWrites(string type, string id, string machine, int sinceVersion = 0)
    {
        var all = Files(type, id).Select(x => Load(x.Path, throwOnBad: false))
            .Where(r => r is not null).Select(r => r!).ToList();
        if (all.Count == 0) return Array.Empty<LostWrite>();
        var byVer = all.GroupBy(r => r.Meta.Version).ToDictionary(g => g.Key, g => g.ToList());
        var head = ByWinner(byVer[byVer.Keys.Max()]).First();
        // chuỗi bản đang dùng: từ bản mới nhất lần theo BaseSha (bản cũ chưa có BaseSha → bản thắng của version gốc)
        var chain = new HashSet<string>();
        for (var cur = head; cur is not null;)
        {
            chain.Add(cur.Meta.Sha256 + "|" + cur.Meta.OpId);
            if (cur.Meta.Base <= 0 || !byVer.TryGetValue(cur.Meta.Base, out var prev)) break;
            cur = cur.Meta.BaseSha is { } bs ? prev.FirstOrDefault(p => p.Meta.Sha256 == bs) ?? ByWinner(prev).First() : ByWinner(prev).First();
        }
        return all.Where(r => r.Meta.Machine == machine && r.Meta.Version > sinceVersion && !chain.Contains(r.Meta.Sha256 + "|" + r.Meta.OpId))
            .Where(r => !chain.Any(c => c.StartsWith(r.Meta.Sha256 + "|", StringComparison.Ordinal)))    // cùng nội dung với một bản trong chuỗi → không mất gì
            .Select(r => new LostWrite(type, id, r, head)).ToList();
    }

    private string WriteStaging(byte[] bytes)
    {
        var dir = Path.Combine(Root, "staging", SafeId(Machine)); Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { fs.Write(bytes); fs.Flush(true); }
        if (Sha256Hex(File.ReadAllBytes(tmp)) != Sha256Hex(bytes)) { TryDelete(tmp); throw new IntegrityException("Ghi tạm lỗi kiểm tra hash"); }
        return tmp;
    }

    /// <summary>Blob theo nội dung (ảnh, xlsx, pdf): blobs/aa/&lt;sha&gt;.&lt;ext&gt;; đã tồn tại cùng hash = thành công.</summary>
    public string PutBlob(byte[] bytes, string ext)
    {
        var sha = Sha256Hex(bytes);
        var dir = Path.Combine(Root, "blobs", sha[..2]); Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, $"{sha}.{ext.TrimStart('.')}");
        if (File.Exists(dst))
        {
            if (Sha256Hex(File.ReadAllBytes(dst)) != sha) throw new IntegrityException($"Blob hỏng: {dst}");
            return sha;
        }
        var tmp = WriteStaging(bytes);
        try { File.Move(tmp, dst, overwrite: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { TryDelete(tmp); if (!TargetAppeared(dst)) throw; }
        if (Sha256Hex(File.ReadAllBytes(dst)) != sha) throw new IntegrityException($"Blob hỏng sau khi ghi: {dst}");
        return sha;
    }

    public byte[] GetBlob(string sha, string ext)
    {
        var p = Path.Combine(Root, "blobs", sha[..2], $"{sha}.{ext.TrimStart('.')}");
        var b = File.ReadAllBytes(p);
        if (Sha256Hex(b) != sha) throw new IntegrityException($"Blob hỏng: {p}");
        return b;
    }

    public string SaveConflict(string type, string id, JsonNode mine, string note)
    {
        var d = Path.Combine(Root, "conflicts", type, SafeId(id)); Directory.CreateDirectory(d);
        var p = Path.Combine(d, Guid.NewGuid().ToString("N") + ".json");
        var doc = new JsonObject { ["mine"] = mine.DeepClone(), ["note"] = note, ["machine"] = Machine, ["at"] = DateTimeOffset.Now.ToString("O") };
        TryCreateNew(p, Encoding.UTF8.GetBytes(doc.ToJsonString(J)));
        return p;
    }

    private void AppendEvent(string type, string id, int version)
    {
        var day = Path.Combine(Root, "events", DateTime.UtcNow.ToString("yyyyMMdd")); Directory.CreateDirectory(day);
        var p = Path.Combine(day, $"{DateTime.UtcNow.Ticks}-{SafeId(Machine)}-{Guid.NewGuid():N}.json");
        TryCreateNew(p, Encoding.UTF8.GetBytes(new JsonObject { ["type"] = type, ["id"] = id, ["version"] = version }.ToJsonString(J)));
    }

    /// <summary>Tạo file mới nguyên tử; false nếu đã tồn tại (FileMode.CreateNew).</summary>
    public static bool TryCreateNew(string path, byte[] bytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            fs.Write(bytes); fs.Flush(true); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (TargetAppeared(path)) return false;    // máy khác tạo trước (qua SMB có thể báo Access denied)
            throw;
        }
    }

    /// <summary>Sau lỗi tạo/đổi tên: chờ tối đa ~1 s xem file đích có xuất hiện không (metadata SMB có thể trễ).</summary>
    internal static bool TargetAppeared(string path)
    {
        for (var i = 0; i < 10; i++) { if (File.Exists(path)) return true; Thread.Sleep(100); }
        return false;
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { /* bỏ qua: file tạm */ } }
}
