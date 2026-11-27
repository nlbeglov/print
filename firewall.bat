@echo off
rem Blocks internet access for EpsPrint.exe and Ghostscript using Windows Firewall.
rem Updater.exe is NOT blocked - it is the only part that may go online (to GitHub).
rem Run as Administrator (right click - Run as administrator). Re-run if you move the folder.
setlocal
net session >nul 2>&1
if errorlevel 1 (
  echo Please run this file as Administrator: right click - Run as administrator.
  pause
  exit /b 1
)
set "RULE=EpsPrint - no internet"
netsh advfirewall firewall delete rule name="%RULE%" >nul 2>&1

call :block "%~dp0EpsPrint.exe"
for /d %%D in ("%ProgramFiles%\gs\gs*" "%ProgramFiles(x86)%\gs\gs*") do (
  for %%E in (gswin64c.exe gswin64.exe gswin32c.exe gswin32.exe) do (
    if exist "%%~D\bin\%%E" call :block "%%~D\bin\%%E"
  )
)
if exist "%~dp0gs\bin\gswin64c.exe" call :block "%~dp0gs\bin\gswin64c.exe"

echo.
echo Done. EpsPrint and Ghostscript have no internet access now.
pause
exit /b 0

:block
netsh advfirewall firewall add rule name="%RULE%" dir=out action=block program=%1 enable=yes >nul
netsh advfirewall firewall add rule name="%RULE%" dir=in action=block program=%1 enable=yes >nul
echo Blocked: %~1
exit /b 0
