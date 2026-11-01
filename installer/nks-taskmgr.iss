[Setup]
AppId={{3DC15B35-D778-4DD6-AF81-55EC144B6575}
AppName=NKS Task Manager
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}+{#SourceRevision}
VersionInfoTextVersion={#AppVersion}
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
Uninstallable=not IsPortableMode
CreateUninstallRegKey=not IsPortableMode
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

[CustomMessages]
english.InvalidInstallMode=PORTABLE and NKSTRANSACTION must have the value 0 or 1.
czech.InvalidInstallMode=Parametry PORTABLE a NKSTRANSACTION musí mít hodnotu 0 nebo 1.
english.ExplicitDirectoryRequired=This installation mode requires an explicit /DIR with an absolute local directory path.
czech.ExplicitDirectoryRequired=Tento režim vyžaduje výslovné /DIR s úplnou cestou k místní složce.
english.FixedDirectoryChanged=The update destination cannot be changed during this transaction.
czech.FixedDirectoryChanged=Cílovou složku nelze během této aktualizace změnit.
english.PortableModeRequired=This directory contains a portable copy. Run Setup with /PORTABLE=1 and an explicit /DIR.
czech.PortableModeRequired=V této složce je přenosná kopie. Spusťte instalátor s /PORTABLE=1 a výslovným /DIR.
english.InvalidPortableMarker=The portable marker in this directory is invalid or cannot be read. No files have been installed.
czech.InvalidPortableMarker=Označení přenosné kopie v této složce je neplatné nebo je nelze přečíst. Nebyly nainstalovány žádné soubory.
english.PortableMarkerWriteFailed=The application files were installed, but the portable marker could not be saved. Do not use this copy for automatic updates.
czech.PortableMarkerWriteFailed=Soubory aplikace byly nainstalovány, ale označení přenosné kopie se nepodařilo uložit. Tuto kopii nepoužívejte k automatickým aktualizacím.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; Check: not IsPortableMode

[Files]
Source: "{#AppDirectory}\*"; DestDir: "{app}"; Excludes: "\portable.nks"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NKS Task Manager"; Filename: "{app}\NksTaskMgr.WinUI.exe"; WorkingDir: "{app}"; Check: not IsPortableMode
Name: "{autodesktop}\NKS Task Manager"; Filename: "{app}\NksTaskMgr.WinUI.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not IsPortableMode

[Run]
Filename: "{app}\NksTaskMgr.WinUI.exe"; Description: "{cm:LaunchProgram,NKS Task Manager}"; Flags: nowait postinstall skipifsilent; Check: not IsTransactionMode

[Code]
const
  PortableMarkerData = 'format=1' + #10 +
    'appId=3DC15B35-D778-4DD6-AF81-55EC144B6575' + #10;

var
  ExplicitDirectory: String;

function IsPortableMode: Boolean;
begin
  Result := ExpandConstant('{param:PORTABLE|0}') = '1';
end;

function IsTransactionMode: Boolean;
begin
  Result := ExpandConstant('{param:NKSTRANSACTION|0}') = '1';
end;

function IsLocalDirectory(const Directory: String): Boolean;
var
  Drive: String;
begin
  Result := False;
  if Length(Directory) <= 3 then
    Exit;
  Drive := Uppercase(Copy(Directory, 1, 1));
  Result := (Drive >= 'A') and (Drive <= 'Z') and
    (Copy(Directory, 2, 2) = ':\') and (Pos('/', Directory) = 0) and
    (Pos('"', Directory) = 0);
end;

function NormalizeDirectory(const Directory: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Directory));
end;

function InitializeSetup: Boolean;
var
  PortableFlag, TransactionFlag: String;
begin
  Result := False;
  PortableFlag := ExpandConstant('{param:PORTABLE|0}');
  TransactionFlag := ExpandConstant('{param:NKSTRANSACTION|0}');
  if ((PortableFlag <> '0') and (PortableFlag <> '1')) or
     ((TransactionFlag <> '0') and (TransactionFlag <> '1')) then
  begin
    MsgBox(CustomMessage('InvalidInstallMode'), mbError, MB_OK);
    Exit;
  end;
  if IsPortableMode or IsTransactionMode then
  begin
    ExplicitDirectory := ExpandConstant('{param:DIR|}');
    if not IsLocalDirectory(ExplicitDirectory) then
    begin
      MsgBox(CustomMessage('ExplicitDirectoryRequired'), mbError, MB_OK);
      Exit;
    end;
    ExplicitDirectory := NormalizeDirectory(ExplicitDirectory);
    if not IsLocalDirectory(ExplicitDirectory) then
    begin
      MsgBox(CustomMessage('ExplicitDirectoryRequired'), mbError, MB_OK);
      Exit;
    end;
  end;
  Result := True;
end;

procedure InitializeWizard;
begin
  if IsPortableMode or IsTransactionMode then
    WizardForm.DirEdit.Text := ExplicitDirectory;
  if IsTransactionMode then
  begin
    WizardForm.DirEdit.Enabled := False;
    WizardForm.DirBrowseButton.Enabled := False;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = wpSelectTasks) and IsPortableMode) or
    ((PageID = wpSelectDir) and IsTransactionMode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Directory, MarkerPath: String;
  MarkerContent: AnsiString;
begin
  Result := '';
  Directory := NormalizeDirectory(WizardDirValue);
  if IsTransactionMode and
     (CompareText(Directory, ExplicitDirectory) <> 0) then
  begin
    Result := CustomMessage('FixedDirectoryChanged');
    Exit;
  end;
  if IsPortableMode and not IsLocalDirectory(Directory) then
  begin
    Result := CustomMessage('ExplicitDirectoryRequired');
    Exit;
  end;
  MarkerPath := AddBackslash(Directory) + 'portable.nks';
  if FileExists(MarkerPath) or DirExists(MarkerPath) then
  begin
    if not IsPortableMode then
      Result := CustomMessage('PortableModeRequired')
    else if not LoadStringFromFile(MarkerPath, MarkerContent) then
      Result := CustomMessage('InvalidPortableMarker')
    else if MarkerContent <> PortableMarkerData then
      Result := CustomMessage('InvalidPortableMarker');
  end;
end;

procedure SavePortableMarker;
var
  MarkerPath: String;
  MarkerContent: AnsiString;
begin
  MarkerPath := ExpandConstant('{app}\portable.nks');
  if not FileExists(MarkerPath) then
    if not SaveStringToFile(MarkerPath, PortableMarkerData, False) then
      RaiseException(CustomMessage('PortableMarkerWriteFailed'));
  if not LoadStringFromFile(MarkerPath, MarkerContent) then
    RaiseException(CustomMessage('PortableMarkerWriteFailed'));
  if MarkerContent <> PortableMarkerData then
    RaiseException(CustomMessage('PortableMarkerWriteFailed'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if IsPortableMode then
      SavePortableMarker;
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
