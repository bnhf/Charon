#define MyAppName "Charon"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "Charon"
#define MyAppExeName "Charon.exe"

[Setup]
AppId={{8F4E9A12-3C7D-4B5E-9A21-6E8F7C4D3B2A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
SetupIconFile=src\Charon\Charon.ico
AppPublisher={#MyAppPublisher}
; Install directly to C:\Charon so config.json, Profiles\, and Providers\channels.json are freely writable
DefaultDirName=C:\{#MyAppName}
DisableDirPage=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=userdocs:..\Desktop
OutputBaseFilename=Charon-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Start Charon automatically when Windows starts"; GroupDescription: "Server Options:"; Flags: unchecked

[Files]
; 1. Copy all published .NET 11 runtime files, Charon.exe, and ffmpeg.exe (exclude Profiles, config.json, and channels.json from unconditional overwrite)
Source: "C:\Charon\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Profiles\*,config.json,channels.json,LOCK*,Singleton*"



[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: startup

[Run]
; Open Windows Firewall TCP port 48590 so Channels DVR can connect over the LAN
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Charon Streaming Server"""; Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Charon Streaming Server"" dir=in action=allow protocol=TCP localport=48590"; Flags: runhidden

; Option to launch Charon at the end of installation
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent