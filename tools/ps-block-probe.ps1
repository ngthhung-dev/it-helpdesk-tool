$ErrorActionPreference = 'Continue'
function Out1($m) { Write-Host "::notice title=r::$m" }
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'
New-Item "$k\DisallowRun" -Force | Out-Null
Set-ItemProperty $k -Name DisallowRun -Value 1 -Type DWord
$names = 'powershell.exe','powershell_ise.exe','pwsh.exe','wt.exe','WindowsTerminal.exe'
$i = 0; foreach ($n in $names) { $i++; Set-ItemProperty "$k\DisallowRun" -Name "ITHD$i" -Value $n }
Out1 ("terminal installed: " + [bool](Get-AppxPackage Microsoft.WindowsTerminal -ErrorAction SilentlyContinue))
function T($label, [scriptblock]$b) {
  Get-Process powershell,pwsh,WindowsTerminal -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID -and $_.StartTime -gt (Get-Date).AddSeconds(-1) } | Out-Null
  $before = @(Get-Process powershell,pwsh,WindowsTerminal,OpenConsole -ErrorAction SilentlyContinue).Id
  try { & $b; Start-Sleep 4 } catch { Out1 "  err: $($_.Exception.Message)" }
  $new = @(Get-Process powershell,pwsh,WindowsTerminal,OpenConsole -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
  Out1 "$label -> mở được: $($new.Count) $(($new | ForEach-Object Name) -join ',')"
  $new | Stop-Process -Force -ErrorAction SilentlyContinue
}
T 'ShellExecute powershell (như Win+R/Start)' { Start-Process powershell.exe -ArgumentList '-NoExit' }
T 'ShellExecute wt' { Start-Process wt.exe }
T 'explorer.exe powershell' { Start-Process explorer.exe -ArgumentList 'powershell.exe' }
T 'Packaged Terminal (shell:AppsFolder)' { Start-Process explorer.exe -ArgumentList 'shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App' }
T 'CreateProcess powershell (từ chương trình khác)' { [Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo 'powershell.exe' -Property @{ UseShellExecute = $false; Arguments = '-NoExit' })) | Out-Null }
Remove-Item "$k\DisallowRun" -Recurse -Force; Remove-ItemProperty $k -Name DisallowRun
