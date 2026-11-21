@echo off
rem IT Helpdesk Tool - buoc cai trong moi truong WinPE.
rem Truoc khi xoa: moi loi deu quay ve Windows cu (khong mat gi).
rem Sau khi xoa: dung lai, giu cua so lenh va ghi log de xu ly.
setlocal EnableExtensions
set "STG=%~1"
if not defined STG set "STG=%~dp0"
if "%STG:~-1%"=="\" set "STG=%STG:~0,-1%"
set "LOG=%STG%\logs\winpe.log"
if not exist "%STG%\logs" mkdir "%STG%\logs"
set "ITH_ENTRY="
set "ITH_RAMDISK="
if exist "%STG%\bootids.cmd" call "%STG%\bootids.cmd"
call :log "=== WinPE start %DATE% %TIME% - staging %STG%"

rem ---- 1. Tim o Windows can cai lai theo file danh dau ----
set "TGT="
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
  if exist "%%d:\ITH-TARGET-{{ID}}.tag" set "TGT=%%d:"
)
if not defined TGT (
  call :log "ERROR: target tag ITH-TARGET-{{ID}}.tag not found"
  goto safe
)
if not exist "%TGT%\Windows\System32" (
  call :log "ERROR: %TGT% has no Windows folder"
  goto safe
)
{{STAGING_CHECK}}
call :log "Target: %TGT%"

rem ---- 2. Kiem tra file cai doc duoc ----
dism /English /Get-WimInfo /WimFile:"%STG%\{{IMAGE}}" /Index:{{INDEX}} >>"%LOG%" 2>&1
if errorlevel 1 (
  call :log "ERROR: cannot read %STG%\{{IMAGE}} index {{INDEX}}"
  goto safe
)
{{TEST_FAIL_BEFORE_FORMAT}}

{{WIPE}}

rem ---- 4. Bung ban Windows ----
call :log "Applying image index {{INDEX}} ..."
dism /English /Apply-Image /ImageFile:"%STG%\{{IMAGE}}" /Index:{{INDEX}} /ApplyDir:%TGT%\ >>"%LOG%" 2>&1
if errorlevel 1 (
  call :log "ERROR: apply-image failed"
  goto hard
)

rem ---- 5. Driver cua may ----
if exist "%STG%\drivers\" (
  call :log "Adding drivers ..."
  dism /English /Image:%TGT%\ /Add-Driver /Driver:"%STG%\drivers" /Recurse >>"%LOG%" 2>&1
  if errorlevel 1 call :log "WARN: some drivers were not added"
)

rem ---- 6. unattend.xml va cai dat sau khi len may ----
if not exist "%TGT%\Windows\Panther" mkdir "%TGT%\Windows\Panther"
copy /y "%STG%\unattend.xml" "%TGT%\Windows\Panther\unattend.xml" >>"%LOG%" 2>&1
if errorlevel 1 (
  call :log "ERROR: copy unattend.xml failed"
  goto hard
)
xcopy "%STG%\post" "%TGT%\ProgramData\ITHReinstall\" /E /I /Y /Q >>"%LOG%" 2>&1

rem ---- 7. File khoi dong ----
rem bcdboot khong /s: tu tim phan vung he thong (ESP voi UEFI, phan vung Active voi BIOS) theo kieu khoi dong hien tai
rem (/f bat buoc di kem /s nen khong dung o lan dau).
call :log "bcdboot ({{FIRMWARE}}) ..."
rem May UEFI: mountvol /s gan ky tu cho DUNG phan vung EFI ma firmware vua dung de khoi dong (cua Windows cu)
rem -> may nhieu o cung (o du lieu con phan vung EFI cu) khong bi ghi nham sang o khac. Loi thi bcdboot tu dong nhu cu.
if /i not "{{FIRMWARE}}"=="UEFI" goto bootauto
set "SL="
for %%l in (W V U T R Q P O N M) do if not defined SL if not exist %%l:\ set "SL=%%l"
if not defined SL goto bootauto
mountvol %SL%: /s >>"%LOG%" 2>&1
if not exist %SL%:\ goto bootauto
call :log "Boot EFI partition mounted as %SL%: by mountvol /s"
bcdboot %TGT%\Windows /s %SL%: /f UEFI >>"%LOG%" 2>&1
if not errorlevel 1 goto bootok
call :log "WARN: bcdboot to the boot EFI partition failed - trying automatic"
:bootauto
bcdboot %TGT%\Windows >>"%LOG%" 2>&1
if not errorlevel 1 goto bootok
rem Du phong: gan ky tu cho phan vung he thong (diskpart danh dau "System") roi chi ro /s /f
call :log "bcdboot auto failed - assigning a letter to the system partition"
> "%TEMP%\ith-lv.txt" echo list volume
set "SYSVOL="
for /f "tokens=2" %%v in ('diskpart /s "%TEMP%\ith-lv.txt" ^| findstr /i /r /c:"Volume [0-9].*System"') do if not defined SYSVOL set "SYSVOL=%%v"
if not defined SYSVOL (
  call :log "ERROR: system partition not found"
  goto hard
)
> "%TEMP%\ith-as.txt" echo select volume %SYSVOL%
>>"%TEMP%\ith-as.txt" echo assign letter=Y
diskpart /s "%TEMP%\ith-as.txt" >>"%LOG%" 2>&1
bcdboot %TGT%\Windows /s Y: /f {{FIRMWARE}} >>"%LOG%" 2>&1
if errorlevel 1 (
  call :log "ERROR: bcdboot failed"
  goto hard
)
:bootok
call :removeentries

> "%STG%\logs\winpe.done" echo %DATE% %TIME%
call :log "=== Done. Rebooting into the new Windows."
wpeutil reboot
exit /b 0

rem ======================================================================
:safe
rem Chua xoa gi: bo muc khoi dong tam, ve Windows cu.
call :removeentries
> "%STG%\logs\winpe.safe" echo %DATE% %TIME%
call :log "SAFE STOP: nothing was erased. Rebooting to the old Windows in 30s."
echo.
echo [ITH] Chua xoa gi. May se khoi dong lai ve Windows cu sau 30 giay.
ping -n 31 127.0.0.1 >nul
wpeutil reboot
exit /b 1

:safe_after_format_attempt
rem diskpart bao loi khi format: kiem tra Windows cu con khong.
if exist "%TGT%\Windows\System32" goto safe
goto hard

:hard
call :removeentries
> "%STG%\logs\winpe.failed" echo %DATE% %TIME%
call :log "FAILED after erasing %TGT%. Old Windows is gone. See %LOG%"
echo.
echo [ITH] LOI: o %TGT% da bi xoa nhung chua cai xong Windows moi.
echo [ITH] Nhat ky: %LOG%
echo [ITH] Go: notepad "%LOG%"  de xem.  Go: wpeutil reboot  de khoi dong lai.
cmd /k
exit /b 2

:removeentries
if defined ITH_ENTRY bcdedit /delete %ITH_ENTRY% /cleanup >>"%LOG%" 2>&1
if defined ITH_RAMDISK bcdedit /delete %ITH_RAMDISK% >>"%LOG%" 2>&1
exit /b 0

:log
echo [%TIME%] %~1
>>"%LOG%" echo [%TIME%] %~1
exit /b 0
