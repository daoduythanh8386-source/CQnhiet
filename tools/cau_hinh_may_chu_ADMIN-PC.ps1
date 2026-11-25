# CAU HINH MAY CHU KHO CHUNG (ADMIN-PC) — chị tự chạy 1 lần: chuột phải → "Run with PowerShell" (cần quyền quản trị).
# Mục đích: chỉ cần BẬT NGUỒN ADMIN-PC (không cần đăng nhập Windows) là các máy khác vẫn mở app, tạo / duyệt CQ.
# Script CHỈ chỉnh: chế độ ngủ/ngủ đông, tường lửa "Chia sẻ tệp và máy in" (mạng nội bộ), dịch vụ chia sẻ file.
# KHÔNG đổi quyền thư mục, KHÔNG xoá / di chuyển dữ liệu.
$ErrorActionPreference = 'Continue'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""; exit
}
$kho = 'C:\Users\Admin\SynologyDrive\DU LIEU NHIET'
$log = Join-Path $PSScriptRoot 'cau_hinh_may_chu.log'
function Ghi($t) { Write-Host $t; $t | Add-Content $log -Encoding UTF8 }
"=== $(Get-Date)" | Set-Content $log -Encoding UTF8

Ghi '1) Không cho máy NGỦ / NGỦ ĐÔNG khi cắm điện (máy ngủ thì các máy khác mất kết nối kho)'
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg /hibernate off            # tắt ngủ đông + "khởi động nhanh" → bật nguồn là chạy sạch, chia sẻ file sẵn sàng
Ghi '   xong: không ngủ, không ngủ đông (màn hình vẫn tắt bình thường)'

Ghi '2) Dịch vụ chia sẻ file (Server / LanmanServer) tự chạy khi bật máy — chạy cả khi CHƯA đăng nhập'
Set-Service LanmanServer -StartupType Automatic; Start-Service LanmanServer
Ghi ('   trạng thái: ' + (Get-Service LanmanServer).Status)

Ghi '3) Tường lửa: mở "Chia sẻ tệp và máy in" cho mạng nội bộ (Private / Domain)'
Get-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue |
  Where-Object { $_.Profile -match 'Private|Domain|Any' } | Enable-NetFirewallRule
Ghi ('   số luật đang bật: ' + @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28502' | Where-Object Enabled -eq 'True').Count)

Ghi '4) Kiểu mạng: phải là "Private" (mạng công ty) — mạng "Public" chặn chia sẻ file'
Get-NetConnectionProfile | ForEach-Object { Ghi ("   " + $_.InterfaceAlias + ': ' + $_.NetworkCategory) }
$pub = @(Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public')
if ($pub.Count -gt 0) {
  $ans = Read-Host '   Có mạng đang để Public. Đổi sang Private (mạng công ty tin cậy)? (c/k)'
  if ($ans -eq 'c') { $pub | Set-NetConnectionProfile -NetworkCategory Private; Ghi '   đã đổi sang Private' }
}

Ghi '5) Kiểm tra thư mục kho chung và thư mục chia sẻ'
Ghi ("   kho: $kho → " + (Test-Path $kho))
Get-SmbShare | Where-Object { $kho.StartsWith($_.Path, [StringComparison]::OrdinalIgnoreCase) -and $_.Path.Length -gt 3 } |
  ForEach-Object { Ghi ("   chia sẻ: \\$env:COMPUTERNAME\" + $_.Name + '  →  ' + $_.Path) }
Ghi ''
Ghi 'XONG. Việc còn lại làm tay 1 lần (xem HUONG_DAN_MAY_CHU_VA_CAI_DAT.md):'
Ghi ' - BIOS: "Restore on AC Power Loss" = Power On (mất điện có lại tự bật máy) — nếu muốn.'
Ghi ' - Synology Drive chỉ chạy khi có người ĐĂNG NHẬP Windows: chưa đăng nhập thì bản sao lên NAS tạm dừng,'
Ghi '   app vẫn dùng bình thường; lần đăng nhập sau Synology tự đồng bộ bù.'
Read-Host 'Bấm Enter để đóng'
