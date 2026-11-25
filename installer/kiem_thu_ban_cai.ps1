# TASK 33: KHÔNG build lại. Lấy bộ cài mới nhất trong BO_CAI → cài thử (chỉ cho người dùng, thư mục tạm) → chạy bản đã cài với KHO THỬ NGHIỆM
# → tự bấm chuột thật: tạo quản trị, đăng nhập, mở từng mục menu, chụp màn hình → đóng → gỡ cài đặt. Không đụng kho nghiệp vụ DU LIEU NHIET.
param([string]$Base = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Continue'
$base = $Base; $log = Join-Path $base 'kiem_thu_ban_cai.log'
"=== BAT DAU $(Get-Date)" | Set-Content $log -Encoding UTF8
function L($m) { "$(Get-Date -Format HH:mm:ss) $m" | Add-Content $log -Encoding UTF8 }
$shots = Join-Path $base 'e2e_kiem_thu_ban_cai'; if (Test-Path $shots) { Remove-Item $shots -Recurse -Force }; New-Item -ItemType Directory -Force $shots | Out-Null

$setup = Get-ChildItem (Join-Path $base 'BO_CAI') -Filter 'CQNhietLuyen-Setup-*.exe' | Sort-Object { [version](($_.Name -replace '^.*Setup-([\d\.]+)-.*$','$1')) } | Select-Object -Last 1
if (-not $setup) { L "KHONG THAY BO CAI v25"; exit 1 }
$boCai = Join-Path $base 'BO_CAI'; New-Item -ItemType Directory -Force $boCai | Out-Null
Copy-Item $setup.FullName $boCai -Force
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash
L "BO CAI: $($setup.Name) $([math]::Round($setup.Length/1MB,1)) MB SHA256=$hash -> $boCai"
$vi = (Get-Item $setup.FullName).VersionInfo; L "BO CAI VERSION: $($vi.ProductName) $($vi.ProductVersion) / $($vi.CompanyName)"

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetDlgItemText(IntPtr h, int id, System.Text.StringBuilder sb, int n);
  public static string MsgText(IntPtr h) { var sb = new System.Text.StringBuilder(2000); GetDlgItemText(h, 0xFFFF, sb, 2000); return sb.ToString(); }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(120); mouse_event(2,0,0,0,0); System.Threading.Thread.Sleep(60); mouse_event(4,0,0,0,0); }
}
'@
function SaveIcon($exe, $name) { try { $ic = [System.Drawing.Icon]::ExtractAssociatedIcon($exe); $ic.ToBitmap().Save((Join-Path $shots $name), 'Png'); L "ICON $name OK ($($ic.Width)x$($ic.Height))" } catch { L "ICON $name LOI $_" } }
function Shot($name, $hwnd) {
  try {
    $r = New-Object W+RECT; [void][W]::GetWindowRect($hwnd, [ref]$r)
    $wd = $r.R - $r.L; $ht = $r.B - $r.T; if ($wd -le 0) { $wd = 1366; $ht = 768 }
    $bmp = New-Object System.Drawing.Bitmap $wd, $ht; $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $bmp.Save((Join-Path $shots "$name.png"), 'Png'); $g.Dispose(); $bmp.Dispose(); L "SHOT $name ${wd}x${ht}"
  } catch { L "SHOT $name LOI $_" }
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function ById($root, $id) { $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function ByName($root, $n) { $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $n))) }
function ClickEl($el) { $b = $el.Current.BoundingRectangle; [W]::Click([int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2)) }
function SetVal($el, $v) { $p = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $p.SetValue($v) }
function TopWin($procId, $titleLike) {
  for ($i = 0; $i -lt 40; $i++) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    foreach ($w in $AE::RootElement.FindAll($TS::Children, $c)) { if ($w.Current.Name -like $titleLike -and $w.Current.ClassName -ne '#32770') { return $w } }
    Start-Sleep -Milliseconds 500
  }
  return $null
}
function CloseDialogs($procId) {   # bấm OK các hộp thông báo (MessageBox) nếu có, ghi lại nội dung
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
  $cands = @()
  foreach ($top in $AE::RootElement.FindAll($TS::Children, $c)) {
    if ($top.Current.ClassName -eq '#32770') { $cands += $top }
    # MessageBox có chủ (owner) nằm DƯỚI cửa sổ chủ trong cây UIA
    $cands += @($top.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, '#32770'))))
  }
  foreach ($w in $cands) {
    if ($w) {
      $hw = [IntPtr]$w.Current.NativeWindowHandle
      L "THONG BAO: [$($w.Current.Name)] $([W]::MsgText($hw))"
      [void][W]::PostMessage($hw, 0x0111, [IntPtr]1, [IntPtr]::Zero)   # WM_COMMAND IDOK = bấm OK
      Start-Sleep -Milliseconds 400
      if ([W]::MsgText($hw)) { [void][W]::PostMessage($hw, 0x0111, [IntPtr]6, [IntPtr]::Zero) }  # IDYES nếu là hộp Có/Không
      Start-Sleep 1
    }
  }
}

# 1) cài thử chỉ cho người dùng hiện tại vào thư mục tạm (bản cài thật ở Program Files KHÔNG bị đụng)
$dir = Join-Path $env:TEMP 'cqnl_v33_install'; if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
$p = Start-Process $setup.FullName -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /DIR=`"$dir`" /LOG=`"$base\install_v33.log`"" -PassThru -Wait
$exe = Join-Path $dir 'CQNhietLuyen.exe'
L "INSTALL EXIT $($p.ExitCode); exe=$(Test-Path $exe); version=$((Get-Item $exe -ErrorAction SilentlyContinue).VersionInfo.FileVersion)"
SaveIcon $exe 'icon_exe.png'; SaveIcon $setup.FullName 'icon_bo_cai.png'
$lnk = Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*Nhi*t Luy*n*' -or $_.Name -like '*CQ*' } | Select-Object -First 3
foreach ($k in $lnk) { L "SHORTCUT: $($k.FullName)" }

# 2) chạy với kho thử nghiệm mới (không phải kho nghiệp vụ)
$store = "\\ADMIN-PC\Users\Admin\Documents\CQNhietLuyen\unc_test\app_v33_$(Get-Date -Format yyyyMMddHHmmss)"; New-Item -ItemType Directory -Force $store | Out-Null
$local = Join-Path $env:TEMP 'cqnl_v33_local'; if (Test-Path $local) { Remove-Item $local -Recurse -Force }; New-Item -ItemType Directory -Force $local | Out-Null
$app = Start-Process $exe -ArgumentList "--store `"$store`" --local `"$local`"" -PassThru
L "RUN pid=$($app.Id) store=$store"
$login = TopWin $app.Id '*Đăng nhập*'
if (-not $login) { L "LOI: khong thay cua so dang nhap"; } else {
  $h = [IntPtr]$login.Current.NativeWindowHandle; [void][W]::SetForegroundWindow($h); Start-Sleep 3
  Shot '01_dang_nhap_lan_dau' $h
  # tạo quản trị (giá trị thử nghiệm, chỉ cho kho thử)
  $pw = 'Thu' + (Get-Random -Minimum 100000 -Maximum 999999) + '!a'
  # kho trống + máy chứa kho: KHÔNG tự hiện form; phải bấm "Khởi tạo quản trị cho kho mới…" và xác nhận
  $fa = ById $login 'FirstAdminPanel'; L "FORM TAO ADMIN HIEN SAN (phai False): $([bool]$fa -and -not $fa.Current.IsOffscreen); nut khoi tao: $([bool](ById $login 'InitBtn'))"
  ClickEl (ById $login 'InitBtn'); Start-Sleep 2; CloseDialogs $app.Id; Start-Sleep 2
  SetVal (ById $login 'NewUserBox') 'admin'; SetVal (ById $login 'NewNameBox') 'Quản trị thử'
  SetVal (ById $login 'NewPassBox') $pw; SetVal (ById $login 'NewPass2Box') $pw
  ClickEl (ByName $login 'Tạo tài khoản quản trị'); Start-Sleep 4; CloseDialogs $app.Id; Start-Sleep 2
  Shot '02_dang_nhap' $h
  SetVal (ById $login 'UserBox') 'khongco'; SetVal (ById $login 'PassBox') 'abc123'
  ClickEl (ById $login 'LoginBtn'); Start-Sleep 3; Shot '02b_tai_khoan_khong_co' $h; CloseDialogs $app.Id; Start-Sleep 1
  SetVal (ById $login 'UserBox') 'admin'; SetVal (ById $login 'PassBox') $pw
  ClickEl (ById $login 'LoginBtn'); Start-Sleep 6; CloseDialogs $app.Id
  $main = TopWin $app.Id 'CQ Nhiệt Luyện'
  if (-not $main) { L "LOI: khong mo duoc cua so chinh" } else {
    L "DANG NHAP OK: cua so chinh '$($main.Current.Name)'"
    $mh = [IntPtr]$main.Current.NativeWindowHandle; [void][W]::ShowWindow($mh, 3); [void][W]::SetForegroundWindow($mh); Start-Sleep 3
    $i = 3
    foreach ($nav in 'NavDash','NavOrders','NavCq','NavTemplates','NavUsers','NavAudit','NavSettings') {
      $el = ById $main $nav
      if (-not $el) { L "THIEU NUT $nav"; continue }
      ClickEl $el; Start-Sleep 3; CloseDialogs $app.Id
      $state = try { ($el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState } catch { '?' }
      Shot ("{0:00}_{1}" -f $i, $nav) $mh; L "MENU $nav ($($el.Current.Name)) toggle=$state"; $i++
    }
    $app.Refresh(); L "SAU KHI BAM HET MENU: alive=$(-not $app.HasExited) mem=$([math]::Round($app.WorkingSet64/1MB))MB"
  }
}
Get-ChildItem (Join-Path $local 'logs') -ErrorAction SilentlyContinue | ForEach-Object { L "ERRLOG $($_.Name)"; Get-Content $_.FullName -TotalCount 30 | Add-Content $log }
$audit = Get-ChildItem (Join-Path $store 'CQNL\audit') -Recurse -File -ErrorAction SilentlyContinue
L "KHO THU: store.json=$(Test-Path (Join-Path $store 'CQNL\store.json')); so su kien nhat ky=$($audit.Count)"
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force; Start-Sleep 2 }

# 3) gỡ cài đặt: chỉ xóa chương trình, dữ liệu giữ nguyên
$u = Start-Process (Join-Path $dir 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru -Wait
for ($j = 0; $j -lt 30 -and (Test-Path $exe); $j++) { Start-Sleep 1 }
L "UNINSTALL EXIT $($u.ExitCode); exe removed=$(-not (Test-Path $exe)); local data kept=$(Test-Path $local); store kept=$(Test-Path (Join-Path $store 'CQNL\store.json'))"
L "BAN CAI THAT (Program Files) van con: $(Test-Path 'C:\Program Files\CQ Nhiet Luyen\CQNhietLuyen.exe')"
"=== XONG $(Get-Date)" | Add-Content $log
