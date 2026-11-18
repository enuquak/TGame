@echo off
setlocal
set "tgame_server_script=%~n0"
call "%~dp0Run-ServerScript.bat" %*
exit /b %errorlevel%
