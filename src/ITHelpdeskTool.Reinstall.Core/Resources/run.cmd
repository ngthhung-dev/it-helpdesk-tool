@echo off
rem IT Helpdesk Tool - moi truong cai lai Windows (nam trong boot.wim, chay sau wpeinit).
rem Tim thu muc dem theo file danh dau roi chay apply.cmd trong do (apply.cmd nam ngoai boot.wim de sua duoc).
rem O cung thu hai / o cham co the chua san sang ngay sau wpeinit: tim lai toi da ~60 giay, giua chung quet lai o dia.
title IT Helpdesk Tool - Cai lai Windows
set "STG="
for /l %%n in (1,1,12) do if not defined STG call :find %%n
if defined STG goto found

echo [ITH] Khong tim thay thu muc dem {{STAGING_REL}} - chua xoa gi.
rem Ghi chan doan len o Windows (tim theo file danh dau) de xem lai sau khi ve Windows cu
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do if exist "%%d:\ITH-TARGET-{{ID}}.tag" call :diag %%d
echo [ITH] May se khoi dong lai ve Windows cu sau 30 giay.
ping -n 31 127.0.0.1 >nul
wpeutil reboot
exit /b 1

:found
call "%STG%\apply.cmd" "%STG%"
exit /b

:find
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do if exist "%%d:{{STAGING_REL}}\ITH-STAGING-{{ID}}.tag" set "STG=%%d:{{STAGING_REL}}"
if defined STG exit /b 0
echo [ITH] Dang cho o dia san sang (%1/12) ...
if "%1"=="4" (echo rescan| diskpart >nul 2>&1)
if "%1"=="8" (echo rescan| diskpart >nul 2>&1)
ping -n 6 127.0.0.1 >nul
exit /b 0

:diag
> "%TEMP%\ith-lv.txt" echo list volume
> "%1:\ITH-winpe-khong-thay-o-dem.txt" echo %DATE% %TIME% - khong tim thay {{STAGING_REL}}\ITH-STAGING-{{ID}}.tag
diskpart /s "%TEMP%\ith-lv.txt" >> "%1:\ITH-winpe-khong-thay-o-dem.txt" 2>&1
exit /b 0
