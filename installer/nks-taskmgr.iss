[Setup]
AppId={{3DC15B35-D778-4DD6-AF81-55EC144B6575}
AppName=NKS Task Manager
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}+{#SourceRevision}
VersionInfoTextVersion={#AppVersion}+{#SourceRevision}
VersionInfoProductName=NKS Task Manager
VersionInfoDescription=NKS Task Manager Setup
VersionInfoOriginalFileName=NksTaskMgrSetup.exe
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
CloseApplications=no
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

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    try
      if Exec(ExpandConstant('{app}\nks-update-worker.exe'),
          '--cache-installer "' + ExpandConstant('{srcexe}') + '"',
          ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        if ResultCode = 0 then
          Log('Verified installer cache recorded.')
        else
          Log('Installer cache is unavailable for this build.');
      end
      else
        Log('Installer cache helper could not start.');
    except
      Log('Installer cache helper was unavailable.');
    end;
  end;
end;
