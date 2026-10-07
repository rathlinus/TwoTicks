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
; Setup speaks the language Windows shows, and asks only when it has none of it.
ShowLanguageDialog=auto
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Messages]
en.WelcomeLabel2=This installs [name/ver] on your computer.%n%nWinWhatsApp links to WhatsApp on your phone the way WhatsApp Web does.
en.FinishedHeadingLabel=WinWhatsApp is installed
en.FinishedLabelNoIcons=Open WinWhatsApp and scan the code with WhatsApp on your phone.%n%nTo remove WinWhatsApp later, open Settings, go to Apps, then Installed apps.
en.FinishedLabel=Open WinWhatsApp and scan the code with WhatsApp on your phone.%n%nTo remove WinWhatsApp later, open Settings, go to Apps, then Installed apps.
de.WelcomeLabel2=Hiermit wird [name/ver] auf deinem Computer installiert.%n%nWinWhatsApp verknüpft sich mit WhatsApp auf deinem Telefon wie WhatsApp Web.
de.FinishedHeadingLabel=WinWhatsApp ist installiert
de.FinishedLabelNoIcons=Öffne WinWhatsApp und scanne den Code mit WhatsApp auf deinem Telefon.%n%nUm WinWhatsApp später zu entfernen, öffne die Einstellungen und gehe zu Apps und dann Installierte Apps.
de.FinishedLabel=Öffne WinWhatsApp und scanne den Code mit WhatsApp auf deinem Telefon.%n%nUm WinWhatsApp später zu entfernen, öffne die Einstellungen und gehe zu Apps und dann Installierte Apps.

[CustomMessages]
en.AutostartTask=Start WinWhatsApp when I sign in, in the notification area
en.StartApp=Start WinWhatsApp
en.DeleteData=Also delete your messages and downloaded files from this PC? They stay on your phone.
de.AutostartTask=WinWhatsApp bei der Anmeldung im Infobereich starten
de.StartApp=WinWhatsApp starten
de.DeleteData=Auch deine Nachrichten und heruntergeladenen Dateien von diesem PC löschen? Auf deinem Telefon bleiben sie erhalten.
en.UpdateInstalling=Installing update
en.UpdateStarting=Starting WinWhatsApp
de.UpdateInstalling=Update wird installiert
de.UpdateStarting=WinWhatsApp wird gestartet

[Tasks]
Name: "autostart"; Description: "{cm:AutostartTask}"

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
Filename: "{app}\WinWhatsApp.exe"; Description: "{cm:StartApp}"; Flags: postinstall nowait skipifsilent
; The app updates itself by running setup with /VERYSILENT and /RELAUNCH=window or
; /RELAUNCH=background, and quitting. Setup then starts the new version the same way;
; see the [Code] section for the other switches.
Filename: "{app}\WinWhatsApp.exe"; Parameters: "{code:RelaunchParameters}"; Flags: nowait; Check: Relaunching

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM WinWhatsApp.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopWinWhatsApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM WinWhatsApp.Bridge.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopBridge"

[Code]
// The app runs setup to update itself with these switches besides /RELAUNCH:
//
//   /WAITPID=<id>   the app's process, which setup waits for before it replaces files
//   /UPDATEWINDOW=<left>,<top>,<width>,<height>,<dpi>
//                   where the app shows its update window, in pixels
//   /LOGO=<file>    the logo for that window, as a PNG of the right size
//
// With /UPDATEWINDOW, setup draws the same window at the same place before the
// app quits, so the window stays on screen until the new version starts. Its
// layout and the hop of the logo match UpdateWindow.xaml in the app.

const
  UpdateWindowTitle = 'WinWhatsApp update';
  // The logo hops this high, in DIPs, for HopTime milliseconds out of every HopPeriod.
  HopHeight = 14;
  HopTime = 700;
  HopPeriod = 1100;
  SYNCHRONIZE = $00100000;
  WAIT_TIMEOUT = $102;
  PM_REMOVE = 1;
  DWMWA_WINDOW_CORNER_PREFERENCE = 33;
  DWMWCP_ROUND = 2;

type
  TMsg = record
    Wnd: HWND;
    Message: Longword;
    WParam: Longword;
    LParam: Longword;
    Time: Longword;
    X: Longint;
    Y: Longint;
    Reserved: Longword;
  end;

function OpenProcess(Access: Longword; Inherit: BOOL; ProcessId: Longword): THandle;
external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Longword): Longword;
external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
external 'CloseHandle@kernel32.dll stdcall';
function GetTickCount: Longword;
external 'GetTickCount@kernel32.dll stdcall';
function PeekMessage(var Msg: TMsg; Wnd: HWND; FilterMin, FilterMax, Remove: Longword): BOOL;
external 'PeekMessageW@user32.dll stdcall';
function TranslateMessage(var Msg: TMsg): BOOL;
external 'TranslateMessage@user32.dll stdcall';
function DispatchMessage(var Msg: TMsg): Longint;
external 'DispatchMessageW@user32.dll stdcall';
function SetTimer(Wnd: HWND; Id, Elapse, TimerFunc: Longword): Longword;
external 'SetTimer@user32.dll stdcall';
function KillTimer(Wnd: HWND; Id: Longword): BOOL;
external 'KillTimer@user32.dll stdcall';
function CreateRoundRectRgn(Left, Top, Right, Bottom, WidthEllipse, HeightEllipse: Integer): THandle;
external 'CreateRoundRectRgn@gdi32.dll stdcall';
function SetWindowRgn(Wnd: HWND; Region: THandle; Redraw: BOOL): Integer;
external 'SetWindowRgn@user32.dll stdcall';
function DwmSetWindowAttribute(Wnd: HWND; Attribute: Longword; var Value: Longword; Size: Longword): Longint;
external 'DwmSetWindowAttribute@dwmapi.dll stdcall delayload';

var
  UpdateForm: TSetupForm;
  UpdateLogo: TPanel;
  UpdateTitle: TNewStaticText;
  UpdateFill: TPanel;
  UpdateDpi: Integer;
  UpdateLogoTop: Integer;
  UpdateBarWidth: Integer;
  UpdateTimer: Longword;
  UpdateStart: Longword;
  Installed: Boolean;

function Relaunching: Boolean;
begin
  Result := WizardSilent and ((ExpandConstant('{param:relaunch|no}') = 'window') or (ExpandConstant('{param:relaunch|no}') = 'background'));
end;

function RelaunchParameters(Param: String): String;
begin
  if ExpandConstant('{param:relaunch|no}') = 'background' then
    Result := '--background'
  else
    Result := '';
end;

// DIPs to the window's pixels.
function Px(Dips: Integer): Integer;
begin
  Result := (Dips * UpdateDpi + 48) div 96;
end;

// Takes the next number off a list like '10,20,30'.
function NextNumber(var List: String): Integer;
var
  Comma: Integer;
begin
  Comma := Pos(',', List);
  if Comma = 0 then
    Comma := Length(List) + 1;
  Result := StrToIntDef(Trim(Copy(List, 1, Comma - 1)), -1);
  Delete(List, 1, Comma);
end;

procedure RoundCorners(Control: TWinControl);
begin
  SetWindowRgn(Control.Handle, CreateRoundRectRgn(0, 0, Control.Width + 1, Control.Height + 1, Control.Height, Control.Height), True);
end;

function NewText(Top, Size: Integer; FontName: String; Color: TColor; Text: String): TNewStaticText;
begin
  Result := TNewStaticText.Create(UpdateForm);
  Result.Parent := UpdateForm;
  Result.AutoSize := True;
  Result.Font.Name := FontName;
  Result.Font.Height := -Px(Size);
  Result.Font.Color := Color;
  Result.Caption := Text;
  Result.Top := Px(Top);
  Result.Left := (UpdateForm.ClientWidth - Result.Width) div 2;
end;

procedure SetUpdateTitle(Text: String);
begin
  UpdateTitle.Caption := Text;
  UpdateTitle.Left := (UpdateForm.ClientWidth - UpdateTitle.Width) div 2;
end;

procedure SetUpdateProgress(Done, Total: Integer);
var
  Width: Integer;
begin
  if Total <= 0 then
    Exit;
  // Integers, as / between two of them divides without the remainder in Pascal Script.
  Width := Integer(Int64(UpdateBarWidth) * Done div Total);
  UpdateFill.Visible := Width >= UpdateFill.Height;
  if UpdateFill.Visible and (UpdateFill.Width <> Width) then
  begin
    UpdateFill.Width := Width;
    RoundCorners(UpdateFill);
  end;
end;

// Lets the window paint and the logo hop while setup waits.
procedure PumpMessages;
var
  Msg: TMsg;
begin
  while PeekMessage(Msg, 0, 0, 0, PM_REMOVE) do
  begin
    TranslateMessage(Msg);
    DispatchMessage(Msg);
  end;
end;

procedure Wait(Milliseconds: Longword);
var
  Start: Longword;
begin
  Start := GetTickCount;
  while GetTickCount - Start < Milliseconds do
  begin
    PumpMessages;
    Sleep(15);
  end;
end;

procedure HopTimer(Wnd, Msg, Id, Time: Longword);
var
  T, Hop: Integer;
begin
  // A parabola, as a thrown object flies, then a rest on the ground.
  T := (GetTickCount - UpdateStart) mod HopPeriod;
  if T < HopTime then
    Hop := Px(HopHeight) * 4 * T * (HopTime - T) div (HopTime * HopTime)
  else
    Hop := 0;
  if UpdateLogo.Top <> UpdateLogoTop - Hop then
    UpdateLogo.Top := UpdateLogoTop - Hop;
end;

procedure ShowUpdateWindow;
var
  Placement, Logo: String;
  Image: TBitmapImage;
  Left, Top, Width, Height: Integer;
  Track: TPanel;
  Corner: Longword;
begin
  Placement := ExpandConstant('{param:updatewindow|}');
  if Placement = '' then
    Exit;
  Left := NextNumber(Placement);
  Top := NextNumber(Placement);
  Width := NextNumber(Placement);
  Height := NextNumber(Placement);
  UpdateDpi := NextNumber(Placement);
  if (Width <= 0) or (Height <= 0) or (UpdateDpi <= 0) then
    Exit;

  UpdateForm := CreateCustomForm(Width, Height, True, True);
  UpdateForm.BorderStyle := bsNone;
  UpdateForm.Position := poDesigned;
  UpdateForm.FormStyle := fsStayOnTop;
  UpdateForm.Caption := UpdateWindowTitle;
  UpdateForm.Color := $1A140B;
  UpdateForm.SetBounds(Left, Top, Width, Height);

  // The logo sits in a window of its own: Windows moves a window's pixels as
  // they are, where a moved image would be painted again and flicker.
  UpdateLogoTop := Px(64);
  UpdateLogo := TPanel.Create(UpdateForm);
  UpdateLogo.Parent := UpdateForm;
  UpdateLogo.BevelOuter := bvNone;
  UpdateLogo.ParentBackground := False;
  UpdateLogo.Color := UpdateForm.Color;
  UpdateLogo.SetBounds((Width - Px(96)) div 2, UpdateLogoTop, Px(96), Px(96));
  Image := TBitmapImage.Create(UpdateForm);
  Image.Parent := UpdateLogo;
  Image.BackColor := UpdateForm.Color;
  Image.Center := True;
  Image.SetBounds(0, 0, UpdateLogo.Width, UpdateLogo.Height);
  Logo := ExpandConstant('{param:logo|}');
  if (Logo <> '') and FileExists(Logo) then
    Image.PngImage.LoadFromFile(Logo);

  UpdateTitle := NewText(186, 16, 'Segoe UI Semibold', $EFEDE9, CustomMessage('UpdateInstalling'));
  NewText(212, 13, 'Segoe UI', $A09686, 'WinWhatsApp {#AppVersion}');

  UpdateBarWidth := Px(180);
  Track := TPanel.Create(UpdateForm);
  Track.Parent := UpdateForm;
  Track.BevelOuter := bvNone;
  Track.ParentBackground := False;
  Track.Color := $383123;
  Track.SetBounds((Width - UpdateBarWidth) div 2, Px(250), UpdateBarWidth, Px(4));
  RoundCorners(Track);

  UpdateFill := TPanel.Create(UpdateForm);
  UpdateFill.Parent := UpdateForm;
  UpdateFill.BevelOuter := bvNone;
  UpdateFill.ParentBackground := False;
  UpdateFill.Color := $84A800;
  UpdateFill.SetBounds(Track.Left, Track.Top, 0, Track.Height);
  UpdateFill.Visible := False;

  UpdateForm.Show;
  PumpMessages;
  Corner := DWMWCP_ROUND;
  try
    DwmSetWindowAttribute(UpdateForm.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, Corner, SizeOf(Corner));
  except
    // Square corners before Windows 11.
  end;
  UpdateStart := GetTickCount;
  UpdateTimer := SetTimer(0, 0, 15, CreateCallback(@HopTimer));
end;

procedure InitializeWizard;
begin
  if Relaunching then
    ShowUpdateWindow;
end;

// The app quits after it started setup, and passes /NOCLOSEAPPLICATIONS:
// waiting for it here is quicker than Restart Manager, which looks through
// every file for programs that use it.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Process: THandle;
  Start: Longword;
  Code: Integer;
begin
  Result := '';
  Process := OpenProcess(SYNCHRONIZE, False, StrToIntDef(ExpandConstant('{param:waitpid|0}'), 0));
  if Process = 0 then
    Exit;
  try
    Log('Waiting for WinWhatsApp to quit');
    Start := GetTickCount;
    while (WaitForSingleObject(Process, 15) = WAIT_TIMEOUT) and (GetTickCount - Start < 30000) do
      PumpMessages;
    if WaitForSingleObject(Process, 0) = WAIT_TIMEOUT then
    begin
      // The helper goes with it, as the app ties it to its own process.
      Log('WinWhatsApp did not quit; ending it');
      Exec(ExpandConstant('{sys}	askkill.exe'), '/F /PID ' + ExpandConstant('{param:waitpid|0}'), '', SW_HIDE, ewWaitUntilTerminated, Code);
      WaitForSingleObject(Process, 5000);
    end;
    Log(Format('Waited %d ms for WinWhatsApp to quit', [GetTickCount - Start]));
  finally
    CloseHandle(Process);
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if UpdateForm <> nil then
    SetUpdateProgress(CurProgress, MaxProgress);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    Installed := True;
    if UpdateForm <> nil then
    begin
      SetUpdateTitle(CustomMessage('UpdateStarting'));
      SetUpdateProgress(1, 1);
    end;
  end;
end;

procedure DeinitializeSetup;
var
  Start: Longword;
  Code: Integer;
begin
  if UpdateForm <> nil then
  begin
    // Stays until the new version has its window. The title may carry the
    // number of unread chats, so the window is found by its WinUI class.
    Start := GetTickCount;
    while Installed and (FindWindowByClassName('WinUIDesktopWin32WindowClass') = 0) and (GetTickCount - Start < 10000) do
      Wait(50);
    KillTimer(0, UpdateTimer);
    UpdateForm.Hide;
    UpdateForm.Free;
    UpdateForm := nil;
  end;
  // An update that failed starts the old version again, so the app does not just vanish.
  if Relaunching and not Installed then
  try
    if FileExists(ExpandConstant('{app}\WinWhatsApp.exe')) then
      Exec(ExpandConstant('{app}\WinWhatsApp.exe'), RelaunchParameters(''), '', SW_SHOW, ewNoWait, Code);
  except
  end;
end;

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
      if MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
