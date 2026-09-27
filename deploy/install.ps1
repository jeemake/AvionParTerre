# Installe le plugin « Avion par terre » pour Revit (2025 par défaut).
# Usage : powershell -ExecutionPolicy Bypass -File deploy\install.ps1 [-RevitVersion 2025]
# Revit doit être redémarré pour charger (ou recharger) le plugin.
param(
    [string]$RevitVersion = "2025",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\AvionParTerre.Revit\AvionParTerre.Revit.csproj"

Write-Host "Compilation ($Configuration, Revit $RevitVersion)..."
dotnet build $project -c $Configuration -p:RevitVersion=$RevitVersion -clp:NoSummary
if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation." }

$bin = Join-Path $root "src\AvionParTerre.Revit\bin\$Configuration\net8.0-windows"
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$target = Join-Path $addins "AvionParTerre"

if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    Write-Warning "Revit est ouvert : si le plugin est déjà chargé, ses fichiers sont verrouillés. Fermer Revit puis relancer ce script."
}
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -Path (Join-Path $bin "*") -Destination $target -Recurse -Force
Copy-Item -Path (Join-Path $PSScriptRoot "AvionParTerre.addin") -Destination $addins -Force
Write-Host "Installé dans $target"
Write-Host "Redémarrer Revit $RevitVersion : onglet « Avion par terre »."
