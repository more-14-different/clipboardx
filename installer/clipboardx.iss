; ClipboardX Inno Setup Script
; 框架依赖（默认）:
;   iscc /DAppVersion=1.2.0 /DPublishDir=..\publish\fdd clipboardx.iss
;   → ClipboardX-{version}-setup.exe，安装前检测 .NET 8 桌面运行时。
; 自包含:
;   iscc /DAppVersion=1.2.0 /DPublishDir=..\publish\sc /DSETUP_SKIP_DOTNET /DSetupOutputSuffix=-setup-self-contained clipboardx.iss
;   → ClipboardX-{version}-setup-self-contained.exe，不检测运行时。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\fdd"
#endif
#ifndef SetupOutputSuffix
  #define SetupOutputSuffix "-setup"
#endif

[Setup]
AppId={{E2F6D4A8-7B1C-4D3E-9F0A-5C8B2E1D6A7F}
AppName=ClipboardX
AppVersion={#AppVersion}
AppVerName=ClipboardX {#AppVersion}
AppPublisher=ClipboardX
AppPublisherURL=https://github.com/chaojimct/clipboardx
DefaultDirName={userpf}\ClipboardX
DefaultGroupName=ClipboardX
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputBaseFilename=ClipboardX-{#AppVersion}{#SetupOutputSuffix}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\ClipboardX.exe
SetupIconFile=..\assets\clipboard.ico
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "runonstartup"; Description: "开机自动启动"; GroupDescription: "其他选项:"; Flags: checked

[Files]
Source: "{#PublishDir}\ClipboardX.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\ClipboardX"; Filename: "{app}\ClipboardX.exe"
Name: "{group}\卸载 ClipboardX"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ClipboardX"; Filename: "{app}\ClipboardX.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ClipboardX.exe"; Description: "启动 ClipboardX"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ClipboardX"; ValueData: """{app}\ClipboardX.exe"""; Tasks: runonstartup; Flags: uninsdeletevalue

[UninstallRun]
Filename: "taskkill"; Parameters: "/f /im ClipboardX.exe"; Flags: runhidden; RunOnceId: "KillClipboardX"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

#ifdef SETUP_SKIP_DOTNET
[Code]

function InitializeSetup: Boolean;
begin
  Result := True;
end;

#else
[Code]

function HasNet8DesktopUnderRoot(Root: Integer; const SubKey: string): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if not RegGetSubkeyNames(Root, SubKey, Names) then
    Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    if Pos('8.', Names[I]) = 1 then
    begin
      Result := True;
      Exit;
    end;
end;

function IsDotNet8DesktopRuntimeInstalled: Boolean;
const
  SubKey = 'Software\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
begin
  Result := HasNet8DesktopUnderRoot(HKLM64, SubKey) or HasNet8DesktopUnderRoot(HKCU64, SubKey);
end;

function InitializeSetup: Boolean;
var
  ErrCode: Integer;
begin
  Result := True;
  if IsDotNet8DesktopRuntimeInstalled then
    Exit;
  if MsgBox(
    '未检测到本机已安装 .NET 8 桌面运行时（Microsoft.WindowsDesktop.App，x64）。' + #13#10 + #13#10 +
    'ClipboardX 为 WPF 程序，须先安装该运行时后才能运行。' + #13#10 + #13#10 +
    '是否打开 Microsoft 官方下载页？（请安装 “.NET Desktop Runtime” Windows x64 8.x，完成后请重新运行本安装程序。）',
    mbConfirmation, MB_YESNO) = IDYES then
    ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
  Result := False;
end;

#endif
