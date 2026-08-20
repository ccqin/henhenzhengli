; DesktopManager Inno Setup 安装脚本
; 构建：1) dotnet publish -c Release -o publish（App 项目）
;       2) ISCC.exe docs\tools\installer.iss（需 Inno Setup 6+，中文语言包 InnoSetup\Languages\ChineseSimplified.isl）
; 产物：artifacts\DesktopManager_Setup.exe

#define AppName "DesktopManager 桌面整理"
#define AppVersion "1.0.0"
#define AppPublisher "DesktopManager Dev"
#define AppExeName "DesktopManager.App.exe"

[Setup]
AppId={{8E4A7B2C-1D3E-4F5A-9B6C-7D8E9F0A1B2C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\DesktopManager
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\..\artifacts
OutputBaseFilename=DesktopManager_Setup
SetupIconFile=..\..\src\DesktopManager.App\Assets\desktop-tool.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "autostart"; Description: "开机自动启动 {#AppName}"; GroupDescription: "附加任务:"
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Files]
; publish 目录三 exe + 依赖 dll（构建前先 publish）
Source: "..\..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启（HKCU，免管理员；应用内设置开关也会写同一键）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DesktopManager"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先退出应用（托盘常驻进程占用文件）
Filename: "{cmd}"; Parameters: "/c taskkill /f /im DesktopManager.App.exe /im DesktopManager.Player.Wallpaper.exe /im DesktopManager.Player.Icons.exe"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
; 不删 %AppData% 的用户数据（config/logs），仅清安装目录
Type: filesandordirs; Name: "{app}"
