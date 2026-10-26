<#
  Construit une release complète de TaskbarVisu dans build\, en une commande :
    build\portable\TaskbarVisu.exe               exe autonome (un seul fichier, .NET inclus)
    build\TaskbarVisu-x.y.z-portable.zip         exe + portable.txt + LISEZMOI.txt + LICENSE.txt
    build\TaskbarVisu-x.y.z-setup.exe            installeur Inno Setup 6
    build\SHA256SUMS.txt                         empreintes des deux fichiers à publier
    build\winget\manifests\...                   manifestes winget prêts à soumettre (à la main)
  La version est lue dans src\TaskbarVisu\TaskbarVisu.csproj. Rien n'est publié : ni push, ni release GitHub.

  Usage : powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 [-SkipInstaller]
#>
param([switch]$SkipInstaller)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\TaskbarVisu\TaskbarVisu.csproj'
$build = Join-Path $root 'build'
$repo = '26mataa/TaskbarVisu'

$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Version introuvable dans $project" }
Write-Host "TaskbarVisu $version" -ForegroundColor Cyan

# Un exe lancé depuis build\ verrouillerait les fichiers à remplacer.
$running = Get-Process TaskbarVisu -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($build, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "TaskbarVisu tourne depuis $($running[0].Path) : quittez-le d'abord." }

function Write-Utf8([string]$path, [string]$text, [bool]$bom) {
    [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object Text.UTF8Encoding $bom))
}

# --- 1. Exe autonome
$portable = Join-Path $build 'portable'
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
Write-Host '1/4  dotnet publish (autonome, un seul fichier)…'
# Sans compression : un exe compressé décompresse tout .NET en mémoire au démarrage (+80 Mo de mémoire
# privée, mesuré). Non compressé, les assemblys sont lues directement dans le fichier ; le zip et
# l'installeur le compressent de toute façon pour le téléchargement.
& dotnet publish $project -c Release -r win-x64 --self-contained true -nologo -v q `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -o $portable
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish a échoué' }
$exe = Join-Path $portable 'TaskbarVisu.exe'

# --- 2. Zip portable : portable.txt garde les réglages à côté de l'exe (clé USB).
Write-Host '2/4  zip portable…'
$stage = Join-Path $build 'zip'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item $exe $stage
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Write-Utf8 (Join-Path $stage 'portable.txt') @'
Ce fichier active le mode portable : réglages et journal dans le dossier « data » à côté de TaskbarVisu.exe.
This file enables portable mode: settings and log go to the "data" folder next to TaskbarVisu.exe.
'@ $true
Write-Utf8 (Join-Path $stage 'LISEZMOI.txt') @"
TaskbarVisu $version - version portable / portable version

FR : lancez TaskbarVisu.exe, rien à installer.
     Grâce à portable.txt, les réglages restent dans le dossier « data » à côté de l'exe
     (pratique sur une clé USB). Supprimez portable.txt pour les ranger dans %APPDATA%\TaskbarVisu.
     Les mises à jour ouvrent la page de téléchargement.

EN : run TaskbarVisu.exe, nothing to install.
     Thanks to portable.txt, the settings stay in the "data" folder next to the exe
     (handy on a USB stick). Delete portable.txt to store them in %APPDATA%\TaskbarVisu.
     Updates open the download page.

https://github.com/$repo - GPL-3.0, voir / see LICENSE.txt
"@ $true
$zip = Join-Path $build "TaskbarVisu-$version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

# --- 3. Installeur
$setup = Join-Path $build "TaskbarVisu-$version-setup.exe"
if ($SkipInstaller) {
    Write-Host '3/4  installeur ignoré (-SkipInstaller)'
} else {
    Write-Host '3/4  installeur Inno Setup…'
    $iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw 'ISCC.exe introuvable : installez Inno Setup 6 (ou relancez avec -SkipInstaller).' }
    & $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\TaskbarVisu.iss')
    if ($LASTEXITCODE -ne 0) { throw 'ISCC a échoué' }
}

# --- 4. Empreintes et manifestes winget
Write-Host '4/4  empreintes et manifestes winget…'
$files = @($zip) + @(if (Test-Path $setup) { $setup })
$sums = foreach ($f in $files) { '{0}  {1}' -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $f -Leaf) }
Write-Utf8 (Join-Path $build 'SHA256SUMS.txt') (($sums -join "`n") + "`n") $false

if (Test-Path $setup) {
    $id = '26mataa.TaskbarVisu'
    $schema = '1.9.0'
    $dir = Join-Path $build "winget\manifests\2\26mataa\TaskbarVisu\$version"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory $dir -Force | Out-Null
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
    $date = Get-Date -Format 'yyyy-MM-dd'

    Write-Utf8 (Join-Path $dir "$id.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ $false

    Write-Utf8 (Join-Path $dir "$id.installer.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
MinimumOSVersion: 10.0.22000.0
InstallerType: inno
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
ProductCode: '{6B0E3F52-6D2A-4C1B-9E7A-2F1C9A7D5E10}_is1'
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: https://github.com/$repo/releases/download/v$version/TaskbarVisu-$version-setup.exe
  InstallerSha256: $hash
ManifestType: installer
ManifestVersion: $schema
"@ $false

    Write-Utf8 (Join-Path $dir "$id.locale.en-US.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: 26mataa
PublisherUrl: https://github.com/26mataa
PublisherSupportUrl: https://github.com/$repo/issues
PackageName: TaskbarVisu
PackageUrl: https://github.com/$repo
License: GPL-3.0
LicenseUrl: https://github.com/$repo/blob/main/LICENSE
ShortDescription: Experimental audio visualizers living inside the Windows 11 taskbar.
Description: |-
  TaskbarVisu draws audio visualizers in the empty space of the Windows 11 taskbar:
  spectrogram, spectrum, waveform, peak/LUFS and VU meters, stereo imager, tuner and key detection,
  piano roll, XY oscilloscope and more. Dot-matrix / y2k look, per-app audio capture, multi-screen.
Moniker: taskbarvisu
Tags:
- audio
- visualizer
- spectrum
- taskbar
- music
- vu-meter
ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$version
ManifestType: defaultLocale
ManifestVersion: $schema
"@ $false

    Write-Utf8 (Join-Path $dir "$id.locale.fr-FR.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: fr-FR
Publisher: 26mataa
PackageName: TaskbarVisu
License: GPL-3.0
ShortDescription: Visualiseurs audio expérimentaux dans la barre des tâches de Windows 11.
ManifestType: locale
ManifestVersion: $schema
"@ $false
}

Write-Host ''
Write-Host 'Terminé :' -ForegroundColor Green
Get-ChildItem $build -File | Where-Object { $_.Name -like "*$version*" -or $_.Name -eq 'SHA256SUMS.txt' } |
    ForEach-Object { '  {0,-40} {1,8:N1} Mo' -f $_.Name, ($_.Length / 1MB) }
if (Test-Path $setup) { "  winget : $(Join-Path $build "winget\manifests\2\26mataa\TaskbarVisu\$version")" }
Write-Host 'Rien n''a été publié. Après les tests : tag v' -NoNewline; Write-Host $version -NoNewline; Write-Host ', release GitHub avec le zip et le setup, puis soumission winget.'
