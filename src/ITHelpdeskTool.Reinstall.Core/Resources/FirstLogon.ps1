# IT Helpdesk Tool - chay o lan dang nhap dau tien cua Windows moi (FirstLogonCommands trong unattend.xml).
# Ap tinh chinh (ca may roi tai khoan), cho mang va winget san sang, cai phan mem, ghi log, chep log ve thu muc dem.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$base = Join-Path $env:ProgramData 'ITHReinstall'
$logDir = Join-Path $base 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'firstlogon.log'
function Log([string]$m) { $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $m; Write-Host $line; Add-Content -Path $log -Value $line -Encoding UTF8 }
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$cfg = Get-Content (Join-Path $base 'postinstall.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Log "=== Bat dau sau khi cai Windows - $env:COMPUTERNAME\$env:USERNAME"
$results = @()

function Run-Cmd([string]$c) {
  Log "> $c"
  $out = cmd.exe /c "$c 2>&1"
  if ($out) { Add-Content -Path $log -Value $out -Encoding UTF8 }
  return $LASTEXITCODE
}

# ---- 1. Tinh chinh ----
foreach ($c in @($cfg.machine)) { if ($c) { [void](Run-Cmd $c) } }
foreach ($c in @($cfg.user)) { if ($c) { [void](Run-Cmd $c) } }

# ---- 2. Cho mang (toi da 5 phut) ----
$online = $false
for ($i = 0; $i -lt 60; $i++) {
  try { $r = Invoke-WebRequest 'http://www.msftconnecttest.com/connecttest.txt' -UseBasicParsing -TimeoutSec 5; if ($r.Content -like 'Microsoft Connect Test*') { $online = $true; break } } catch { }
  Start-Sleep 5
}
Log "Mang: $online"

# ---- 3. winget (App Installer dang ky cham o lan dang nhap dau) ----
function Find-Winget {
  $c = Get-Command winget.exe -ErrorAction SilentlyContinue
  if ($c) { return $c.Source }
  $p = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe" -ErrorAction SilentlyContinue |
       Sort-Object FullName -Descending | Select-Object -First 1
  if ($p) { return $p.FullName }
  return $null
}
$needWinget = @($cfg.software | Where-Object { $_.kind -eq 'Winget' }).Count -gt 0
$wg = $null
if ($needWinget -and $online) {
  for ($i = 0; $i -lt 20 -and -not $wg; $i++) {
    $wg = Find-Winget
    if (-not $wg) {
      try { Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction Stop } catch { }
      Start-Sleep 30
    }
  }
  Log "winget: $wg"
}

function Resolve-Url([string]$u) {
  if ($u -notlike 'github:*') { return $u }
  $spec = $u.Substring(7); $repo, $pattern = $spec -split '\|', 2
  $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -UseBasicParsing -Headers @{ 'User-Agent' = 'ITHelpdeskTool' }
  $a = $rel.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
  if (-not $a) { throw "khong thay file khop $pattern trong $repo" }
  return $a.browser_download_url
}

function New-Shortcut([string]$lnk, [string]$target) {
  $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $s.TargetPath = $target; $s.WorkingDirectory = Split-Path $target; $s.Save()
}

# ---- 4. Phan mem ----
foreach ($s in @($cfg.software)) {
  if (-not $s) { continue }
  $ok = $false
  try {
    if (-not $online) { throw 'khong co mang' }
    switch ($s.kind) {
      'Winget' {
        if (-not $wg) { throw 'khong co winget' }
        Log "winget install $($s.id)"
        $out = & $wg install --id $s.id --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1
        Add-Content -Path $log -Value ($out | Out-String) -Encoding UTF8
        $ok = ($LASTEXITCODE -eq 0) -or ($LASTEXITCODE -eq -1978335189)   # 0x8A15002B = da co ban moi nhat
      }
      'Installer' {
        $url = Resolve-Url $s.url
        $file = Join-Path $env:TEMP ([IO.Path]::GetFileName(([Uri]$url).AbsolutePath))
        Log "Tai $url"
        Invoke-WebRequest $url -OutFile $file -UseBasicParsing
        if ($file -like '*.msi') { $p = Start-Process msiexec.exe -ArgumentList "/i `"$file`" /qn /norestart" -Wait -PassThru }
        else { $p = Start-Process $file -ArgumentList $s.args -Wait -PassThru }
        $ok = $p.ExitCode -in 0, 3010
      }
      'Portable' {
        $url = Resolve-Url $s.url
        $zip = Join-Path $env:TEMP ($s.id + '.zip')
        Log "Tai $url"
        Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
        $dir = Join-Path $env:ProgramFiles $s.name
        Expand-Archive $zip -DestinationPath $dir -Force
        $exe = Get-ChildItem $dir -Recurse -Filter $s.exe | Select-Object -First 1
        if (-not $exe) { throw "khong thay $($s.exe)" }
        New-Shortcut "$env:PUBLIC\Desktop\$($s.name).lnk" $exe.FullName
        New-Shortcut "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\$($s.name).lnk" $exe.FullName
        $ok = $true
      }
    }
  } catch { Log "LOI $($s.name): $($_.Exception.Message)" }
  Log ("{0}: {1}" -f $s.name, $(if ($ok) { 'OK' } else { 'LOI' }))
  $results += [pscustomobject]@{ name = $s.name; ok = $ok }
}

# ---- 5. Chep lai du lieu da sao luu (o dem: <thu muc dem>\backup) ----
$stg = $null
foreach ($d in [IO.DriveInfo]::GetDrives()) {
  $s = $d.Name.TrimEnd('\') + $cfg.stagingRel
  if (Test-Path (Join-Path $s ("ITH-STAGING-" + $cfg.id + ".tag"))) { $stg = $s }
}
$restored = @()
if ($stg) {
  $folders = @{ Desktop = 'Desktop'; Documents = 'MyDocuments'; Pictures = 'MyPictures'; Videos = 'MyVideos'; Music = 'MyMusic' }
  foreach ($r in @($cfg.restore)) {
    if (-not $r) { continue }
    $src = Join-Path $stg ('backup\' + $r.folder)
    if (-not (Test-Path $src)) { Log "Khong thay ban sao luu $src"; continue }
    $ok = $false
    try {
      switch ($r.kind) {
        'UserFolder' {
          $dst = if ($r.to -eq 'Downloads') { Join-Path $env:USERPROFILE 'Downloads' } else { [Environment]::GetFolderPath($folders[$r.to]) }
          Log "Chep lai $($r.title) -> $dst"
          robocopy $src $dst /E /COPY:DAT /XJ /R:1 /W:1 /MT:8 /NP /NFL /NDL | Out-Null
          $ok = $LASTEXITCODE -lt 8
        }
        'WiFi' {
          foreach ($x in Get-ChildItem $src -Filter *.xml) { netsh wlan add profile filename="$($x.FullName)" user=all | Out-Null }
          $ok = $true
        }
        'Bookmarks' {
          $bm = Join-Path $src 'Default\Bookmarks'
          $dstDir = Join-Path $env:LOCALAPPDATA ($r.to + '\Default')
          if ((Test-Path $bm) -and -not (Test-Path (Join-Path $dstDir 'Bookmarks'))) {
            New-Item -ItemType Directory -Force $dstDir | Out-Null
            Copy-Item $bm $dstDir -Force
          }
          $ok = $true
        }
      }
    } catch { Log "LOI chep lai $($r.title): $($_.Exception.Message)" }
    Log ("Chep lai {0}: {1}" -f $r.title, $(if ($ok) { 'OK' } else { 'LOI' }))
    $restored += [pscustomobject]@{ name = $r.title; ok = $ok }
  }
}
$restored | ConvertTo-Json | Set-Content (Join-Path $logDir 'restore.json') -Encoding UTF8

# ---- 6. Ket thuc ----
$results | ConvertTo-Json | Set-Content (Join-Path $logDir 'software.json') -Encoding UTF8
Remove-Item "$env:SystemRoot\Panther\unattend.xml" -Force -ErrorAction SilentlyContinue
Set-Content (Join-Path $logDir 'firstlogon.done') (Get-Date -Format s)
Log '=== Xong'

# Chep log ve thu muc dem (neu o dem con) de xem lai / kiem thu
foreach ($d in [IO.DriveInfo]::GetDrives()) {
  $stg = $d.Name.TrimEnd('\') + $cfg.stagingRel
  if (Test-Path (Join-Path $stg ("ITH-STAGING-" + $cfg.id + ".tag"))) {
    $dst = Join-Path $stg 'logs\post'
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item "$logDir\*" $dst -Force
  }
}
if ($cfg.after) { Log "> $($cfg.after)"; cmd.exe /c $cfg.after }
