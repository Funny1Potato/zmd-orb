; 终末地加速球 · Inno Setup 脚本（M4）
; CI 里用法：iscc /DMyAppVersion=1.2.3 /DMySourceDir=<发布目录> installer\zmd-orb.iss
; 自包含发布（zmd-orb.exe 里已带 .NET 运行时），backend.exe 是 PyInstaller 打的采集端。

#ifndef MyAppVersion
  #define MyAppVersion "0.2.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\orb\bin\Release\net6.0-windows\publish"
#endif

#define MyAppName "终末地加速球"
#define MyAppNameEn "zmd-orb"
#define MyAppPublisher "Funny1Potato"
#define MyAppUrl "https://github.com/Funny1Potato/zmd-orb"
#define MyExeName "zmd-orb.exe"

[Setup]
AppId={{9E2E7A31-6C1B-4E2C-9C46-2F2C4A7A51D0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
DefaultDirName={autopf}\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyExeName}
OutputDir=..\dist
OutputBaseFilename=zmd-orb-setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 自包含发布是 64 位，装到 64 位的 Program Files
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 需要的话可以让安装包自己提权；默认按用户目录装就不需要
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
; 中文语言包是 Inno 的"非官方翻译"（官方安装包不带），CI 会从 issrc 仓库抓一份放到这个目录；
; 抓不到就只出英文向导（下面的 #if 保证不会因为缺文件而编译失败）。
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(SourcePath) + "ChineseSimplified.isl")
Name: "chinese"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"
#endif

[Tasks]
Name: "autostart"; Description: "开机自启（在托盘里静默启动）"; GroupDescription: "其它选项："
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MySourceDir}\{#MyExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\backend.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#MySourceDir}\*.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; 许可与说明也随安装包一起发（MIT / ISC 都要求把版权声明与许可随副本一起给）。
; skipifsourcedoesntexist：本地直接编译安装包时源目录里没有这几个文件也不报错。
Source: "{#MySourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#MySourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#MySourceDir}\THIRD-PARTY-LICENSES.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"; Tasks: desktopicon

[Registry]
; 开机自启：写 HKCU 的 Run 键（和面板里那个"开机自启"开关是同一个键，两边状态一致）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "zmd-orb"; ValueData: """{app}\{#MyExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先把进程结束掉，免得文件占用卸载不干净
Filename: "{cmd}"; Parameters: "/c taskkill /F /IM {#MyExeName} /T"; Flags: runhidden; RunOnceId: "killapp"