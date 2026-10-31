@echo off
rem Adds PDF Viewer to the Start Menu and to the "Open with" and right-click
rem menus of PDF files. Writes only to your own user account (HKCU) - no
rem administrator rights, nothing installed system-wide.
set PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\setup.ps1" -Register
echo.
pause
