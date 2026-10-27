#ifndef PayloadRoot
  #error PayloadRoot must point to the published Windows package
#endif
#ifndef DependencyRoot
  #error DependencyRoot must point to the verified prerequisite directory
#endif
#ifndef AndroidApk
  #error AndroidApk must point to the built, signed release APK
#endif
#ifndef CompilerLicense
  #error CompilerLicense must point to Inno Setup's License.txt
#endif
#ifndef OutputRoot
  #error OutputRoot must point to the artifacts directory
#endif

[Setup]
AppId={{08F219D0-78DC-4CD8-B619-7ACCB41AB687}
AppName=KeyBridge
AppVersion=0.1.2
AppPublisher=KeyBridge contributors
DefaultDirName={localappdata}\Programs\KeyBridge
DefaultGroupName=KeyBridge
DisableProgramGroupPage=yes
UsePreviousTasks=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#OutputRoot}
OutputBaseFilename=KeyBridge-0.1.2-Setup-x64
SetupIconFile=..\windows\KeyBridge.App\KeyBridge.ico
UninstallDisplayIcon={app}\app\KeyBridge.exe
LicenseFile=..\LICENSE
InfoBeforeFile=INSTALLER.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
DisableWelcomePage=no
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AppMutex=Local\KeyBridge.Desktop.UpgradeGuard
UninstallDisplayName=KeyBridge

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"

[InstallDelete]
Type: files; Name: "{app}\android\KeyBridge-debug.apk"

[Files]
Source: "{#PayloadRoot}\*"; DestDir: "{app}"; Excludes: "Install.ps1,Uninstall.ps1"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#DependencyRoot}\adb\*"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#DependencyRoot}\usbip-LICENSE.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "{#CompilerLicense}"; DestDir: "{app}\licenses"; DestName: "InnoSetup.txt"; Flags: ignoreversion
Source: "{#AndroidApk}"; DestDir: "{app}\android"; DestName: "KeyBridge-release.apk"; Flags: ignoreversion
Source: "{#DependencyRoot}\USBip-0.9.8.1-x64.exe"; Flags: dontcopy

[Icons]
Name: "{userprograms}\KeyBridge"; Filename: "{app}\app\KeyBridge.exe"; WorkingDir: "{app}\app"; Check: not WizardNoIcons
Name: "{userdesktop}\KeyBridge"; Filename: "{app}\app\KeyBridge.exe"; WorkingDir: "{app}\app"; Tasks: desktopicon

[Run]
Filename: "{tmp}\USBip-0.9.8.1-x64.exe"; Parameters: "/NORESTART /RESTARTEXITCODE=3010 /TYPE=compact /TASKS=""vcredist"""; Verb: "runas"; Description: "Install the signed USB/IP driver"; Flags: postinstall shellexec runascurrentuser waituntilterminated hidewizard skipifsilent; Check: DriverMissing; BeforeInstall: VerifyDriverPayload; AfterInstall: DriverFinished
Filename: "{app}\app\KeyBridge.exe"; Description: "Open KeyBridge"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{userprograms}\KeyBridge.lnk"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "KeyBridge"; Flags: dontcreatekey uninsdeletevalue

[Code]

function DriverMissing: Boolean;
begin
  Result := True;
  if RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\usbip2_ude') and
     RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\usbip2_filter') and
     FileExists(ExpandConstant('{commonpf64}\USBip\usbip.exe')) then begin
    try
      Result := CompareText(GetSHA256OfFile(ExpandConstant('{commonpf64}\USBip\usbip.exe')),
        '817DA219FA153B4B37C3BC1AA8FCC63F33E046188C57CC3DD2C60A6EF0883C56') <> 0;
    except
      Result := True;
    end;
  end;
end;

procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel2.Caption :=
    'Install the Windows companion, private Android Debug Bridge tools, offline diagnostic, and phone APK in one setup.' + #13#10#13#10 +
    'After KeyBridge is installed, Finish opens the signed USB/IP driver installer if needed. It asks for administrator approval. No browser extension is required.' + #13#10#13#10 +
    'Phone debugging authorization, KeyBridge pairing and fingerprints remain local actions on your phone.';
end;

procedure VerifyDriverPayload;
var
  Installer: String;
begin
  ExtractTemporaryFile('USBip-0.9.8.1-x64.exe');
  Installer := ExpandConstant('{tmp}\USBip-0.9.8.1-x64.exe');
  if CompareText(GetSHA256OfFile(Installer), '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A') <> 0 then
    RaiseException('Driver integrity check failed. KeyBridge is installed, but no driver will be started.');
end;

procedure DriverFinished;
begin
  if DriverMissing then
    MsgBox('KeyBridge is installed. The USB/IP driver is still missing or did not finish. Open Checks in KeyBridge to review what remains, or run setup again to retry the driver step.', mbInformation, MB_OK);
end;
