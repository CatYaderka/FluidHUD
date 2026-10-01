@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "exitCode=%errorlevel%"
endlocal & exit /b %exitCode%
