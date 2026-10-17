$ErrorActionPreference = 'Continue'
$d = "$env:TEMP\zp"; New-Item -ItemType Directory -Force $d | Out-Null
Invoke-WebRequest 'https://zalo.me/download/zalo-pc?utm=90000' -OutFile "$d\ZaloSetup.exe" -UseBasicParsing
Start-Process "$d\ZaloSetup.exe" -ArgumentList '/S' -Wait
Start-Sleep 15
$ver = (Get-ChildItem "$env:LOCALAPPDATA\Programs\Zalo" -Directory -Filter 'Zalo-*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
& npx --yes @electron/asar@3.2.17 extract "$ver\resources\app.asar" "$d\x" 2>&1 | Out-Null
$js = Get-Content "$d\x\main-dist\main.js" -Raw
function Ctx($pat, $n = 8, $w = 900) {
  "===== $pat"
  $ms = [regex]::Matches($js, $pat)
  "count: $($ms.Count)"
  foreach ($m in ($ms | Select-Object -First $n)) { $i = [Math]::Max(0, $m.Index - 300); "  >> " + $js.Substring($i, [Math]::Min($w, $js.Length - $i)) }
}
Ctx 'globalShortcut' 10 1000
Ctx 'ZaloCap' 6 1000
Ctx 'shortcut_register_exception' 3 1200
Ctx 'shortcut-screenshot"' 6 700
"===== bootstrap single instance"
$b = Get-Content "$d\x\bootstrap.js" -Raw
$m = [regex]::Match($b, 'requestSingleInstanceLock'); $i = [Math]::Max(0, $m.Index - 600); $b.Substring($i, [Math]::Min(1400, $b.Length - $i))
