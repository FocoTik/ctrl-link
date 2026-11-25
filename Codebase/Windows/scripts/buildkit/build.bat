@echo off
setlocal
rem Builds Ctrl Link on this PC from the source in this folder. No internet, no admin rights, nothing gets installed:
rem the .NET SDK and every library are inside this folder. Result: "Ctrl Link\CtrlLink.exe" next to this file.
cd /d "%~dp0"
if not exist "sdk\dotnet.exe" (echo sdk\dotnet.exe is missing. Extract the whole zip first, then run build.bat again. & pause & exit /b 1)

rem Keep the SDK's own files in this folder and stop it from looking online.
set "DOTNET_ROOT=%~dp0sdk"
set "DOTNET_CLI_HOME=%~dp0.home"
set "NUGET_PACKAGES=%~dp0packages"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_MULTILEVEL_LOOKUP=0
set DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1
set DOTNET_GENERATE_ASPNET_CERTIFICATE=false
set "PATH=%~dp0sdk;%PATH%"

echo Building Ctrl Link. The first build takes a minute or two...
if exist "Ctrl Link\CtrlLink.exe" (
  tasklist /fi "imagename eq CtrlLink.exe" | find /i "CtrlLink.exe" >nul && (echo Ctrl Link is running. Quit it from the tray icon first, then run build.bat again. & pause & exit /b 1)
  rmdir /s /q "Ctrl Link"
)
"sdk\dotnet.exe" publish "Windows\src\CtrlLink.App" -c Release -r win-x64 --self-contained -p:PublishReadyToRun=false -o "Ctrl Link"
if errorlevel 1 (echo. & echo Build failed. See the messages above. & pause & exit /b 1)

echo.
echo Done: %~dp0Ctrl Link\CtrlLink.exe
echo The Ctrl Link folder is the whole app. Move it anywhere you like and double-click CtrlLink.exe.
start "" explorer "%~dp0Ctrl Link"
pause
