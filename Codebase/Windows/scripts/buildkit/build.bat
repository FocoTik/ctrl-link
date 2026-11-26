@echo off
setlocal
rem Builds Ctrl Link on this PC from the source in this folder, then installs it for this user.
rem No internet, no admin rights: the .NET SDK and every library are inside this folder.
rem Result: Ctrl Link in %LOCALAPPDATA%\Programs\Ctrl Link, with Start menu and desktop shortcuts.
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
set "DEST=%LOCALAPPDATA%\Programs\Ctrl Link"

tasklist /fi "imagename eq CtrlLink.exe" | find /i "CtrlLink.exe" >nul && (echo Ctrl Link is running. Quit it from the tray icon first, then run build.bat again. & pause & exit /b 1)

echo [1/3] Building Ctrl Link. The first build takes a minute or two...
if exist "Ctrl Link" rmdir /s /q "Ctrl Link"
"sdk\dotnet.exe" publish "Windows\src\CtrlLink.App" -c Release -r win-x64 --self-contained -p:PublishReadyToRun=false -o "Ctrl Link"
if errorlevel 1 (echo. & echo Build failed. See the messages above. & pause & exit /b 1)
if not exist "Ctrl Link\CtrlLink.exe" (echo. & echo Build finished but CtrlLink.exe is missing. & pause & exit /b 1)

echo.
echo [2/3] Installing to %DEST%
if exist "%DEST%" rmdir /s /q "%DEST%"
robocopy "Ctrl Link" "%DEST%" /e /njh /njs /nfl /ndl /np >nul
if errorlevel 8 (echo Copying the app failed. & pause & exit /b 1)

copy /y "uninstall.cmd" "%DEST%\Uninstall Ctrl Link.cmd" >nul

rem List it in Settings > Apps (per-user, no admin) so it can be removed like any app.
set "VER=1.0"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Select-Xml -Path 'Windows\Directory.Build.props' -XPath '//Version').Node.InnerText"`) do set "VER=%%v"
set "UKEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\CtrlLink-BuildKit"
reg add "%UKEY%" /v DisplayName /d "Ctrl Link" /f >nul
reg add "%UKEY%" /v DisplayVersion /d "%VER%" /f >nul
reg add "%UKEY%" /v Publisher /d "Focotik agency" /f >nul
reg add "%UKEY%" /v DisplayIcon /d "%DEST%\CtrlLink.exe" /f >nul
reg add "%UKEY%" /v InstallLocation /d "%DEST%" /f >nul
reg add "%UKEY%" /v UninstallString /d "\"%DEST%\Uninstall Ctrl Link.cmd\"" /f >nul
reg add "%UKEY%" /v NoModify /t REG_DWORD /d 1 /f >nul
reg add "%UKEY%" /v NoRepair /t REG_DWORD /d 1 /f >nul

echo [3/3] Adding Start menu and desktop shortcuts
powershell -NoProfile -ExecutionPolicy Bypass -Command "$w=New-Object -ComObject WScript.Shell; foreach($d in @([Environment]::GetFolderPath('Programs'),[Environment]::GetFolderPath('Desktop'))){ $s=$w.CreateShortcut((Join-Path $d 'Ctrl Link.lnk')); $s.TargetPath=$env:DEST+'\CtrlLink.exe'; $s.WorkingDirectory=$env:DEST; $s.IconLocation=$env:DEST+'\CtrlLink.exe,0'; $s.Description='Ctrl Link'; $s.Save() }"
if errorlevel 1 echo Could not add shortcuts. You can still start Ctrl Link from "%DEST%\CtrlLink.exe".

echo.
echo Done. Ctrl Link is installed and starting now.
echo Find it later in the Start menu or on the desktop.
echo To remove it: Settings ^> Apps ^> Installed apps ^> Ctrl Link ^> Uninstall, or run "%DEST%\Uninstall Ctrl Link.cmd".
echo The first time, Windows Firewall asks about Ctrl Link: allow it on private networks.
start "" "%DEST%\CtrlLink.exe"
pause
