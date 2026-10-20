# Paquet de bêta de GenerateFollower : publie l'exe autonome, assemble le dossier de l'application et le zippe.
# Usage (depuis le dossier GenerateFollower) : powershell -ExecutionPolicy Bypass -File beta\make_package.ps1
# Sortie : work\publish\GenerateFollower\ (dossier) et dist\GenerateFollower_<version>.zip
# Le dossier ne contient ni settings.json ni work\ : chez le joueur, réglages dans %LOCALAPPDATA%\GenerateFollower.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$version = ([xml](Get-Content src\GenerateFollower.csproj)).Project.PropertyGroup.InformationalVersion | Where-Object { $_ } | Select-Object -First 1

# 1. exe autonome en un fichier (runtime .NET compris)
$pub = 'work\publish\app'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
# DebugType=None : pas de chemin de compilation (dossier du projet) inscrit dans l'exe
dotnet publish src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $pub -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish a échoué' }

# 2. dossier de l'application
$app = 'work\publish\GenerateFollower'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory $app | Out-Null
Copy-Item "$pub\GenerateFollower.exe", config.json, beta\LISEZMOI_BETA.txt, beta\README_BETA.txt, beta\CHANGELOG.txt $app
function Copy-Dir($from, $to, [string[]]$files = @('*.*')) {
    robocopy $from $to $files /E /XD __pycache__ tests /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $from a échoué ($LASTEXITCODE)" }
}
Copy-Dir builder "$app\builder"
robocopy offline "$app\offline" *.py /NFL /NDL /NJH /NJS | Out-Null
Copy-Dir offline\pylib "$app\offline\pylib"
robocopy papyrus "$app\papyrus" *.psc /NFL /NDL /NJH /NJS | Out-Null
Copy-Dir papyrus\pex "$app\papyrus\pex"
foreach ($d in 'python', 'pynifly', '7zip', 'texconv') { Copy-Dir "deps\$d" "$app\deps\$d" }
# Python allégé : exécutables et modules inutilisés par nos scripts (moins de fichiers à faire vérifier par Nexus) ;
# libcrypto gardé (hashlib), libffi gardé (ctypes, PyNifly)
foreach ($x in 'pythonw.exe', 'Lib\venv', 'Lib\sqlite3', 'DLLs\_sqlite3.pyd', 'DLLs\sqlite3.dll', 'DLLs\_ssl.pyd', 'DLLs\libssl-3.dll',
               'DLLs\libtommath.dll') {
    $p = Join-Path "$app\deps\python" $x
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
Copy-Item deps\LICENCES.md "$app\deps"
# GPL-3 : code source livré avec l'application (le reste des sources est déjà là : builder\, offline\, papyrus\)
New-Item -ItemType Directory "$app\source" | Out-Null
robocopy src "$app\source\src" *.cs *.csproj /S /XD bin obj /NFL /NDL /NJH /NJS | Out-Null
# pas README.md : documentation de développement, avec les chemins locaux de l'auteur
Copy-Item beta\make_package.ps1 "$app\source"
if (Test-Path LICENSE.txt) { Copy-Item LICENSE.txt $app } else { Write-Warning 'LICENSE.txt (texte de la GPL-3) absent : à ajouter avant une publication' }
# le .psc et son .pex doivent être à jour ensemble (le .pex livré sert sans Creation Kit)
Get-ChildItem papyrus\*.psc | ForEach-Object {
    $pex = "papyrus\pex\$($_.BaseName).pex"
    if (-not (Test-Path $pex) -or (Get-Item $pex).LastWriteTime -lt $_.LastWriteTime) { throw "$pex absent ou plus ancien que sa source : faire un build avec le Creation Kit" }
}

# .pex sans nom d'utilisateur ni nom de PC (inscrits par le compilateur Papyrus)
py -I offline\sanitize_pex.py "$app\papyrus\pex" | Out-Null
# aucune information personnelle dans le paquet : nom d'utilisateur, nom du PC, dossier du projet
py -I beta\check_privacy.py $app $env:USERNAME $env:COMPUTERNAME (Split-Path $root -Parent)
if ($LASTEXITCODE -ne 0) { throw 'informations personnelles trouvées dans le paquet (voir ci-dessus)' }

# 3. ZIP
$zip = "dist\GenerateFollower_$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $app), (Join-Path $root $zip), 'Optimal', $true)
$files = Get-ChildItem $app -Recurse -File
"Version $version : {0} fichiers, {1} Mo -> $zip ({2} Mo)" -f $files.Count, [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB), [math]::Round((Get-Item $zip).Length / 1MB)
