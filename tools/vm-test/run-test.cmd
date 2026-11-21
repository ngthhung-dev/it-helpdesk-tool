@echo off
rem Chay trong may ao thu (Windows "may cu" vua cai xong): chay ithreinstall de cai lai Windows.
rem run-vm.sh thay: __STAGING__ (S:\ITH | auto | C:\ITH), __MKDISK__ (1 = tao o dem S: tren o thu hai), __EXTRA__.
set "T=%~dp0"
set "LOG=C:\ith-test.log"
echo [%DATE% %TIME%] run-test start staging=__STAGING__ >> "%LOG%"
whoami >> "%LOG%" 2>&1
whoami /groups | find /i "Mandatory Label" >> "%LOG%" 2>&1

if "__MKDISK__"=="1" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Disk | ?{ $_.PartitionStyle -eq 'RAW' } | Select -First 1 | Initialize-Disk -PartitionStyle GPT -PassThru | New-Partition -UseMaximumSize -DriveLetter S | Format-Volume -FileSystem NTFS -NewFileSystemLabel STAGING -Confirm:$false" >> "%LOG%" 2>&1
  if not exist S:\ (
    echo [%DATE% %TIME%] ERROR no S: >> "%LOG%"
    shutdown /s /t 30
    exit /b 1
  )
  mkdir S:\ITH\logs 2>nul
)

rem ---- O dia cai Windows ----
set "SRC="
for %%d in (D E F G H I J K L M N O P Q R T U V W) do if exist "%%d:\sources\install.wim" set "SRC=%%d:\sources\install.wim"
for %%d in (D E F G H I J K L M N O P Q R T U V W) do if not defined SRC if exist "%%d:\sources\install.esd" set "SRC=%%d:\sources\install.esd"
rem Kich ban esd: tu tai bo cai goc cua Microsoft (ithreinstall download) roi cai lai bang chinh bo cai do
set "EDITION="
if "__ESD__"=="1" (
  "%T%cli\ithreinstall.exe" download --dest S:\ITH-Windows --product 11 --lang en-us --arch x64 >> "%LOG%" 2>&1
  for /r S:\ITH-Windows %%f in (install.esd) do if exist "%%f" set "SRC=%%f"
  set "EDITION=--edition Professional"
)
echo [%DATE% %TIME%] source=%SRC% %EDITION% >> "%LOG%"

rem Neu may quay ve Windows cu (kich ban an toan, hoac WinPE loi truoc khi xoa): ghi dau hieu roi tat may.
rem Cai lai thanh cong thi Windows cu (va tac vu nay) bi xoa nen khong chay.
if "__MKDISK__"=="1" schtasks /create /tn ITHBackToOld /sc onstart /ru SYSTEM /rl HIGHEST /f /tr "cmd /c echo back>S:\ITH\logs\back-to-old.txt & bcdedit /enum all > S:\ITH\logs\bcd-after.txt & shutdown /s /t 20" >> "%LOG%" 2>&1

rem Du lieu "quan trong" cua tai khoan cu de kiem tra sao luu va tu chep lai vao tai khoan moi
echo ITH backup test %DATE% %TIME%> "%USERPROFILE%\Desktop\ith-backup-test.txt"
mkdir "%USERPROFILE%\Documents\Ke toan" 2>nul
echo so lieu> "%USERPROFILE%\Documents\Ke toan\bao-cao.txt"

rem Windows 11 24H2 tu bat ma hoa thiet bi (may ao co TPM + Secure Boot) -> thu chuc nang tat ma hoa truoc khi cai
reg add HKLM\SYSTEM\CurrentControlSet\Control\BitLocker /v PreventDeviceEncryption /t REG_DWORD /d 1 /f >> "%LOG%" 2>&1
"%T%cli\ithreinstall.exe" decrypt --drive C: --wait >> "%LOG%" 2>&1
if "__MKDISK__"=="1" "%T%cli\ithreinstall.exe" decrypt --drive S: --wait >> "%LOG%" 2>&1
echo [%DATE% %TIME%] decrypt exit %ERRORLEVEL% >> "%LOG%"

rem Kich ban multi: 2 o cung, nhieu phan vung du lieu, o thu hai con phan vung EFI cu
if "__MULTI__"=="1" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%T%make-multi.ps1" >> "%LOG%" 2>&1
  for %%v in (K L N) do "%T%cli\ithreinstall.exe" decrypt --drive %%v: --wait >> "%LOG%" 2>&1
  mkdir K:\ITH\logs 2>nul
)

"%T%cli\ithreinstall.exe" info --image "%SRC%" >> "%LOG%" 2>&1
"%T%cli\ithreinstall.exe" backup --staging C:\ITH-list --list >> "%LOG%" 2>&1
"%T%cli\ithreinstall.exe" prepare --image "%SRC%" --index 1 %EDITION% --staging __STAGING__ --account itadmin --password "Test@12345" --computer NEW-PC --software 7zip.7zip,unikey --tweaks default --backup Desktop,Documents,wifi,apps,printers --after "shutdown /s /t 60" --no-device-encryption --arm --reboot __EXTRA__ >> "%LOG%" 2>&1
set RC=%ERRORLEVEL%
echo [%DATE% %TIME%] prepare exit %RC% >> "%LOG%"

rem ---- Chep log vao thu muc dem (S:\ITH, phan vung ITH-STAGING hoac C:\ITH) ----
set "STG="
if exist S:\ITH\ set "STG=S:\ITH"
if "__MULTI__"=="1" set "STG=K:\ITH"
for %%d in (D E F G H I J K L M N O P Q R T U V W Y Z) do if not defined STG if exist "%%d:\ITH-AUTOPART.tag" set "STG=%%d:\ITH"
if not defined STG if exist C:\ITH\ set "STG=C:\ITH"
if defined STG (
  mkdir "%STG%\logs" 2>nul
  copy /y "%LOG%" "%STG%\logs\run-test.log" >nul
  bcdedit /enum all > "%STG%\logs\bcd-before.txt" 2>&1
)
if not "%RC%"=="0" shutdown /s /t 30
