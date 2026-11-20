@echo off
rem Chay trong may ao thu (Windows "may cu" vua cai xong): tao o dem S:, chay ithreinstall de cai lai Windows.
rem Bien __EXTRA__ do run-vm.sh thay (vd --test-fail-before-format cho kich ban an toan).
set "T=%~dp0"
set "LOG=C:\ith-test.log"
echo [%DATE% %TIME%] run-test start >> "%LOG%"

rem ---- O 1 (trong) -> S: NTFS ----
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Disk | ?{ $_.PartitionStyle -eq 'RAW' } | Select -First 1 | Initialize-Disk -PartitionStyle GPT -PassThru | New-Partition -UseMaximumSize -DriveLetter S | Format-Volume -FileSystem NTFS -NewFileSystemLabel STAGING -Confirm:$false" >> "%LOG%" 2>&1
if not exist S:\ (
  echo [%DATE% %TIME%] ERROR no S: >> "%LOG%"
  shutdown /s /t 30
  exit /b 1
)
mkdir S:\ITH\logs 2>nul
copy /y "%LOG%" S:\ITH\logs\run-test.log >nul

rem ---- O dia cai Windows ----
set "SRC="
for %%d in (D E F G H I J K L M N O P Q R T U V W) do if exist "%%d:\sources\install.wim" set "SRC=%%d:\sources\install.wim"
for %%d in (D E F G H I J K L M N O P Q R T U V W) do if not defined SRC if exist "%%d:\sources\install.esd" set "SRC=%%d:\sources\install.esd"
echo [%DATE% %TIME%] source=%SRC% >> "%LOG%"

rem Neu may quay ve Windows cu (kich ban an toan, hoac WinPE loi truoc khi xoa): ghi dau hieu roi tat may.
rem Cai lai thanh cong thi Windows cu (va tac vu nay) bi xoa nen khong chay.
schtasks /create /tn ITHBackToOld /sc onstart /ru SYSTEM /rl HIGHEST /f /tr "cmd /c echo back>S:\ITH\logs\back-to-old.txt & bcdedit /enum all > S:\ITH\logs\bcd-after.txt & shutdown /s /t 20" >> "%LOG%" 2>&1

rem Du lieu "quan trong" cua tai khoan cu de kiem tra sao luu va tu chep lai vao tai khoan moi
echo ITH backup test %DATE% %TIME%> "%USERPROFILE%\Desktop\ith-backup-test.txt"
mkdir "%USERPROFILE%\Documents\Ke toan" 2>nul
echo so lieu> "%USERPROFILE%\Documents\Ke toan\bao-cao.txt"

"%T%cli\ithreinstall.exe" info --image "%SRC%" >> "%LOG%" 2>&1
"%T%cli\ithreinstall.exe" backup --staging S:\ITH --list >> "%LOG%" 2>&1
"%T%cli\ithreinstall.exe" prepare --image "%SRC%" --index 1 --staging S:\ITH --account itadmin --password "Test@12345" --computer NEW-PC --software 7zip.7zip,unikey --tweaks default --backup Desktop,Documents,wifi,apps,printers --after "shutdown /s /t 60" --arm --reboot __EXTRA__ >> "%LOG%" 2>&1
set RC=%ERRORLEVEL%
echo [%DATE% %TIME%] prepare exit %RC% >> "%LOG%"
copy /y "%LOG%" S:\ITH\logs\run-test.log >nul
bcdedit /enum all > S:\ITH\logs\bcd-before.txt 2>&1
if not "%RC%"=="0" shutdown /s /t 30
