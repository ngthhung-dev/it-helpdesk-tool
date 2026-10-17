$ErrorActionPreference = 'Continue'
$d = "$env:TEMP\zp"; New-Item -ItemType Directory -Force $d | Out-Null
Invoke-WebRequest 'https://zalo.me/download/zalo-pc?utm=90000' -OutFile "$d\ZaloSetup.exe" -UseBasicParsing
Start-Process "$d\ZaloSetup.exe" -ArgumentList '/S' -Wait
Start-Sleep 15
$ver = (Get-ChildItem "$env:LOCALAPPDATA\Programs\Zalo" -Directory -Filter 'Zalo-*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
& npx --yes @electron/asar@3.2.17 extract "$ver\resources\app.asar" "$d\x" 2>&1 | Out-Null
$js = Get-Content "$d\x\main-dist\main.js" -Raw
$m = [regex]::Match($js, 'const r=e\(t,\["zalo2017",a\(\)\+m\]\)')
"===== before spawn (find a and m)"
$i = [Math]::Max(0, $m.Index - 2500); $js.Substring($i, 2500)
"===== strings ZaloCap.exe"
$cap = "$ver\plugins\capture\ZaloCap.exe"
$bytes = [IO.File]::ReadAllBytes($cap)
$a = [Text.Encoding]::ASCII.GetString($bytes); $u = [Text.Encoding]::Unicode.GetString($bytes)
foreach ($s in @($a, $u)) {
  [regex]::Matches($s, '[\x20-\x7e]{5,}') | ForEach-Object Value | Where-Object { $_ -match 'Mutex|mutex|Global\\|Local\\|pipe|Pipe|Single|single|Instance|instance|lock|Lock|zalo2017|QLocal|Server|hotkey|HotKey' } | Select-Object -Unique -First 60
}
"===== imports"
foreach ($s in @($a)) { [regex]::Matches($s, '(CreateMutex\w*|OpenMutex\w*|RegisterHotKey|FindWindow\w*|CreateNamedPipe\w*|WaitNamedPipe\w*|SetWindowsHookEx\w*)') | ForEach-Object Value | Select-Object -Unique }
"===== spawn test"
$p1 = Start-Process $cap -ArgumentList 'zalo2017','test1' -PassThru; Start-Sleep 5
$p2 = Start-Process $cap -ArgumentList 'zalo2017','test2' -PassThru; Start-Sleep 5
"p1 exited=$($p1.HasExited) p2 exited=$($p2.HasExited) code2=$(if($p2.HasExited){$p2.ExitCode})"
Get-Process ZaloCap -ErrorAction SilentlyContinue | ForEach-Object { "  ZaloCap pid $($_.Id)" }
