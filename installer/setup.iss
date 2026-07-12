#define MyAppName "YMB Claude使用量モニター"
#define MyAppExeName "YmbClaudeUsage.exe"
#define MyAppPublisher "yumebi"
#define MyAppURL "https://github.com/yumebi/ymb_claude_usage"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyPublishDir
  #define MyPublishDir "..\src\ClaudeUsage.App\bin\Release\net10.0-windows\win-x64\publish"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\dist"
#endif

[Setup]
AppId={{FCDBD3C3-31E1-486E-8CF1-3F7035DF767D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#MyOutputDir}
OutputBaseFilename=YmbClaudeUsage-Setup-{#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Windows起動時に自動起動する"; GroupDescription: "追加オプション:"

[InstallDelete]
; アップグレードインストール時に旧バージョンの残留ファイルを一掃してから新ファイルを配置する。
; (self-contained時代のcoreclr.dll等が残ると、hostfxrがapp-localランタイムと誤認し
;  グローバルの.NETランタイムを探さなくなる不具合があったため)
; ユーザー設定は{app}の外(%APPDATA%\YmbClaudeUsage)に保存されているため影響なし。
Type: filesandordirs; Name: "{app}"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Windows起動時の自動起動(per-user)。アンインストール時に削除される
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "YmbClaudeUsage"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
