@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0idle-shutdown-test.ps1" %*
exit /b %ERRORLEVEL%
