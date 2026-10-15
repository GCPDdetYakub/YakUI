; Установщик YakUI (NFR-01, 16.1): только для текущего пользователя, без прав администратора.
; Собирается build\publish.ps1: iscc /DAppVersion=… /DSourceDir=… /DArch=x64|arm64 /DOutputDir=… YakUI.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir не задан: папка опубликованного YakUI (dotnet publish)
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

[Setup]
AppId={{6F3B0C9E-5D21-4B8A-9E37-2A41C7D8F512}
AppName=YakUI
AppVersion={#AppVersion}
AppVerName=YakUI {#AppVersion}
AppPublisher=YakUI
; 16.1: единственный вариант — для текущего пользователя; папку можно изменить.
DefaultDirName={localappdata}\Programs\YakUI
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DisableProgramGroupPage=yes
DisableDirPage=no
UsePreviousAppDir=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=YakUI-{#AppVersion}-win-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=YakUI
UninstallDisplayIcon={app}\YakUI.exe
; Запущенный YakUI закрывается через Restart Manager перед заменой файлов.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.DesktopIcon=Значок на рабочем столе
english.DesktopIcon=Desktop shortcut
russian.NotWritable=В папку «%1» нельзя записать. Выберите папку, в которую текущий пользователь может записывать.
english.NotWritable=The folder "%1" is not writable. Choose a folder the current user can write to.
russian.RemoveData=Удалить также данные YakUI: настройки, кэш каталога, скачанные .yak, yak-инструменты, журнал и логи?%n%n%1%n%2%n%nУстановленные плагины Rhino не затрагиваются.
english.RemoveData=Also remove YakUI data: settings, catalog cache, downloaded .yak files, yak tools, journal and logs?%n%n%1%n%2%n%nInstalled Rhino plug-ins are not affected.
russian.LaunchApp=Запустить YakUI
english.LaunchApp=Launch YakUI

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
; portable.flag в установленную версию не попадает: данные — в профиле Windows (16.1).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "portable.flag,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\YakUI"; Filename: "{app}\YakUI.exe"
Name: "{userdesktop}\YakUI"; Filename: "{app}\YakUI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\YakUI.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
// 16.1: папка установки проверяется на право записи текущим пользователем.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir, Probe: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Dir := WizardDirValue;
    Probe := AddBackslash(Dir) + '.yakui-write-test';
    if not ForceDirectories(Dir) or not SaveStringToFile(Probe, 'test', False) then
    begin
      MsgBox(FmtMessage(CustomMessage('NotWritable'), [Dir]), mbError, MB_OK);
      Result := False;
    end
    else
      DeleteFile(Probe);
  end;
end;

// 16.1: деинсталлятор спрашивает, удалить ли данные пользователя (при тихом удалении — не удаляет).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigDir, DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    ConfigDir := ExpandConstant('{userappdata}\YakUI');
    DataDir := ExpandConstant('{localappdata}\YakUI');
    if (DirExists(ConfigDir) or DirExists(DataDir)) and
       (MsgBox(FmtMessage(CustomMessage('RemoveData'), [ConfigDir, DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      DelTree(ConfigDir, True, True, True);
      DelTree(DataDir, True, True, True);
    end;
  end;
end;
