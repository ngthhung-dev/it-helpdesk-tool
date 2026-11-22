#!/usr/bin/env bash
# Thử "Cài lại Windows" trên máy ảo QEMU/KVM (runner Linux của GitHub).
#   run-vm.sh <uefi|bios> <happy|safe|auto|samedrive|esd|multi> <win.iso> <thư mục ithreinstall> <thư mục kết quả>
# happy: cài Windows "máy cũ" → ithreinstall prepare --arm --reboot → WinPE cài lại → Windows mới cài phần mềm → tắt máy.
# safe : như trên nhưng WinPE dừng trước bước format → phải quay về Windows cũ, không mất gì.
# auto : máy chỉ có một ổ, --staging auto (thu nhỏ C: tạo phân vùng đệm, không được thì đệm ngay trên C:).
# samedrive: máy một ổ, --staging C:\ITH (không format, xoá mọi thứ trừ C:\ITH).
# legacy / legacyesd: như happy / esd nhưng ép đường Windows 7 (ITH_LEGACY=1): đọc .iso trong code (DiscUtils),
#   wimlib-imagex thay DISM, không thu nhỏ ổ. legacy lấy bộ cài từ file win.iso trên một ổ cứng thứ ba.
set -euo pipefail
FW=$1; SCEN=$2; ISO=$3; CLI=$4; OUT=$5
LEGACY=0; BASE=$SCEN
case "$SCEN" in
  legacy) LEGACY=1; BASE=happy ;;
  legacyesd) LEGACY=1; BASE=esd ;;
esac
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=/mnt/vm/$FW-$SCEN
mkdir -p "$WORK" "$OUT/shots"
cd "$WORK"

# ---- Đĩa công cụ: autounattend.xml + run-test.cmd + ithreinstall ----
mkdir -p tools/cli
cp -r "$CLI"/. tools/cli/
cp "$HERE/make-multi.ps1" tools/
if [ "$FW" = uefi ]; then
  DISK='          <CreatePartitions>
            <CreatePartition wcm:action="add"><Order>1</Order><Type>EFI</Type><Size>300</Size></CreatePartition>
            <CreatePartition wcm:action="add"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition>
            <CreatePartition wcm:action="add"><Order>3</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
          </CreatePartitions>
          <ModifyPartitions>
            <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition>
            <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
          </ModifyPartitions>'
  INSTALLTO=3
else
  DISK='          <CreatePartitions>
            <CreatePartition wcm:action="add"><Order>1</Order><Type>Primary</Type><Size>500</Size></CreatePartition>
            <CreatePartition wcm:action="add"><Order>2</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
          </CreatePartitions>
          <ModifyPartitions>
            <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>NTFS</Format><Label>System</Label><Active>true</Active></ModifyPartition>
            <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>2</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
          </ModifyPartitions>'
  INSTALLTO=2
fi
python3 - "$HERE/autounattend.template.xml" tools/autounattend.xml "$INSTALLTO" <<PY
import sys
t = open(sys.argv[1], encoding='utf-8').read()
t = t.replace('__DISKCONFIG__', '''$DISK''').replace('__INSTALLTO__', sys.argv[3])
open(sys.argv[2], 'w', encoding='utf-8', newline='\r\n').write(t)
PY
# uefi/happy cài mọi phần mềm trong danh mục trừ WhatsApp (Microsoft Store lỗi chứng chỉ trên Windows mới cài, mặc định không chọn)
SOFTWARE="7zip.7zip,unikey"; ALLSW=0; [ "$FW" = uefi ] && [ "$SCEN" = happy ] && ALLSW=1 && SOFTWARE="7zip.7zip,CocCoc.CocCoc,evkey,Google.Chrome,it-helpdesk-tool,CodecGuide.K-LiteCodecPack.Standard,mail-lite,Mozilla.Firefox,pdf-editor-lite,rustdesk,DucFabulous.UltraViewer,Foxit.FoxitReader,Telegram.TelegramDesktop,unikey,VideoLAN.VLC,Tencent.WeChat.Universal,RARLab.WinRAR,Zoom.Zoom"
EXTRA=""; [ "$BASE" = safe ] && EXTRA="--test-fail-before-format"
ESD=0; [ "$BASE" = esd ] && ESD=1
MULTI=0; [ "$BASE" = multi ] && MULTI=1
case "$SCEN" in
  happy|safe|esd|legacy|legacyesd) STAGING='S:\\ITH'; MKDISK=1 ;;
  auto) STAGING=auto; MKDISK=0 ;;
  multi) STAGING='K:\\ITH'; MKDISK=0 ;;
  samedrive) STAGING='C:\\ITH'; MKDISK=0 ;;
esac
sed -e "s/__EXTRA__/$EXTRA/" -e "s/__ESD__/$ESD/g" -e "s/__MULTI__/$MULTI/g" -e "s/__STAGING__/$STAGING/g" -e "s/__LEGACY__/$LEGACY/g" -e "s/__SOFTWARE__/$SOFTWARE/g" -e "s/__MKDISK__/$MKDISK/g" "$HERE/run-test.cmd" | sed 's/$/\r/' > tools/run-test.cmd
grep -n "prepare" tools/run-test.cmd | head -2
genisoimage -quiet -J -joliet-long -r -V ITHTEST -o tools.iso tools

# ---- Ổ đĩa: 0 = Windows (sẽ bị cài lại), 1 = ổ đệm ----
qemu-img create -f qcow2 disk0.qcow2 64G > /dev/null
qemu-img create -f qcow2 disk1.qcow2 40G > /dev/null
# legacy: ổ cứng thứ ba (NTFS) chứa win.iso — như người dùng Win 7 tải ISO về ổ D:
ISODISK=()
if [ "$SCEN" = legacy ]; then
  sudo modprobe nbd max_part=16
  qemu-img create -f qcow2 disk2.qcow2 12G > /dev/null
  sudo qemu-nbd -c /dev/nbd3 disk2.qcow2
  echo 'type=7' | sudo sfdisk -q /dev/nbd3
  for i in $(seq 1 10); do [ -e /dev/nbd3p1 ] && break; sleep 1; done
  sudo mkntfs -Q -L ISOS /dev/nbd3p1 > /dev/null
  sudo mkdir -p /mnt/isos && sudo mount -t ntfs-3g /dev/nbd3p1 /mnt/isos
  sudo cp "$ISO" /mnt/isos/win.iso && ls -l /mnt/isos
  sudo umount /mnt/isos && sudo qemu-nbd -d /dev/nbd3 > /dev/null
  ISODISK=(-drive file=disk2.qcow2,if=none,id=d2 -device ide-hd,drive=d2,bus=ahci.4)
fi

MON=$WORK/mon.sock
# CPU: runner GitHub đã là máy ảo → KVM lồng nhau. Với "-cpu host" Windows 11 thấy VT-x/AMD-V nên tự bật VBS / Hyper-V
# (lồng thêm một tầng) và bị màn hình xanh 0x3B lúc có lúc không — cả khi đang cài Windows gốc, trước khi app chạy (run 44975344972),
# và lúc tắt máy (khởi động lại không sạch → mất mục BCD). Tắt vmx/svm + bật các "enlightenment" Hyper-V như libvirt khuyên cho Windows.
lscpu | grep -E 'Model name|Vendor ID|Virtualization|Hypervisor' || true
CPU="host,-vmx,-svm,hv_relaxed,hv_vapic,hv_spinlocks=0x1fff,hv_time,hv_vpindex,hv_synic,hv_stimer"
COMMON=(-enable-kvm -cpu "$CPU" -smp 4 -m 6144
  -drive file=disk0.qcow2,if=none,id=d0 -drive file=disk1.qcow2,if=none,id=d1
  -drive file="$ISO",media=cdrom,if=none,id=cd0,readonly=on -drive file=tools.iso,media=cdrom,if=none,id=cd1,readonly=on
  -nic user,model=e1000e
  -display none -monitor unix:$MON,server,nowait)
if [ "$FW" = uefi ]; then
  cp /usr/share/OVMF/OVMF_VARS_4M.ms.fd vars.fd
  mkdir -p tpm
  swtpm socket --tpmstate dir=tpm --ctrl type=unixio,path=$WORK/tpm/sock --tpm2 --daemon
  ARGS=(-machine q35,smm=on -global driver=cfi.pflash01,property=secure,value=on
    -drive if=pflash,format=raw,unit=0,file=/usr/share/OVMF/OVMF_CODE_4M.ms.fd,readonly=on
    -drive if=pflash,format=raw,unit=1,file=vars.fd
    -chardev socket,id=chrtpm,path=$WORK/tpm/sock -tpmdev emulator,id=tpm0,chardev=chrtpm -device tpm-tis,tpmdev=tpm0
    -device ahci,id=ahci -device ide-hd,drive=d0,bus=ahci.0,bootindex=0 -device ide-hd,drive=d1,bus=ahci.1,bootindex=2
    -device ide-cd,drive=cd0,bus=ahci.2,bootindex=1 -device ide-cd,drive=cd1,bus=ahci.3)
else
  ARGS=(-machine q35 -device ahci,id=ahci -device ide-hd,drive=d0,bus=ahci.0,bootindex=0 -device ide-hd,drive=d1,bus=ahci.1,bootindex=2
    -device ide-cd,drive=cd0,bus=ahci.2,bootindex=1 -device ide-cd,drive=cd1,bus=ahci.3)
fi

qemu-system-x86_64 "${COMMON[@]}" "${ARGS[@]}" "${ISODISK[@]}" &
QPID=$!
mon() { echo "$1" | socat - unix-connect:$MON > /dev/null 2>&1 || true; }
shot() { mon "screendump $WORK/s.ppm"; sleep 1; convert "$WORK/s.ppm" -resize 800x "$OUT/shots/$1.png" 2>/dev/null || true; }

# "Press any key to boot from CD" ở lần bật đầu
# Trong ~50 giây đầu (máy runner bận thì firmware chậm). Dùng Tab, KHÔNG dùng Enter/Space:
# runner nhanh thì Setup đã hiện trong 50 giây và Enter bấm nhầm nút Cancel (lượt 6); Tab chỉ chuyển ô chọn.
# Các lần khởi động sau vào thẳng ổ cứng
sleep 2
for i in $(seq 1 48); do mon "sendkey tab"; sleep 1; done

# ---- Chờ máy ảo tự tắt (run-test / FirstLogon.ps1 tắt máy khi xong), chụp màn hình mỗi phút ----
# Màn hình y hệt 15 phút liền = máy ảo đứng hình (lượt 45021075588 đứng ở màn khởi động 84 phút):
# dừng sớm để đọc log, không chờ hết giờ. Đồng hồ khay hệ thống và vòng xoay làm ảnh đổi khi máy còn chạy.
FREEZE=15; SAME=0; PREV=""
# Lượt bình thường ~40 phút (cài 18 phần mềm thêm ~10 phút): quá 90 phút là bất thường → tắt máy ảo, đọc log trên ổ
START=$(date +%s); LIMIT=$((90 * 60)); n=0
while kill -0 $QPID 2>/dev/null; do
  sleep 60; n=$((n + 1))
  # nhích chuột 1 điểm để Windows không tắt màn hình (màn đen làm ảnh chụp giống nhau dù máy vẫn chạy)
  mon "mouse_move 1 0"; sleep 1; mon "mouse_move -1 0"; sleep 2
  shot "$(printf '%03d' $n)"
  CUR=$(md5sum "$WORK/s.ppm" 2>/dev/null | cut -d" " -f1)   # so điểm ảnh gốc (PNG có dấu thời gian nên luôn khác)
  if [ -n "$CUR" ] && [ "$CUR" = "$PREV" ]; then SAME=$((SAME + 1)); else SAME=0; fi
  PREV=$CUR
  if [ $SAME -ge $FREEZE ]; then
    echo "::error::Máy ảo đứng hình $FREEZE phút (ảnh $(printf '%03d' $n)) — dừng sớm"
    mon quit; sleep 5; kill $QPID 2>/dev/null || true
    TIMEOUT=1
    break
  fi
  if [ $(( $(date +%s) - START )) -gt $LIMIT ]; then
    echo "::error::Hết giờ ($((LIMIT / 60)) phút) — máy ảo chưa tự tắt"
    mon quit; sleep 5; kill $QPID 2>/dev/null || true
    TIMEOUT=1
    break
  fi
done
wait $QPID 2>/dev/null || true
echo "Máy ảo đã tắt sau $(( ($(date +%s) - START) / 60 )) phút"

# ---- Đọc kết quả trên ổ đệm (và ổ Windows) ----
sudo modprobe nbd max_part=16
mount_largest_ntfs() {   # $1 = file qcow2, $2 = /dev/nbdX, $3 = thư mục mount
  sudo qemu-nbd -r -c "$2" "$1"
  for i in $(seq 1 10); do ls "$2"p* > /dev/null 2>&1 && break; sleep 1; done
  echo "--- $1"; sudo lsblk -o NAME,SIZE,FSTYPE,LABEL "$2" || true
  local best="" bestsize=0 p t sz
  for p in "$2"p*; do
    t=$(sudo blkid -o value -s TYPE "$p" 2>/dev/null || true)
    [ "$t" = ntfs ] || continue
    sz=$(sudo blockdev --getsize64 "$p")
    if [ "$sz" -gt "$bestsize" ]; then best=$p; bestsize=$sz; fi
  done
  [ -n "$best" ] || { echo "Không thấy phân vùng NTFS trên $1"; return 1; }
  sudo mkdir -p "$3"
  # ntfs-3g từ chối ổ "chưa tắt sạch" (Windows ngủ đông / Fast Startup) → thử driver ntfs3 của kernel, chỉ đọc
  sudo mount -t ntfs-3g -o ro "$best" "$3" 2>&1 || sudo mount -t ntfs3 -o ro,force "$best" "$3" 2>&1 || { echo "Không mount được $best"; return 1; }
  echo "Đã mount $best → $3"
}
mount_largest_ntfs disk1.qcow2 /dev/nbd0 /mnt/stg || true
mount_largest_ntfs disk0.qcow2 /dev/nbd1 /mnt/win || true
sudo cp -r /mnt/stg/ITH/logs "$OUT/logs" 2>/dev/null || true
# Một ổ: log nằm trên ổ Windows mới (C:\ITH-Backup sau khi gộp phân vùng đệm, hoặc C:\ITH)
[ -d "$OUT/logs" ] || sudo cp -r /mnt/win/ITH-Backup/logs "$OUT/logs" 2>/dev/null || sudo cp -r /mnt/win/ITH/logs "$OUT/logs" 2>/dev/null || true
sudo blkid /dev/nbd1p* > "$OUT/partitions.txt" 2>/dev/null || true
mkdir -p "$OUT/logs"; sudo cp /mnt/win/ith-test.log "$OUT/logs/" 2>/dev/null || true
sudo chown -R "$USER" "$OUT"

# multi: mount mọi phân vùng theo nhãn (DATA-D, DATA-E, DATA-F, OLDEFI) để kiểm tra dữ liệu còn nguyên
if [ "$BASE" = multi ]; then
  for p in /dev/nbd0p* /dev/nbd1p*; do
    l=$(sudo blkid -o value -s LABEL "$p" 2>/dev/null || true); [ -n "$l" ] || continue
    m=$(findmnt -n -o TARGET "$p" 2>/dev/null || true)
    if [ -n "$m" ]; then sudo mkdir -p /mnt/lbl; sudo ln -sfn "$m" "/mnt/lbl/$l"; continue; fi
    sudo mkdir -p "/mnt/lbl/$l"
    sudo mount -o ro "$p" "/mnt/lbl/$l" 2>/dev/null || sudo mount -t ntfs3 -o ro,force "$p" "/mnt/lbl/$l" 2>/dev/null || true
  done
  ls -la /mnt/lbl/ || true
  sudo cp -r /mnt/lbl/DATA-D/ITH/logs/. "$OUT/logs/" 2>/dev/null || true
  sudo blkid /dev/nbd0p* /dev/nbd1p* > "$OUT/partitions.txt" 2>/dev/null || true
  sudo chown -R "$USER" "$OUT"
fi
check() { if [ -e "$1" ]; then echo "OK   $2"; else echo "FAIL $2 ($1)"; FAIL=1; fi; }
FAIL=0
echo "## Kết quả $FW / $SCEN" | tee "$OUT/result.txt"
{
  # Không có log của app ở đâu cả → máy ảo hỏng trước khi app chạy (vd màn hình xanh lúc cài Windows gốc): lỗi hạ tầng, không phải app
  # (auto/multi: ổ đệm đã gộp lại / nằm ở K: → dấu vết app là ProgramData\ITHReinstall hoặc ITH-Backup trên Windows mới)
  if [ ! -e /mnt/stg/ITH/logs/prepare.log ] && [ ! -e /mnt/win/ITH/logs/prepare.log ] && [ ! -e /mnt/win/ith-test.log ] \
     && [ ! -e /mnt/win/ProgramData/ITHReinstall ] && [ ! -e /mnt/win/ITH-Backup ]; then
    echo "HẠ TẦNG: app chưa từng chạy — máy ảo hỏng trước đó (xem ảnh chụp cuối: màn hình xanh / kẹt cài Windows gốc)"
  fi
  if [ "$BASE" = happy ] || [ "$BASE" = safe ] || [ "$BASE" = esd ]; then
    check /mnt/stg/ITH/logs/prepare.log "ithreinstall prepare đã chạy"
    check /mnt/stg/ITH/logs/winpe.log "môi trường cài (WinPE) đã chạy"
  fi
  [ -f "$OUT/logs/winpe.log" ] && { echo "### winpe.log (90 dòng cuối)"; tail -90 "$OUT/logs/winpe.log" | tr -d '
'; } || true
  if [ "$BASE" = auto ] || [ "$BASE" = samedrive ]; then
    if [ -d /mnt/win/ITH-Backup ]; then L=/mnt/win/ITH-Backup/logs; B=/mnt/win/ITH-Backup; echo "Chế độ: phân vùng đệm riêng (thu nhỏ C:) → đã gộp"
      check $L/merged.done "phân vùng đệm đã gộp lại vào C:"
      grep -q 'ITH-STAGING' "$OUT/partitions.txt" && { echo "FAIL phân vùng ITH-STAGING vẫn còn"; FAIL=1; } || echo "OK   không còn phân vùng ITH-STAGING"
    else L=/mnt/win/ITH/logs; B=/mnt/win/ITH/backup; echo "Chế độ: thư mục đệm ngay trên C: (C:\\ITH)"
      [ -e /mnt/win/ITH/install.wim ] && { echo "FAIL file cài vẫn còn trong C:\\ITH"; FAIL=1; } || echo "OK   đã dọn file cài trong C:\\ITH"
    fi
    [ "$BASE" = samedrive ] && [ -d /mnt/win/ITH-Backup ] && { echo "FAIL samedrive mà lại tạo phân vùng đệm"; FAIL=1; }
    check $L/winpe.done "WinPE cài xong"
    check $L/post/firstlogon.done "Windows mới vào Desktop, chạy FirstLogon.ps1"
    check $B/backup.json "đã sao lưu trước khi cài"
    check "/mnt/win/Program Files/7-Zip/7z.exe" "7-Zip (winget) đã cài"
    check /mnt/win/Users/itadmin/Desktop/ith-backup-test.txt "Desktop cũ đã tự chép lại vào tài khoản mới"
    check "/mnt/win/Users/itadmin/Documents/Ke toan/bao-cao.txt" "Documents cũ đã tự chép lại"
    [ -e /mnt/win/Users/olduser ] && { echo "FAIL tài khoản cũ olduser vẫn còn"; FAIL=1; } || echo "OK   Windows cũ đã được thay"
  elif [ "$BASE" = multi ]; then
    L=/mnt/lbl/DATA-D/ITH/logs
    check $L/winpe.done "WinPE cài xong (ổ đệm D: cùng ổ cứng với C:)"
    check $L/post/firstlogon.done "Windows mới vào Desktop, chạy FirstLogon.ps1"
    grep -q "Boot EFI partition mounted" "$OUT/logs/winpe.log" 2>/dev/null && echo "OK   ghi file khởi động vào phân vùng EFI vừa khởi động (mountvol /s)" || { echo "FAIL không gắn được phân vùng EFI vừa khởi động"; FAIL=1; }
    check /mnt/lbl/DATA-D/data-d.txt "dữ liệu phân vùng D: (cùng ổ cứng Windows) còn nguyên"
    check /mnt/lbl/DATA-E/data-e.txt "dữ liệu E: (ổ cứng thứ hai) còn nguyên"
    check /mnt/lbl/DATA-F/data-f.txt "dữ liệu F: (ổ cứng thứ hai) còn nguyên"
    check /mnt/lbl/OLDEFI/old-efi.txt "phân vùng EFI cũ trên ổ cứng thứ hai còn nguyên"
    [ -e /mnt/lbl/OLDEFI/EFI/Microsoft/Boot/BCD ] && { echo "FAIL file khởi động bị ghi nhầm sang EFI của ổ cứng thứ hai"; FAIL=1; } || echo "OK   không ghi file khởi động sang ổ cứng thứ hai"
    check "/mnt/win/Program Files/7-Zip/7z.exe" "7-Zip (winget) đã cài"
    check /mnt/win/Users/itadmin/Desktop/ith-backup-test.txt "Desktop cũ đã tự chép lại vào tài khoản mới"
    [ -e /mnt/win/Users/olduser ] && { echo "FAIL tài khoản cũ olduser vẫn còn"; FAIL=1; } || echo "OK   Windows cũ đã được thay"
  elif [ "$BASE" = happy ] || [ "$BASE" = esd ]; then
    if [ "$BASE" = esd ]; then ls /mnt/stg/ITH-Windows/*/ith-media.done >/dev/null 2>&1 && echo "OK   đã tự tải bộ cài gốc từ Microsoft (SHA1 khớp) và dựng bộ cài" || { echo "FAIL không thấy bộ cài tải về (/mnt/stg/ITH-Windows/*/ith-media.done)"; FAIL=1; }; fi
    check /mnt/stg/ITH/logs/winpe.done "WinPE cài xong"
    check /mnt/stg/ITH/logs/post/firstlogon.done "Windows mới vào Desktop, chạy FirstLogon.ps1"
    check /mnt/win/ProgramData/ITHReinstall/logs/firstlogon.done "log sau cài nằm trên ổ Windows mới"
    check "/mnt/win/Program Files/7-Zip/7z.exe" "7-Zip (winget) đã cài"
    check /mnt/win/Users/itadmin "tài khoản itadmin đã tạo"
    check /mnt/stg/ITH/backup/backup.json "đã sao lưu trước khi cài (backup.json)"
    check /mnt/stg/ITH/backup/apps/phan-mem.csv "đã lưu danh sách phần mềm"
    check /mnt/win/Users/itadmin/Desktop/ith-backup-test.txt "Desktop cũ đã tự chép lại vào tài khoản mới"
    check "/mnt/win/Users/itadmin/Documents/Ke toan/bao-cao.txt" "Documents cũ đã tự chép lại vào tài khoản mới"
    [ -e /mnt/win/Users/olduser ] && { echo "FAIL tài khoản cũ olduser vẫn còn (ổ chưa bị cài lại)"; FAIL=1; } || echo "OK   Windows cũ đã được thay"
    [ -e /mnt/stg/ITH/logs/back-to-old.txt ] && { echo "FAIL máy đã quay về Windows cũ"; FAIL=1; } || true
    if [ "$LEGACY" = 1 ]; then
      grep -qa 'wimlib-imagex' /mnt/stg/ITH/logs/prepare.log && echo "OK   đường Windows 7: xử lý .wim bằng wimlib (không dùng DISM mới)" || { echo "FAIL không chạy đường Windows 7 (wimlib)"; FAIL=1; }
      grep -qa 'Mount-DiskImage\|Export-Image\|Apply-Image' /mnt/stg/ITH/logs/prepare.log && { echo "FAIL đường Windows 7 vẫn gọi lệnh chỉ có từ Windows 8"; FAIL=1; } || echo "OK   không gọi Mount-DiskImage / dism Export-Image / Apply-Image"
      if [ "$SCEN" = legacy ]; then
        grep -qa 'từ ISO' /mnt/stg/ITH/logs/prepare.log && echo "OK   đọc file ISO trong code (Windows 7 không mở được ISO)" || { echo "FAIL không đọc ISO trong code"; FAIL=1; }
      fi
    fi
  else
    check /mnt/stg/ITH/logs/winpe.safe "WinPE dừng an toàn trước khi xoá"
    check /mnt/stg/ITH/logs/back-to-old.txt "máy khởi động lại vào Windows cũ"
    check /mnt/win/Users/olduser "dữ liệu Windows cũ còn nguyên"
    check /mnt/win/Users/olduser/Desktop/ith-backup-test.txt "file trên Desktop cũ còn nguyên"
    if [ -f /mnt/stg/ITH/logs/bcd-after.txt ] && grep -qi "ITH - Cai lai" /mnt/stg/ITH/logs/bcd-after.txt; then
      echo "FAIL mục khởi động tạm vẫn còn"; FAIL=1
    else echo "OK   mục khởi động tạm đã được xoá"; fi
  fi
  if [ "$ALLSW" = 1 ] && [ -f /mnt/stg/ITH/logs/post/software.json ]; then
    # Mỗi phần mềm một dòng OK / FAIL
    python3 -c 'import json,sys; d=json.load(open(sys.argv[1],encoding="utf-8-sig")); d=d if isinstance(d,list) else [d]; [print(("OK   " if x["ok"] else "FAIL") + " cài phần mềm " + x["name"]) for x in d]' /mnt/stg/ITH/logs/post/software.json
  fi
  [ -f /mnt/stg/ITH/logs/post/software.json ] && { echo "### Phần mềm"; cat /mnt/stg/ITH/logs/post/software.json; } || true
} | tee -a "$OUT/result.txt"
grep -q '^FAIL' "$OUT/result.txt" && FAIL=1
[ -f /mnt/win/ITH-winpe-khong-thay-o-dem.txt ] && { echo "### WinPE không thấy ổ đệm"; sudo cat /mnt/win/ITH-winpe-khong-thay-o-dem.txt | tr -d "
"; } >> "$OUT/result.txt" || true
{ echo "### ith-test.log (máy ảo)"; sudo tail -40 /mnt/win/ith-test.log 2>/dev/null | tr -d '
'; } >> "$OUT/result.txt" || true
sudo umount /mnt/stg /mnt/win 2>/dev/null || true
sudo qemu-nbd -d /dev/nbd0 > /dev/null; sudo qemu-nbd -d /dev/nbd1 > /dev/null || true
[ "${TIMEOUT:-0}" = 1 ] && FAIL=1
exit $FAIL
