; Bert's IPTV Hub - Windows installer (Inno Setup 6)
; Built by .github/workflows/hub-installer.yml:
;   ISCC /DAppVersion=<hub/VERSION> /DBuildNumber=<run number> hub\installer\BertsIptvHub.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef BuildNumber
  #define BuildNumber "0"
#endif
#define AppName "Bert's IPTV Hub"
#define ServiceName "BertsIptvHub"
#define ExeName "BertsIptvHub.exe"
#define HubPort "8090"

[Setup]
AppId={{5B7E2C4A-9D3F-4E61-A8B2-7C1D0E9F4A36}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Bert's Apps
VersionInfoVersion={#AppVersion}.{#BuildNumber}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName=C:\BertsIptvHub
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
OutputDir=..\output
OutputBaseFilename=BertsIptvHub-Setup-{#AppVersion}
SetupIconFile=assets\hub.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName} {#AppVersion}
WizardStyle=modern
WizardImageFile=assets\wizard.bmp,assets\wizard-150.bmp,assets\wizard-200.bmp
WizardSmallImageFile=assets\wizard-small.bmp,assets\wizard-small-150.bmp,assets\wizard-small-200.bmp
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Desktop shortcut to the hub status page"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Never overwrite your settings on upgrade, and keep them if the hub is uninstalled.
Source: "..\publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "assets\hub.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "Edit-HubSettings.ps1"; DestDir: "{app}"; Flags: ignoreversion

[INI]
Filename: "{autoprograms}\{#AppName}\Hub status page.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://localhost:{#HubPort}/"
Filename: "{autoprograms}\{#AppName}\Hub status page.url"; Section: "InternetShortcut"; Key: "IconFile"; String: "{app}\hub.ico"
Filename: "{autoprograms}\{#AppName}\Hub status page.url"; Section: "InternetShortcut"; Key: "IconIndex"; String: "0"
Filename: "{autodesktop}\{#AppName}.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://localhost:{#HubPort}/"; Tasks: desktopicon
Filename: "{autodesktop}\{#AppName}.url"; Section: "InternetShortcut"; Key: "IconFile"; String: "{app}\hub.ico"; Tasks: desktopicon
Filename: "{autodesktop}\{#AppName}.url"; Section: "InternetShortcut"; Key: "IconIndex"; String: "0"; Tasks: desktopicon

[Icons]
Name: "{autoprograms}\{#AppName}\Edit hub settings"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Edit-HubSettings.ps1"""; IconFilename: "{app}\hub.ico"
Name: "{autoprograms}\{#AppName}\Hub logs"; Filename: "{win}\explorer.exe"; Parameters: """{app}\data\logs"""; IconFilename: "{app}\hub.ico"

[Run]
Filename: "http://localhost:{#HubPort}/"; Description: "Open the hub status page"; Flags: shellexec postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopHub"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteHub"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -Command ""Remove-NetFirewallRule -Name BertsIptvHub-TCP -ErrorAction SilentlyContinue"""; Flags: runhidden waituntilterminated; RunOnceId: "FirewallHub"

[UninstallDelete]
Type: files; Name: "{autoprograms}\{#AppName}\Hub status page.url"
Type: files; Name: "{autodesktop}\{#AppName}.url"
Type: dirifempty; Name: "{autoprograms}\{#AppName}"

[Code]
var
  ProviderPage: TInputQueryWizardPage;
  SettingsExisted: Boolean;

function SettingsFile: String;
begin
  Result := ExpandConstant('{app}\appsettings.json');
end;

procedure InitializeWizard;
begin
  ProviderPage := CreateInputQueryPage(wpSelectDir,
    'IPTV provider',
    'Enter your Xtream account details',
    'The hub is the only device that logs into your IPTV account. These details are stored only on this PC.' + #13#10 +
    'Your TVs then log into the hub using the TV password you choose below.');
  ProviderPage.Add('Server URL (for example http://line.example.com:8080):', False);
  ProviderPage.Add('Xtream username:', False);
  ProviderPage.Add('Xtream password:', True);
  ProviderPage.Add('TV password (what Bert''s IPTV on your TVs will use):', True);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  { Upgrades keep the existing settings, so don't ask again. }
  if (ProviderPage <> nil) and (PageID = ProviderPage.ID) then
    Result := FileExists(SettingsFile);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Url: String;
begin
  Result := True;
  if (ProviderPage = nil) or (CurPageID <> ProviderPage.ID) then
    Exit;
  Url := Lowercase(Trim(ProviderPage.Values[0]));
  if (Pos('http://', Url) <> 1) and (Pos('https://', Url) <> 1) then
  begin
    MsgBox('Enter the provider server URL, starting with http:// or https://', mbError, MB_OK);
    Result := False;
  end
  else if Trim(ProviderPage.Values[1]) = '' then
  begin
    MsgBox('Enter your Xtream username.', mbError, MB_OK);
    Result := False;
  end
  else if ProviderPage.Values[2] = '' then
  begin
    MsgBox('Enter your Xtream password.', mbError, MB_OK);
    Result := False;
  end
  else if Length(ProviderPage.Values[3]) < 4 then
  begin
    MsgBox('Choose a TV password of at least 4 characters.', mbError, MB_OK);
    Result := False;
  end;
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure WriteSettings;
var
  Lines: TArrayOfString;
  I: Integer;
  L, Url: String;
begin
  if not LoadStringsFromFile(SettingsFile, Lines) then
    Exit;
  Url := Trim(ProviderPage.Values[0]);
  while (Length(Url) > 0) and (Url[Length(Url)] = '/') do
    Delete(Url, Length(Url), 1);
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    L := Lines[I];
    StringChangeEx(L, '"http://your-provider-host:8080"', '"' + JsonEscape(Url) + '"', True);
    StringChangeEx(L, '"YOUR_XTREAM_USERNAME"', '"' + JsonEscape(Trim(ProviderPage.Values[1])) + '"', True);
    StringChangeEx(L, '"YOUR_XTREAM_PASSWORD"', '"' + JsonEscape(ProviderPage.Values[2]) + '"', True);
    if Pos('"LocalPassword"', L) > 0 then
      StringChangeEx(L, '"changeme"', '"' + JsonEscape(ProviderPage.Values[3]) + '"', True);
    Lines[I] := L;
  end;
  SaveStringsToUTF8File(SettingsFile, Lines, False);
end;

procedure RunHidden(const FileName, Params: String);
var
  Code: Integer;
begin
  Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure RegisterService;
var
  Code: Integer;
  Sc, Exe: String;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  Exe := ExpandConstant('{app}\{#ExeName}');
  Exec(Sc, 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Code = 1060 then  { 1060 = service does not exist }
    RunHidden(Sc, 'create {#ServiceName} binPath= "\"' + Exe + '\"" start= delayed-auto DisplayName= "Bert''s IPTV Hub"')
  else
    RunHidden(Sc, 'config {#ServiceName} binPath= "\"' + Exe + '\"" start= delayed-auto');
  RunHidden(Sc, 'description {#ServiceName} "Shares one IPTV account with every TV in the house. Status page: http://localhost:{#HubPort}"');
  RunHidden(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000');
end;

procedure OpenFirewall;
begin
  RunHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Remove-NetFirewallRule -Name BertsIptvHub-TCP -ErrorAction SilentlyContinue; ' +
    'New-NetFirewallRule -Name BertsIptvHub-TCP -DisplayName ''Berts IPTV Hub (TVs)'' -Direction Inbound -Protocol TCP ' +
    '-LocalPort {#HubPort} -RemoteAddress LocalSubnet -Action Allow -Profile Any"');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  SettingsExisted := FileExists(SettingsFile);
  { Stop a running hub so its exe can be replaced. }
  RunHidden(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#ExeName}');
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not SettingsExisted then
      WriteSettings;
    RegisterService;
    OpenFirewall;
    RunHidden(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}');
  end;
end;
