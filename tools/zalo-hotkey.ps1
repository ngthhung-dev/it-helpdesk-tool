$ErrorActionPreference = 'Continue'
$d = "$env:TEMP\zp"; New-Item -ItemType Directory -Force $d | Out-Null
Invoke-WebRequest 'https://zalo.me/download/zalo-pc?utm=90000' -OutFile "$d\ZaloSetup.exe" -UseBasicParsing
Start-Process "$d\ZaloSetup.exe" -ArgumentList '/S' -Wait
Start-Sleep 15
$root = "$env:LOCALAPPDATA\Programs\Zalo"
$ver = (Get-ChildItem $root -Directory -Filter 'Zalo-*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
"ver: $ver"
"--- files (exe/node/dll, top+resources)"
Get-ChildItem $ver -Recurse -Include *.exe,*.node,*.asar -ErrorAction SilentlyContinue | ForEach-Object { "  $($_.FullName.Substring($ver.Length)) $($_.Length)" }
"--- asar search"
$tmp = "$d\asar"; New-Item -ItemType Directory -Force $tmp | Out-Null
foreach ($a in Get-ChildItem "$ver\resources" -Filter *.asar) {
  & npx --yes @electron/asar@3.2.17 extract $a.FullName "$tmp\$($a.BaseName)" 2>&1 | Select-Object -First 3
}
$files = Get-ChildItem $tmp -Recurse -File -Include *.js,*.json -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch 'node_modules' }
"js files: $($files.Count)"
foreach ($pat in 'globalShortcut','RegisterHotKey','registerHotkey','CtrlOrCmd\+Alt\+S','Alt\+S','requestSingleInstanceLock','screenshot','capture') {
  $hits = $files | Select-String -Pattern $pat -SimpleMatch:($pat -notmatch '\\') -List -ErrorAction SilentlyContinue
  "[$pat] $($hits.Count) files: " + (($hits | Select-Object -First 6 | ForEach-Object { $_.Path.Substring($tmp.Length) }) -join ', ')
}
"--- context globalShortcut"
$files | Select-String -Pattern 'globalShortcut\.(register|unregister)[A-Za-z]*\(' -AllMatches -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object {
  $l = $_.Line; $i = [Math]::Max(0, $_.Matches[0].Index - 250); "  $($_.Filename): " + $l.Substring($i, [Math]::Min(600, $l.Length - $i))
}
"--- hotkey/shortcut config keys"
$files | Select-String -Pattern '(shortcut|hotkey|hotKey)[A-Za-z_]*\s*[:=]' -AllMatches -ErrorAction SilentlyContinue | Select-Object -First 10 | ForEach-Object {
  $l = $_.Line; $i = [Math]::Max(0, $_.Matches[0].Index - 150); "  $($_.Filename): " + $l.Substring($i, [Math]::Min(400, $l.Length - $i))
}
"--- capture exe strings"
Get-ChildItem $ver -Recurse -Include *.exe -ErrorAction SilentlyContinue | Where-Object Name -match 'cap|shot|snip|hook' | ForEach-Object { "  $($_.FullName)" }
"--- registry HKCU Zalo"
Get-ChildItem 'HKCU:\Software' | Where-Object Name -match 'Zalo|VNG' | ForEach-Object { "  $($_.Name)"; Get-ItemProperty $_.PSPath | Out-String }
