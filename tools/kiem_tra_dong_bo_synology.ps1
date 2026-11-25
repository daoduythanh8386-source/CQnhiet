# Kiểm tra máy này đã sẵn sàng chạy CQ Nhiệt Luyện ĐỘC LẬP trên bản đồng bộ Synology Drive (0.9.2).
# Chỉ ĐỌC — không sửa, không xoá gì. Chuột phải → Run with PowerShell.
param([string]$Kho = "$env:USERPROFILE\SynologyDrive\DU LIEU NHIET")
$ok = $true
function Bao($muc, $tot, $chiTiet) { $script:ok = $script:ok -and $tot; Write-Host ("[{0}] {1}: {2}" -f ($(if ($tot) { "OK " } else { "LOI" })), $muc, $chiTiet) -ForegroundColor $(if ($tot) { "Green" } else { "Red" }) }

$p = Get-Process | Where-Object { $_.ProcessName -like "cloud-drive*" }
Bao "Synology Drive Client" ($null -ne $p) $(if ($p) { "dang chay" } else { "CHUA chay - cai Synology Drive Client, dang nhap NAS edoc.oristar.vn, dong bo thu muc TQM HUNG YEN" })

Bao "Thu muc kho" (Test-Path $Kho) $Kho
$store = Join-Path $Kho "CQNL\store.json"
Bao "Du lieu kho" (Test-Path $store) $(if (Test-Path $store) { "da dong bo ($store)" } else { "CHUA thay $store - cho Synology Drive dong bo xong" })

if (Test-Path (Join-Path $Kho "CQNL")) {
    # file "chi tren may chu" (placeholder, chua tai ve) → app doc cham / loi khi mat mang
    $ph = Get-ChildItem (Join-Path $Kho "CQNL") -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { ($_.Attributes -band 0x00400000) -or ($_.Attributes -band [IO.FileAttributes]::Offline) } | Select-Object -First 5
    Bao "File da tai ve may" ($null -eq $ph) $(if ($ph) { "co file chua tai ve (vd $($ph[0].FullName)) - chuot phai thu muc DU LIEU NHIET -> Synology Drive -> 'Luon giu tren thiet bi nay'" } else { "tat ca file nam tren may" })
    $cf = Get-ChildItem (Join-Path $Kho "CQNL\records") -Recurse -File -Filter "*Conflict*" -ErrorAction SilentlyContinue | Select-Object -First 3
    Bao "File xung dot Synology" ($null -eq $cf) $(if ($cf) { "co file 'Conflict' trong records (app bo qua, bao quan tri xem): $($cf[0].FullName)" } else { "khong co" })
}
try { $r = Test-NetConnection edoc.oristar.vn -Port 6690 -WarningAction SilentlyContinue; Bao "Ket noi NAS" $r.TcpTestSucceeded "edoc.oristar.vn:6690" } catch { Bao "Ket noi NAS" $false $_.Exception.Message }

Write-Host ""
if ($ok) { Write-Host "MAY NAY SAN SANG. Mo app, kho: $Kho" -ForegroundColor Green } else { Write-Host "Con muc LOI o tren - sua roi chay lai." -ForegroundColor Yellow }
Read-Host "Nhan Enter de dong"
