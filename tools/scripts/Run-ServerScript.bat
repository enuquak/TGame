@echo off
setlocal
if not defined tgame_server_script exit /b 1
set "tgame_pwsh=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%tgame_pwsh%" goto run
where pwsh.exe >nul 2>nul
if errorlevel 1 goto missing
set "tgame_pwsh=pwsh.exe"

:run
"%tgame_pwsh%" -NoLogo -NoProfile -File "%~dp0powershell\%tgame_server_script%.ps1" %*
set "tgame_exit_code=%errorlevel%"
if defined TGAME_NONINTERACTIVE goto finish
if not "%tgame_exit_code%"=="0" pause

:finish
exit /b %tgame_exit_code%

:missing
echo PowerShell 7 is required. Install Microsoft.PowerShell and retry.
exit /b 1
