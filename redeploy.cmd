@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0redeploy.ps1" %*
exit /b %ERRORLEVEL%

