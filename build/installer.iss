; =============================================================================
; IPA Studio - Inno Setup installer script
;
; Prerequisites (run on Windows):
;   1. dotnet publish src/IPAStudio.App -c Release -r win-x64 --self-contained `
;        -p:PublishSingleFile=false -o build/publish
;   2. powershell -File build/Fetch-Tools.ps1 -OutDir build/publish/tools
;   3. iscc build/installer.iss
;
; Output: build/output/IPAStudio-Setup-<version>.exe
; =============================================================================

#define MyAppName "IPA Studio"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "IPA Studio"
#define MyAppExeName "IPAStudio.App.exe"
#define MyAppURL "https://github.com/kda2495/IPA_Downloader"

[Setup]
AppId={{7E1A4C52-9B7D-4E1F-A2C3-D4E5F6A7B8C9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=output
OutputBaseFilename=IPAStudio-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
ShowLanguageDialog=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Entire self-contained publish output, including the tools/ subfolder.
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Warn (do not block) if Apple Mobile Device Support / iTunes drivers are not
// detected: the app can still download IPA files, but device installation
// requires the Apple USB drivers.
function IsAppleMobileDeviceSupportInstalled(): Boolean;
begin
  Result :=
    RegKeyExists(HKLM, 'SOFTWARE\Apple Inc.\Apple Mobile Device Support') or
    RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Apple Inc.\Apple Mobile Device Support') or
    FileExists(ExpandConstant('{commonpf}\Common Files\Apple\Mobile Device Support\AppleMobileDeviceProcess.exe'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not IsAppleMobileDeviceSupportInstalled() then
    begin
      if ActiveLanguage() = 'russian' then
        MsgBox('Драйверы Apple (Apple Mobile Device Support) не обнаружены.' + #13#10 +
               'Для установки приложений на iPhone установите iTunes с сайта Apple ' +
               'или из Microsoft Store.' + #13#10#13#10 +
               'Загрузка IPA-файлов будет работать и без драйверов.',
               mbInformation, MB_OK)
      else
        MsgBox('Apple drivers (Apple Mobile Device Support) were not detected.' + #13#10 +
               'To install apps onto an iPhone, please install iTunes from Apple''s ' +
               'website or the Microsoft Store.' + #13#10#13#10 +
               'Downloading IPA files will work without the drivers.',
               mbInformation, MB_OK);
    end;
  end;
end;
