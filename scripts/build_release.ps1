$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'

function Get-ReleaseFile([string]$path) {
    $file = Get-Item -LiteralPath $path
    if ($file.PSIsContainer -or $file.Length -eq 0) {
        throw "Expected a non-empty release file: $path"
    }
    if ($file.Length -lt 1024 -and
        [IO.File]::ReadAllText($file.FullName).StartsWith('version https://git-lfs.github.com/spec/v1')) {
        throw "Git LFS pointer found at $path. Run git lfs pull and retry."
    }
    $file
}

$viewer = Join-Path $root 'runtime'
$unityFiles = @('Myproject.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe')
$unityDirectories = @('Myproject_Data', 'MonoBleedingEdge')
foreach ($name in $unityFiles) {
    Get-ReleaseFile (Join-Path $viewer $name) | Out-Null
}
foreach ($name in 'Myproject_Data/globalgamemanagers', 'Myproject_Data/level0',
        'Myproject_Data/Managed/Assembly-CSharp.dll', 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll') {
    Get-ReleaseFile (Join-Path $viewer $name) | Out-Null
}
foreach ($name in $unityDirectories) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $viewer $name) -Recurse -File)
    if ($files.Count -eq 0) { throw "Missing Unity player files in $name." }
    foreach ($file in $files) {
        # Unity can ship empty metadata files.
        if ($file.Length -gt 0) { Get-ReleaseFile $file.FullName | Out-Null }
    }
}
$calibrationFiles = @('intrinsics_left.npz', 'intrinsics_right.npz', 'left_pose.npz', 'right_pose.npz',
    'left_extrinsics.json', 'right_extrinsics.json')
foreach ($file in $calibrationFiles) {
    Get-ReleaseFile (Join-Path $root "calib/$file") | Out-Null
}
$demoVideos = foreach ($sample in 'sample1', 'sample2', 'sample3') {
    foreach ($camera in 'left', 'right') {
        $relative = "samples/$sample/$camera/recording.mp4"
        Get-ReleaseFile (Join-Path $root $relative) | Out-Null
        $relative
    }
}
foreach ($model in 'yolo11m.pt', 'yolo11n.pt') {
    Get-ReleaseFile (Join-Path $root "models/$model") | Out-Null
}

$buildId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$stage = Join-Path $dist ".stage-$buildId"
$app = Join-Path $stage 'app'
$portableName = 'AIfootball-Portable-v3.0'
$portable = Join-Path $stage $portableName
New-Item -ItemType Directory -Path $app -Force | Out-Null

Write-Host 'Publishing the self-contained Windows x64 app...'
dotnet publish (Join-Path $root 'src/AIfootball.App/AIfootball.App.csproj') -c Release -r win-x64 --self-contained true -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

foreach ($relative in @('AIfootball.exe', 'web/index.html', 'python_engine/aifootball/__main__.py')) {
    Get-ReleaseFile (Join-Path $app $relative) | Out-Null
}

Write-Host 'Staging Unity, calibration, models and demo videos...'
$newViewer = New-Item -ItemType Directory -Path (Join-Path $app 'viewer')
foreach ($name in ($unityFiles + $unityDirectories)) {
    Copy-Item -LiteralPath (Join-Path $viewer $name) -Destination $newViewer.FullName -Recurse
}
New-Item -ItemType Directory -Path (Join-Path $app 'data') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'data/goalkeepers') -Destination (Join-Path $app 'data') -Recurse
foreach ($relative in ($demoVideos + @('models/yolo11m.pt', 'models/yolo11n.pt') +
        ($calibrationFiles | ForEach-Object { "calib/$_" }))) {
    $destination = Join-Path $app $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destination
}

Copy-Item -LiteralPath $app -Destination $portable -Recurse
@"
AIfootball for Windows 10 (build 19041 or later) / Windows 11, x64.
Extract or copy this folder to a location you can write to, then run AIfootball.exe.
Install Microsoft Edge WebView2 Evergreen Runtime and the Visual C++ x64 Redistributable if needed.
Use the in-app environment setup on first launch. It requires internet access.
Sample videos and their calibration are included. Calibrate again for your own camera setup.
"@ | Set-Content -LiteralPath (Join-Path $portable 'README.txt') -Encoding utf8

$previous = Join-Path $dist "previous-$buildId"
foreach ($name in 'app', $portableName) {
    $destination = Join-Path $dist $name
    if (Test-Path -LiteralPath $destination) {
        New-Item -ItemType Directory -Path $previous -Force | Out-Null
        Move-Item -LiteralPath $destination -Destination (Join-Path $previous $name)
    }
    Move-Item -LiteralPath (Join-Path $stage $name) -Destination $destination
    Write-Host "Ready: $destination"
}
Remove-Item -LiteralPath $stage
