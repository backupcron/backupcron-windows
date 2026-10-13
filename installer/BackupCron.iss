#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
[Setup]
AppId={{706BF377-952C-4B91-A757-4B793C9B79DC}
AppName=BackupCron
AppVersion={#AppVersion}
AppPublisher=BackupCron contributors
AppPublisherURL=https://github.com/backupcron/backupcron-windows
AppSupportURL=https://github.com/backupcron/backupcron-windows/issues
AppUpdatesURL=https://github.com/backupcron/backupcron-windows/releases
DefaultDirName={localappdata}\Programs\BackupCron
DefaultGroupName=BackupCron
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=BackupCron-{#AppVersion}-win-x64-setup
SetupIconFile=..\resources\AppIcon.ico
UninstallDisplayIcon={app}\BackupCron.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\BackupCron"; Filename: "{app}\BackupCron.exe"
Name: "{autodesktop}\BackupCron"; Filename: "{app}\BackupCron.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\BackupCron.exe"; Description: "{cm:LaunchProgram,BackupCron}"; Flags: nowait postinstall skipifsilent
[UninstallDelete]
Type: files; Name: "{app}\*.pdb"
; Job settings, encrypted credentials and backups are deliberately preserved.
