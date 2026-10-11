#define AppName "默译 · Tacily"
#define AppPublisher "Tacily contributors"
#define AppURL GetEnv("SCREEN_TRANSLATOR_REPOSITORY_URL")
#define AppExeName "ScreenTranslator.exe"
#define AppVersion GetEnv("SCREEN_TRANSLATOR_VERSION")
#define PublishDir GetEnv("SCREEN_TRANSLATOR_PUBLISH_DIR")
#define ArtifactDir GetEnv("SCREEN_TRANSLATOR_ARTIFACT_DIR")
#define RepoRoot SourcePath + ".."

#if AppVersion == ""
  #error SCREEN_TRANSLATOR_VERSION is required
#endif
#if PublishDir == ""
  #error SCREEN_TRANSLATOR_PUBLISH_DIR is required
#endif
#if ArtifactDir == ""
  #error SCREEN_TRANSLATOR_ARTIFACT_DIR is required
#endif

[Setup]
AppId={{A2708E29-0D5E-467A-87B5-A2188370BAE7}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
#if AppURL != ""
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
#endif
DefaultDirName={localappdata}\Programs\ScreenTranslator
DefaultGroupName=默译 · Tacily
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#RepoRoot}\src\ScreenTranslator.App\Assets\ScreenTranslator.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile={#RepoRoot}\LICENSE
OutputDir={#ArtifactDir}
OutputBaseFilename=ScreenTranslator-Setup-x64
CloseApplications=force
CloseApplicationsFilter=ScreenTranslator.exe
RestartApplications=no
SetupLogging=yes
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked
Name: "startup"; Description: "登录 Windows 后自动启动默译 · Tacily"; GroupDescription: "启动："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\默译 · Tacily"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载默译 · Tacily"; Filename: "{uninstallexe}"
Name: "{autodesktop}\默译 · Tacily"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\默译 · Tacily"; Filename: "{app}\{#AppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Parameters: "--register-browser-host"; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "启动默译 · Tacily"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--unregister-browser-host"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterBrowserHost"

[Code]
var
  DeleteUserDataCheckBox: TNewCheckBox;

procedure InitializeUninstallProgressForm();
begin
  UninstallProgressForm.ClientHeight :=
    UninstallProgressForm.ClientHeight + ScaleY(34);
  DeleteUserDataCheckBox := TNewCheckBox.Create(UninstallProgressForm);
  DeleteUserDataCheckBox.Parent := UninstallProgressForm;
  DeleteUserDataCheckBox.Left := ScaleX(24);
  DeleteUserDataCheckBox.Top :=
    UninstallProgressForm.ClientHeight - ScaleY(31);
  DeleteUserDataCheckBox.Width :=
    UninstallProgressForm.ClientWidth - ScaleX(48);
  DeleteUserDataCheckBox.Caption :=
    '同时删除 API Key、快捷键和个人设置';
  DeleteUserDataCheckBox.Checked := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and
     Assigned(DeleteUserDataCheckBox) and
     DeleteUserDataCheckBox.Checked then
  begin
    DelTree(
      ExpandConstant('{localappdata}\ScreenTranslator'),
      True,
      True,
      True);
  end;
end;
