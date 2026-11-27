@echo off
rem Builds EpsPrint.exe and Updater.exe into dist\ using the C# compiler
rem that ships with every Windows 10/11 (.NET Framework 4.8). Nothing to install.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] .NET Framework 4.x C# compiler not found.
  exit /b 1
)

set /p VER=<VERSION
if not exist shared mkdir shared
echo [assembly: System.Reflection.AssemblyVersion("%VER%")]> shared\Version.cs
echo [assembly: System.Reflection.AssemblyFileVersion("%VER%")]>> shared\Version.cs

if not exist dist mkdir dist
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
set OPTS=/nologo /optimize+ /codepage:65001 /platform:anycpu

echo Building EpsPrint.exe ...
"%CSC%" %OPTS% /target:winexe /out:dist\EpsPrint.exe %REFS% /r:System.Web.Extensions.dll src\*.cs shared\Version.cs || goto :fail

echo Building Updater.exe ...
"%CSC%" %OPTS% /target:winexe /out:dist\Updater.exe %REFS% /r:System.Web.Extensions.dll updater\*.cs shared\Version.cs || goto :fail

copy /y README.txt dist\ >nul
copy /y firewall.bat dist\ >nul

echo.
echo Build OK. Version %VER%. Files are in dist\
exit /b 0

:fail
echo.
echo [ERROR] Build failed.
exit /b 1
