Ctrl Link - build and install on this PC
========================================

Use this if the regular installer (CtrlLink-Windows-x64-Setup.exe) is blocked on your PC.
Everything needed is in this folder: the source code, the .NET SDK (in "sdk") and every library (in "packages").
Nothing is downloaded and no admin rights are needed. Because the app is built on your own PC,
Windows SmartScreen does not block it.

1. Before extracting: right-click CtrlLink-Windows-BuildKit.zip > Properties > tick "Unblock" > OK.
   (Removes the "downloaded from the internet" mark, so build.bat runs without warnings.)
2. Extract to a short path, for example C:\CtrlLink-build  (long paths under Downloads can break the build).
3. Double-click build.bat. It builds Ctrl Link (a minute or two the first time), installs it to
   %LOCALAPPDATA%\Programs\Ctrl Link, adds Start menu and desktop shortcuts, and starts it.
4. The first time, Windows Firewall asks about Ctrl Link: allow it on private networks.
   Your Wi-Fi must be set to Private (Settings > Network > your Wi-Fi).

To start it with Windows, use the setting inside the app.
To update: download the newest build kit and run build.bat again (quit Ctrl Link from the tray first).
To remove: Settings > Apps > Installed apps > Ctrl Link > Uninstall
           (or run "Uninstall Ctrl Link.cmd" in %LOCALAPPDATA%\Programs\Ctrl Link).
           It removes the app, its shortcuts and the start-at-login entry. Your pairings in %APPDATA%\Ctrl Link are kept;
           delete that folder too for a clean removal.
App data: %APPDATA%\Ctrl Link. Received files: Downloads\Ctrl Link.

You can delete this build folder afterwards. The installed app keeps working without it.
