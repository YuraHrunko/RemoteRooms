#define AppName "RemoteRooms"
#define AppVersion "1.0.10"

[Setup]
AppId={{741C49C4-61F1-4F52-A81C-6EA14AE93B5D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=RemoteRooms
DefaultDirName={localappdata}\Programs\RemoteRooms
DefaultGroupName=RemoteRooms
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\outputs
OutputBaseFilename=Setup
SetupIconFile=app\app.ico
UninstallDisplayIcon={app}\RemoteRooms.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Tasks]
Name: "desktopicon"; Description: "Створити ярлик на робочому столі"; GroupDescription: "Додаткові ярлики:"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installer\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not WebView2Installed

[Icons]
Name: "{autoprograms}\RemoteRooms"; Filename: "{app}\RemoteRooms.exe"
Name: "{autodesktop}\RemoteRooms"; Filename: "{app}\RemoteRooms.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "Встановлення компонента WebView2 для SSH-термінала…"; Flags: runhidden waituntilterminated; Check: not WebView2Installed
Filename: "{app}\RemoteRooms.exe"; Description: "Запустити RemoteRooms"; Flags: nowait postinstall skipifsilent

[Code]
function WebView2Installed: Boolean;
var Version: String;
begin
  Result := RegQueryStringValue(HKLM32, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
    or RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
  Result := Result and (Version <> '') and (Version <> '0.0.0.0');
end;
