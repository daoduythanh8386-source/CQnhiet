# Build + chạy toàn bộ kiểm thử .NET trên máy Windows. Kết quả: build_test.log + TestResults\*.trx
$ErrorActionPreference = 'Continue'
$here = $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$log = Join-Path $here 'build_test.log'
# Kiểm thử kho qua SMB thật: thư mục THỬ NGHIỆM (không phải kho nghiệp vụ DU LIEU NHIET)
$env:CQNL_UNC_TEST = '\\ADMIN-PC\Users\Admin\Documents\CQNhietLuyen\unc_test'
New-Item -ItemType Directory -Force $env:CQNL_UNC_TEST | Out-Null
Set-Location $here
"=== $(Get-Date) dotnet $(& dotnet --version)" | Set-Content $log -Encoding UTF8
if (-not (Test-Path (Join-Path $here 'CqNl.sln'))) {
  & dotnet new sln -n CqNl 2>&1 | Add-Content $log
  & dotnet sln CqNl.sln add src\CqNl.Domain\CqNl.Domain.csproj src\CqNl.Infrastructure\CqNl.Infrastructure.csproj src\CqNl.Application\CqNl.Application.csproj src\CqNl.Wpf\CqNl.Wpf.csproj tests\CqNl.Tests\CqNl.Tests.csproj tests\CqNl.UiTests\CqNl.UiTests.csproj 2>&1 | Add-Content $log
}
"=== BUILD" | Add-Content $log
& dotnet build CqNl.sln -c Release -nologo -clp:NoSummary 2>&1 | Add-Content $log
"BUILD EXIT $LASTEXITCODE" | Add-Content $log
"=== TEST" | Add-Content $log
& dotnet test CqNl.sln -c Release --no-build -nologo --logger "trx;LogFileName=ketqua.trx" --results-directory TestResults 2>&1 | Add-Content $log
"TEST EXIT $LASTEXITCODE" | Add-Content $log
Get-ChildItem (Join-Path $here 'tests\CqNl.Tests\bin\Release') -Recurse -Filter 'CQ_*.*' -ErrorAction SilentlyContinue | ForEach-Object { "E2E: $($_.FullName) $($_.Length) bytes" | Add-Content $log }
Get-ChildItem (Join-Path $here 'tests\CqNl.Tests\bin\Release') -Recurse -Filter 'ocr_report.txt' -ErrorAction SilentlyContinue | ForEach-Object { "OCR:"; Get-Content $_.FullName -Encoding UTF8 } | Add-Content $log
Get-ChildItem (Join-Path $here 'tests\CqNl.Tests\bin\Release') -Recurse -Filter 'ocr_vision_report.txt' -ErrorAction SilentlyContinue | ForEach-Object { "VISION:"; Get-Content $_.FullName -Encoding UTF8 } | Add-Content $log
if (Test-Path (Join-Path $env:CQNL_UNC_TEST 'perf_result.txt')) { "PERF: " + (Get-Content (Join-Path $env:CQNL_UNC_TEST 'perf_result.txt')) | Add-Content $log }
Get-ChildItem (Join-Path $here 'tests\CqNl.UiTests\bin\Release') -Recurse -Filter 'hieu_chinh_vi_tri.txt' -ErrorAction SilentlyContinue | ForEach-Object { "CALIB:"; Get-Content $_.FullName -Encoding UTF8 } | Add-Content $log
if (Test-Path (Join-Path $env:CQNL_UNC_TEST 'unc_result.txt')) { "UNC: " + (Get-Content (Join-Path $env:CQNL_UNC_TEST 'unc_result.txt')) | Add-Content $log }
Get-ChildItem (Join-Path $here 'tests\CqNl.UiTests\bin\Release') -Recurse -Filter '*.png' -ErrorAction SilentlyContinue | Where-Object { $_.DirectoryName -like '*\ui' } | ForEach-Object { "UI: $($_.Name) $($_.Length) bytes" | Add-Content $log }
Get-ChildItem (Join-Path $here 'tests\CqNl.UiTests\bin\Release') -Recurse -Filter 'thong_bao.txt' -ErrorAction SilentlyContinue | ForEach-Object { "UI-MSG:"; Get-Content $_.FullName -Encoding UTF8 } | Add-Content $log

# ================= ĐÓNG GÓI + BỘ CÀI + CÀI THỬ =================
"=== PUBLISH" | Add-Content $log
$pub = Join-Path $here 'publish'
& dotnet publish src\CqNl.Wpf\CqNl.Wpf.csproj -c Release -r win-x64 --self-contained true -nologo -o $pub 2>&1 | Select-Object -Last 5 | Add-Content $log
"PUBLISH EXIT $LASTEXITCODE" | Add-Content $log
$ver = ([xml](Get-Content (Join-Path $here 'Directory.Build.props'))).Project.PropertyGroup.Version
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
"=== ISCC $iscc version $ver" | Add-Content $log
$isccOut = & $iscc /Qp "/DAppVersion=$ver" "/DPublishDir=$pub" (Join-Path $here 'installer\CQNhietLuyen.iss') 2>&1
$isccOut | Select-Object -Last 8 | Add-Content $log
"ISCC EXIT $LASTEXITCODE (giao dien tieng Viet)" | Add-Content $log
if ($LASTEXITCODE -ne 0) {
  & $iscc /Qp "/DAppVersion=$ver" "/DPublishDir=$pub" "/DNoViMessages=1" (Join-Path $here 'installer\CQNhietLuyen.iss') 2>&1 | Select-Object -Last 5 | Add-Content $log
  "ISCC EXIT $LASTEXITCODE (du phong tieng Anh)" | Add-Content $log
}
$setup = Get-ChildItem (Join-Path $here 'installer_out') -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($setup) {
  "SETUP: $($setup.Name) $([math]::Round($setup.Length/1MB,1)) MB" | Add-Content $log
  $sig = Get-AuthenticodeSignature $setup.FullName; "SETUP SIGNATURE: $($sig.Status)" | Add-Content $log
  $dir = Join-Path $env:TEMP 'cqnl_install_test'; if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  $realLocal = Join-Path $env:LOCALAPPDATA 'CQNhietLuyen'; $realLocalBefore = Test-Path $realLocal
  $p = Start-Process $setup.FullName -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /DIR=`"$dir`" /LOG=`"$here\install.log`"" -PassThru -Wait
  "INSTALL EXIT $($p.ExitCode); exe=$(Test-Path "$dir\CQNhietLuyen.exe"); templates=$((Get-ChildItem "$dir\assets\templates" -Filter *.xlsx -ErrorAction SilentlyContinue).Count); maps=$((Get-ChildItem "$dir\assets\template-maps" -ErrorAction SilentlyContinue).Count); tessdata=$(Test-Path "$dir\assets\tessdata\eng.traineddata")" | Add-Content $log
  # chạy thử bản đã cài với KHO THỬ NGHIỆM (không đụng kho nghiệp vụ)
  $runStore = Join-Path $env:CQNL_UNC_TEST 'app_run'; New-Item -ItemType Directory -Force $runStore | Out-Null
  $runLocal = Join-Path $env:TEMP 'cqnl_install_local'; New-Item -ItemType Directory -Force $runLocal | Out-Null
  $app = Start-Process "$dir\CQNhietLuyen.exe" -ArgumentList "--store `"$runStore`" --local `"$runLocal`"" -PassThru
  Start-Sleep -Seconds 10; $app.Refresh()
  "RUN: alive=$(-not $app.HasExited) title='$($app.MainWindowTitle)' mem=$([math]::Round($app.WorkingSet64/1MB))MB" | Add-Content $log
  if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
  "RUN: store created=$(Test-Path (Join-Path $runStore 'CQNL\store.json')); local outbox=$(Test-Path (Join-Path $runLocal 'outbox'))" | Add-Content $log
  Get-ChildItem (Join-Path $runLocal 'logs') -ErrorAction SilentlyContinue | ForEach-Object { "RUN-ERRLOG: $($_.Name)"; Get-Content $_.FullName -TotalCount 20 } | Add-Content $log
  # gỡ cài đặt: chỉ xóa chương trình, dữ liệu giữ nguyên
  $u = Start-Process "$dir\unins000.exe" -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru -Wait
  for ($i = 0; $i -lt 30 -and (Test-Path "$dir\CQNhietLuyen.exe"); $i++) { Start-Sleep 1 }
  "UNINSTALL EXIT $($u.ExitCode); exe removed=$(-not (Test-Path "$dir\CQNhietLuyen.exe")); test local data kept=$(Test-Path (Join-Path $runLocal 'outbox')); test store kept=$(Test-Path (Join-Path $runStore 'CQNL\store.json')); real LocalAppData before=$realLocalBefore after=$(Test-Path $realLocal)" | Add-Content $log
}
"=== XONG $(Get-Date)" | Add-Content $log
