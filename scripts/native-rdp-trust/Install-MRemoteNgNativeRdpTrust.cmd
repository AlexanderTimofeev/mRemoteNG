@echo off
setlocal

set "SCRIPT=%~dp0Install-MRemoteNgNativeRdpTrust.ps1"

if not exist "%SCRIPT%" (
    echo PowerShell script not found:
    echo %SCRIPT%
    pause
    exit /b 1
)

net session >nul 2>&1
if errorlevel 1 (
    echo Requesting Administrator privileges...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo Setup completed: READY
) else (
    echo Setup failed with exit code %RC%.
    echo Review: %%ProgramData%%\mRemoteNG\NativeRdpTrust\native-rdp-trust.log
)

echo.
pause
exit /b %RC%
