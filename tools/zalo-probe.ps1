# Thử cách chạy nhiều Zalo cùng lúc
$ErrorActionPreference = 'Continue'
$d = "$env:TEMP\zp"; New-Item -ItemType Directory -Force $d | Out-Null
Invoke-WebRequest 'https://zalo.me/download/zalo-pc?utm=90000' -OutFile "$d\ZaloSetup.exe" -UseBasicParsing
"setup size: $((Get-Item "$d\ZaloSetup.exe").Length)"
$p = Start-Process "$d\ZaloSetup.exe" -ArgumentList '/S' -PassThru -Wait
"setup exit: $($p.ExitCode)"
Start-Sleep 10
$exe = @("$env:LOCALAPPDATA\Programs\Zalo\Zalo.exe", "${env:ProgramFiles}\Zalo\Zalo.exe", "${env:ProgramFiles(x86)}\Zalo\Zalo.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { $exe = (Get-ChildItem $env:LOCALAPPDATA, $env:ProgramFiles -Recurse -Filter Zalo.exe -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
"exe: $exe"
Get-ChildItem (Split-Path $exe) | Select-Object -First 30 | ForEach-Object { "  $($_.Name)" }
Get-Process | Where-Object Name -like 'Zalo*' | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 3
"--- known dirs before"
Get-ChildItem $env:APPDATA, $env:LOCALAPPDATA -Directory | Where-Object Name -like '*alo*' | ForEach-Object { "  $($_.FullName)" }

function Count { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count }

"--- A: chạy thường"
Start-Process $exe; Start-Sleep 25
"procs: $(Count)"
Get-ChildItem $env:APPDATA, $env:LOCALAPPDATA -Directory | Where-Object Name -like '*alo*' | ForEach-Object { "  dir $($_.FullName)" }

"--- B: --user-data-dir"
$u = "$env:TEMP\zalo-p2"
Start-Process $exe -ArgumentList "--user-data-dir=`"$u`""; Start-Sleep 25
"procs: $(Count) · p2 exists: $(Test-Path $u) · files: $(@(Get-ChildItem $u -Recurse -ErrorAction SilentlyContinue).Count)"
Get-ChildItem $u -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object { "  $($_.Name)" }

"--- C: APPDATA/LOCALAPPDATA env"
$r3 = "$env:TEMP\zalo-p3"
$psi = New-Object System.Diagnostics.ProcessStartInfo $exe
$psi.UseShellExecute = $false
$psi.Environment['APPDATA'] = "$r3\Roaming"; $psi.Environment['LOCALAPPDATA'] = "$r3\Local"; $psi.Environment['USERPROFILE'] = "$r3"
New-Item -ItemType Directory -Force "$r3\Roaming","$r3\Local" | Out-Null
[System.Diagnostics.Process]::Start($psi) | Out-Null; Start-Sleep 25
"procs: $(Count) · p3 files: $(@(Get-ChildItem $r3 -Recurse -ErrorAction SilentlyContinue).Count)"
Get-ChildItem $r3 -Recurse -Depth 2 -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object { "  $($_.FullName)" }

"--- main windows"
Get-Process | Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 } | ForEach-Object { "  pid $($_.Id) title '$($_.MainWindowTitle)'" }
"--- cmdlines"
Get-CimInstance Win32_Process -Filter "Name='Zalo.exe'" | Where-Object { $_.CommandLine -notmatch '--type=' } | ForEach-Object { "  $($_.ProcessId): $($_.CommandLine)" }
