@echo off
setlocal
set "APP=%~dp0dist\Suisheng-stage1-win-x64\Suisheng.exe"
if not exist "%APP%" (
  echo Missing stage 1 executable: %APP%
  pause
  exit /b 1
)
start "" "%APP%"
