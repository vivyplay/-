@echo off
setlocal
set "ROOT=%~dp0.."
call "%ROOT%\packaging\build.cmd"
if errorlevel 1 exit /b 1
set "ISCC="
where ISCC.exe >nul 2>nul
if not errorlevel 1 set "ISCC=ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC (
  echo Inno Setup 6 compiler not found. Install Inno Setup 6, then run this script again.
  exit /b 2
)
"%ISCC%" "%ROOT%\packaging\Suisheng.iss"
if errorlevel 1 exit /b 1
if not exist "%ROOT%\dist\Suisheng-Setup-0.4.0-win-x64.exe" exit /b 1
echo Installer: %ROOT%\dist\Suisheng-Setup-0.4.0-win-x64.exe
exit /b 0
