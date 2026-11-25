Ctrl Link - build on this PC
===========================

Everything needed is in this folder: the source code, the .NET SDK (in "sdk") and every library (in "packages").
Nothing is downloaded and nothing is installed on the PC.

1. Before extracting: right-click CtrlLink-build.zip > Properties > tick "Unblock" > OK.
   (Skips the "downloaded from the internet" mark, so build.bat runs without warnings.)
2. Extract to a short path, for example C:\CtrlLink-build  (long paths under Downloads can break the build).
3. Double-click build.bat. When it finishes, the "Ctrl Link" folder opens.
4. Double-click Ctrl Link\CtrlLink.exe. The first time, Windows Firewall asks about Ctrl Link:
   allow it on private networks. Your Wi-Fi must be set to Private (Settings > Network > your Wi-Fi).

The Ctrl Link folder is the whole app: move it anywhere, delete it to remove the app.
To start it with Windows, use the setting inside the app.
App data: %APPDATA%\Ctrl Link. Received files: Downloads\Ctrl Link.

Delete this build folder whenever you like. The built Ctrl Link folder keeps working without it.
