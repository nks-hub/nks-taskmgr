[Setup]
AppId={{3DC15B35-D778-4DD6-AF81-55EC144B6575}
AppName=NKS Task Manager
AppVersion={#AppVersion}
AppPublisher=NKS Hub
AppComments=Build {#SourceRevision}
DefaultDirName={localappdata}\Programs\NKS Task Manager
DefaultGroupName=NKS Task Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\app.ico
LicenseFile={#AppDirectory}\LICENSE
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputBaseName}
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=NksTaskMgr.WinUI.exe,nks-icon-worker.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NKS Task Manager"; Filename: "{app}\NksTaskMgr.WinUI.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\NKS Task Manager"; Filename: "{app}\NksTaskMgr.WinUI.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\NksTaskMgr.WinUI.exe"; Description: "{cm:LaunchProgram,NKS Task Manager}"; Flags: nowait postinstall skipifsilent
