; The WinWhatsApp setup program, compiled by scripts\release.ps1 with Inno Setup:
;
;   ISCC /DAppVersion=1.2.0 /DSourceDir=<artifacts\app> /DOutputDir=<artifacts\release> WinWhatsApp.iss
;
; WinWhatsApp is a plain folder of programs, so setup copies that folder for the
; current user, adds it to the Start menu and, if wanted, to the programs that
; start at sign-in. The messages and the session stay in %LOCALAPPDATA%\WinWhatsApp;
; the uninstaller asks before deleting them.

#ifndef AppVersion
  #error Pass the version with /DAppVersion=<major.minor.patch>
#endif
#ifndef SourceDir
  #error Pass the folder with WinWhatsApp.exe with /DSourceDir=<path>
#endif
#ifndef OutputDir
  #define OutputDir SourceDir
#endif

[Setup]
AppId={{9C4E7B2A-5D18-4F63-A0E9-3B7C1D6F8A42}
AppName=WinWhatsApp
AppVersion={#AppVersion}
AppVerName=WinWhatsApp {#AppVersion}
AppPublisher=WinWhatsApp
VersionInfoVersion={#AppVersion}
VersionInfoDescription=WinWhatsApp Setup

OutputDir={#OutputDir}
OutputBaseFilename=WinWhatsApp-{#AppVersion}-Setup
SetupIconFile=..\src\WinWhatsApp.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\WinWhatsApp.exe
WizardStyle=modern
WizardImageFile=installer-side.bmp
WizardSmallImageFile=installer-small.bmp

; Installed for the current user only. No administrator is needed.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\WinWhatsApp
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes

; A running WinWhatsApp keeps its files locked. Setup asks it to close, and the
; finish page starts the new version.
CloseApplications=force
RestartApplications=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This installs [name/ver] on your computer.%n%nWinWhatsApp links to WhatsApp on your phone the way WhatsApp Web does.
FinishedHeadingLabel=WinWhatsApp is installed
FinishedLabelNoIcons=Open WinWhatsApp and scan the code with WhatsApp on your phone.%n%nTo remove WinWhatsApp later, open Settings, go to Apps, then Installed apps.
FinishedLabel=Open WinWhatsApp and scan the code with WhatsApp on your phone.%n%nTo remove WinWhatsApp later, open Settings, go to Apps, then Installed apps.

[Tasks]
Name: "autostart"; Description: "Start WinWhatsApp when I sign in, in the notification area"

[InstallDelete]
; The files of the Windows App SDK differ from version to version; leftovers of
; an older one must not mix with the new.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; The same AppUserModelID the app gives its window and notifications, so a
; pinned taskbar button and the running app are one button.
Name: "{userprograms}\WinWhatsApp"; Filename: "{app}\WinWhatsApp.exe"; AppUserModelID: "WinWhatsApp"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WinWhatsApp"; ValueData: """{app}\WinWhatsApp.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue
; WinWhatsApp writes this itself at every start; listing it here lets the
; uninstaller remove it.
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\WinWhatsApp"; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\WinWhatsApp.exe"; Description: "Start WinWhatsApp"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM WinWhatsApp.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopWinWhatsApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM WinWhatsApp.Bridge.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopBridge"

[Code]
// Messages, media and the link to the phone are not part of the program. They
// go only when asked, so reinstalling keeps them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\WinWhatsApp');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox('Also delete your messages and downloaded files from this PC? They stay on your phone.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
