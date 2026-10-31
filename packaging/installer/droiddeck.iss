; DroidDeck per-user Windows installer.
;
; Compiled by the release workflow against the already-validated portable
; publish output:
;
;   ISCC /DAppVersion=<version> /DAppSource=<publish dir> /DAppOutput=<dir> droiddeck.iss
;
; The publish directory is expected to contain signed binaries; the workflow
; signs the produced setup executable after compilation.

#define AppName "DroidDeck"
#define AppPublisher "DroidDeck contributors"
#define AppUrl "https://github.com/raulshma/droiddeck"

#ifndef AppVersion
#error AppVersion must be defined (ISCC /DAppVersion=x.y.z)
#endif

#ifndef AppSource
#error AppSource must be defined (ISCC /DAppSource=<publish dir>)
#endif

#ifndef AppOutput
#define AppOutput "artifacts"
#endif

[Setup]
; Stable identity for upgrade detection; never change between releases.
; Inno escapes braces by doubling: {{ here yields a literal { in the AppId.
AppId={{CDE7909C-98BA-402F-9C02-6FF97C817896}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Per-user install: no elevation, install and shortcuts only for the
; current user. {autopf} resolves to %LocalAppData%\Programs here.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=yes

; The portable build is self-contained (Windows App SDK included), so the
; installer needs no runtime download. Windows 10 1809 is the app's minimum.
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

DisableProgramGroupPage=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
LZMANumBlockThreads=4

OutputDir={#AppOutput}
OutputBaseFilename=DroidDeck-{#AppVersion}-setup-win-x64
SetupIconFile=..\..\src\DroidDeck.App\Assets\droiddeck.ico
UninstallDisplayIcon={app}\DroidDeck.exe

; Preserve user data in %LocalAppData%\DroidDeck across uninstall.
Uninstallable=yes
CreateAppDir=yes

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; \
    Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; \
    Flags: unchecked

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\DroidDeck.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\DroidDeck.exe"; \
    Tasks: desktopicon

[Run]
Filename: "{app}\DroidDeck.exe"; \
    Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

[Messages]
; The standard "close applications" page only knows about install files;
; DroidDeck keeps its data outside {app}, so a plain retry loop is enough.
ExitSetupTitle=Setup was interrupted
ExitSetupMessage=Setup was not completed. DroidDeck was not fully installed.%n%nYou can run setup again at a later time.
