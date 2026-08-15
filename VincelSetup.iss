#ifndef AppVersion
#define AppVersion "1.0"
#endif

[Setup]
AppId={{9E5D2F4A-7B31-4C8E-9A2D-6F1B3C4D5E6F}
AppName=深澈 Vincel
AppVersion={#AppVersion}
AppPublisher=Vincel
AppPublisherURL=https://vincel.netlify.app
DefaultDirName={autopf}\Vincel
DefaultGroupName=深澈 Vincel
DisableProgramGroupPage=yes
OutputDir={#SourcePath}发布包
OutputBaseFilename=Vincel_v{#AppVersion}_Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\Vincel.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
SetupIconFile={#SourcePath}vincel.ico

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Files]
Source: "{#SourcePath}发布包\_pkg\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "MicrosoftEdgeWebview2Setup.exe,使用说明.txt"
Source: "{#SourcePath}WebView2Runtime\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\深澈 Vincel"; Filename: "{app}\Vincel.exe"; IconFilename: "{app}\Vincel.exe"
Name: "{autodesktop}\深澈 Vincel"; Filename: "{app}\Vincel.exe"; IconFilename: "{app}\Vincel.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装 WebView2 运行时（首次运行需要，请稍候）..."; Flags: waituntilterminated; Check: WebView2NotInstalled
Filename: "{app}\Vincel.exe"; Description: "立即运行 深澈 Vincel"; Flags: nowait postinstall

[Code]
function WebView2NotInstalled: Boolean;
var
  ver: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', ver) then
    Exit;
  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', ver) then
    Exit;
  Result := True;
end;
