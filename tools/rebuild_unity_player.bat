@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  重建 Unity 播放器并同步到 runtime/
rem  改了 Myproject 里的场景/模型/脚本后，必须跑一次这个脚本，
rem  否则 WPF 和 dotnet 编译产物运行的仍是旧播放器。
rem ============================================================

set "UNITY=D:\Unity2021.3.x\2021.3.45f2c1\Editor\Unity.exe"
set "PROJ=D:\AIfootball\Myproject"
set "OUT=D:\AIfootball\_build_demo"
set "RT=D:\AIfootball\runtime"
set "LOG=D:\AIfootball\logs\build_demo.log"

if not exist "%UNITY%" (
  echo [ERROR] 找不到 Unity 编辑器: %UNITY%
  echo         请修改本脚本第 11 行的 UNITY 变量。
  pause
  exit /b 1
)

if not exist "%LOG%" mkdir "D:\AIfootball\logs"

echo [1/3] Unity batchmode 构建中（约 1-3 分钟）...
"%UNITY%" -quit -batchmode -projectPath "%PROJ%" -executeMethod BuildDemoPlayer.Build -logFile "%LOG%"

if not exist "%OUT%\Myproject.exe" (
  echo [ERROR] 构建失败，请查看日志: %LOG%
  pause
  exit /b 1
)

echo [2/3] 同步到 runtime\ ...
if not exist "%RT%\Myproject_Data" mkdir "%RT%\Myproject_Data"
xcopy /E /Y /I /Q "%OUT%\Myproject_Data\*" "%RT%\Myproject_Data\" >nul
copy /Y "%OUT%\Myproject.exe" "%RT%\Myproject.exe" >nul
copy /Y "%OUT%\UnityPlayer.dll" "%RT%\UnityPlayer.dll" >nul
copy /Y "%OUT%\UnityCrashHandler64.exe" "%RT%\UnityCrashHandler64.exe" >nul
xcopy /E /Y /I /Q "%OUT%\MonoBleedingEdge\*" "%RT%\MonoBleedingEdge\" >nul

echo [3/3] 清理临时构建目录...
rmdir /S /Q "%OUT%"

echo.
echo 完成。runtime\ 已是新播放器，直接运行 WPF 即可看到改动。
echo 提醒: runtime\data 与 runtime\models 不会被覆盖，属于外部数据。
pause
