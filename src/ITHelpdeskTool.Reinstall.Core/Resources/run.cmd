@echo off
rem IT Helpdesk Tool - moi truong cai lai Windows (nam trong boot.wim, chay sau wpeinit).
rem Tim thu muc dem theo file danh dau roi chay apply.cmd trong do (apply.cmd nam ngoai boot.wim de sua duoc).
title IT Helpdesk Tool - Cai lai Windows
set "STG="
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
  if exist "%%d:{{STAGING_REL}}\ITH-STAGING-{{ID}}.tag" set "STG=%%d:{{STAGING_REL}}"
)
if not defined STG (
  echo [ITH] Khong tim thay thu muc dem {{STAGING_REL}} - chua xoa gi.
  echo [ITH] May se khoi dong lai ve Windows cu sau 30 giay.
  ping -n 31 127.0.0.1 >nul
  wpeutil reboot
  exit /b 1
)
call "%STG%\apply.cmd" "%STG%"
