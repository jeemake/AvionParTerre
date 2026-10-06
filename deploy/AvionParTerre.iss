; Installeur Windows « Avion par terre » (Inno Setup 6).
; Installation par utilisateur, sans droits administrateur, dans
; %APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\ ; désinstallation par « Applications installées ».
; Compilé par .github/workflows/installer.yml ; en local, après `dotnet build -c Release` :
;   iscc /DAppVersion=0.3.0 deploy\AvionParTerre.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef RevitVersion
  #define RevitVersion "2025"
#endif
#ifndef BinDir
  #define BinDir "..\src\AvionParTerre.Revit\bin\Release\net8.0-windows"
#endif

[Setup]
AppId={{8F3D2C71-5B4A-4E2B-9C1D-7A6E5F4B3C21}_{#RevitVersion}
AppName=Avion par terre pour Revit {#RevitVersion}
AppVersion={#AppVersion}
AppVerName=Avion par terre {#AppVersion} (Revit {#RevitVersion})
AppPublisher=Koffi & Diabaté Architectes — Cellule IA
DefaultDirName={userappdata}\Autodesk\Revit\Addins\{#RevitVersion}\AvionParTerre
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=AvionParTerre-{#AppVersion}-Revit{#RevitVersion}-Setup
SetupIconFile=avion.ico
UninstallDisplayIcon={app}\avion.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[InstallDelete]
; Anciennes versions (y compris installées par deploy\install.ps1) : aucun fichier périmé ne reste chargé.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#BinDir}\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "avion.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "AvionParTerre.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\{#RevitVersion}"; Flags: ignoreversion

[Messages]
fr.FinishedLabelNoIcons=Avion par terre est installé. Au prochain démarrage de Revit {#RevitVersion}, l'onglet « Avion par terre » apparaît dans le ruban.

[Code]
function IsRevitRunning: Boolean;
var
  Wmi, Procs: Variant;
begin
  Result := False;
  try
    Wmi := CreateOleObject('WbemScripting.SWbemLocator').ConnectServer('.', 'root\CIMV2');
    Procs := Wmi.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''Revit.exe''');
    Result := Procs.Count > 0;
  except
  end;
end;

{ Revit verrouille le plugin chargé : on attend sa fermeture plutôt que de laisser une copie partielle. }
function WaitForRevitClosed: Boolean;
begin
  Result := True;
  while IsRevitRunning do
    if SuppressibleMsgBox('Revit est ouvert. Enregistrez votre travail, fermez Revit, puis cliquez sur OK.' + #13#10 +
                          'Annuler interrompt l''opération.', mbInformation, MB_OKCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;

function InitializeSetup: Boolean;
begin
  Result := WaitForRevitClosed;
end;

function InitializeUninstall: Boolean;
begin
  Result := WaitForRevitClosed;
end;
