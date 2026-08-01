@echo off
:: ============================================================================
:: build.cmd — double-clickable wrapper for build.ps1.
::  1) Relaunches itself elevated (UAC) if not already administrator, so the
::     build can stop running app/WebView2 processes and replace locked files.
::  2) Runs build.ps1 with -ExecutionPolicy Bypass (works regardless of the
::     machine's PowerShell script policy).
:: build.ps1 bootstraps its own toolchain paths (user-local dotnet, nodejs).
:: ============================================================================
setlocal

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator elevation...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -WorkingDirectory '%~dp0'"
    exit /b
)

cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if %errorlevel% neq 0 (
    echo.
    echo ******** BUILD FAILED ^(exit %errorlevel%^) ********
    pause
    exit /b %errorlevel%
)

echo.
echo Build finished successfully.
pause
