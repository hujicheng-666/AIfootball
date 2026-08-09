@echo off
REM ============================================================
REM  AIfootball v2.0 - Release build script (ASCII-only)
REM  Stages dist/app for Inno Setup and creates a portable copy.
REM  DEMO package: no calibration, samples = sample1/2/3 only.
REM  Requires: .NET 8.0 SDK
REM ============================================================
setlocal enabledelayedexpansion

set ROOT=%~dp0..
set SRC=%ROOT%\src
set OUTPUT=%ROOT%\dist
set APP_PROJ=%SRC%\AIfootball.App\AIfootball.App.csproj
set RUNTIME_DATA=%ROOT%

echo.
echo ========================================
echo   AIfootball v2.0 - Release build
echo ========================================
echo.

REM --- Clean output ---
if exist "%OUTPUT%" (
    echo [1/6] Cleaning old build...
    rmdir /s /q "%OUTPUT%" 2>nul
)
mkdir "%OUTPUT%" 2>nul

REM --- Restore NuGet ---
echo [2/6] Restoring NuGet packages...
dotnet restore "%APP_PROJ%" --runtime win-x64
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] NuGet restore failed!
    exit /b 1
)

REM --- Build WPF app ---
echo [3/6] Publishing WPF app (Release)...
dotnet publish "%APP_PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -p:EnableMsixTooling=false -o "%OUTPUT%\app"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] WPF publish failed!
    exit /b 1
)
echo   OK: WPF app published

REM --- Stage runtime data ---
echo [4/6] Staging runtime data...

REM Demo package: goalkeepers only (no calibration, no prebuilt CSVs)
if exist "%RUNTIME_DATA%\data\goalkeepers" (
    mkdir "%OUTPUT%\app\data\goalkeepers" 2>nul
    robocopy "%RUNTIME_DATA%\data\goalkeepers" "%OUTPUT%\app\data\goalkeepers" /e /njh /njs >nul
    if errorlevel 8 exit /b 1
    echo   OK: data/goalkeepers/
)

REM Demo package: reserve empty calib\ folders for on-site photo calibration
mkdir "%OUTPUT%\app\calib" 2>nul
mkdir "%OUTPUT%\app\calib\left" 2>nul
mkdir "%OUTPUT%\app\calib\right" 2>nul
(
echo AIfootball camera calibration folder
echo.
echo INTRINSICS: left/right chessboard photos
echo   Put left camera chessboard photos in:  calib\left
echo   Put right camera chessboard photos in: calib\right
echo   Then run Intrinsics calibration from the app Calibration page.
echo.
echo EXTRINSICS: left/right football-field reference photos
echo   Choose one photo per camera in the app, then run Extrinsics
echo   calibration and click reference points when prompted.
echo.
echo Result files (intrinsics_*.npz, *_pose.npz, *_extrinsics.json)
echo will be written here and used by 3D reconstruction.
) > "%OUTPUT%\app\calib\README.txt"
echo   OK: calib/ (empty, reserved for on-site photo calibration)

if exist "%RUNTIME_DATA%\models" (
    mkdir "%OUTPUT%\app\models" 2>nul
    robocopy "%RUNTIME_DATA%\models" "%OUTPUT%\app\models" /e /njh /njs >nul
    if errorlevel 8 exit /b 1
    echo   OK: models/
)

REM Demo package: sample1/2/3 ORIGINAL VIDEOS ONLY (no derived outputs)
for %%d in (sample1 sample2 sample3) do (
    if exist "%RUNTIME_DATA%\samples\%%d" (
        mkdir "%OUTPUT%\app\samples\%%d" 2>nul
        robocopy "%RUNTIME_DATA%\samples\%%d" "%OUTPUT%\app\samples\%%d" *.mp4 /njh /njs >nul
        if errorlevel 8 exit /b 1
    )
)
echo   OK: samples/ (sample1 sample2 sample3)

REM --- Copy pipeline scripts (runtime/project -> app/project) ---
if exist "%RUNTIME_DATA%\runtime\project" (
    mkdir "%OUTPUT%\app\project" 2>nul
    robocopy "%RUNTIME_DATA%\runtime\project" "%OUTPUT%\app\project" /e /njh /njs >nul
    if errorlevel 8 exit /b 1
    echo   OK: project/ (pipeline scripts)
)

REM --- Copy Unity viewer ---
echo [5/6] Copying Unity viewer...
set "UNITY_RUNTIME=%ROOT%\runtime"
if not exist "%UNITY_RUNTIME%\Myproject.exe" (
    echo [ERROR] Unity viewer is missing: %UNITY_RUNTIME%\Myproject.exe
    exit /b 1
)
mkdir "%OUTPUT%\app\viewer" 2>nul
copy /y "%UNITY_RUNTIME%\Myproject.exe" "%OUTPUT%\app\viewer\Myproject.exe" >nul
copy /y "%UNITY_RUNTIME%\UnityPlayer.dll" "%OUTPUT%\app\viewer\UnityPlayer.dll" >nul
if exist "%UNITY_RUNTIME%\UnityCrashHandler64.exe" copy /y "%UNITY_RUNTIME%\UnityCrashHandler64.exe" "%OUTPUT%\app\viewer\UnityCrashHandler64.exe" >nul
robocopy "%UNITY_RUNTIME%\Myproject_Data" "%OUTPUT%\app\viewer\Myproject_Data" /e /njh /njs >nul
if errorlevel 8 exit /b 1
robocopy "%UNITY_RUNTIME%\MonoBleedingEdge" "%OUTPUT%\app\viewer\MonoBleedingEdge" /e /njh /njs >nul
if errorlevel 8 exit /b 1
if not exist "%OUTPUT%\app\viewer\Myproject.exe" exit /b 1
if not exist "%OUTPUT%\app\viewer\Myproject_Data" exit /b 1

REM --- Create portable copy ---
set PORTABLE=%OUTPUT%\AIfootball-Portable-v2.0
xcopy /y /e /q "%OUTPUT%\app\*" "%PORTABLE%\" >nul
(
echo AIfootball v2.0 - AI Football Trajectory Analysis Platform
echo ========================================
echo.
echo This is the DEMO package. Camera calibration files are NOT
echo included, so you can demonstrate the full workflow from
echo calibration onward.
echo.
echo Quick start:
echo   1. Run AIfootball.exe
echo   2. On first launch click "One-click install environment"
echo   3. Wait for Python + PyTorch + YOLO model setup
echo.
echo Directory notes:
echo   python_env\    auto-created on first run
echo   models\        YOLO model
echo   samples\       demo samples (sample1/2/3)
echo   data\          goalkeeper presets
echo   output\        created during demo processing
) > "%PORTABLE%\README.txt"
echo   OK: portable %PORTABLE%

REM --- Optional MSIX ---
echo [6/6] Building MSIX (optional, needs Windows 10 SDK)...
where signtool >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    dotnet publish "%APP_PROJ%" -c Release -r win-x64 --self-contained false -p:EnableMsixTooling=true -p:AppxPackageDir="%OUTPUT%\msix" -p:AppxBundle=Always -p:AppxBundlePlatforms="x64" -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=false 2>nul
    if %ERRORLEVEL% EQU 0 (
        echo   OK: MSIX at %OUTPUT%\msix\
    ) else (
        echo   MSIX skipped (missing Windows SDK)
    )
) else (
    echo   MSIX skipped (Windows SDK not detected)
)

echo.
echo ========================================
echo   Build finished!
echo ========================================
echo.
echo   Portable: %PORTABLE%
echo   App:      %OUTPUT%\app\
if exist "%OUTPUT%\msix\*.msixbundle" echo   MSIX:     %OUTPUT%\msix\
echo.
endlocal
