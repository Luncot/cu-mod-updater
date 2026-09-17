@echo off
REM ============================================================
REM  CU-ModUpdater build script
REM
REM  NOTE: NuGet 7.9 shipped with SDK 10 resolves its machine-wide
REM  settings directory from ProgramFiles / ProgramFiles(x86).
REM  If those variables are missing from the environment, restore
REM  fails with:  Value cannot be null. (Parameter 'path1')
REM  So we must set them explicitly before calling dotnet.
REM ============================================================
set "USERPROFILE=C:\Users\Administrator"
set "HOMEDRIVE=C:"
set "HOMEPATH=\Users\Administrator"
set "APPDATA=C:\Users\Administrator\AppData\Roaming"
set "LOCALAPPDATA=C:\Users\Administrator\AppData\Local"
set "ProgramData=C:\ProgramData"
set "ALLUSERSPROFILE=C:\ProgramData"
set "SystemDrive=C:"
set "SystemRoot=C:\Windows"
set "windir=C:\Windows"
set "ProgramFiles=C:\Program Files"
set "ProgramFiles(x86)=C:\Program Files (x86)"
set "ProgramW6432=C:\Program Files"
set "CommonProgramFiles=C:\Program Files\Common Files"
set "CommonProgramFiles(x86)=C:\Program Files (x86)\Common Files"
set "CommonProgramW6432=C:\Program Files\Common Files"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"

if not exist "D:\AS\ASSV\_fallback" mkdir "D:\AS\ASSV\_fallback"
cd /d "D:\AS\ASSV"

echo === [1/3] restore (win-x64) ===
"C:\Program Files\dotnet\dotnet.exe" restore -r win-x64
if errorlevel 1 goto :fail

echo === [2/3] build ===
"C:\Program Files\dotnet\dotnet.exe" build -c Release --no-restore
if errorlevel 1 goto :fail

echo === [3/3] publish ===
"C:\Program Files\dotnet\dotnet.exe" publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-restore -o ".\publish"
if errorlevel 1 goto :fail

echo.
echo [OK] All done! New exe: D:\AS\ASSV\publish\CU-ModUpdater.exe
exit /b 0

:fail
echo.
echo [FAILED]
exit /b 1
