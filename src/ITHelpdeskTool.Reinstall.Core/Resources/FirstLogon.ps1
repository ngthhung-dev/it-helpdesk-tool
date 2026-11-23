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
# Phai goi qua loi tat %LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe (dang ky theo tai khoan):
# chay thang file trong C:\Program Files\WindowsApps bi Windows tu choi (Access is denied).
function Find-Winget {
  $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
  if (Test-Path $alias) { return $alias }
  $c = Get-Command winget.exe -ErrorAction SilentlyContinue
  if ($c -and $c.Source -notlike "*\Program Files\WindowsApps\*") { return $c.Source }
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
# Moi phan mem toi da $perApp giay (tai + cai). Qua gio: dung ca cay tien trinh, ghi LOI, cai tiep phan mem sau
# (bo cai hien cua so hoi du da bao cai im lang thi khong lam treo ca buoi cai).
$perApp = 600
if ($cfg.softwareTimeoutSec) { $perApp = [int]$cfg.softwareTimeoutSec }
function Left-Sec { [Math]::Max(5, [int]($deadline - (Get-Date)).TotalSeconds) }
# Ten phan mem da co trong danh sach go cai dat (Uninstall) cua Windows chua
function Test-Installed([string]$rx) {
  foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
                 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*') {
    foreach ($e in @(Get-ItemProperty $k -ErrorAction SilentlyContinue)) { if ($e.DisplayName -and $e.DisplayName -match $rx) { return $true } }
  }
  return $false
}
function Wait-Proc($p, [string]$detect = '') {
  [void]$p.Handle   # giu handle de doc duoc ExitCode sau khi tien trinh thoat
  # Bo cai khong tu thoat sau khi cai xong (Coc Coc): thay ten trong Uninstall thi doi them 60 giay roi dong, coi la OK
  if ($detect) {
    $seen = $null
    while (-not $p.WaitForExit(5000)) {
      if ((Get-Date) -gt $deadline) { break }
      if (-not $seen -and (Test-Installed $detect)) { $seen = Get-Date; Log 'da thay phan mem trong danh sach cai dat, cho bo cai tu thoat' }
      if ($seen -and ((Get-Date) - $seen).TotalSeconds -ge 60) {
        & taskkill.exe /T /F /PID $p.Id 2>&1 | Out-Null
        Log 'bo cai khong tu thoat sau khi cai xong - da dong'
        return 0
      }
    }
  }
  if (-not $p.WaitForExit((Left-Sec) * 1000)) {
    & taskkill.exe /T /F /PID $p.Id 2>&1 | Out-Null
    throw ("qua {0} phut chua cai xong - da dung, bo qua" -f [Math]::Round($perApp / 60))
  }
  return $p.ExitCode
}
# Tai song song: moi file (.exe/.msi/.zip) bat dau tai ngay tu dau, trong luc winget cai lan luot.
# Cai van lan luot: Windows chi cho 1 bo cai MSI chay mot luc, winget chay song song de loi.
$dl = @{}
$sw = @($cfg.software | Where-Object { $_ })
if ($online) {
  foreach ($s in @($sw | Where-Object { $_.kind -ne 'Winget' })) {
    try {
      $url = Resolve-Url $s.url
      $name = if ($s.kind -eq 'Portable') { $s.id + '.zip' } else { [IO.Path]::GetFileName(([Uri]$url).AbsolutePath) }
      # Link khong co duoi file (vd zalo.me/download/zalo-pc) -> coi la .exe
      if ($s.kind -ne 'Portable' -and $name -notmatch '\.(exe|msi)$') { $name = $s.id + '.exe' }
      $file = Join-Path $env:TEMP $name
      $wc = New-Object Net.WebClient
      # Giong App Store: zalo.me chi tra file cai khi User-Agent la trinh duyet Windows (khong thi ve trang web)
      $wc.Headers.Add('User-Agent', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) ITHelpdeskTool')
      Log "Tai (song song) $url"
      $dl[$s.id] = @{ file = $file; wc = $wc; task = $wc.DownloadFileTaskAsync($url, $file) }
    } catch { $dl[$s.id] = @{ error = $_.Exception.Message } }
  }
}
# Doan tham so cai im lang theo chuoi nhan dang trong file (giong InstallerRunner.Detect cua App Store)
function Get-SilentArgs([string]$file) {
  $fs = [IO.File]::OpenRead($file)
  try {
    $len = [int][Math]::Min($fs.Length, 6MB); $buf = New-Object byte[] $len
    $n = $fs.Read($buf, 0, $len); $text = [Text.Encoding]::ASCII.GetString($buf, 0, $n)
    if ($fs.Length -gt $len) {
      $t = [int][Math]::Min($fs.Length - $len, 2MB); [void]$fs.Seek(-$t, 'End'); $tail = New-Object byte[] $t
      $m = $fs.Read($tail, 0, $t); $text += [Text.Encoding]::ASCII.GetString($tail, 0, $m)
    }
  } finally { $fs.Dispose() }
  if ($text.Contains('Inno Setup')) { return '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' }
  if ($text.Contains('Nullsoft')) { return '/S' }
  if ($text.Contains('.wixburn')) { return '/quiet /norestart' }
  if ($text.Contains('InstallShield')) { return '/s /v"/qn /norestart"' }
  if ($text.Contains('Advanced Installer')) { return '/exenoui /qn /norestart' }
  if ($text.Contains('Squirrel')) { return '--silent' }
  if ($text.Contains('CocCocUpdate')) { return '/silent /install' }
  return ''
}
function Get-File($s) {
  $d = $dl[$s.id]
  if (-not $d) { throw 'chua tai duoc' }
  if ($d.error) { throw $d.error }
  try { $done = $d.task.Wait((Left-Sec) * 1000) } catch { throw ('loi tai: ' + $_.Exception.GetBaseException().Message) }
  if (-not $done) { $d.wc.CancelAsync(); throw ("tai qua {0} phut - bo qua" -f [Math]::Round($perApp / 60)) }
  $head = New-Object byte[] 2
  $fs = [IO.File]::OpenRead($d.file); try { [void]$fs.Read($head, 0, 2) } finally { $fs.Dispose() }
  if ($s.kind -ne 'Portable' -and $d.file -notlike '*.msi' -and -not ($head[0] -eq 0x4D -and $head[1] -eq 0x5A)) { throw 'link tra ve trang web, khong phai file cai' }
  return $d.file
}
# Chay winget co gioi han thoi gian (theo $deadline cua phan mem dang cai), ghi output vao log
function Invoke-Winget([string[]]$a) {
  $wout = Join-Path $env:TEMP ("winget-{0}.txt" -f [guid]::NewGuid().ToString('N'))
  $p = Start-Process $wg -ArgumentList $a -NoNewWindow -PassThru -RedirectStandardOutput $wout -RedirectStandardError "$wout.err"
  try { return (Wait-Proc $p) }
  finally {
    foreach ($f in $wout, "$wout.err") { if (Test-Path $f) { Add-Content -Path $log -Value (Get-Content $f -Raw -Encoding UTF8) -Encoding UTF8; Remove-Item $f -ErrorAction SilentlyContinue } }
  }
}
# winget truoc; cac file tai song song cai sau (luc do da tai xong hoac gan xong)
$sw = @($sw | Where-Object { $_.kind -eq 'Winget' }) + @($sw | Where-Object { $_.kind -ne 'Winget' })
foreach ($s in $sw) {
  $ok = $false
  $deadline = (Get-Date).AddSeconds($perApp)
  try {
    if (-not $online) { throw 'khong co mang' }
    switch ($s.kind) {
      'Winget' {
        if (-not $wg) { throw 'khong co winget' }
        Log "winget install $($s.id)"
        # Id dang 9NKSQGP7F2NH (12 ky tu) = app Microsoft Store (vd WhatsApp) -> nguon msstore
        $src = if ($s.id -cmatch '^[0-9A-Z]{12}$') { 'msstore' } else { 'winget' }
        $code = Invoke-Winget @('install', '--id', $s.id, '--exact', '--source', $src, '--silent',
          '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
        # 0x8A15005E = chung chi may chu Store khong khop chung chi winget ban cu ghim san (Windows moi cai,
        # App Installer chua cap nhat). Tam bo ghim chung chi cho rieng nguon Store (van kiem tra HTTPS), thu lai, bat lai.
        if ($code -eq -1978335138 -and $src -eq 'msstore') {
          Log 'Store: chung chi khong khop ban winget cu - tam bo ghim chung chi, thu lai'
          [void](Invoke-Winget @('settings', '--enable', 'BypassCertificatePinningForMicrosoftStore'))
          try {
            $code = Invoke-Winget @('install', '--id', $s.id, '--exact', '--source', $src, '--silent',
              '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
          } finally { [void](Invoke-Winget @('settings', '--disable', 'BypassCertificatePinningForMicrosoftStore')) }
        }
        $ok = ($code -eq 0) -or ($code -eq -1978335189)   # 0x8A15002B = da co ban moi nhat
        if (-not $ok) { Log "winget ma loi $code" }
      }
      'Installer' {
        $file = Get-File $s
        $isMsi = $file -like '*.msi'
        if (-not $isMsi) {
          $a = if ($s.args) { $s.args } else { Get-SilentArgs $file }
          if (-not $a) { throw 'khong nhan ra kieu bo cai de cai im lang' }
        }
        # Bo cai dung dot ngot (vd Zalo 0xC0000005 khi may vua cai xong phan mem khac) -> cho 15 giay, thu lai 1 lan
        for ($try = 1; $try -le 2; $try++) {
          if ($isMsi) { $p = Start-Process msiexec.exe -ArgumentList "/i `"$file`" /qn /norestart" -PassThru }
          else { Log "Cai $([IO.Path]::GetFileName($file)) $a"; $p = Start-Process $file -ArgumentList $a -PassThru }
          $code = Wait-Proc $p $s.detect
          $ok = $code -in 0, 3010
          if ($ok) { break }
          Log "bo cai tra ma $code (lan $try)"
          if ($try -eq 1 -and $code -lt 0 -and (Left-Sec) -gt 60) { Start-Sleep 15 } else { break }
        }
      }
      'Portable' {
        $zip = Get-File $s
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
        'AppData' {
          # vd %APPDATA%\Microsoft\Signatures (chu ky Outlook) cua tai khoan moi
          $dst = [Environment]::ExpandEnvironmentVariables($r.to)
          Log "Chep lai $($r.title) -> $dst"
          robocopy $src $dst /E /COPY:DAT /XJ /R:1 /W:1 /NP /NFL /NDL | Out-Null
          $ok = $LASTEXITCODE -lt 8
        }
        'Fonts' {
          # Cai font cho ca may: chep vao Windows\Fonts + ghi registry (khong ghi de font co san)
          $fontDir = Join-Path $env:SystemRoot 'Fonts'
          $reg = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts'
          $n = 0
          foreach ($f in Get-ChildItem $src -File | Where-Object { $_.Extension -match '^\.(ttf|otf|ttc|otc|fon|fnt)$' }) {
            $dst = Join-Path $fontDir $f.Name
            if (Test-Path $dst) { continue }
            Copy-Item $f.FullName $dst -Force
            $kind = if ($f.Extension -match 'otf|otc') { 'OpenType' } elseif ($f.Extension -match 'fon|fnt') { '' } else { 'TrueType' }
            $label = $f.BaseName + $(if ($kind) { " ($kind)" } else { '' })
            New-ItemProperty -Path $reg -Name $label -Value $f.Name -PropertyType String -Force | Out-Null
            $n++
          }
          Log "Da cai lai $n font"
          $ok = $true
        }
        'PowerPlans' {
          foreach ($p in Get-ChildItem $src -Filter *.pow) { powercfg /import "$($p.FullName)" | Out-Null }
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
if ($stg) {
  $dst = Join-Path $stg 'logs\post'
  New-Item -ItemType Directory -Force $dst | Out-Null
  Copy-Item "$logDir\*" $dst -Force
}
# ---- 6b. Thư mục đệm nằm trên ổ Windows mới (C:\ITH): xoá file cài, giữ bản sao lưu + log ----
if ($stg -and $stg.Substring(0, 2) -ieq $env:SystemDrive) {
  foreach ($x in 'install.wim', 'install.esd', 'boot.wim', 'boot.sdi', 'drivers', 'post', 'mount') {
    Remove-Item (Join-Path $stg $x) -Recurse -Force -ErrorAction SilentlyContinue
  }
  Log "Da don file cai trong $stg (giu backup, logs)"
}

# ---- 7. Phân vùng đệm do app tạo (máy chỉ có ổ C:): giữ bản sao lưu ở C:\ITH-Backup, xoá phân vùng, nới C: lại ----
if ($cfg.mergeStaging -and $stg) {
  $letter = $stg.Substring(0, 1)
  $keep = Join-Path $env:SystemDrive 'ITH-Backup'
  try {
    $vol = Get-Volume -DriveLetter $letter
    if ($vol.FileSystemLabel -ne 'ITH-STAGING' -or -not (Test-Path ($letter + ':\ITH-AUTOPART.tag'))) { throw "o $letter khong phai phan vung dem cua app" }
    Log "Chep ban sao luu va log ve $keep"
    if (Test-Path "$stg\backup") { robocopy "$stg\backup" $keep /E /COPY:DAT /XJ /R:1 /W:1 /NP /NFL /NDL | Out-Null }
    robocopy "$stg\logs" (Join-Path $keep 'logs') /E /COPY:DAT /R:1 /W:1 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "chep log loi $LASTEXITCODE" }
    $p = Get-Partition -DriveLetter $letter
    Remove-Partition -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -Confirm:$false
    $c = Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1)
    $m = (Get-PartitionSupportedSize -DiskNumber $c.DiskNumber -PartitionNumber $c.PartitionNumber).SizeMax
    if ($m -gt $c.Size) { Resize-Partition -DiskNumber $c.DiskNumber -PartitionNumber $c.PartitionNumber -Size $m }
    Log ("Da gop phan vung dem vao {0} ({1:0.0} GB)" -f $env:SystemDrive, ($m / 1GB))
    Set-Content (Join-Path $keep 'logs\merged.done') (Get-Date -Format s)
  } catch { Log "LOI gop phan vung dem: $($_.Exception.Message) - xoa tay bang Disk Management" }
}

if ($cfg.after) { Log "> $($cfg.after)"; cmd.exe /c $cfg.after }
