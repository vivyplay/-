@echo off
setlocal
set "ROOT=%~dp0.."
dotnet build "%~dp0Suisheng.Tests.csproj" -c Debug
if not "%errorlevel%"=="0" exit /b 1
"%~dp0bin\Debug\net10.0-windows\win-x64\Suisheng.Tests.exe" "%~dp0."
if not "%errorlevel%"=="0" exit /b 1
"%~dp0bin\Debug\net10.0-windows\win-x64\Suisheng.Tests.exe" "%~dp0." --desktop
if not "%errorlevel%"=="0" exit /b 1
if "%~1"=="" exit /b 0
"%~dp0bin\Debug\net10.0-windows\win-x64\Suisheng.Tests.exe" "%~dp0." --desktop --executable "%~f1"
if not "%errorlevel%"=="0" exit /b 1
exit /b 0
