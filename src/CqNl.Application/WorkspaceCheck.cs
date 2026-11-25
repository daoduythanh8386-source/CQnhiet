namespace CqNl.Application;

public enum CheckLevel { Ok, Warn, Fail }

/// <summary>Một mục kiểm tra máy: tên, mức (ĐẠT / LƯU Ý / LỖI), chi tiết, cách sửa.</summary>
public sealed record CheckItem(string Name, CheckLevel Level, string Detail, string? Fix = null);

/// <summary>"Kiểm tra máy này": máy có đủ điều kiện tạo CQ dùng chung kho với các máy khác không.</summary>
public sealed partial class Workspace
{
    /// <summary>Thư mục tồn tại (tạo nếu chưa có) và GHI được — ghi file thử rồi xoá. Lỗi → BusinessException rõ ràng.</summary>
    public static void ProbeWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var p = Path.Combine(folder, $".cqnl-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(p, "ok"); File.Delete(p);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new BusinessException($"Không ghi được vào thư mục: {folder} ({e.Message})"); }
    }

    /// <summary>Cập nhật bản nhớ "thư mục lưu CQ chung" vào cấu hình máy (khi đang online). Trả true nếu có thay đổi.</summary>
    public bool SyncSharedCqFolder(LocalConfig cfg)
    {
        if (!Online) return false;
        string? shared;
        try { shared = Settings!.CqFolderPath(); } catch { return false; }
        // thư mục chung là ổ C của MÁY KHÁC mà máy này không đổi được sang kho của mình → bỏ qua (lưu mặc định + cảnh báo)
        if (shared is not null && !shared.StartsWith(@"\\") && !Directory.Exists(shared) && !IsStoreHost()) shared = null;
        if (shared == cfg.SharedCqFolder) return false;
        cfg.SharedCqFolder = shared;
        return true;
    }

    /// <summary>Vị trí kho: bản đồng bộ Synology Drive trên máy này (mỗi máy một bản, đồng bộ qua NAS) / thư mục mạng ADMIN-PC / ổ thường.</summary>
    public static CheckItem StoreLocationCheck(string storePath)
    {
        var p = storePath.Trim();
        if (AppInfo.IsSyncFolder(p))
            return new("Vị trí kho chung", CheckLevel.Ok, $"{p} — bản đồng bộ Synology Drive trên máy này: chạy độc lập, ADMIN-PC tắt vẫn dùng được. " +
                "Cần internet để đồng bộ với các máy khác (thường vài giây – vài phút).");
        if (p.StartsWith(@"\\"))
            return new("Vị trí kho chung", CheckLevel.Ok, $"{p} — kho trên máy khác qua mạng LAN: máy đó phải BẬT NGUỒN. " +
                "Muốn chạy độc lập: cài Synology Drive, đồng bộ thư mục TQM HƯNG YÊN, rồi đổi kho sang " + AppInfo.DefaultStore + ".");
        return new("Vị trí kho chung", CheckLevel.Fail, $"Kho đang là thư mục thường trên ổ của máy này: {p}. Máy khác không dùng chung được.",
            "Dùng thư mục DU LIEU NHIET do Synology Drive đồng bộ (" + AppInfo.DefaultStore + "): màn Đăng nhập → \"Đổi đường dẫn kho…\".");
    }

    /// <summary>Phần mềm Synology Drive có đang chạy trên máy này không (tiến trình cloud-drive-*). null: không kiểm được.</summary>
    public static bool? SynologyDriveRunning()
    {
        try { return System.Diagnostics.Process.GetProcesses().Any(pr => { try { return pr.ProcessName.StartsWith("cloud-drive", StringComparison.OrdinalIgnoreCase); } catch { return false; } }); }
        catch { return null; }
    }

    /// <summary>Chạy các mục kiểm tra (không sửa gì). <paramref name="excel"/> và <paramref name="printers"/> do lớp giao diện cung cấp.</summary>
    public IReadOnlyList<CheckItem> CheckMachine(LocalConfig cfg, (bool Ok, string Detail) excel, IReadOnlyList<string> printers)
    {
        var r = new List<CheckItem>();
        var online = Probe();                                          // thử đọc + GHI kho ngay lúc kiểm
        r.Add(online
            ? new("Kho chung (đọc + ghi)", CheckLevel.Ok, StorePath)
            : new("Kho chung (đọc + ghi)", CheckLevel.Fail, OfflineReason ?? ("Không truy cập được: " + StorePath),
                "Kiểm tra mạng, máy chứa kho (ADMIN-PC) đang bật và thư mục được chia sẻ quyền GHI cho máy này. Đường dẫn đổi ở màn Đăng nhập → \"Đổi đường dẫn kho…\"."));
        r.Add(StoreLocationCheck(StorePath));
        if (AppInfo.IsSyncFolder(StorePath) && SynologyDriveRunning() == false)
            r.Add(new("Đồng bộ Synology Drive", CheckLevel.Warn, "Không thấy Synology Drive đang chạy — việc làm trên máy này CHƯA lên NAS (máy khác chưa thấy), máy này cũng chưa nhận dữ liệu mới của máy khác.",
                "Mở Synology Drive (biểu tượng ở khay đồng hồ), đăng nhập tài khoản NAS, bật đồng bộ thư mục TQM HƯNG YÊN."));
        r.Add(excel.Ok ? new("Microsoft Excel (xuất PDF CQ)", CheckLevel.Ok, excel.Detail)
                       : new("Microsoft Excel (xuất PDF CQ)", CheckLevel.Fail, excel.Detail, "Cài Microsoft Excel (bản desktop) trên máy này — WPS / Excel online không xuất được PDF CQ."));
        SharedSettings? st = null;
        if (online) { try { SyncSharedCqFolder(cfg); st = Settings!.Get().Value; } catch { } }
        // thư mục lưu CQ
        try
        {
            ProbeWritable(cfg.PdfRoot);
            r.Add(online && string.IsNullOrWhiteSpace(st?.CqFolder)
                ? new("Thư mục lưu file CQ", CheckLevel.Warn, $"{cfg.PdfRoot} ({cfg.PdfRootSource}) — ghi được, nhưng quản trị chưa đặt thư mục lưu CQ CHUNG nên file CQ nằm rải rác từng máy.",
                      "Quản trị: Cài đặt → Nguồn dữ liệu → Thư mục lưu file CQ → chọn thư mục trong kho chung.")
                : new("Thư mục lưu file CQ", CheckLevel.Ok, $"{cfg.PdfRoot} ({cfg.PdfRootSource}) — ghi được"));
        }
        catch (BusinessException e) { r.Add(new("Thư mục lưu file CQ", CheckLevel.Fail, e.Message, "Tab Xuất CQ → Xuất khác ▾ → Chọn thư mục lưu CQ… (hoặc nhờ quản trị đặt thư mục chung có quyền ghi).")); }
        r.Add(printers.Count > 0 ? new("Máy in", CheckLevel.Ok, $"{printers.Count} máy in: {string.Join(", ", printers.Take(3))}{(printers.Count > 3 ? "…" : "")}")
                                 : new("Máy in", CheckLevel.Warn, "Máy này chưa có máy in (vẫn tạo và lưu PDF được).", "Cài máy in trong Windows nếu cần in từ máy này."));
        if (!online || st is null)
        {
            r.Add(new("Cài đặt dùng chung (ERP, mẫu CQ, chữ ký…)", CheckLevel.Fail, "Chưa kiểm được vì không vào được kho chung.", "Sửa mục Kho chung trước."));
            return r;
        }
        // ERP
        var erpPath = Settings!.ErpFolderPath();
        if (string.IsNullOrWhiteSpace(st.ErpFolder))
            r.Add(new("Thư mục ERP theo dõi", CheckLevel.Warn, "Chưa đặt — app chưa tự nhập ERP. Vẫn nhập tay được: Đơn gia công → Nhập ERP ▾ → Chọn một file ERP.",
                "Quản trị: Cài đặt → Nguồn dữ liệu ERP → chọn thư mục MẠNG chứa file MF0015 (máy nào cũng vào được)."));
        else if (!Directory.Exists(erpPath))
            r.Add(new("Thư mục ERP theo dõi", CheckLevel.Warn, $"Máy này không vào được: {erpPath} (máy khác có thể vẫn tự nhập).",
                "Nếu là ổ C của một máy: đổi sang thư mục mạng dùng chung. Nếu là thư mục mạng: kiểm tra quyền chia sẻ."));
        else
        {
            var n = 0; try { n = CountNewErpFiles(); } catch { }
            r.Add(new("Thư mục ERP theo dõi", CheckLevel.Ok, $"{erpPath} — vào được · {n} file chưa nhập"));
        }
        var localPaths = new[] { ("thư mục ERP", st.ErpFolder), ("thư mục lưu CQ", st.CqFolder) }
            .Where(x => !string.IsNullOrWhiteSpace(x.Item2) && !x.Item2!.StartsWith(@"\\"))
            .Where(x => FolderToken(ResolveFolder(x.Item2)) is not { } t || !t.StartsWith(StoreToken, StringComparison.OrdinalIgnoreCase)).ToList();   // trong kho đồng bộ → dùng chung được
        if (localPaths.Count > 0)
            r.Add(new("Đường dẫn dùng chung", CheckLevel.Warn,
                string.Join("; ", localPaths.Select(x => $"{x.Item1}: {x.Item2}")) + " — là ổ trên MỘT máy, máy khác không vào được (file CQ sẽ nằm rải rác từng máy).",
                "Quản trị mở Cài đặt trên máy chứa kho: app tự đề nghị đổi sang đường dẫn mạng (\\\\TÊN-MÁY\\...) (chọn lại thư mục trong kho chung)."));
        // mẫu CQ, bảng mác, chữ ký, con dấu
        var tpls = 0; try { tpls = TemplateList().Count; } catch { }
        r.Add(tpls > 0 ? new("Mẫu CQ (template)", CheckLevel.Ok, $"{tpls} mẫu") : new("Mẫu CQ (template)", CheckLevel.Fail, "Kho chưa có mẫu CQ nào.", "Quản trị: Template CQ → thêm mẫu."));
        r.Add(st.SteelTableSha is not null ? new("Bảng mác thép", CheckLevel.Ok, st.SteelTableName ?? "đã có")
                                           : new("Bảng mác thép", CheckLevel.Warn, "Chưa có bảng mác — không tự điền nhiệt độ / dung sai.", "Quản trị: Cài đặt → Bảng mác thép → Cập nhật bảng mác…"));
        var signers = st.Signers.Count(x => x.Active);
        r.Add(signers > 0 && st.StampSha is not null ? new("Chữ ký và con dấu", CheckLevel.Ok, $"{signers} chữ ký · có con dấu")
            : new("Chữ ký và con dấu", CheckLevel.Fail, $"{signers} chữ ký · {(st.StampSha is null ? "CHƯA có con dấu" : "có con dấu")} — chưa duyệt được CQ có dấu + chữ ký.",
                  "Quản trị: Cài đặt → Chữ ký và con dấu."));
        r.Add(st.DefaultApproverId is not null && st.DefaultCheckerId is not null ? new("Người duyệt / kiểm tra mặc định", CheckLevel.Ok, "đã đặt")
            : new("Người duyệt / kiểm tra mặc định", CheckLevel.Warn, "Chưa đặt — máy mới phải chọn người ký ở lần duyệt đầu tiên.", "Quản trị: Cài đặt → Chữ ký và con dấu → Người duyệt / kiểm tra mặc định → Lưu mặc định."));
        // phiên bản
        var cur = CurrentVersion; var rels = Releases();
        var latest = rels.Select(x => Version.TryParse(x.Version, out var v) ? v : null).Where(v => v is not null).Max();
        if (latest is null)
            r.Add(new("Phiên bản ứng dụng", CheckLevel.Warn, $"{VersionText(cur)} — kho chưa có bản phát hành nào, máy khác không tự cập nhật được.", "Quản trị: Cài đặt → Cập nhật phần mềm → Phát hành bản cập nhật… (chọn bộ cài mới nhất)."));
        else if (latest > cur)
            r.Add(new("Phiên bản ứng dụng", CheckLevel.Warn, $"Máy này {VersionText(cur)} — kho đã có {VersionText(latest)}.", "Cài đặt → Cập nhật máy này (hoặc đăng xuất rồi mở lại app để được hỏi cập nhật)."));
        else if (latest < cur)
            r.Add(new("Phiên bản ứng dụng", CheckLevel.Warn, $"Máy này {VersionText(cur)} mới hơn bản phát hành trên kho ({VersionText(latest)}) — máy khác chưa được cập nhật.", "Quản trị: Cài đặt → Cập nhật phần mềm → Phát hành bản cập nhật… (bộ cài bản này)."));
        else r.Add(new("Phiên bản ứng dụng", CheckLevel.Ok, $"{VersionText(cur)} — bản mới nhất trên kho"));
        if (Session is { } s) r.Add(new("Tài khoản đăng nhập", CheckLevel.Ok, $"{s.User.Username} ({s.User.Role}) · máy {Machine}"));
        return r;
    }
}

public sealed partial class Workspace
{
    /// <summary>Đầu đường dẫn "thư mục kho trên máy này" trong cài đặt chung — vd "{KHO}\File CQ".</summary>
    public const string StoreToken = "{KHO}";

    /// <summary>Đường dẫn thư mục trong cài đặt chung → đường dẫn thật trên máy này: "{KHO}\…" → thư mục kho của máy này;
    /// đường dẫn tuyệt đối cũ trỏ vào thư mục kho của máy KHÁC (C:\Users\Admin\SynologyDrive\DU LIEU NHIET\…, \\ADMIN-PC\…\DU LIEU NHIET\…)
    /// mà máy này không có → đổi sang cùng thư mục con trong kho của máy này. Biến môi trường (%USERPROFILE%) được mở rộng.</summary>
    public string? ResolveFolder(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        p = Environment.ExpandEnvironmentVariables(p.Trim());
        var store = StorePath.TrimEnd('\\', '/');
        if (p.StartsWith(StoreToken, StringComparison.OrdinalIgnoreCase))
        {
            var sub = p[StoreToken.Length..].TrimStart('\\', '/');
            return sub.Length == 0 ? store : Path.Combine(store, sub);
        }
        var baseName = Path.GetFileName(store);
        if (baseName.Length == 0 || p.StartsWith(store, StringComparison.OrdinalIgnoreCase)) return p;
        var marker = "\\" + baseName + "\\"; var q = p.TrimEnd('\\') + "\\";
        var i = q.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return p;                                                     // thư mục ngoài kho: giữ nguyên (không dò mạng)
        bool exists; try { exists = Directory.Exists(p); } catch { exists = false; }
        if (exists) return p;                                                    // máy này vào được đường dẫn cũ → dùng luôn
        var rest = q[(i + marker.Length)..].TrimEnd('\\');
        return rest.Length == 0 ? store : Path.Combine(store, rest);
    }

    /// <summary>Thư mục nằm trong thư mục kho → lưu dạng "{KHO}\…" (mỗi máy tự hiểu theo kho của mình); ngoài kho → giữ nguyên.</summary>
    public string? FolderToken(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        p = p.Trim();
        if (p.StartsWith(StoreToken, StringComparison.OrdinalIgnoreCase)) return p;
        var store = StorePath.TrimEnd('\\', '/');
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(p))); } catch { return p; }
        if (full.Equals(store, StringComparison.OrdinalIgnoreCase)) return StoreToken;
        if (full.StartsWith(store + "\\", StringComparison.OrdinalIgnoreCase)) return StoreToken + full[store.Length..];
        return p;
    }

    /// <summary>Thư mục chọn trên MÁY CHỨA KHO dạng ổ cục bộ (vd C:\Users\Admin\SynologyDrive\DU LIEU NHIET\ERP) nhưng nằm trong thư mục kho
    /// → đổi sang đường dẫn MẠNG tương ứng (\\ADMIN-PC\Users\Admin\SynologyDrive\DU LIEU NHIET\ERP) để máy khác cũng vào được.
    /// Kiểm chứng bằng file thử: ghi ở đường dẫn cục bộ, đọc thấy ở đường dẫn mạng. Không đổi được → null.</summary>
    public string? SharedPathFor(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || localPath.StartsWith(@"\\") || !StorePath.StartsWith(@"\\")) return null;
        var parts = StorePath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localPath));
        string? root = Path.GetPathRoot(p);
        if (string.IsNullOrEmpty(root)) return null;
        // ứng viên thư mục kho trên ổ cục bộ: <ổ>\<phần sau tên share> hoặc <ổ>\<cả đường dẫn sau tên máy>
        var tails = new[] { string.Join('\\', parts.Skip(2)), string.Join('\\', parts.Skip(1)) };
        foreach (var tail in tails.Where(t => t.Length > 0))
        {
            var localStore = Path.TrimEndingDirectorySeparator(Path.Combine(root, tail));
            if (!(p.Equals(localStore, StringComparison.OrdinalIgnoreCase) || p.StartsWith(localStore + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            var unc = StorePath.TrimEnd('\\') + p[localStore.Length..];
            try
            {
                if (!Directory.Exists(p)) continue;
                var name = $".cqnl-map-{Guid.NewGuid():N}.tmp";
                File.WriteAllText(Path.Combine(p, name), "map");
                try { if (File.Exists(Path.Combine(unc, name))) return unc; }
                finally { File.Delete(Path.Combine(p, name)); }
            }
            catch { }
        }
        return null;
    }
}
