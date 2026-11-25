using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CqNl.Application;
using Microsoft.Win32;

namespace CqNl.Wpf.Views;

public sealed record SignerItem(string Id, string Name, string State, BitmapImage? Thumb);

/// <summary>Cài đặt (quản trị) — bố cục gọn theo mẫu người dùng: Nguồn dữ liệu · Dùng chung nhiều người · Dấu &amp; chữ ký ·
/// Tên khách in trên CQ · Cập nhật · Sao lưu · Reset dữ liệu. Chọn thư mục là LƯU luôn (không cần nút Lưu riêng), có kiểm tra quyền ghi.</summary>
public partial class SettingsView : UserControl
{
    private static Workspace Ws => App.Ws;
    private bool _loading;
    public SettingsView() { InitializeComponent(); Loaded += (_, _) => Reload(); }

    private static BitmapImage? Bmp(byte[]? b)
    {
        if (b is null) return null;
        var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 400; bi.StreamSource = new MemoryStream(b); bi.EndInit(); bi.Freeze(); return bi;
    }

    private static void Status(TextBlock t, string text, bool ok)
    {
        t.Text = (ok ? "✓ " : "⚠ ") + text;
        t.Foreground = ok ? new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)) : new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    }

    private void Reload()
    {
        _loading = true;
        try { ReloadCore(); } finally { _loading = false; }
        if (!_askedLocal) { _askedLocal = true; OfferNetworkPaths(); }
    }

    private static bool _askedLocal;
    /// <summary>Thư mục ERP / lưu CQ đang là ổ C của máy chứa kho → đề nghị đổi sang đường dẫn mạng tương ứng (đã kiểm bằng file thử) để mọi máy dùng được.</summary>
    private void OfferNetworkPaths()
    {
        if (!Ws.Online) return;
        SharedSettings? st = null; Ui.Run(() => st = Ws.Settings!.Get().Value);
        if (st is null) return;
        var erp = Ws.SharedPathFor(st.ErpFolder); var cq = Ws.SharedPathFor(st.CqFolder);
        if (erp is null && cq is null) return;
        var msg = "Các thư mục dùng chung đang là ổ C của máy này — máy khác KHÔNG vào được:\n\n"
                  + (erp is not null ? $"• Thư mục ERP: {st.ErpFolder}\n   → {erp}\n" : "")
                  + (cq is not null ? $"• Thư mục lưu CQ: {st.CqFolder}\n   → {cq}\n" : "")
                  + "\nĐổi sang đường dẫn mạng (cùng thư mục, đã kiểm tra mở được)?";
        if (!Ui.Ask(msg)) return;
        Ui.Run(() =>
        {
            if (erp is not null) Ws.Settings!.SetErpFolder(Ws.Session!, erp);
            if (cq is not null) { Ws.Settings!.SetCqFolder(Ws.Session!, cq); var cfg = LocalConfig.Load(App.LocalDir); cfg.SharedCqFolder = cq; try { cfg.Save(App.LocalDir); } catch { } }
        });
        _loading = true; try { ReloadCore(); } finally { _loading = false; }
    }

    /// <summary>Ghi chú: thư mục nằm trong kho chung → mọi máy (đồng bộ Synology Drive) tự dùng đúng thư mục này trên máy mình.</summary>
    private static string Shared(string? saved) => saved is not null && saved.StartsWith(Workspace.StoreToken, StringComparison.OrdinalIgnoreCase)
        ? " Nằm trong kho chung → mọi máy dùng chung." : "";

    private void ReloadCore()
    {
        StoreBox.Text = Ws.StorePath;
        var who = Ws.Session is { } se ? $"{se.User.Username} ({Ui.RoleText(se.User.Role)})" : "—";
        Status(StoreText, Ws.Online ? $"Đang dùng chung — người dùng hiện tại: {who}. Tự đồng bộ mỗi 30 giây." : "OFFLINE — " + Ws.OfflineReason, Ws.Online);
        var cur = Workspace.VersionText(Workspace.CurrentVersion);
        if (!Ws.Online) { Status(SteelText, "OFFLINE — cài đặt dùng chung cần kết nối kho chung", false); UpdateText.Text = $"Phiên bản đang chạy: {cur}"; return; }
        Ui.Run(() =>
        {
            var rel = Ws.Releases(); var newer = Ws.NewerRelease();
            UpdateText.Text = $"Phiên bản đang chạy: {cur} · " + (rel.Count == 0 ? "chưa phát hành bản nào lên kho chung."
                : $"bản mới nhất trên kho: {rel[0].Version} ({rel[0].At:dd/MM/yyyy}, {rel[0].PublishedBy})")
                + (newer is not null ? $" → máy này cần cập nhật lên {newer.Version}." : rel.Count > 0 ? " ✓ máy này đang dùng bản mới nhất." : "");
            UpdateNowBtn.IsEnabled = newer is not null;
        });
        Ui.Run(() =>
        {
            var st = Ws.Settings!.Get().Value;
            // nguồn dữ liệu
            SteelBox.Text = st.SteelTableName ?? "";
            CqNl.Infrastructure.Excel.SteelGradeTable? t = null;
            try { t = Ws.SteelTable(); } catch (Exception ex) { Status(SteelText, "Bảng mác hiện tại không đọc được: " + ex.Message, false); }
            if (st.SteelTableSha is null) Status(SteelText, "Chưa có bảng mác — bấm Chọn… để nạp file.", false);
            else if (t is not null) Status(SteelText, $"Đã đọc {t.Grades.Count} mác thép." + (t.Issues.Count > 0 ? $" {t.Issues.Count} cảnh báo: " + string.Join("; ", t.Issues.Take(3)) + (t.Issues.Count > 3 ? "…" : "") : ""), t.Issues.Count == 0);
            var erpPath = Ws.Settings!.ErpFolderPath(); var cqPath = Ws.Settings.CqFolderPath();
            ErpFolderBox.Text = erpPath ?? "";
            if (st.ErpFolder is null) Status(ErpText, "Chưa chọn — vẫn nhập tay được ở Đơn gia công → Nhập ERP ▾.", false);
            else if (Directory.Exists(erpPath)) Status(ErpText, $"Máy này vào được · {Ws.CountNewErpFiles()} file chưa nhập." + Shared(st.ErpFolder), true);
            else Status(ErpText, "Máy này KHÔNG vào được thư mục này.", false);
            CqFolderBox.Text = cqPath ?? "";
            if (st.CqFolder is null) Status(CqFolderText, "Chưa chọn — mỗi máy đang lưu CQ vào Documents\\CQ Nhiet Luyen của máy đó.", false);
            else if (Directory.Exists(cqPath)) Status(CqFolderText, "Máy này vào được. Mọi máy lưu CQ vào đây (máy nào cần vẫn chọn riêng ở tab Xuất CQ)." + Shared(st.CqFolder), true);
            else Status(CqFolderText, "Máy này KHÔNG vào được thư mục này.", false);
            var tpls = Ws.TemplateList();
            TemplateText.Text = $"Template CQ: {tpls.Count} mẫu ({string.Join(", ", tpls.Select(x => x.DisplayName))}) — thêm / sửa ở mục Template CQ (menu bên trái).";
            // tên khách
            AliasGrid.ItemsSource = st.CustomerAliases.OrderBy(x => x.Value).ThenBy(x => x.Key).ToList();
            AliasText.Text = st.CustomerAliases.Count == 0 ? "Chưa có tên viết tắt — CQ đang in tên đầy đủ theo ERP." : $"{st.CustomerAliases.Count} khách hàng đã có tên viết tắt.";
            // dấu + chữ ký
            var act = st.Signers.Where(x => x.Active).OrderBy(x => x.Name).ToList();
            SignerList.ItemsSource = act.Select(x => new SignerItem(x.Id, x.Name,
                x.Id == st.DefaultApproverId && x.Id == st.DefaultCheckerId ? "Người duyệt + người kiểm tra mặc định"
                : x.Id == st.DefaultApproverId ? "Người duyệt mặc định" : x.Id == st.DefaultCheckerId ? "Người kiểm tra mặc định" : "",
                Bmp(Ws.ImageBytes(x.PaperSha, "png")))).ToList();
            DefApprover.ItemsSource = act; DefChecker.ItemsSource = act;
            DefApprover.SelectedItem = act.FirstOrDefault(x => x.Id == st.DefaultApproverId); DefChecker.SelectedItem = act.FirstOrDefault(x => x.Id == st.DefaultCheckerId);
            StampImg.Source = st.StampSha is null ? null : Bmp(Ws.ImageBytes(st.StampSha, "png"));
            StampText.Text = st.StampSha is null ? "Chưa có con dấu — chưa duyệt được CQ có dấu." : "Đang dùng (dùng chung mọi máy).";
        });
    }

    /// <summary>Đường dẫn không phải thư mục mạng → hỏi lại (máy khác sẽ không dùng chung được).</summary>
    private bool ConfirmShared(string f, string what) =>
        f.StartsWith(@"\\") || (Ws.FolderToken(f)?.StartsWith(Workspace.StoreToken) ?? false)     // trong kho đồng bộ → máy nào cũng có
        || Ui.Ask($"\"{f}\" là thư mục trên máy này (không phải đường dẫn mạng \\\\TÊN-MÁY\\...).\nCác máy khác sẽ KHÔNG {what} được. Vẫn dùng?");

    private void TestStore_Click(object sender, RoutedEventArgs e) { Ui.Run(() => Ws.Probe()); Reload(); if (Window.GetWindow(this) is MainWindow m) m.ShowStatus(Ws.Status()); }

    private void ChangeStore_Click(object sender, RoutedEventArgs e)
    {
        var p = Ui.Prompt(Window.GetWindow(this), "Đường dẫn kho chung", "Thư mục DU LIEU NHIET do Synology Drive đồng bộ về máy này. Mặc định: " + AppInfo.DefaultStore + " (= " + AppInfo.Expand(AppInfo.DefaultStore) + ")", Ws.StorePath);
        if (p is null || p == Ws.StorePath) return;
        if (!Ui.Ask($"Đổi kho sang:\n{p}\n\nỨng dụng sẽ đăng xuất để dùng kho mới. Tiếp tục?")) return;
        var cfg = LocalConfig.Load(App.LocalDir); cfg.StorePath = p; cfg.Save(App.LocalDir);
        Ui.Info("Đã lưu. Hãy đăng nhập lại.");
        Ws.Logout(); App.Rebuild(p); App.StartLogin();
        if (Window.GetWindow(this) is MainWindow m) { m.SkipPrompt(); m.Close(); }
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Bộ cài CQ Nhiệt Luyện (CQNhietLuyen-Setup-*.exe)|CQNhietLuyen-Setup-*.exe|Chương trình (*.exe)|*.exe", Title = "Chọn file bộ cài mới" };
        if (d.ShowDialog() != true) return;
        var v = Workspace.SetupVersion(d.FileName);
        if (v is null) { Ui.Info("Không đọc được số phiên bản của file này."); return; }
        var notes = Ui.Prompt(Window.GetWindow(this), "Phát hành bản cập nhật", $"Bộ cài phiên bản {Workspace.VersionText(v)}.\nGhi chú thay đổi (hiện cho người dùng khi được hỏi cập nhật):", "", required: false);
        if (notes is null) return;
        if (Ui.Run(() => { var r = Ws.PublishRelease(d.FileName, notes); Ui.Info($"Đã phát hành bản {r.Version} lên kho chung. Các máy sẽ được hỏi cập nhật khi mở ứng dụng."); })) Reload();
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        var r = Ws.NewerRelease();
        if (r is not null) Updater.Run(Window.GetWindow(this), r, ask: true);
    }

    private async void ImportAliases_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Sổ CQ / theo dõi đơn hàng (*.xlsx;*.xlsm)|*.xlsx;*.xlsm", Title = "Chọn file CQ 20xx.xlsx (có sheet THEO DÕI ĐƠN HÀNG)" };
        if (d.ShowDialog() != true) return;
        var path = d.FileName;
        var scan = await Ui.RunAsync(this, "Đang đọc sổ theo dõi…", () => Ws.ScanTrackingAliases(path), AliasText);
        if (scan is null) return;
        if (scan.Proposals.Count == 0) { Ui.Info($"Đọc {scan.TrackingRows} dòng trong sheet \"{scan.Sheet}\" — không có đề xuất mới (khách hàng chưa có đơn ERP trong kho, hoặc đã có tên viết tắt)."); return; }
        var sample = string.Join("\n", scan.Proposals.Take(15).Select(p => $"• {p.ErpName}  →  {p.ShortName}   ({p.Source}, {p.Orders} đơn)"));
        if (!Ui.Ask($"Đọc {scan.TrackingRows} dòng trong sheet \"{scan.Sheet}\".\nĐề xuất {scan.Proposals.Count} tên viết tắt" + (scan.UnmappedCustomers > 0 ? $" ({scan.UnmappedCustomers} khách chưa dò được — sửa tay trong màn hình đơn)" : "") + ":\n\n"
                    + sample + (scan.Proposals.Count > 15 ? $"\n… và {scan.Proposals.Count - 15} khách khác" : "") + "\n\nLưu các tên viết tắt này?")) return;
        if (Ui.Run(() => Ws.ApplyAliasProposals(scan.Proposals))) Reload();
    }

    private void DeleteAlias_Click(object sender, RoutedEventArgs e)
    {
        if (AliasGrid.SelectedItem is not KeyValuePair<string, string> kv) { Ui.Info("Chọn một dòng"); return; }
        if (!Ui.Ask($"Xoá tên viết tắt \"{kv.Value}\" của khách \"{kv.Key}\"? CQ sau này sẽ in tên đầy đủ theo ERP.")) return;
        if (Ui.Run(() => Ws.SetCustomerAlias(kv.Key, null))) Reload();
    }





    private void Steel_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Bảng mác (*.xlsx)|*.xlsx", Title = "Chọn file bảng mác thép" };
        if (d.ShowDialog() != true) return;
        if (Ui.Run(() =>
        {
            var t = Ws.Settings!.SetSteelTable(Ws.Session!, File.ReadAllBytes(d.FileName), Path.GetFileName(d.FileName));
            Ui.Info($"Đã cập nhật bảng mác: {t.Grades.Count} dòng." + (t.Issues.Count > 0 ? $"\n{t.Issues.Count} cảnh báo (mác trùng khác thông số, thiếu nhiệt độ…) — QC sẽ được yêu cầu chọn/nhập tay khi gặp." : ""));
        })) Reload();
    }

    private void AddSigner_Click(object sender, RoutedEventArgs e)
    {
        var w = new SignatureImportWindow { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() == true) Reload();
    }


    private void Stamp_Click(object sender, RoutedEventArgs e)
    {
        var w = new SignatureImportWindow(stamp: true) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() == true) Reload();
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Chọn thư mục đặt bản sao lưu (nên khác ổ/khác máy với kho)" };
        if (d.ShowDialog() != true) return;
        var dest = d.FolderName;
        var r = await Ui.RunAsync(this, "Đang sao lưu…", () =>
        {
            Ws.RequireOnline("sao lưu");
            var p = BackupService.Backup(Ws.DataRoot, dest);
            Ws.Audit!.Write(Ws.Session!.User.Username, "backup", p);
            return p;
        }, BackupText);
        if (r is not null) BackupText.Text = "Đã sao lưu và kiểm tra toàn vẹn: " + r;
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Chọn thư mục bản sao lưu (backup-yyyyMMdd-HHmmss)" };
        if (d.ShowDialog() != true) return;
        var dir = d.FolderName;
        var bad = await Ui.RunAsync(this, "Đang kiểm tra…", () =>
        {
            if (!File.Exists(Path.Combine(dir, "manifest.json"))) throw new BusinessException("Thư mục này không phải bản sao lưu (thiếu manifest.json)");
            return BackupService.Verify(dir);
        }, BackupText);
        if (bad is null) return;
        BackupText.Text = bad.Count == 0 ? "Bản sao lưu nguyên vẹn (mọi file khớp mã kiểm)." : $"PHÁT HIỆN {bad.Count} file hỏng/thiếu: " + string.Join(", ", bad.Take(10));
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var src = new OpenFolderDialog { Title = "Chọn bản sao lưu cần khôi phục" };
        if (src.ShowDialog() != true) return;
        var dst = new OpenFolderDialog { Title = "Chọn thư mục TRỐNG để khôi phục vào" };
        if (dst.ShowDialog() != true) return;
        var target = Path.Combine(dst.FolderName, AppInfo.DataFolder);
        if (!Ui.Ask($"Khôi phục\n{src.FolderName}\nvào\n{target}\n\nKho đang dùng KHÔNG bị thay đổi.")) return;
        var ok = await Ui.RunAsync(this, "Đang khôi phục…", () => { BackupService.Restore(src.FolderName, target); if (Ws.Online) Ws.Audit!.Write(Ws.Session!.User.Username, "restore", target, src.FolderName); return true; }, BackupText);
        if (ok) BackupText.Text = $"Đã khôi phục và kiểm tra: {target}\nMuốn dùng bản này: Đổi đường dẫn kho thành {dst.FolderName}";
    }

    // ---------------- nguồn dữ liệu: chọn là lưu luôn ----------------
    private void BrowseErp_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Chọn thư mục chứa file ERP MF0015 (trong kho chung)" };
        if (!string.IsNullOrWhiteSpace(ErpFolderBox.Text) && Directory.Exists(ErpFolderBox.Text)) d.InitialDirectory = ErpFolderBox.Text;
        if (d.ShowDialog() != true) return;
        var f = Ws.SharedPathFor(d.FolderName) ?? d.FolderName;                    // ổ C máy chứa kho → đường dẫn mạng tương ứng
        if (!ConfirmShared(f, "tự nhập ERP từ thư mục này")) return;
        if (Ui.Run(() => Ws.Settings!.SetErpFolder(Ws.Session!, f))) Reload();
    }

    private async void ScanErp_Click(object sender, RoutedEventArgs e)
    {
        var scan = await Ui.RunAsync(this, "", () => Ws.ScanErpFolder());
        if (scan is null) return;
        if (scan.NewFiles.Count == 0) { Ui.Info($"Không có file ERP mới trong thư mục:\n{scan.Folder}"); return; }
        if (new ImportPreviewWindow(scan) { Owner = Window.GetWindow(this) }.ShowDialog() == true) Reload();
    }

    private void BrowseCqFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Chọn thư mục lưu CQ chung (trong kho chung)" };
        if (!string.IsNullOrWhiteSpace(CqFolderBox.Text) && Directory.Exists(CqFolderBox.Text)) d.InitialDirectory = CqFolderBox.Text;
        if (d.ShowDialog() != true) return;
        var f = Ws.SharedPathFor(d.FolderName) ?? d.FolderName;                    // ổ C máy chứa kho → đường dẫn mạng tương ứng
        if (!ConfirmShared(f, "lưu CQ vào thư mục này")) return;
        if (Ui.Run(() => Ws.Settings!.SetCqFolder(Ws.Session!, f)))
        {
            var cfg = LocalConfig.Load(App.LocalDir); cfg.SharedCqFolder = f; try { cfg.Save(App.LocalDir); } catch { }
            Reload();
        }
    }

    private void OpenCqFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = !string.IsNullOrWhiteSpace(CqFolderBox.Text) ? CqFolderBox.Text : LocalConfig.Load(App.LocalDir).PdfRoot;
        if (Directory.Exists(dir)) Ui.OpenFile(dir); else Ui.Info("Máy này không vào được thư mục: " + dir);
    }

    // ---------------- dùng chung nhiều người ----------------
    private void OpenStore_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(Ws.StorePath)) Ui.OpenFile(Ws.StorePath); else Ui.Info("Máy này không vào được thư mục kho: " + Ws.StorePath);
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var r = await Ui.RunAsync(this, "", () => Ws.Sync());
        if (r is null) return;
        Ui.Info(r.Offline ? "Không đồng bộ được: " + (r.Error ?? "kho chung không truy cập được") + $". Còn {r.Pending} nháp chờ."
                          : $"Đồng bộ xong: đã đẩy {r.Pushed} nháp, {r.Conflicts} xung đột, còn {r.Pending} mục.");
        if (Window.GetWindow(this) is MainWindow m) m.ShowStatus(Ws.Status());
        Reload();
    }

    private void SwitchUser_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Ask("Đăng xuất để người khác đăng nhập trên máy này?")) return;
        Ws.Logout(); App.StartLogin();
        if (Window.GetWindow(this) is MainWindow m) { m.SkipPrompt(); m.Close(); }
    }

    private void ChangePass_Click(object sender, RoutedEventArgs e) => new ChangePasswordWindow(false) { Owner = Window.GetWindow(this) }.ShowDialog();
    private void CheckMachine_Click(object sender, RoutedEventArgs e) => new MachineCheckWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    // ---------------- chữ ký ----------------
    private void RenameSigner_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        var cur = (SignerList.ItemsSource as IEnumerable<SignerItem>)?.FirstOrDefault(x => x.Id == id);
        var n = Ui.Prompt(Window.GetWindow(this), "Đổi tên người ký", "Tên mới (CQ đã duyệt giữ nguyên chữ ký đã in):", cur?.Name ?? "");
        if (n is null || n.Trim() == cur?.Name) return;
        if (Ui.Run(() => Ws.Settings!.RenameSigner(Ws.Session!, id, n))) Reload();
    }

    private void RemoveSigner_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        var cur = (SignerList.ItemsSource as IEnumerable<SignerItem>)?.FirstOrDefault(x => x.Id == id);
        if (!Ui.Ask($"Bỏ chữ ký \"{cur?.Name}\" khỏi danh sách? CQ đã duyệt trước đây không bị ảnh hưởng; ảnh chữ ký vẫn lưu trong kho.")) return;
        if (Ui.Run(() => Ws.Settings!.RemoveSigner(Ws.Session!, id))) Reload();
    }

    private void Defaults_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var a = (DefApprover.SelectedItem as SignerRecord)?.Id; var c = (DefChecker.SelectedItem as SignerRecord)?.Id;
        if (Ui.Run(() => Ws.Settings!.SetDefaults(Ws.Session!, a, c))) Reload();
    }

    // ---------------- reset dữ liệu ----------------
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var plan = await Ui.RunAsync(this, "", () => Ws.PlanReset(), ResetText);
        var (records, others, pending) = plan;
        if (others is null) return;
        if (others.Count > 0) { Ui.Info("Đang có người dùng ở máy khác: " + string.Join(", ", others) + ".\nNhờ họ đăng xuất rồi reset lại."); return; }
        if (pending > 0) { Ui.Info($"Máy này còn {pending} nháp chưa đồng bộ — bấm \"Đồng bộ ngay\" trước."); return; }
        var d = new OpenFolderDialog { Title = "Chọn thư mục đặt BẢN SAO LƯU trước khi reset (nên khác thư mục kho)" };
        if (d.ShowDialog() != true) return;
        var bkRoot = d.FolderName;
        var typed = Ui.Prompt(Window.GetWindow(this), "Xác nhận RESET dữ liệu",
            $"Sẽ XOÁ {records} bản ghi (đơn, CQ, lưu ý, file ERP đã nhập) cùng ảnh, nhật ký, số báo cáo đã cấp.\n" +
            $"Trước khi xoá, app sao lưu toàn bộ kho vào:\n{bkRoot}\n\nGõ chữ RESET để xác nhận:", "");
        if (typed is null) return;
        if (typed.Trim() != "RESET") { Ui.Info("Chưa gõ đúng chữ RESET — không xoá gì."); return; }
        var prog = new Progress<string>(txt => ResetText.Text = txt);
        var r = await Ui.RunAsync(this, "Đang reset…", () => Ws.ResetData(bkRoot, prog));
        if (r is null) { ResetText.Text = "Chưa reset — dữ liệu giữ nguyên."; return; }
        ResetText.Text = $"Đã reset: xoá {r.Records} bản ghi, {r.BlobsDeleted} file ảnh/CQ. Bản sao lưu (đã kiểm tra): {r.BackupDir}";
        Ui.Info(ResetText.Text + "\n\nCác máy khác tự làm mới khi kết nối lại. App sẽ tự nhập lại đơn từ thư mục ERP.");
        Reload();
    }
}
