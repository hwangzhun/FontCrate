; Requires Inno Setup 6.5+. The upstream Simplified Chinese translation is vendored.
#ifndef PublishDir
  #error PublishDir must point to a complete self-contained win-x64 publish directory.
#endif
#ifndef OutputDir
  #error OutputDir must point to the installer output directory.
#endif
#ifndef AppVersion
  #error AppVersion must match the published application's assembly version.
#endif
#define AppName "FontCrate"
#define AppExe "FontCrate.exe"

[Setup]
AppId=FontCrate
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Hwangzhun
AppPublisherURL=https://github.com/hwangzhun/FontCrate/
AppSupportURL=https://github.com/hwangzhun/FontCrate/issues
AppUpdatesURL=https://github.com/hwangzhun/FontCrate/releases
DefaultDirName={localappdata}\Programs\FontCrate
DefaultGroupName=FontCrate
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; Windows 10 1809: current-user font installation and DirectWrite support.
MinVersion=10.0.17763
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
LicenseFile={#PublishDir}\LICENSE
SetupIconFile=..\..\src\FontCrate.App\Assets\FontCrate.ico
OutputDir={#OutputDir}
OutputBaseFilename=FontCrate-Setup-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\FontCrate-Running
SetupMutex=Local\FontCrate-Setup
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[Messages]
english.ConfirmUninstall=Are you sure you want to completely remove %1 and its installed application files?%n%nYour font library, settings, original fonts and fonts installed for your Windows user will be kept.
chinesesimplified.ConfirmUninstall=确定要卸载 %1 并移除已安装的程序文件吗？%n%n字体库、设置、原始字体和已安装到当前 Windows 用户的字体会保留。

english.SetupAppRunningError=%1 is still running. Use "Exit and deactivate temporary fonts" in FontCrate or its tray menu, then click OK to continue or Cancel to exit.
english.UninstallAppRunningError=%1 is still running. Use "Exit and deactivate temporary fonts" in FontCrate or its tray menu, then click OK to continue or Cancel to exit.
chinesesimplified.SetupAppRunningError=%1 仍在运行。请在程序或托盘菜单中选择“退出并停用临时字体”，然后点击确定继续，或取消退出。
chinesesimplified.UninstallAppRunningError=%1 仍在运行。请在程序或托盘菜单中选择“退出并停用临时字体”，然后点击确定继续，或取消退出。

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
chinesesimplified.DesktopShortcut=创建桌面快捷方式
english.LaunchApp=Launch FontCrate
chinesesimplified.LaunchApp=启动 FontCrate
english.DataNotice=FontCrate installs for the current user and includes the .NET runtime. Upgrades keep your font library and settings. Uninstalling removes application files and shortcuts only; your font library, settings, original fonts and user-installed fonts are kept.
chinesesimplified.DataNotice=FontCrate 仅安装到当前用户，已包含 .NET 运行时。升级会保留字体库和设置。卸载仅移除程序文件与快捷方式，字体库、设置、原始字体和已安装到当前用户的字体会保留。
english.AppRunning=FontCrate is still running. Use "Exit and deactivate temporary fonts" in FontCrate or its tray menu, then try again. Setup will not force the application to close.
chinesesimplified.AppRunning=FontCrate 仍在运行。请在程序或托盘菜单中选择“退出并停用临时字体”后重试。安装程序不会强制结束进程。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\FontCrate.exe"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Previous releases deployed these application files beside the EXE.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\*.md"
Type: files; Name: "{app}\FontCrate.deps.json"
Type: files; Name: "{app}\FontCrate.runtimeconfig.json"
Type: files; Name: "{app}\LICENSE"
Type: files; Name: "{app}\createdump.exe"

; Old framework satellite folders; current resources are inside the EXE.
Type: filesandordirs; Name: "{app}\cs"
Type: filesandordirs; Name: "{app}\de"
Type: filesandordirs; Name: "{app}\en"
Type: filesandordirs; Name: "{app}\en-US"
Type: filesandordirs; Name: "{app}\es"
Type: filesandordirs; Name: "{app}\fr"
Type: filesandordirs; Name: "{app}\it"
Type: filesandordirs; Name: "{app}\ja"
Type: filesandordirs; Name: "{app}\ko"
Type: filesandordirs; Name: "{app}\pl"
Type: filesandordirs; Name: "{app}\pt-BR"
Type: filesandordirs; Name: "{app}\ru"
Type: filesandordirs; Name: "{app}\tr"
Type: filesandordirs; Name: "{app}\zh-CN"
Type: filesandordirs; Name: "{app}\zh-TW"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"

[Icons]
Name: "{userprograms}\FontCrate"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\FontCrate"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Check again in case the application started after the initial AppMutex check. }
  if CheckForMutexes('Local\FontCrate-Running') then
    Result := CustomMessage('AppRunning')
  else
    Result := '';
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
  Result := Result + NewLine + NewLine + CustomMessage('DataNotice');
end;
