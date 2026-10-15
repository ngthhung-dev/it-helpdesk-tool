# Đo thời gian từng bước khi gỡ font hàng loạt
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class G {
  [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] public static extern bool RemoveFontResource(string f);
  [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] public static extern int AddFontResource(string f);
}
"@
$src = "$env:WINDIR\Fonts\arial.ttf"
$dir = "$env:LOCALAPPDATA\Microsoft\Windows\Fonts"; New-Item -ItemType Directory $dir -Force | Out-Null
$key = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts'
$N = 300
$files = 0..($N-1) | % { $f = "$dir\probe$_.ttf"; Copy-Item $src $f -Force; New-ItemProperty $key -Name "Probe $_ (TrueType)" -Value $f -Force | Out-Null; $f }
$sw = [Diagnostics.Stopwatch]::StartNew(); $files | % { [void][G]::RemoveFontResource($_) }; "RemoveFontResource (chưa nạp) x$N : $($sw.ElapsedMilliseconds) ms"
$sw.Restart(); $files[0..49] | % { [void][G]::AddFontResource($_) }; "AddFontResource x50 : $($sw.ElapsedMilliseconds) ms"
$sw.Restart(); $files[0..49] | % { [void][G]::RemoveFontResource($_) }; "RemoveFontResource (đã nạp) x50 : $($sw.ElapsedMilliseconds) ms"
$sw.Restart(); $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts', $true); 0..($N-1) | % { $k.DeleteValue("Probe $_ (TrueType)", $false) }; $k.Close(); "Registry delete x$N : $($sw.ElapsedMilliseconds) ms"
$sw.Restart(); $files | % { [IO.File]::Delete($_) }; "File delete x$N : $($sw.ElapsedMilliseconds) ms"
# Lặp lại với registry mở/đóng mỗi lần
$files = 0..($N-1) | % { $f = "$dir\probe$_.ttf"; Copy-Item $src $f -Force; New-ItemProperty $key -Name "Probe $_ (TrueType)" -Value $f -Force | Out-Null; $f }
$sw.Restart(); 0..($N-1) | % { $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts', $true); $k.DeleteValue("Probe $_ (TrueType)", $false); $k.Close() }; "Registry open+delete x$N : $($sw.ElapsedMilliseconds) ms"
$sw.Restart(); $files | % { [IO.File]::Exists($_) | Out-Null; [IO.File]::Delete($_) }; "File delete 2 x$N : $($sw.ElapsedMilliseconds) ms"
