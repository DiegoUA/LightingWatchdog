@echo off
setlocal EnableExtensions
rem ============================================================================
rem  LightingWatchdog launcher
rem  Double-click for a menu, or pass a mode: monitor | manage | scan
rem    monitor : watch and log only, never touches any process (no admin needed)
rem    manage  : watch AND restart a leaking service / auto-kill (needs admin)
rem    scan    : one diagnostic scan, then exit (no admin needed)
rem ============================================================================

title LightingWatchdog
set "LW_SCRIPT=%~dp0scripts\NetworkDiag.ps1"

rem Colors use ANSI escape codes (Windows 10 and later). Without them the menu stays plain.
set "ESC="
for /f %%a in ('echo prompt $E ^| cmd') do set "ESC=%%a"
set "C_RST=" & set "C_TITLE=" & set "C_DIM=" & set "C_KEY=" & set "C_LBL=" & set "C_OK=" & set "C_ERR="
if defined ESC (
    set "C_RST=%ESC%[0m"
    set "C_TITLE=%ESC%[96;1m"
    set "C_DIM=%ESC%[90m"
    set "C_KEY=%ESC%[93m"
    set "C_LBL=%ESC%[97m"
    set "C_OK=%ESC%[92m"
    set "C_ERR=%ESC%[91m"
)

if not exist "%LW_SCRIPT%" (
    echo.
    echo   %C_ERR%Cannot find scripts\NetworkDiag.ps1%C_RST%
    echo   Keep this file next to the "scripts" folder.
    echo.
    pause
    exit /b 1
)

where powershell >nul 2>&1
if errorlevel 1 (
    echo.
    echo   %C_ERR%Windows PowerShell was not found.%C_RST%
    echo.
    pause
    exit /b 1
)

if /i "%~1"=="monitor" goto :monitor
if /i "%~1"=="manage"  goto :manage
if /i "%~1"=="scan"    goto :scan

:menu
cls
echo.
echo   %C_TITLE%LightingWatchdog%C_RST%
echo   %C_DIM%------------------------------------------%C_RST%
echo.
echo   %C_KEY%1%C_RST%  %C_LBL%Monitor only%C_RST%        %C_DIM%watch and log only%C_RST%
echo   %C_KEY%2%C_RST%  %C_LBL%Monitor and manage%C_RST%  %C_DIM%restart leaks (admin)%C_RST%
echo   %C_KEY%3%C_RST%  %C_LBL%One scan%C_RST%            %C_DIM%then exit%C_RST%
echo   %C_KEY%Q%C_RST%  %C_LBL%Quit%C_RST%
echo.
echo   %C_DIM%Press 1, 2, 3 or Q%C_RST%
choice /c 123Q /n >nul
if errorlevel 4 exit /b 0
if errorlevel 3 goto :scan
if errorlevel 2 goto :manage
if errorlevel 1 goto :monitor
goto :menu

:monitor
echo.
echo   %C_OK%Starting: monitor only%C_RST%
start "LightingWatchdog - monitor only" powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File "%LW_SCRIPT%" -Watchdog -MonitorOnly
exit /b 0

:manage
rem Already elevated (for example started by Task Scheduler with highest privileges)? Then no UAC prompt is needed.
>nul 2>&1 fltmc
if not errorlevel 1 (
    echo.
    echo   %C_OK%Starting: monitor and manage%C_RST%
    start "LightingWatchdog - manage" powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File "%LW_SCRIPT%" -Watchdog
    exit /b 0
)
echo.
echo   %C_DIM%Windows will ask for administrator rights...%C_RST%
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$q=[char]34; try { Start-Process -FilePath powershell.exe -Verb RunAs -ErrorAction Stop -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -NoExit -File ' + $q + $env:LW_SCRIPT + $q + ' -Watchdog'); exit 0 } catch { exit 1 }"
if errorlevel 1 (
    echo.
    echo   %C_ERR%Administrator rights were not granted.%C_RST%
    echo   Nothing was started. Option 1 needs no rights.
    echo.
    pause
    goto :menu
)
exit /b 0

:scan
echo.
echo   %C_DIM%Running one scan...%C_RST%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%LW_SCRIPT%"
echo.
pause
exit /b 0
