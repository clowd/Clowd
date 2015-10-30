#include <idp.iss>

#define MyAppName "Clowd"
#define MyAppPublisher "Caesa Consulting Ltd."
#define MyAppURL "http://clowd.ca"
#define MyAppExeName "Clowd.exe"
#define MySourceDirectory "C:\Users\Caelan\Source\Workspaces\Clowd\Clowd\bin\Release"
#define MyBuildDirectory "C:\Users\Caelan\Source\Workspaces\Clowd\tools\installer"
#define MyContextName "Upload with Clowd"

[Setup]
AppId={{73508F56-6F06-4478-B45B-D76DE0C2D93D}
AppName={#MyAppName}
AppVerName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppCopyright=Copyright © 2014-2015 Caesa Consulting Ltd.
AppMutex=ClowdMutex000
DefaultDirName={pf}\{#MyAppName}
DisableDirPage=yes
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#MyBuildDirectory}
OutputBaseFilename=clowd-setup
SetupIconFile={#MyBuildDirectory}\images\clowd.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma
SolidCompression=yes
SignedUninstaller=yes
SignTool=clowdsign


[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Types]
Name: "full"; Description: "Reccommended"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "program"; Description: "Clowd Program Files"; Types: full custom; Flags: fixed
Name: "contextmenu"; Description: "Install Context Menu"; Types: full
Name: "autostart"; Description: "Start Clowd With Windows"; Types: full

[Files]
Source: "{#MyBuildDirectory}\images\*"; DestDir: "{tmp}"; Flags: dontcopy nocompression;
Source: "{#MySourceDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion 
;Source: "{#MySourceDirectory}\Clowd.exe"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\Accord.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\Accord.Video.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\Accord.Video.FFMPEG.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\avcodec-53.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\avdevice-53.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\avformat-53.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\avutil-51.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\Clowd.exe"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\Clowd.Interop.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\Clowd.Shared.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\ColorPickerLib.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\DrawToolsLib.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\Hardcodet.Wpf.TaskbarNotification.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\Ionic.Zip.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\MahApps.Metro.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\NAppUpdate.Framework.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\NAudio.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\NReco.VideoConverter.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\PhotoLoader.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\postproc-52.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\RT.Util.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\Screeney.exe"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\SharpDX.Direct3D11.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\SharpDX.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\SharpDX.DXGI.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\swresample-0.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\swscale-2.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\System.Threading.Tasks.Dataflow.dll"; DestDir: "{app}"; Flags: ignoreversion
;Source: "{#MySourceDirectory}\System.Windows.Interactivity.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:ProgramOnTheWeb,{#MyAppName}}"; Filename: "{#MyAppURL}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
;auto-run
Root: HKLM; Subkey: "Software\\Microsoft\\Windows\\CurrentVersion\\Run"; ValueName: "{#MyAppName}"; ValueType: string; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey; Components: autostart;
;context-menu
Root: HKLM; Subkey: "Software\Classes\*\shell\{#MyAppName}"; Flags: uninsdeletekey; Components: contextmenu;
Root: HKLM; Subkey: "Software\Classes\*\shell\{#MyAppName}"; ValueName: ""; ValueType: string; ValueData: "{#MyContextName}"; Components: contextmenu; Flags: uninsdeletekey;
Root: HKLM; Subkey: "Software\Classes\*\shell\{#MyAppName}"; ValueName: "Icon"; ValueType: string; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey; Components: contextmenu;
Root: HKLM; Subkey: "Software\Classes\*\shell\{#MyAppName}\command"; ValueName: ""; ValueType: string; ValueData: "{app}\{#MyAppExeName} ""%1"""; Flags: uninsdeletekey; Components: contextmenu;
Root: HKLM; Subkey: "Software\Classes\Directory\shell\{#MyAppName}"; Flags: uninsdeletekey; Components: contextmenu;
Root: HKLM; Subkey: "Software\Classes\Directory\shell\{#MyAppName}"; ValueName: ""; ValueType: string; ValueData: "{#MyContextName}"; Components: contextmenu; Flags: uninsdeletekey;
Root: HKLM; Subkey: "Software\Classes\Directory\shell\{#MyAppName}"; ValueName: "Icon"; ValueType: string; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey; Components: contextmenu;
Root: HKLM; Subkey: "Software\Classes\Directory\shell\{#MyAppName}\command"; ValueName: ""; ValueType: string; ValueData: "{app}\{#MyAppExeName} ""%1"""; Flags: uninsdeletekey; Components: contextmenu;
;general
Root: HKLM; Subkey: "Software\{#MyAppName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\{#MyAppName}\Settings"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#MyAppName}"
Type: filesandordirs; Name: "{commonappdata}\{#MyAppName}"
Type: filesandordirs; Name: "{pf}\{#MyAppName}"

[Code]
var
  DPIValString: String;

procedure CheckDPI;
var
  CurrentDPI, StandardDPI, MediumDPI, LargeDPI, UltraDPI: Integer;
begin
  // Get the current DPI
  CurrentDPI  := WizardForm.Font.PixelsPerInch;

  // Store defaults determined from Windows DPI settings
  StandardDPI := 96;  // 100%
  MediumDPI   := 120; // 125%
  LargeDPI    := 144; // 150%
  UltraDPI    := 168; // 175% introduced by Windows 10 

  if (CurrentDPI >= StandardDPI) and (CurrentDPI < MediumDPI) then 
  begin
      DPIValString := '96';
  end
  else if (CurrentDPI >= MediumDPI) and (CurrentDPI < LargeDPI) then
  begin
      DPIValString := '120';
  end
  else if (CurrentDPI >= LargeDPI) and (CurrentDPI < UltraDPI)then
  begin
      DPIValString := '144';
  end
  else if (CurrentDPI >= UltraDPI) then
  begin
      DPIValString := '168';
  end;
end;

procedure SetDPIImages;
begin
  CheckDPI;
  ExtractTemporaryFile('clowd-small-' + DPIValString + '.bmp');
  ExtractTemporaryFile('clowd-' + DPIValString + '.bmp');
  if (FileExists(ExpandConstant('{tmp}\clowd-small-' + DPIValString + '.bmp')))
   and (FileExists(ExpandConstant('{tmp}\clowd-' + DPIValString + '.bmp'))) 
   then begin 
    with WizardForm.WizardSmallBitmapImage do
      Bitmap.LoadFromFile(ExpandConstant('{tmp}\clowd-small-' + DPIValString + '.bmp'));
    with WizardForm.WizardBitmapImage do
      Bitmap.LoadFromFile(ExpandConstant('{tmp}\clowd-' + DPIValString + '.bmp'));
  end; 
end;

function Framework45IsNotInstalled(): Boolean;
var
  bSuccess: Boolean;
  regVersion: Cardinal;
begin
  Result := True;

  bSuccess := RegQueryDWordValue(HKLM, 'Software\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', regVersion);
  if (True = bSuccess) and (regVersion >= 378389) then begin
    Result := False;
  end;
end;

procedure InitializeWizard;
begin
  SetDPIImages;
  if Framework45IsNotInstalled() then
  begin
    idpAddFile('http://go.microsoft.com/fwlink/?LinkId=397707', ExpandConstant('{tmp}\NetFrameworkInstaller.exe'));
    idpDownloadAfter(wpReady);
  end;
end;

procedure InstallFramework;
var
  StatusText: string;
  ResultCode: Integer;
begin
  StatusText := WizardForm.StatusLabel.Caption;
  WizardForm.StatusLabel.Caption := 'Installing .NET Framework 4.5.2. This might take a while...';
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    if not Exec(ExpandConstant('{tmp}\NetFrameworkInstaller.exe'), '/passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('.NET installation failed with code: ' + IntToStr(ResultCode) + '.', mbError, MB_OK);
    end;
  finally
    WizardForm.StatusLabel.Caption := StatusText;
    WizardForm.ProgressGauge.Style := npbstNormal;

    DeleteFile(ExpandConstant('{tmp}\NetFrameworkInstaller.exe'));
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  case CurStep of
    ssPostInstall:
      begin
        if Framework45IsNotInstalled() then
        begin
          InstallFramework();
        end;
      end;
  end;
end;

