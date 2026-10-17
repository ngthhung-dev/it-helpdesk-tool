$ErrorActionPreference = 'Continue'
$d = "$env:TEMP\zp"; New-Item -ItemType Directory -Force $d | Out-Null
Invoke-WebRequest 'https://zalo.me/download/zalo-pc?utm=90000' -OutFile "$d\ZaloSetup.exe" -UseBasicParsing
Start-Process "$d\ZaloSetup.exe" -ArgumentList '/S' -Wait
Start-Sleep 15
Get-Process Zalo* -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 3
$ver = (Get-ChildItem "$env:LOCALAPPDATA\Programs\Zalo" -Directory -Filter 'Zalo-*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
$capDir = "$ver\plugins\capture"; $cap = "$capDir\ZaloCap.exe"
"capture dir:"; Get-ChildItem $capDir | ForEach-Object { "  $($_.Name)" }
function Pipes { [IO.Directory]::GetFiles('\\.\pipe\') | ForEach-Object { $_.Substring(9) } }
$base = @(Pipes)
function Kill { Get-Process ZaloCap -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2 }
function Run($exe, $tag, [hashtable]$envs = @{}) {
  $psi = New-Object Diagnostics.ProcessStartInfo $exe; $psi.Arguments = "zalo2017 `"$env:TEMP\log-$tag`""; $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true
  foreach ($k in $envs.Keys) { $psi.Environment[$k] = $envs[$k] }
  $p = [Diagnostics.Process]::Start($psi); Start-Sleep 4; $p
}
"--- A: 1 bản"
Kill; $p1 = Run $cap 'a1'; "p1 alive=$(!$p1.HasExited)"; "new pipes: " + ((Pipes | Where-Object { $base -notcontains $_ }) -join ', ')
"--- B: 2 bản cùng exe, cùng env"
$p2 = Run $cap 'a2'; "p1 alive=$(!$p1.HasExited) p2 alive=$(!$p2.HasExited)"
"--- C: 2 bản, env khác (APPDATA/LOCALAPPDATA/USERPROFILE/TEMP)"
Kill; $p1 = Run $cap 'c1'
$r = "$env:TEMP\prof2"; New-Item -ItemType Directory -Force "$r\AppData\Roaming","$r\AppData\Local","$r\Temp" | Out-Null
$p2 = Run $cap 'c2' @{ APPDATA="$r\AppData\Roaming"; LOCALAPPDATA="$r\AppData\Local"; USERPROFILE=$r; TEMP="$r\Temp"; TMP="$r\Temp" }
"p1 alive=$(!$p1.HasExited) p2 alive=$(!$p2.HasExited)"
"--- D: bản 2 chạy từ thư mục khác (hardlink)"
Kill; $p1 = Run $cap 'd1'
$cd = "$env:TEMP\capcopy"; New-Item -ItemType Directory -Force $cd | Out-Null
Get-ChildItem $capDir -Recurse -File | ForEach-Object { $t = Join-Path $cd $_.FullName.Substring($capDir.Length + 1); New-Item -ItemType Directory -Force (Split-Path $t) | Out-Null; New-Item -ItemType HardLink -Path $t -Target $_.FullName -ErrorAction SilentlyContinue | Out-Null }
$p2 = Run "$cd\ZaloCap.exe" 'd2'
"p1 alive=$(!$p1.HasExited) p2 alive=$(!$p2.HasExited)"; "pipes now: " + ((Pipes | Where-Object { $base -notcontains $_ }) -join ', ')
"--- E: bản 2 đổi tên exe"
Kill; $p1 = Run $cap 'e1'
Copy-Item $cap "$capDir\ZaloCap2.exe"
$p2 = Run "$capDir\ZaloCap2.exe" 'e2'
"p1 alive=$(!$p1.HasExited) p2 alive=$(!$p2.HasExited)"; "pipes now: " + ((Pipes | Where-Object { $base -notcontains $_ }) -join ', ')
Kill
"--- strings near SingleApp (unicode)"
$u = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($cap))
[regex]::Matches($u, '[\x20-\x7e]{4,}') | ForEach-Object Value | Where-Object { $_ -match 'zalo|Zalo|cap|Cap|single|Single|server|Server' } | Select-Object -Unique -First 40
