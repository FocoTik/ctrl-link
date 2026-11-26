@echo off
setlocal
rem Removes Ctrl Link that build.bat installed. Settings > Apps runs this too.
rem Your pairings and settings in %APPDATA%\Ctrl Link and received files in Downloads\Ctrl Link are kept.
set "DEST=%LOCALAPPDATA%\Programs\Ctrl Link"

rem Run from a temporary copy, so the install folder (which holds this file) can be deleted.
if /i not "%~1"=="/run" (
  copy /y "%~f0" "%TEMP%\CtrlLink-uninstall.cmd" >nul
  "%TEMP%\CtrlLink-uninstall.cmd" /run
)

choice /m "Remove Ctrl Link from this PC"
if errorlevel 2 goto :end

echo Quitting Ctrl Link...
taskkill /im CtrlLink.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul

echo Removing shortcuts and the start-at-login entry...
powershell -NoProfile -ExecutionPolicy Bypass -Command "foreach($d in @([Environment]::GetFolderPath('Programs'),[Environment]::GetFolderPath('Desktop'))){ Remove-Item -LiteralPath (Join-Path $d 'Ctrl Link.lnk') -ErrorAction SilentlyContinue }; $r='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'; $v=(Get-ItemProperty -Path $r -Name 'Ctrl Link' -ErrorAction SilentlyContinue).'Ctrl Link'; if($v -and $v -like ('*'+$env:DEST+'*')){ Remove-ItemProperty -Path $r -Name 'Ctrl Link' }"
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\CtrlLink-BuildKit" /f >nul 2>&1

echo Removing %DEST%
if exist "%DEST%" rmdir /s /q "%DEST%"
if exist "%DEST%" (echo Some files could not be removed. Close Ctrl Link and delete "%DEST%" by hand.) else (echo Ctrl Link was removed.)
echo Your pairings and settings are kept in %APPDATA%\Ctrl Link. Delete that folder too for a clean removal.

:end
pause
rem Delete this temporary copy.
(goto) 2>nul & del "%~f0"
