; AIfootball complete Windows installer.
; Run scripts\build_release.bat before compiling this script.

#define AppName "AIfootball"
#define AppVersion "2.0.0"
#define AppPublisher "AIfootball Team"
#define AppExeName "AIfootball.exe"
#define SourceDir "..\dist\app"

[Setup]
AppId={{D3F3F8C2-1B4A-4B18-8EC4-610FB7C87086}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=AIfootball-Setup-{#AppVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
SetupLogging=yes
; 应用图标与安装包图标
SetupIconFile=..\assets\icons\AIfootball.ico

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Staged files include the WPF app, Python engine, data/model assets, and Unity viewer.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Registry]
; 应用设置键（卸载时 Inno Setup 自动删除该键）
Root: HKCU; Subkey: "Software\AIfootball"; Flags: uninsdeletekeyifempty

[UninstallDelete]
; 首次运行自动生成的 Python 环境（体积较大）
Name: "{app}\python_env"; Type: filesandordirs
; 分析结果目录
Name: "{app}\output"; Type: filesandordirs
; 运行时录制的样例视频
Name: "{app}\samples\sample_live"; Type: filesandordirs
Name: "{app}\samples\unity_*"; Type: filesandordirs
; 运行时生成的轨迹 CSV
Name: "{app}\data\penalty_*_trajectory.csv"; Type: files
; Python 字节码缓存（其余由 [Code] 递归清理）
Name: "{app}\project\__pycache__"; Type: filesandordirs

[Code]
function InitializeSetup(): Boolean;
begin
  if not IsWin64 then begin
    MsgBox('AIfootball 需要 64 位 Windows 10 或 Windows 11。', mbError, MB_OK);
    Result := False;
  end else
    Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  // 卸载前提示用户关闭正在运行的程序，避免文件占用导致清理不干净
  if MsgBox('卸载前请关闭正在运行的 AIfootball 程序。' #13#13 '确定已关闭？',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDNO then
    Result := False
  else
    Result := True;
end;

// 递归删除指定目录下所有 __pycache__ 文件夹
procedure DeletePycacheDirs(const BasePath: string);
var
  FindRec: TFindRec;
begin
  if FindFirst(BasePath + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
          begin
            if CompareText(FindRec.Name, '__pycache__') = 0 then
              DelTree(BasePath + '\' + FindRec.Name, True, True, True)
            else
              DeletePycacheDirs(BasePath + '\' + FindRec.Name);
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 彻底删除应用注册表键（含运行时新增的子键）
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\AIfootball');
    // 清理遗留的 Python 缓存目录
    DeletePycacheDirs(ExpandConstant('{app}'));
  end;
end;
