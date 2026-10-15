$ErrorActionPreference = 'Continue'
$d = "$env:TEMP\zp"; New-Item -ItemType Directory -Force $d | Out-Null
Invoke-WebRequest 'https://zalo.me/download/zalo-pc?utm=90000' -OutFile "$d\ZaloSetup.exe" -UseBasicParsing
Start-Process "$d\ZaloSetup.exe" -ArgumentList '/S' -Wait
Start-Sleep 15
$root = "$env:LOCALAPPDATA\Programs\Zalo"
$inner = (Get-ChildItem $root -Directory -Filter 'Zalo-*' | Sort-Object Name -Descending | Select-Object -First 1).FullName + '\Zalo.exe'
"inner: $inner"
Get-ChildItem (Split-Path $inner) | Select-Object -First 40 | ForEach-Object { "  $($_.Name)" }
function Mains { @(Get-CimInstance Win32_Process -Filter "Name='Zalo.exe'" | Where-Object { $_.CommandLine -notmatch '--type=' -and $_.ExecutablePath -like '*Zalo-*' }) }
function KillAll { Get-Process Zalo -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 3 }
KillAll
"--- 1: inner, mặc định"
Start-Process $inner; Start-Sleep 20
"mains: $((Mains).Count)"; (Mains) | ForEach-Object { "  $($_.ProcessId) $($_.CommandLine)" }
"--- 2: inner, mặc định lần 2 (khoá 1 phiên?)"
Start-Process $inner; Start-Sleep 15
"mains: $((Mains).Count)"
"--- 3: inner --user-data-dir"
$u = "$env:TEMP\zalo-u2"
Start-Process $inner -ArgumentList "--user-data-dir=`"$u`""; Start-Sleep 20
"mains: $((Mains).Count) · u2 files: $(@(Get-ChildItem $u -Recurse -ErrorAction SilentlyContinue).Count)"
Get-ChildItem $u -ErrorAction SilentlyContinue | Select-Object -First 12 | ForEach-Object { "  $($_.Name)" }
(Mains) | ForEach-Object { "  $($_.ProcessId) $($_.CommandLine)" }
KillAll
"--- 4: inner + env APPDATA/LOCALAPPDATA/USERPROFILE"
Start-Process $inner; Start-Sleep 15
$r3 = "$env:TEMP\zalo-e3"; New-Item -ItemType Directory -Force "$r3\AppData\Roaming","$r3\AppData\Local" | Out-Null
$psi = New-Object System.Diagnostics.ProcessStartInfo $inner
$psi.UseShellExecute = $false
$psi.Environment['APPDATA'] = "$r3\AppData\Roaming"; $psi.Environment['LOCALAPPDATA'] = "$r3\AppData\Local"; $psi.Environment['USERPROFILE'] = $r3
[System.Diagnostics.Process]::Start($psi) | Out-Null; Start-Sleep 20
"mains: $((Mains).Count) · e3 files: $(@(Get-ChildItem $r3 -Recurse -ErrorAction SilentlyContinue).Count)"
Get-ChildItem $r3 -Recurse -Depth 3 -Directory -ErrorAction SilentlyContinue | Select-Object -First 12 | ForEach-Object { "  $($_.FullName)" }
"--- ZaloData default"
Get-ChildItem "$env:APPDATA\ZaloData" -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object { "  $($_.Name)" }
"--- windows"
Get-Process Zalo -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | ForEach-Object { "  $($_.Id) '$($_.MainWindowTitle)'" }
