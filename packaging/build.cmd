@echo off
setlocal
set "ROOT=%~dp0.."
set "OUT=%ROOT%\dist\Suisheng-stage2-win-x64"
dotnet publish "%ROOT%\src\Suisheng\Suisheng.csproj" -c Release -r win-x64 --self-contained true -o "%OUT%" -p:DebugType=None -p:DebugSymbols=false -p:RestoreLockedMode=true
if errorlevel 1 exit /b 1
if not exist "%OUT%\models" mkdir "%OUT%\models"
if exist "%ROOT%\tests\ggml-base.bin" copy /y "%ROOT%\tests\ggml-base.bin" "%OUT%\models\ggml-base.bin" >nul
if not exist "%OUT%\licenses" mkdir "%OUT%\licenses"
copy /y "%ROOT%\packaging\licenses\*.txt" "%OUT%\licenses\" >nul
if not exist "%OUT%\models\ggml-base.bin" echo Model not bundled. Download or import Base using the app before recording.
echo Output: %OUT%\Suisheng.exe
exit /b 0
