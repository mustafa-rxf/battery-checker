; PilKontrol kurulum sihirbazı (Inno Setup 6)
; Derleme: iscc installer\PilKontrol.iss   (önce publish\PilKontrol.exe üretilmiş olmalı)

#define AppName "PilKontrol"
#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#define AppExe "PilKontrol.exe"

[Setup]
AppId={{8F3C1E52-4A7B-4D19-9C2E-5B6A7D8E9F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=PilKontrol-Setup-{#AppVersion}
SetupIconFile=..\PilKontrol\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
tr.AutoStart=Windows açılışında otomatik başlat (arka planda, tepside)
en.AutoStart=Start automatically with Windows (in the background, tray)
tr.LaunchApp=PilKontrol'ü başlat
en.LaunchApp=Launch PilKontrol

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PilKontrol"; \
    ValueData: """{app}\{#AppExe}"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Çalışan kopyayı düzgünce kapat (eski güç planına, ekran hızına ve uygulamalara dönsün), sonra artıkları temizle.
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; RunOnceId: "QuitApp"; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--cleanup"; RunOnceId: "CleanupApp"; Flags: runhidden waituntilterminated

[Code]
// Güncelleme: eski sürüm çalışıyorsa önce düzgünce kapat.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  if FileExists(ExpandConstant('{app}\{#AppExe}')) then
    Exec(ExpandConstant('{app}\{#AppExe}'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
