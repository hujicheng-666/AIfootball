@echo off
setlocal EnableExtensions

set "ROOT=%~dp0.."
set "STAGED_APP=%ROOT%\dist\app"
set "ISS_FILE=%ROOT%\installer\AIfootball.iss"

call "%ROOT%\scripts\build_release.bat"
if errorlevel 1 exit /b 1

if not exist "%STAGED_APP%\AIfootball.exe" (
    echo [ERROR] Release files are missing: %STAGED_APP%
    echo Run scripts\build_release.bat first.
    exit /b 1
)

where iscc.exe >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Inno Setup 6 was not found.
    echo Install it from https://jrsoftware.org/isdl.php, then run this script again.
    exit /b 1
)

iscc.exe "%ISS_FILE%"
if errorlevel 1 exit /b 1

echo Installer created under: %ROOT%\dist\installer
endlocal
