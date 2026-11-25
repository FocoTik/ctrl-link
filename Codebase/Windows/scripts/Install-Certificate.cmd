@echo off
rem Trusts the "Focotik agency" code-signing certificate on this PC, so Ctrl Link installers it signed show that publisher.
rem Run once (double-click). It asks for administrator rights. Keep Focotik-agency.cer in the same folder as this file.
rem The certificate can only sign programs (not websites or other certificates). Remove it any time with Uninstall below.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0' -ArgumentList '%1'" & exit /b)
cd /d "%~dp0"
if /i "%1"=="uninstall" goto uninstall
if not exist "Focotik-agency.cer" (echo Focotik-agency.cer not found next to this file. & pause & exit /b 1)
certutil -addstore -f Root "Focotik-agency.cer" || goto fail
certutil -addstore -f TrustedPublisher "Focotik-agency.cer" || goto fail
echo.
echo Done. Installers signed by Focotik agency are now trusted on this PC.
echo To remove it later: open a terminal here and run  Install-Certificate.cmd uninstall
pause
exit /b 0
:uninstall
certutil -delstore Root "Focotik agency"
certutil -delstore TrustedPublisher "Focotik agency"
echo Removed.
pause
exit /b 0
:fail
echo Could not add the certificate.
pause
exit /b 1
