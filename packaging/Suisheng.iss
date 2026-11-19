#define MyAppName "随声输入"
#ifndef MyAppVersion
  #define MyAppVersion "0.4.0"
#endif
#ifndef MyAppFileVersion
  #define MyAppFileVersion "0.4.0.0"
#endif
#define MyAppPublisher "随声输入"
#define MyAppExeName "Suisheng.exe"
#ifndef SourceDir
  #define SourceDir "..\dist\Suisheng-stage4-0.4.0-win-x64"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "Suisheng-Setup-" + MyAppVersion + "-win-x64"
#endif

[Setup]
AppId={{6E8E9EE4-35A1-4E2B-9257-ED898CE27E0B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Suisheng
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Local\Suisheng.Desktop.0.1
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppFileVersion}
VersionInfoDescription={#MyAppName} 安装程序
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Suisheng"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDirectory: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent then
    begin
      DataDirectory := ExpandConstant('{localappdata}\Suisheng');
      if DirExists(DataDirectory) then
      begin
        if MsgBox('是否同时删除随声输入的用户数据？' + #13#10#13#10 +
          '选择“是”：删除默认数据目录中的设置、模型、历史、导入图片、AI 模板和 DPAPI 密钥。' + #13#10 +
          '选择“否”：保留以上数据，供重新安装后使用。' + #13#10#13#10 +
          '不会删除你在其他目录自行放置的模型或文件。',
          mbConfirmation, MB_YESNO) = IDYES then
        begin
          if not DelTree(DataDirectory, True, True, True) then
            MsgBox('部分用户数据无法删除。程序已卸载，剩余数据仍保留在：' + DataDirectory,
              mbInformation, MB_OK);
        end;
      end;
    end;
  end;
end;
