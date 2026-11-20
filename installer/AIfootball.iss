; AIfootball complete Windows installer.
; Run scripts\build_release.bat before compiling this script.

#define AppName "AIfootball"
#define AppVersion "3.0.0"
#define AppPublisher "AIfootball Team"
#define AppExeName "AIfootball.exe"
#define SourceDir "..\dist\app"

[Setup]
AppId={{D3F3F8C2-1B4A-4B18-8EC4-610FB7C87086}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=AIfootball-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
PrivilegesRequired=lowest
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
SetupLogging=yes
; 应用图标与安装包图标
SetupIconFile=..\assets\icons\AIfootball.ico

[Languages]
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#else
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

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
; 旧版在应用目录中生成的环境
Name: "{app}\python_env"; Type: filesandordirs
; 分析结果目录
Name: "{app}\output"; Type: filesandordirs
; 运行时录制的样例视频
Name: "{app}\samples\sample_live"; Type: filesandordirs
; WPF → Unity 命令通道
Name: "{app}\runtime\data\wpf-unity-command.txt"; Type: files
; 运行时生成的轨迹 CSV
Name: "{app}\data\penalty_*_trajectory.csv"; Type: files
; Python 字节码缓存（其余由 [Code] 递归清理）
Name: "{app}\python_engine\aifootball\__pycache__"; Type: filesandordirs

[Code]
const
  WebViewKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  VcRuntimeKey = 'Software\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';

function HasWebView2(RootKey: Integer): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, WebViewKey, 'pv', Version) and
    (Version <> '') and (Version <> '0.0.0.0');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  Installed: Cardinal;
begin
  Result := '';
  try
    if not (HasWebView2(HKLM32) or HasWebView2(HKCU)) then begin
      DownloadTemporaryFile('https://go.microsoft.com/fwlink/p/?LinkId=2124703',
        'MicrosoftEdgeWebview2Setup.exe', '', nil);
      if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install',
        '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
        RaiseException('无法启动 Microsoft Edge WebView2 Runtime 安装程序。');
      if not (HasWebView2(HKLM32) or HasWebView2(HKCU)) then
        RaiseException(Format('WebView2 安装失败（%d）。请安装 Evergreen Runtime 后重试。', [ExitCode]));
    end;

    if not (RegQueryDWordValue(HKLM64, VcRuntimeKey, 'Installed', Installed) and (Installed = 1)) then begin
      DownloadTemporaryFile('https://aka.ms/vc14/vc_redist.x64.exe', 'vc_redist.x64.exe', '', nil);
      if not ShellExec('runas', ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart',
        '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
        RaiseException('无法启动 Visual C++ x64 运行库安装程序。请安装运行库后重试。');
      if ExitCode = 3010 then begin
        NeedsRestart := True;
        Result := '请重启 Windows 以完成 Visual C++ 运行库安装，然后继续安装 AIfootball。';
      end else if not (RegQueryDWordValue(HKLM64, VcRuntimeKey, 'Installed', Installed) and (Installed = 1)) then
        RaiseException(Format('Visual C++ x64 运行库安装失败（%d）。', [ExitCode]));
    end;
  except
    Result := GetExceptionMessage;
  end;
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
