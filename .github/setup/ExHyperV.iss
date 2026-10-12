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
; Only request manual exit; silent installs must not close the application either.
CloseApplications=no
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

[CustomMessages]
english.ApplicationInUse=Please close ExHyperV and run Setup again.
chinesesimplified.ApplicationInUse=请关闭 ExHyperV 后重新运行安装程序。
english.ApplicationCheckFailed=Setup could not determine whether ExHyperV.exe is in use. No application files have been replaced. Please retry or cancel Setup.%n%nFile: %1%nDetails: %2
chinesesimplified.ApplicationCheckFailed=无法确认 ExHyperV.exe 是否被占用，尚未替换主程序文件。请重试或取消安装。%n%n文件：%1%n详细信息：%2

[Code]
function RmStartSession(var SessionHandle: DWORD; SessionFlags: DWORD;
  SessionKey: String): DWORD;
  external 'RmStartSession@{sys}\rstrtmgr.dll stdcall delayload';
function RmRegisterResources(SessionHandle: DWORD; FileCount: UINT;
  var FileName: String; ApplicationCount: UINT; Applications: THandle;
  ServiceCount: UINT; ServiceNames: THandle): DWORD;
  external 'RmRegisterResources@{sys}\rstrtmgr.dll stdcall delayload';
function RmGetList(SessionHandle: DWORD; var Needed, Count: UINT;
  ProcessInfo: THandle; var RebootReasons: DWORD): DWORD;
  external 'RmGetList@{sys}\rstrtmgr.dll stdcall delayload';
function RmEndSession(SessionHandle: DWORD): DWORD;
  external 'RmEndSession@{sys}\rstrtmgr.dll stdcall delayload';

function ApplicationExitError: String;
var
  TargetFile, SessionKey, Details: String;
  SessionHandle, Status, EndStatus, RebootReasons: DWORD;
  Needed, Count: UINT;
begin
  Result := '';
  TargetFile := ExpandConstant('{app}\ExHyperV.exe');
  try
    { Read-only Restart Manager query: identify the actual target file, including
      elevated/older builds, without querying protected process paths or asking
      any process to shut down. Never treat a failed query as "not running". }
    SetLength(SessionKey, 33); { CCH_RM_SESSION_KEY + terminator }
    Status := RmStartSession(SessionHandle, 0, SessionKey);
    if Status <> 0 then
      RaiseException(Format('RmStartSession: %d', [Status]));
    try
      Status := RmRegisterResources(SessionHandle, 1, TargetFile, 0, 0, 0, 0);
      if Status <> 0 then
        RaiseException(Format('RmRegisterResources: %d', [Status]));

      Needed := 0;
      Count := 0;
      RebootReasons := 0;
      { No process buffer is required: ERROR_MORE_DATA means a file user exists. }
      Status := RmGetList(SessionHandle, Needed, Count, 0, RebootReasons);
      Log(Format('Application occupancy check: %s; status=%d; count=%d; reasons=%d', [TargetFile, Status, Needed, RebootReasons]));
      if (Status = 234) or ((Status = 0) and (Needed > 0)) then
        Result := CustomMessage('ApplicationInUse')
      else if Status <> 0 then
        RaiseException(Format('RmGetList: %d', [Status]))
      else if RebootReasons <> 0 then
        RaiseException(Format('Restart Manager reboot reasons: %d', [RebootReasons]));
    finally
      EndStatus := RmEndSession(SessionHandle);
      if EndStatus <> 0 then
        Log(Format('RmEndSession: %d', [EndStatus]));
    end;
  except
    Details := GetExceptionMessage;
    Result := FmtMessage(CustomMessage('ApplicationCheckFailed'), [TargetFile, Details]);
  end;
  if Result <> '' then
    Log(Result);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Inno supplies its retry UI; silent mode exits with preparation error code 7. }
  Result := ApplicationExitError;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Error: String;
begin
  if CurStep <> ssInstall then
    Exit;
  { Recheck immediately before installation starts. Abort is fatal in ssInstall,
    but not in a file's BeforeInstall callback. Never continue on a failed check. }
  Error := ApplicationExitError;
  if Error <> '' then
  begin
    if not WizardSilent then
      MsgBox(Error, mbError, MB_OK);
    Abort;
  end;
end;
