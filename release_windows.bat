@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT_DIR=%~dp0"
set "LAUNCHER_DIR=%ROOT_DIR%FootballLauncher"
set "SOURCE_RUNTIME=%ROOT_DIR%runtime"
set "UNITY_BUILD=%ROOT_DIR%Myproject\Builds\Windows"
set "RELEASE_ROOT=%ROOT_DIR%release"
set "RELEASE_DIR=%RELEASE_ROOT%\AIfootball-Windows"
set "PUBLISH_DIR=%LAUNCHER_DIR%\publish"

echo [1/5] Build self-contained Windows launcher...
dotnet restore "%LAUNCHER_DIR%\FootballLauncher.csproj"
if errorlevel 1 exit /b 1
dotnet publish "%LAUNCHER_DIR%\FootballLauncher.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o "%PUBLISH_DIR%"
if errorlevel 1 exit /b 1

echo [2/5] Create clean release directory...
if exist "%RELEASE_DIR%" rmdir /s /q "%RELEASE_DIR%"
mkdir "%RELEASE_DIR%"
mkdir "%RELEASE_DIR%\project"
mkdir "%RELEASE_DIR%\data\goalkeepers"
mkdir "%RELEASE_DIR%\calib"
mkdir "%RELEASE_DIR%\samples"
mkdir "%RELEASE_DIR%\output"

echo [3/5] Copy Unity player and launcher...
copy /y "%PUBLISH_DIR%\FootballLauncher.exe" "%RELEASE_DIR%\FootballLauncher.exe" >nul
copy /y "%UNITY_BUILD%\FootballPenaltyCsvPlatform.exe" "%RELEASE_DIR%\FootballPenaltyCsvPlatform.exe" >nul
copy /y "%UNITY_BUILD%\UnityPlayer.dll" "%RELEASE_DIR%\UnityPlayer.dll" >nul
copy /y "%UNITY_BUILD%\UnityCrashHandler64.exe" "%RELEASE_DIR%\UnityCrashHandler64.exe" >nul
xcopy /e /i /q /y "%UNITY_BUILD%\FootballPenaltyCsvPlatform_Data" "%RELEASE_DIR%\FootballPenaltyCsvPlatform_Data" >nul
xcopy /e /i /q /y "%UNITY_BUILD%\MonoBleedingEdge" "%RELEASE_DIR%\MonoBleedingEdge" >nul

echo [4/5] Copy inference resources...
copy /y "%ROOT_DIR%build\main.py" "%RELEASE_DIR%\main.py" >nul
copy /y "%ROOT_DIR%build\project\*.py" "%RELEASE_DIR%\project\" >nul
copy /y "%ROOT_DIR%build\project\*.jpg" "%RELEASE_DIR%\project\" >nul
copy /y "%ROOT_DIR%build\yolo11m.pt" "%RELEASE_DIR%\yolo11m.pt" >nul
copy /y "%SOURCE_RUNTIME%\calib\*" "%RELEASE_DIR%\calib\" >nul
copy /y "%SOURCE_RUNTIME%\data\goalkeepers\*.json" "%RELEASE_DIR%\data\goalkeepers\" >nul
copy /y "%SOURCE_RUNTIME%\data\penalty_*_trajectory.csv" "%RELEASE_DIR%\data\" >nul
echo.>"%RELEASE_DIR%\project\__init__.py"

echo [5/5] Verify release contents...
if not exist "%RELEASE_DIR%\FootballLauncher.exe" exit /b 2
if not exist "%RELEASE_DIR%\FootballPenaltyCsvPlatform.exe" exit /b 2
if not exist "%RELEASE_DIR%\main.py" exit /b 2
if not exist "%RELEASE_DIR%\yolo11m.pt" exit /b 2
if exist "%RELEASE_DIR%\python_env" exit /b 3
if exist "%RELEASE_DIR%\.git" exit /b 3

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
echo.
echo Release ready: %RELEASE_DIR%
echo Users should start FootballLauncher.exe. Python dependencies are installed automatically on first run.
exit /b 0
