#ifndef PayloadDirectory
  #error PayloadDirectory is required. Use scripts/build-windows-installer.ps1.
#endif
#ifndef AppVersion
  #error AppVersion is required.
#endif
#ifndef NumericVersion
  #error NumericVersion is required.
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required.
#endif
#ifdef TestBuild
  #define ProductId "ShareLink.Windows.Desktop.InstallerTest"
  #define ProductName "ShareLink Installer Test"
  #define RuntimeMutex "Local\ShareLink.Desktop.InstallerTest.Runtime"
#else
  ; Permanent identity: keep this value for EVERY future public release.
  #define ProductId "ShareLink.Windows.Desktop"
  #define ProductName "ShareLink"
  #define RuntimeMutex "Local\ShareLink.Desktop.Runtime"
#endif
[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} v{#AppVersion}
AppPublisher=yanwu06
AppPublisherURL=https://gitee.com/yanwu06/win-sharelink-transfer-release
AppUpdatesURL=https://gitee.com/yanwu06/win-sharelink-transfer-release/releases
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
DirExistsWarning=no
AllowRootDirectory=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
AppMutex={#RuntimeMutex}
SetupMutex={#ProductId}.Setup
CloseApplications=no
RestartApplications=no
Uninstallable=yes
UninstallDisplayIcon={app}\ShareLink.exe
UninstallDisplayName={#ProductName}
UninstallLogMode=append
OutputDir={#OutputDirectory}
OutputBaseFilename=ShareLink-Setup-x64
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoTextVersion={#AppVersion}
SetupIconFile=..\windows\ShareLink.App\ShareLink.ico
WizardStyle=modern
InfoBeforeFile=InstallInfo.txt
Compression=lzma2
SolidCompression=yes
LZMAUseSeparateProcess=yes
SetupLogging=yes
[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
Name: "networkaccess"; Description: "配置手机连接所需的网络权限（需要管理员确认）"
[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\ShareLink.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 {#ProductName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\ShareLink.exe"; WorkingDir: "{app}"; Tasks: desktopicon
[Run]
Filename: "{app}\ShareLink.exe"; Description: "启动 {#ProductName}"; Flags: postinstall nowait skipifsilent unchecked
[Code]
var NetworkAccessFailed: Boolean;

function ChangeNetworkAccess(Remove: Boolean): Boolean;
var
  Parameters: String;
  ResultCode: Integer;
begin
  if Remove then Parameters := '--remove-installed-network-access'
  else Parameters := '--install-network-access';
  Result := ShellExec('runas', ExpandConstant('{app}\ShareLink.exe'), Parameters,
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log(Format('ShareLink network permissions: %s; launched=%d; result=%d', [Parameters, Ord(Result), ResultCode]));
  Result := Result and (ResultCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('networkaccess') then begin
    WizardForm.StatusLabel.Caption := '正在配置手机连接权限，请确认 Windows 管理员授权…';
    if ChangeNetworkAccess(False) then begin
      RegWriteDWordValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1',
        'ShareLinkNetworkAccess', 1);
    end else begin
      NetworkAccessFailed := True;
      SuppressibleMsgBox('ShareLink 已安装，但手机连接权限尚未配置完成。请重新运行安装包并允许管理员授权。如有电脑管理策略限制，请联系管理员。', mbError, MB_OK, IDOK);
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and NetworkAccessFailed then
    WizardForm.FinishedLabel.Caption := 'ShareLink 已安装，但手机连接所需的网络权限尚未配置完成。请重新运行安装包，并允许 Windows 管理员授权。';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Configured: Cardinal;
begin
  if (CurUninstallStep = usUninstall) and
    RegQueryDWordValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1',
      'ShareLinkNetworkAccess', Configured) and (Configured = 1) then begin
    if not ChangeNetworkAccess(True) then
      SuppressibleMsgBox('未能清理 ShareLink 的网络权限规则。程序仍将卸载；如需清理残留规则，请联系电脑管理员。', mbError, MB_OK, IDOK);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var DataDir: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then begin
    DataDir := AddBackslash(ExpandConstant('{localappdata}\ShareLink'));
    if Pos(Lowercase(DataDir), Lowercase(AddBackslash(ExpandFileName(WizardDirValue)))) = 1 then begin
      MsgBox('此目录用于保存 ShareLink 设置和历史。请选择其他文件夹安装程序。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;
