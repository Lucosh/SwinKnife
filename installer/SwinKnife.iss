; Installer di SwinKnife (Inno Setup 6).
; Di solito non si compila a mano: lo fa tools/release/build.ps1, che passa versione e cartella dell'app.
;   iscc installer\SwinKnife.iss /DAppVersion=0.4.0 /DSourceDir=<cartella del dotnet publish> /O<cartella di uscita>

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error Passa /DSourceDir=<cartella del dotnet publish>
#endif

#define AppName "SwinKnife"
#define AppExe "SwinKnife.exe"

[Setup]
; non cambiare AppId: è ciò che fa riconoscere gli aggiornamenti
AppId={{6F2B8E1C-4A1D-4C7B-9E5A-5D3C2B1A9F70}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Luca Pezzoli
AppPublisherURL=https://github.com/Lucosh/SwinKnife
AppSupportURL=https://github.com/Lucosh/SwinKnife/issues
VersionInfoVersion={#AppVersion}
; installazione per l'utente corrente (nessun UAC); chi vuole può scegliere "tutti gli utenti"
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\SwinKnife\Assets\swinknife.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; compressione in un processo separato a 64 bit: con Inno Setup 6 (32 bit) i ~200 MB dell'app esaurirebbero la memoria
LZMAUseSeparateProcess=x64
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=auto
UsePreviousLanguage=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
en.ShellMenu=Add SwinKnife to the Explorer right-click menu
it.ShellMenu=Aggiungi SwinKnife al menu del tasto destro di Esplora file
de.ShellMenu=SwinKnife zum Rechtsklickmenü des Explorers hinzufügen
es.ShellMenu=Añadir SwinKnife al menú contextual del Explorador
fr.ShellMenu=Ajouter SwinKnife au menu contextuel de l'Explorateur
en.RemoveData=Also delete SwinKnife settings, log and downloaded components (FFmpeg, 7-Zip)?
it.RemoveData=Eliminare anche impostazioni, log e componenti scaricati da SwinKnife (FFmpeg, 7-Zip)?
de.RemoveData=Auch die Einstellungen, das Protokoll und die heruntergeladenen Komponenten von SwinKnife (FFmpeg, 7-Zip) löschen?
es.RemoveData=¿Eliminar también la configuración, el registro y los componentes descargados por SwinKnife (FFmpeg, 7-Zip)?
fr.RemoveData=Supprimer aussi les paramètres, le journal et les composants téléchargés par SwinKnife (FFmpeg, 7-Zip) ?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "shellmenu"; Description: "{cm:ShellMenu}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--register-shell"; Tasks: shellmenu; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; aggiornamento automatico (installer avviato dall'app con /SILENT /RELAUNCH=1): riapre SwinKnife a fine installazione
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: Relaunch

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-shell"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterShell"

[Code]
function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Data := ExpandConstant('{localappdata}\SwinKnife');
  if not DirExists(Data) then Exit;
  if UninstallSilent then Exit;
  if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then Exit;
  DelTree(Data + '\ffmpeg', True, True, True);
  DelTree(Data + '\7zip', True, True, True);
  DelTree(Data + '\WebView2', True, True, True);
  DelTree(Data + '\tmp', True, True, True);
  DeleteFile(Data + '\settings.json');
  DeleteFile(Data + '\swinknife.log');
  RemoveDir(Data); { solo se è rimasta vuota }
end;
