# Thử bộ cài Cốc Cốc chính thức trên máy Windows của GitHub: chạy im lặng với vài bộ tham số,
# mỗi lần tối đa 4 phút, in log bộ cập nhật, thư mục cài và tên trong Uninstall.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$dir = Join-Path $env:TEMP 'ccprobe'; New-Item -ItemType Directory -Force $dir | Out-Null
$exe = Join-Path $dir 'CocCocSetup.exe'
$wc = New-Object Net.WebClient; $wc.Headers.Add('User-Agent', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) ITHelpdeskTool')
$wc.DownloadFile('https://files.coccoc.com/browser/download/vi?plat=win&arch=x64', $exe)
"Bo cai: {0:N1} MB, ban {1}" -f ((Get-Item $exe).Length / 1MB), (Get-Item $exe).VersionInfo.ProductVersion
"IP ra ngoai: " + (Invoke-RestMethod 'https://api.ipify.org' -TimeoutSec 10)

function Show-State {
  "--- Program Files\CocCoc:"; Get-ChildItem "$env:ProgramFiles\CocCoc", "${env:ProgramFiles(x86)}\CocCoc" -Recurse -Depth 2 -ErrorAction SilentlyContinue | Select-Object -First 25 | ForEach-Object { '  ' + $_.FullName }
  "--- Uninstall:"; Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'c' -and ($_.Publisher -match 'Coc' -or $_.DisplayName -match 'C.c C.c|CocCoc') } | ForEach-Object { "  [$($_.DisplayName)] $($_.DisplayVersion) $($_.InstallLocation)" }
  "--- Tien trinh:"; Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -match 'CocCoc|ccprobe|SystemTemp' } | ForEach-Object { "  $($_.ProcessId) $($_.Name) $($_.CommandLine)" }
}

foreach ($a in '/silent /install', '--silent --install --system') {
  "===== Chay: $a"
  $t = Get-Date
  $p = Start-Process $exe -ArgumentList $a -PassThru
  [void]$p.Handle
  $done = $p.WaitForExit(240000)
  "Thoat: $done, ma: $(if ($done) { $p.ExitCode }), sau {0:N0} giay" -f ((Get-Date) - $t).TotalSeconds
  Show-State
  if (-not $done) { & taskkill.exe /T /F /PID $p.Id 2>&1 | Out-Null }
  if (Test-Path "$env:ProgramFiles\CocCoc\Browser\Application") { 'DA CAI TRINH DUYET'; break }
}

"===== Log updater"
Get-ChildItem 'C:\ProgramData', "${env:ProgramFiles(x86)}\CocCoc", "$env:LOCALAPPDATA", 'C:\Windows\SystemTemp', $env:TEMP -Recurse -Depth 5 -Filter '*.log' -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -match 'CocCoc' } | ForEach-Object {
    "### $($_.FullName)"
    Get-Content $_.FullName -ErrorAction SilentlyContinue | Where-Object { $_ -match 'C0CC0CBB|error|fail|Install|download|status|result|code' -and $_ -notmatch 'disk space' } |
      Select-Object -Last 60 | ForEach-Object { $_.Substring(0, [Math]::Min(260, $_.Length)) }
  }
