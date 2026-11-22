# Tải wimlib-imagex (bản Windows chính thức, wimlib.net) vào <Dest>\wimlib\x64 và <Dest>\wimlib\x86.
# Dùng cho bản nhẹ Windows 7 (DISM 6.1 không xuất / bung / sửa được .wim của Windows 10/11).
# wimlib-imagex: GPLv3, thư viện libwim: LGPLv3 — kèm file giấy phép và đường dẫn mã nguồn.
param([Parameter(Mandatory)] [string] $Dest)
$ErrorActionPreference = 'Stop'
$ver = '1.14.5'
# SHA-256 của file zip chính thức; trống = in ra để ghim lại
$sha = @{
    'x86_64' = ''
    'i686'   = ''
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("wimlib-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    foreach ($a in @(@{ Zip = 'x86_64'; Dir = 'x64' }, @{ Zip = 'i686'; Dir = 'x86' })) {
        $name = "wimlib-$ver-windows-$($a.Zip)-bin.zip"
        $zip = Join-Path $tmp $name
        Invoke-WebRequest -UseBasicParsing "https://wimlib.net/downloads/$name" -OutFile $zip
        $h = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "$name SHA256=$h"
        if ($sha[$a.Zip] -and $sha[$a.Zip] -ne $h) { throw "Sai SHA-256 của $name" }
        $x = Join-Path $tmp $a.Zip
        Expand-Archive $zip $x -Force
        $out = Join-Path $Dest "wimlib\$($a.Dir)"
        New-Item -ItemType Directory -Force $out | Out-Null
        $exe = Get-ChildItem $x -Recurse -Filter wimlib-imagex.exe | Select-Object -First 1
        if (-not $exe) { throw "Không thấy wimlib-imagex.exe trong $name" }
        Copy-Item (Join-Path $exe.DirectoryName '*.exe') $out
        Copy-Item (Join-Path $exe.DirectoryName '*.dll') $out
        Get-ChildItem $exe.DirectoryName -File | Where-Object { $_.Name -match '^(COPYING|README|NEWS)' } | Copy-Item -Destination $out
        "wimlib $ver - https://wimlib.net/ - source code: https://wimlib.net/downloads/wimlib-$ver.tar.gz" |
            Set-Content (Join-Path $out 'SOURCE.txt') -Encoding ascii
        Get-ChildItem $out | Format-Table Name, Length
    }
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
