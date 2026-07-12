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
; MyAppVersion may be provided by the build pipeline via /DMyAppVersion=x.y.z
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#define MyAppPublisher "IPA Studio"
; Must match <AssemblyName> in IPAStudio.App.csproj (currently "IPAStudio"),
; which produces IPAStudio.exe — NOT IPAStudio.App.exe. A mismatch here makes
; every shortcut point at a non-existent file ("missing shortcut" dialog).
#define MyAppExeName "IPAStudio.exe"
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
// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function IsAppleMobileDeviceSupportInstalled(): Boolean;
begin
  Result :=
    RegKeyExists(HKLM, 'SOFTWARE\Apple Inc.\Apple Mobile Device Support') or
    RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Apple Inc.\Apple Mobile Device Support') or
    FileExists(ExpandConstant('{commonpf}\Common Files\Apple\Mobile Device Support\AppleMobileDeviceProcess.exe'));
end;

// ---------------------------------------------------------------------------
// Pre-install cleanup — removes stale binaries left over from previous builds.
//
// Why: Inno Setup copies new files and overwrites matching names, but any
// file that existed in the old install but is absent from the new build
// (renamed DLL, removed tool, old runtime assembly) stays on disk forever
// and can cause "wrong version" or DLL-conflict bugs at runtime.
//
// Strategy: delete everything inside {app} that looks like a program file
// (*.exe, *.dll, *.pdb, *.json, *.config), but NEVER touch user-data
// sub-directories (Apps, logs, cache — these live under %LOCALAPPDATA%,
// not in {app}, so they are safe regardless).
// The tools\ sub-folder is also wiped so outdated native binaries can't
// shadow newer ones shipped with the new build.
// ---------------------------------------------------------------------------
procedure CleanOldFiles();
var
  AppDir:   String;
  FindRec:  TFindRec;
  FilePath: String;
begin
  AppDir := ExpandConstant('{app}');
  if not DirExists(AppDir) then Exit;  // fresh install — nothing to clean

  // --- root-level program files -------------------------------------------
  if FindFirst(AppDir + '\*', FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
        begin
          FilePath := AppDir + '\' + FindRec.Name;
          // Only wipe recognised binary/config extensions.
          if (CompareText(ExtractFileExt(FindRec.Name), '.exe')    = 0) or
             (CompareText(ExtractFileExt(FindRec.Name), '.dll')    = 0) or
             (CompareText(ExtractFileExt(FindRec.Name), '.pdb')    = 0) or
             (CompareText(ExtractFileExt(FindRec.Name), '.json')   = 0) or
             (CompareText(ExtractFileExt(FindRec.Name), '.config') = 0) or
             (CompareText(ExtractFileExt(FindRec.Name), '.runtimeconfig.json') = 0) then
            DeleteFile(FilePath);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  // --- tools\ sub-folder (native executables shipped with the app) ---------
  // Delete every file recursively; Inno will recreate the folder and copy
  // fresh binaries during the normal [Files] install step.
  DelTree(AppDir + '\tools', False, True, False);
  // (DelTree(path, deleteDir, deleteFiles, deleteSubDirsAlso) — keep the dir)
end;

// ---------------------------------------------------------------------------
// Main setup step handler
// ---------------------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
begin
  // ssInstall fires right before files are copied — the ideal moment to wipe
  // stale binaries so they can be replaced by the new build.
  if CurStep = ssInstall then
    CleanOldFiles();

  if CurStep = ssPostInstall then
  begin
    if not IsAppleMobileDeviceSupportInstalled() then
    begin
      if ActiveLanguage() = 'russian' then
        MsgBox('Драйверы Apple (Apple Mobile Device Support) не обнаружены.' + #13#10 +
               'Для установки приложений на iPhone установите iTunes с сайта Apple.' +
               #13#10#13#10 +
               'Загрузка IPA-файлов будет работать и без драйверов.',
               mbInformation, MB_OK)
      else
        MsgBox('Apple drivers (Apple Mobile Device Support) were not detected.' + #13#10 +
               'To install apps onto an iPhone, please install iTunes from Apple''s website.' +
               #13#10#13#10 +
               'Downloading IPA files will work without the drivers.',
               mbInformation, MB_OK);
    end;
  end;
end;
