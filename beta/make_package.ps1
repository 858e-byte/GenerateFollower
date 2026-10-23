# Paquet de GenerateFollower (application autonome, depuis la 1.0.0, ex-0.15) : un seul exe, sans Python ni outils livrés.
# Usage (depuis le dossier GenerateFollower) : powershell -ExecutionPolicy Bypass -File beta\make_package.ps1
# Sortie : work\publish\GenerateFollower\ (dossier) et dist\GenerateFollower_<version>.zip.
# Le dossier ne contient ni settings.json ni work\ : chez le joueur, tout est créé à côté de l'exe au premier lancement
# (pages du Builder, .pex et WebView2Loader.dll intégrés à l'exe, déposés dans work\app\).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$version = ([xml](Get-Content src\GenerateFollower.csproj)).Project.PropertyGroup.InformationalVersion | Where-Object { $_ } | Select-Object -First 1

# le .psc et son .pex doivent être à jour ensemble (les .pex intégrés à l'exe servent sans Creation Kit)
Get-ChildItem papyrus\*.psc | ForEach-Object {
    $pex = "papyrus\pex\$($_.BaseName).pex"
    if (-not (Test-Path $pex) -or (Get-Item $pex).LastWriteTime -lt $_.LastWriteTime) { throw "$pex absent ou plus ancien que sa source : faire un build avec le Creation Kit" }
}
# contenu intégré à l'exe (pages, .pex, config.json) : aucune information personnelle, vérifié avant la compression de l'exe
py -I beta\check_privacy.py builder $env:USERNAME $env:COMPUTERNAME (Split-Path $root -Parent)
if ($LASTEXITCODE -ne 0) { throw 'informations personnelles trouvées dans builder\ (voir ci-dessus)' }
py -I beta\check_privacy.py papyrus\pex $env:USERNAME $env:COMPUTERNAME (Split-Path $root -Parent)
if ($LASTEXITCODE -ne 0) { throw 'informations personnelles trouvées dans papyrus\pex (voir ci-dessus)' }

# 1. exe autonome en un fichier (runtime .NET compris), compressé (variante A : 0/68 sur VirusTotal, 23/10).
# IncludeNativeLibrariesForSelfExtract=false : pas d'extraction dans %TEMP% (la seule DLL native, WebView2Loader.dll, est
# intégrée et déposée par l'exe dans work\app\) ; DebugType=None : pas de chemin de compilation dans l'exe
$pub = 'work\publish\app'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $pub -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish a échoué' }

# 2. dossier de l'application : l'exe, les guides, les licences et le code source (GPL-3)
$app = 'work\publish\GenerateFollower'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory $app | Out-Null
Copy-Item "$pub\GenerateFollower.exe", beta\LISEZMOI.txt, beta\README.txt, beta\CHANGELOG.txt $app
if (Test-Path LICENSE.txt) { Copy-Item LICENSE.txt $app } else { throw 'LICENSE.txt (texte de la GPL-3) absent' }
Copy-Item beta\LICENCES_AUTONOME.md "$app\LICENCES.md"
# GPL-3 : code source livré avec l'application (fichiers texte seulement ; aussi sur GitHub)
$src = "$app\source"
robocopy src "$src\src" *.cs *.csproj *.ico /S /XD bin obj /NFL /NDL /NJH /NJS | Out-Null
robocopy builder "$src\builder" *.* /E /NFL /NDL /NJH /NJS | Out-Null
robocopy papyrus "$src\papyrus" *.psc /NFL /NDL /NJH /NJS | Out-Null
Copy-Item config.json, beta\make_package.ps1, beta\make_icon_gf.py $src
# aucune information personnelle dans le dossier : nom d'utilisateur, nom du PC, dossier du projet
py -I beta\check_privacy.py $app $env:USERNAME $env:COMPUTERNAME (Split-Path $root -Parent)
if ($LASTEXITCODE -ne 0) { throw 'informations personnelles trouvées dans le paquet (voir ci-dessus)' }

# 3. ZIP, chemins avec « / »
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$base = (Resolve-Path $app).Path
$top = Split-Path $base -Leaf
$zip = "dist\GenerateFollower_$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$files = Get-ChildItem $base -Recurse -File
$z = [IO.Compression.ZipFile]::Open((Join-Path $root $zip), 'Create')
try {
    foreach ($f in $files) {
        $name = "$top/" + $f.FullName.Substring($base.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, $name, 'Optimal') | Out-Null
    }
} finally { $z.Dispose() }
"Version $version : {0} fichiers, {1} Mo -> $zip ({2} Mo)" -f $files.Count, [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB), [math]::Round((Get-Item $zip).Length / 1MB)
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
