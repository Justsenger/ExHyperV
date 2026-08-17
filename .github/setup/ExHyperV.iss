#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef NumericVersion
  #error NumericVersion is required
#endif
#ifndef Architecture
  #error Architecture is required
#endif
#ifndef SourceExe
  #error SourceExe is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif

[Setup]
AppId={{71C8A4EB-33AE-40DB-B199-2A21E214361E}
AppName=ExHyperV
AppVersion={#AppVersion}
AppVerName=ExHyperV {#AppVersion}
UninstallDisplayName=ExHyperV
AppPublisher=Justsenger
DefaultDirName={localappdata}\Programs\ExHyperV
DefaultGroupName=ExHyperV
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\..\src\Assets\Icon.ico
UninstallDisplayIcon={app}\ExHyperV.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
UsePreviousLanguage=yes
UsePreviousTasks=yes
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=ExHyperV
VersionInfoDescription=ExHyperV Setup
VersionInfoCompany=Justsenger
#ifdef EnableSigning
SignTool=ExHyperVSign
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "ExHyperV.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\ExHyperV"; Filename: "{app}\ExHyperV.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ExHyperV"; Filename: "{app}\ExHyperV.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ExHyperV.exe"; Description: "{cm:LaunchProgram,ExHyperV}"; WorkingDir: "{app}"; Verb: "runas"; Flags: nowait postinstall skipifsilent shellexec
