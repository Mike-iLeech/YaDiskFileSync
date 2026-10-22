; Установщик YaDisk File Sync (Inno Setup 6).
; Ставится для текущего пользователя, без прав администратора, в %LOCALAPPDATA%\Programs\YaDiskFileSync —
; поэтому программа может обновлять себя сама (заменяет свой EXE из релизов GitHub).
; Сборка: iscc /DAppVersion=2.0.0 /DSourceExe=..\publish\YaDiskFileSync.exe installer\YaDiskSync.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\YaDiskFileSync.exe"
#endif

[Setup]
AppId=YaDiskFileSync
AppName=YaDisk File Sync
AppVersion={#AppVersion}
AppVerName=YaDisk File Sync {#AppVersion}
AppPublisher=Mike-iLeech
AppPublisherURL=https://github.com/Mike-iLeech/YaDiskFileSync
AppSupportURL=https://github.com/Mike-iLeech/YaDiskFileSync/issues
DefaultDirName={localappdata}\Programs\YaDiskFileSync
DefaultGroupName=YaDisk File Sync
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=YaDiskFileSync-Setup
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\YaDiskFileSync.exe
UninstallDisplayName=YaDisk File Sync
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
; запущенную программу установщик закроет сам (она держит этот мьютекс)
AppMutex=Local\YaDiskFileSync.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Запускать вместе с Windows"; GroupDescription: "Дополнительно:"
Name: "desktopicon"; Description: "Значок на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\YaDisk File Sync"; Filename: "{app}\YaDiskFileSync.exe"
Name: "{userdesktop}\YaDisk File Sync"; Filename: "{app}\YaDiskFileSync.exe"; Tasks: desktopicon

[Registry]
; то же значение, что ставит сама программа в настройках («Запускать вместе с Windows»)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "YaDiskFileSync"; \
  ValueData: """{app}\YaDiskFileSync.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\YaDiskFileSync.exe"; Description: "Запустить YaDisk File Sync"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; убрать пункт YaDiskSync из панели Проводника и автозапуск; папка с файлами остаётся на месте
Filename: "{app}\YaDiskFileSync.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "Unregister"

[UninstallDelete]
Type: files; Name: "{app}\YaDiskFileSync.exe.old"
Type: files; Name: "{app}\YaDiskFileSync.exe.new"

[Code]
// Нужен .NET 8 Desktop Runtime (x64): EXE сделан небольшим, без встроенного .NET.
function HasDesktopRuntime8: Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  Root := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\');
  if FindFirst(Root + '8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Result := True;
      until Result or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not HasDesktopRuntime8 then
  begin
    if MsgBox('Для работы программы нужен .NET 8 Desktop Runtime (x64) от Microsoft, а он не найден.' + #13#10#13#10 +
              'Открыть страницу загрузки? Установите «.NET Desktop Runtime 8 — Windows x64», затем запустите этот установщик снова.' + #13#10#13#10 +
              'Нажмите «Нет», чтобы продолжить установку без него.',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
      Result := False;
    end;
  end;
end;
