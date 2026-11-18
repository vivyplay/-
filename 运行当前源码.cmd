@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "src\Suisheng\Suisheng.csproj"
if not "%errorlevel%"=="0" (
  echo.
  echo Build or startup failed. Install .NET 10 SDK and read QODER_CN_HANDOFF.md.
  pause
)
exit /b %errorlevel%
