@echo off
rem Undoes "Add to Windows". The app folder itself is left alone.
set PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\setup.ps1" -Unregister
echo.
pause
