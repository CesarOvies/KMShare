#define MyAppName "KMShare"
#define MyAppPublisher "Cesar Ovies"
#define MyAppURL "https://github.com/CesarOvies/KMShare"
#define MyAppExeName "KMShare.exe"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{D37E88BC-8C8E-45A0-9A15-052C9FBE41FA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=KMShare-v{#MyAppVersion}-win-x64-installer
SetupIconFile=..\..\assets\KMShare.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Iniciar o KMShare automaticamente com o Windows (User-Space)"; GroupDescription: "Opções de Inicialização:"

[Files]
Source: "..\..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\publish\win-x64\kmshare-updater.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "KMShare-CodeSigning.cer"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\assets\KMShare.ico"; DestDir: "{app}\assets"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --gui"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "certutil.exe"; Parameters: "-user -addstore Root ""{app}\KMShare-CodeSigning.cer"""; Flags: runhidden; StatusMsg: "Registrando certificado de assinatura confiável..."; Check: FileExists(ExpandConstant('{app}\KMShare-CodeSigning.cer'))
Filename: "certutil.exe"; Parameters: "-user -addstore TrustedPublisher ""{app}\KMShare-CodeSigning.cer"""; Flags: runhidden; StatusMsg: "Registrando editor confiável..."; Check: FileExists(ExpandConstant('{app}\KMShare-CodeSigning.cer'))
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent; Parameters: "--gui"

