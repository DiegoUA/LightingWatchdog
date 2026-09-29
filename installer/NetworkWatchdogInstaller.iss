[Setup]
AppName=Network Watchdog Service
AppVersion=3.6.0
AppPublisher=Network Watchdog
DefaultDirName={autopf}\NetworkWatchdog
ArchitecturesInstallIn64BitMode=x64
OutputBaseFilename=NetworkWatchdog_Installer
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
SetupLogging=yes
CloseApplications=force

[Files]
; The Background Service (Ensure you ran `dotnet publish -r win-x64 -c Release /p:PublishSingleFile=true` first)
Source: "..\src\NetworkWatchdogService\bin\Release\net8.0-windows\win-x64\publish\NetworkWatchdogService.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\NetworkWatchdogService\bin\Release\net8.0-windows\win-x64\publish\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion

; The Tray Application
Source: "..\src\NetworkWatchdog.TrayApp\bin\Release\net8.0-windows\NetworkWatchdog.TrayApp.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autostartup}\Network Watchdog Tray"; Filename: "{app}\NetworkWatchdog.TrayApp.exe"
Name: "{group}\Network Watchdog"; Filename: "{app}\NetworkWatchdog.TrayApp.exe"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Pre-install: Kill active instances and stop the service so files are not locked
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM NetworkWatchdog.TrayApp.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop NetworkWatchdogService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000); // Allow OS handles to release
  end;
end;

[Run]
; Post-install: Register and start the background service, then launch the Tray App
Filename: "{sys}\sc.exe"; Parameters: "create NetworkWatchdogService binPath= ""{app}\NetworkWatchdogService.exe"" start= auto"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start NetworkWatchdogService"; Flags: runhidden
Filename: "{app}\NetworkWatchdog.TrayApp.exe"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Clean up service registration on uninstall
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM NetworkWatchdog.TrayApp.exe"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "stop NetworkWatchdogService"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete NetworkWatchdogService"; Flags: runhidden