; JustSlides - script Inno Setup 6. Non si compila a mano: usare tools\Build-Installer.ps1,
; che prepara la cartella di staging, scarica il runtime e passa AppVersion/StagingDir/RuntimeFile/OutDir.

#ifndef AppVersion
  #error AppVersion non definita: usare tools\Build-Installer.ps1
#endif
#ifndef StagingDir
  #error StagingDir non definita: usare tools\Build-Installer.ps1
#endif
#ifndef RuntimeFile
  #error RuntimeFile non definita: usare tools\Build-Installer.ps1
#endif
#ifndef OutDir
  #define OutDir "..\artifacts"
#endif

#define AppName "JustSlides"
#define AppExe "JustSlides.exe"
#define HostExe "JustSlides.PptHost.exe"

[Setup]
; GUID fisso: identifica l'app per aggiornamenti e disinstallazione. NON cambiarlo mai.
AppId={{B7E2A6C4-5D1F-4C8A-9E3B-2F6A81D0C7A5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=JustSlides
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Installazione di {#AppName}
; Per utente corrente senza admin; a scelta "per tutti gli utenti" (con UAC)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
SetupIconFile=..\src\Regia.Output\Assets\JustSlides.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir={#OutDir}
OutputBaseFilename=JustSlides-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Il mutex dell'istanza singola dell'app (AppInfo.MutexName): Setup rifiuta di procedere se la regia e' aperta
AppMutex=JustSlides.SingleInstance,Local\JustSlides.SingleInstance
; Niente chiusura automatica: mai terminare processi, si chiede all'utente di chiudere
CloseApplications=no
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "Crea un collegamento sul &Desktop"; GroupDescription: "Collegamenti:"

[InstallDelete]
; Aggiornamento pulito: via i file della versione precedente (solo dentro la cartella di installazione, mai i dati)
Type: filesandordirs; Name: "{app}\libvlc"
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"
Type: files; Name: "{app}\*.pdb"

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Runtime .NET Desktop: estratto solo se serve (vedi NeedsRuntime), poi cancellato
Source: "{#RuntimeFile}"; DestDir: "{tmp}"; DestName: "windowsdesktop-runtime-win-x64.exe"; Flags: deleteafterinstall dontcopy

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Disinstalla {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Avvia {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Cerca Microsoft.WindowsDesktop.App 10.x nelle posizioni standard del runtime x64.
function DesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
  Base: String;
begin
  Result := False;
  Base := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\');
  if FindFirst(Base + '10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Un exe in esecuzione non si puo' rinominare: se il rinomina fallisce, il processo e' ancora vivo.
function FileInUse(const FileName: String): Boolean;
var
  Probe: String;
begin
  Result := False;
  if not FileExists(FileName) then Exit;
  Probe := FileName + '.chk';
  if RenameFile(FileName, Probe) then
    RenameFile(Probe, FileName)
  else
    Result := True;
end;

function AppFilesInUse(const Dir: String): Boolean;
begin
  Result := FileInUse(Dir + '\{#AppExe}') or FileInUse(Dir + '\{#HostExe}');
end;

function InstallRuntime: String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  ExtractTemporaryFile('windowsdesktop-runtime-win-x64.exe');
  Exe := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
  WizardForm.StatusLabel.Caption := 'Installazione del runtime .NET 10 (puo'' chiedere l''autorizzazione di amministratore)...';
  // 'runas' = richiesta UAC solo per il runtime; JustSlides resta un'installazione per utente
  if not ShellExec('runas', Exe, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := 'Impossibile avviare l''installazione del runtime .NET 10 (autorizzazione negata?).'
  else if (Code <> 0) and (Code <> 3010) and (Code <> 1638) then
    Result := 'L''installazione del runtime .NET 10 non e'' riuscita (codice ' + IntToStr(Code) + ').'
  else if not DesktopRuntimeInstalled then
    Result := 'Il runtime .NET 10 non risulta installato dopo l''installazione.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dir: String;
begin
  Result := '';
  // Chiusura garbata: solo avviso, mai terminare processi
  Dir := WizardDirValue;
  if AppFilesInUse(Dir) then
  begin
    Result := 'JustSlides (o JustSlides.PptHost) e'' in esecuzione. Chiudere JustSlides e riprovare.';
    Exit;
  end;
  if not DesktopRuntimeInstalled then
    Result := InstallRuntime;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if AppFilesInUse(ExpandConstant('{app}')) then
  begin
    if not UninstallSilent then
      MsgBox('JustSlides (o JustSlides.PptHost) e'' in esecuzione. Chiudere JustSlides e rilanciare la disinstallazione.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{localappdata}\JustSlides');
    // Disinstallazione silenziosa: i dati dell'evento restano sempre
    if (not UninstallSilent) and DirExists(Data) then
    begin
      if MsgBox('Eliminare anche i dati di JustSlides (evento, impostazioni, copie dei file, log)?' + #13#10 + #13#10 +
                Data + #13#10 + #13#10 + 'Scegliendo "No" restano e saranno usati da una futura installazione.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
    end;
  end;
end;
