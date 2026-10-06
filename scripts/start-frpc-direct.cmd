@echo off
setlocal
rem Copy this launcher beside frpc.exe and your private frpc.toml.
rem Proxy variables are cleared only for this launcher and its child process.
set "HTTP_PROXY="
set "HTTPS_PROXY="
set "ALL_PROXY="
cd /d "%~dp0"
if errorlevel 1 exit /b 1
if not exist "%~dp0frpc.exe" exit /b 2
if not exist "%~dp0frpc.toml" exit /b 3
echo [%DATE% %TIME%] FRP direct startup >> "%~dp0frpc-startup.log"
"%~dp0frpc.exe" -c "%~dp0frpc.toml" >> "%~dp0frpc-startup.log" 2>&1
exit /b %errorlevel%
