@echo off
setlocal
rem Prefer the standard PowerShell 7 installation, then search PATH.
set "tgame_pwsh=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%tgame_pwsh%" goto run
where pwsh.exe >nul 2>nul
if errorlevel 1 goto missing
set "tgame_pwsh=pwsh.exe"
:run
"%tgame_pwsh%" -NoLogo -NoProfile -File "%~dp0powershell\Connect-Server.ps1" %*
set "tgame_exit_code=%errorlevel%"
goto finish
:missing
echo PowerShell 7 was not found. Install it with:
echo winget install --id Microsoft.PowerShell --exact
set "tgame_exit_code=1"
:finish
echo.
if not "%tgame_exit_code%"=="0" echo Command failed. See the error above.
pause
exit /b %tgame_exit_code%
