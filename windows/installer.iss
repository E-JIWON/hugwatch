; HugWatch 윈도우 설치 프로그램 (Inno Setup) — CI 가 HugWatch-Setup.exe 로 만듦
; 관리자 권한 없이 내 계정에만 설치 (%LOCALAPPDATA%\Programs\HugWatch)

#define AppVersion GetEnv("HUGWATCH_VERSION")
#if AppVersion == ""
  #define AppVersion "0.1.5"
#endif

[Setup]
AppId={{5D955809-CB42-4760-81CA-C87F88EE788A}
AppName=HugWatch
AppVersion={#AppVersion}
AppPublisher=E-JIWON
AppPublisherURL=https://github.com/E-JIWON/hugwatch
DefaultDirName={localappdata}\Programs\HugWatch
DefaultGroupName=HugWatch
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\out
OutputBaseFilename=HugWatch-Setup
SetupIconFile=HugWatch.ico
UninstallDisplayIcon={app}\HugWatch.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
#if FileExists(AddBackslash(CompilerPath) + "Languages\Korean.isl")
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
#else
Name: "en"; MessagesFile: "compiler:Default.isl"
#endif

[Tasks]
Name: "autostart"; Description: "Windows 시작할 때 자동 실행"; Flags: unchecked
Name: "desktopicon"; Description: "바탕 화면 바로가기"; Flags: unchecked

[Files]
Source: "..\out\HugWatch.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\HugWatch"; Filename: "{app}\HugWatch.exe"
Name: "{group}\HugWatch 제거"; Filename: "{uninstallexe}"
Name: "{userdesktop}\HugWatch"; Filename: "{app}\HugWatch.exe"; Tasks: desktopicon

[Registry]
; 앱 설정 창의 '자동 실행' 과 같은 자리
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "HugWatch"; ValueData: """{app}\HugWatch.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\HugWatch.exe"; Description: "HugWatch 실행"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM HugWatch.exe /F"; Flags: runhidden; RunOnceId: "KillHugWatch"

[UninstallDelete]
; 자동 실행을 앱 설정 창에서 켰던 경우도 지움
Type: files; Name: "{app}\selftest.log"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'HugWatch');
end;
