#define MyAppName "iMirror"
#ifndef MyAppVersion
#define MyAppVersion "0.0.0"
#endif
#ifndef MySourceDir
#define MySourceDir "..\artifacts\package\iMirror-" + MyAppVersion + "-win-x64"
#endif
#ifndef MyOutputDir
#define MyOutputDir "..\artifacts"
#endif

[Setup]
AppId={{57E0C02F-1303-42E1-A29F-DC6B57BE7562}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=iMirror
AppPublisherURL=https://github.com/Balragon/iMirror
AppSupportURL=https://github.com/Balragon/iMirror/issues
AppUpdatesURL=https://github.com/Balragon/iMirror/releases
DefaultDirName={localappdata}\Programs\iMirror
DefaultGroupName=iMirror
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#MyOutputDir}
OutputBaseFilename=iMirror-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\iMirror.exe
CloseApplications=yes
RestartApplications=yes
AppMutex=Local\iMirror.App
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\iMirror"; Filename: "{app}\iMirror.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\iMirror"; Filename: "{app}\iMirror.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\iMirror.exe"; Description: "Launch iMirror"; Flags: nowait postinstall skipifsilent
Filename: "{app}\iMirror.exe"; Flags: nowait; Check: ShouldLaunchAfterSilentUpdate

[Code]
function ShouldLaunchAfterSilentUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:IMIRROR_LAUNCH|0}') = '1';
end;
