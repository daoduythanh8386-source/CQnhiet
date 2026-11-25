# KẾT NỐI MÁY QC / KINH DOANH VỚI KHO CHUNG trên ADMIN-PC — chạy 1 lần trên MỖI máy (chuột phải → Run with PowerShell).
# Lưu tài khoản truy cập thư mục chia sẻ của ADMIN-PC vào Windows (Credential Manager) để app tự mở kho, không hỏi mật khẩu.
# Mật khẩu do chị gõ trực tiếp vào hộp của Windows — script không ghi mật khẩu ra file.
$ErrorActionPreference = 'Continue'
$may = 'ADMIN-PC'
$kho = '\\ADMIN-PC\Users\Admin\SynologyDrive\DU LIEU NHIET'
Write-Host "Kiểm tra kết nối tới $may ..."
if (-not (Test-Connection $may -Count 1 -Quiet)) { Write-Host "Không thấy $may trên mạng: kiểm tra máy chủ đã BẬT NGUỒN, cùng mạng công ty." -ForegroundColor Red; Read-Host 'Enter để đóng'; exit 1 }
if (Test-Path $kho) { Write-Host "Đã vào được kho chung: $kho" -ForegroundColor Green; Read-Host 'Enter để đóng'; exit 0 }
Write-Host "Chưa vào được kho chung — nhập tài khoản Windows của ADMIN-PC được phép đọc/ghi thư mục DU LIEU NHIET (vd ADMIN-PC\Admin)."
$c = Get-Credential -Message "Tài khoản truy cập \\$may"
if (-not $c) { exit 1 }
cmdkey /add:$may /user:$($c.UserName) /pass:$($c.GetNetworkCredential().Password) | Out-Null
if (Test-Path $kho) { Write-Host "OK — đã lưu tài khoản, vào được kho chung. Mở app CQ Nhiệt Luyện để dùng." -ForegroundColor Green }
else { Write-Host "Vẫn chưa vào được $kho — kiểm tra tên tài khoản / mật khẩu, hoặc quyền chia sẻ thư mục trên ADMIN-PC." -ForegroundColor Red }
Read-Host 'Enter để đóng'
