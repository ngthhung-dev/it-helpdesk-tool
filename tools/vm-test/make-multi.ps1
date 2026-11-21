# Kịch bản multi: dựng máy "nhiều ổ cứng, nhiều phân vùng" trước khi cài lại.
#   Ổ cứng 1: EFI + MSR + C: (Windows) + D: DATA-D (dữ liệu, dùng làm ổ đệm D:\ITH)
#   Ổ cứng 2: phân vùng EFI CŨ (OLDEFI, không có file khởi động) + E: DATA-E + F: DATA-F
# Sau khi cài lại phải: dữ liệu D/E/F còn nguyên, file khởi động ghi vào EFI của ổ cứng 1 — KHÔNG ghi sang OLDEFI.
$ErrorActionPreference = 'Stop'
$c = Get-Partition -DriveLetter C
Resize-Partition -DriveLetter C -Size ($c.Size - 20GB)
New-Partition -DiskNumber $c.DiskNumber -UseMaximumSize -DriveLetter D | Format-Volume -FileSystem NTFS -NewFileSystemLabel DATA-D -Confirm:$false | Out-Null
'du lieu o D' | Set-Content D:\data-d.txt

$d = Get-Disk | Where-Object { $_.PartitionStyle -eq 'RAW' } | Select-Object -First 1
Initialize-Disk -Number $d.Number -PartitionStyle GPT
$z = New-Partition -DiskNumber $d.Number -Size 300MB -DriveLetter Z
Format-Volume -DriveLetter Z -FileSystem FAT32 -NewFileSystemLabel OLDEFI -Confirm:$false | Out-Null
'phan vung EFI cu tren o cung thu hai' | Set-Content Z:\old-efi.txt
Remove-PartitionAccessPath -DiskNumber $d.Number -PartitionNumber $z.PartitionNumber -AccessPath 'Z:\'
# Đổi kiểu thành phân vùng EFI (diskpart set id)
"select disk $($d.Number)`r`nselect partition $($z.PartitionNumber)`r`nset id=c12a7328-f81f-11d2-ba4b-00a0c93ec93b override" |
    Set-Content "$env:TEMP\ith-esp.txt" -Encoding ascii
diskpart /s "$env:TEMP\ith-esp.txt"

New-Partition -DiskNumber $d.Number -Size 15GB -DriveLetter E | Format-Volume -FileSystem NTFS -NewFileSystemLabel DATA-E -Confirm:$false | Out-Null
New-Partition -DiskNumber $d.Number -UseMaximumSize -DriveLetter F | Format-Volume -FileSystem NTFS -NewFileSystemLabel DATA-F -Confirm:$false | Out-Null
'du lieu o E' | Set-Content E:\data-e.txt
'du lieu o F' | Set-Content F:\data-f.txt

Get-Partition | Format-Table DiskNumber, PartitionNumber, DriveLetter, Size, GptType, IsSystem -AutoSize | Out-String -Width 200
