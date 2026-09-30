@echo off
cd /d "%~dp0"

rem Close a running Argus first (it locks dist\Argus.exe and blocks the build).
tasklist /FI "IMAGENAME eq Argus.exe" 2>nul | find /I "Argus.exe" >nul
if not errorlevel 1 (
    echo Closing running Argus...
    taskkill /F /IM Argus.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
)

echo Building Argus...
dotnet publish src\Argus.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
if errorlevel 1 goto fail
rem Bundle Node.js (from PATH) next to Argus.exe so the CCTV tab works without a separate Node install.
if not exist dist\runtime mkdir dist\runtime
set "NODE_EXE="
for /f "delims=" %%i in ('where node 2^>nul') do if not defined NODE_EXE set "NODE_EXE=%%i"
if defined NODE_EXE (
    copy /y "%NODE_EXE%" dist\runtime\node.exe >nul
    echo Bundled Node.js: %NODE_EXE%
) else (
    echo [warn] node.exe not found on PATH - the CCTV tab will need Node.js installed.
)
echo.
echo Done: %~dp0dist\Argus.exe
pause
exit /b 0
:fail
echo Build failed.
pause
exit /b 1
