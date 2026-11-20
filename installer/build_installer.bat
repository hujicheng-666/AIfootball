@echo off
setlocal EnableExtensions

set "ROOT=%~dp0.."
set "COMPILER="
for /f "delims=" %%I in ('where iscc.exe 2^>nul') do if not defined COMPILER set "COMPILER=%%I"
if not defined COMPILER if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "COMPILER=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined COMPILER if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "COMPILER=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined COMPILER if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "COMPILER=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined COMPILER (
    echo [ERROR] Inno Setup 6.3 or later is required: https://jrsoftware.org/inno-setup/downloads
    exit /b 1
)

call "%ROOT%\scripts\build_release.bat"
if errorlevel 1 exit /b 1

"%COMPILER%" "%ROOT%\installer\AIfootball.iss"
if errorlevel 1 exit /b 1

echo Installer created under: %ROOT%\dist\installer
endlocal
