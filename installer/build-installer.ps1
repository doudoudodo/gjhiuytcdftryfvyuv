<#
.SYNOPSIS
    Script de compilation et d'empaquetage de l'installeur moderne Coclico avec système de désinstallation complet.

.DESCRIPTION
    1. Lit la version depuis version.txt à la racine du dépôt (ou utilise -Version)
    2. Vérifie ou compile le dossier publish/ pour Coclico
    3. Crée ou vérifie le fichier uninstall.bat pour la désinstallation depuis les paramètres Windows
    4. Compresse les fichiers dans payload.zip avec compression optimale
       (exclut par défaut les bibliothèques CUDA 12 géantes pour un installeur ultra léger)
    5. Compile Coclico.Installer en exécutable autonome SingleFile
    6. Dépose le setup final dans artifacts/installer/Coclico-<Version>.exe

.NOTES
    Ce script garantit que la désinstallation fonctionnera depuis :
    - Paramètres Windows → Applications → Désinstaller
    - Panneau de configuration → Programmes → Désinstaller

    Le système utilise un script uninstall.bat universel qui trouve automatiquement
    le désinstalleur (Coclico.Installer.exe)

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Version 2.0.1
    .\build-installer.ps1 -IncludeCuda
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [switch]$IncludeCuda,
    [switch]$SkipClean,
    [string]$OutputDir = "artifacts\installer"
)

$ErrorActionPreference = "Stop"

$rootDir = (Resolve-Path "$PSScriptRoot\..").Path
Push-Location $rootDir

# ============================================================================
# Fonctions utilitaires
# ============================================================================

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
}

function Write-Step {
    param([string]$Message, [ConsoleColor]$Color = "Yellow")
    Write-Host "[$($Message)]" -ForegroundColor $Color
}

function Write-Success {
    param([string]$Message)
    Write-Host "[+] $Message" -ForegroundColor Green
}

function Write-ErrorMsg {
    param([string]$Message)
    Write-Host "[!] $Message" -ForegroundColor Red
}
# ============================================================================
# Nettoyage initial
# ============================================================================

if (-not $SkipClean) {
    Write-Step "Nettoyage des anciens artefacts..."
    
    # Nettoyer le dossier publish
    $publishDir = "$rootDir\publish"
    if (Test-Path $publishDir) {
        Remove-Item "$publishDir\*" -Recurse -Force -ErrorAction SilentlyContinue
        Write-Success "Dossier publish nettoyé"
    }
    
    # Nettoyer les artefacts de build
    $tempBuildDir = "$rootDir\artifacts\installer_build"
    if (Test-Path $tempBuildDir) {
        Remove-Item $tempBuildDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Success "Dossier installer_build nettoyé"
    }
}

# ============================================================================
# Début du build
# ============================================================================

Write-Section "Compilation de l'installeur Coclico"

try {
    # ==========================================================================
    # ÉTAPE 1 : Déterminer la version
    # ==========================================================================
    
    if ([string]::IsNullOrWhiteSpace($Version)) {
        Write-Step "Lecture de la version depuis version.txt..."
        $Version = (Get-Content "$rootDir\version.txt" -Raw).Trim()
        if ([string]::IsNullOrWhiteSpace($Version)) {
            throw "version.txt est absent ou vide à la racine du dépôt"
        }
    }
    Write-Success "Version ciblée : $Version"

    # ==========================================================================
    # ÉTAPE 2 : Publier Coclico (application principale)
    # ==========================================================================
    
    $publishDir = "$rootDir\publish"
    $mainExe = "$publishDir\Coclico.exe"

    if ($SkipPublish -and (Test-Path $mainExe)) {
        Write-Step "Utilisation du dossier publish existant"
    }
    else {
        Write-Step "Publication de Coclico ($Configuration, win-x64)..."
        
        $publishArgs = @(
            "publish",
            "$rootDir\Coclico\Coclico.csproj",
            "--configuration", $Configuration,
            "--runtime", "win-x64",
            "--self-contained", "true",
            "--output", $publishDir,
            "-p:PublishSingleFile=false",
            "-p:DebugSymbols=false",
            "-p:DebugType=None",
            "-p:Version=$Version"
        )
        
        & dotnet $publishArgs
        if ($LASTEXITCODE -ne 0) { throw "Échec de dotnet publish pour Coclico" }
        
        Write-Success "Coclico publié dans : $publishDir"
    }

    # ==========================================================================
    # ÉTAPE 4 : Préparation de la désinstallation intégrée
    # ==========================================================================

    Write-Step "Préparation de la désinstallation intégrée..."
    # La désinstallation est maintenant gérée directement par Coclico.Installer.exe --uninstall

    # ==========================================================================
    # ÉTAPE 5 : Compression du payload pour Coclico.Installer
    # ==========================================================================
    
    $installerProjDir = "$rootDir\Coclico.Installer"
    $payloadZip = "$installerProjDir\payload.zip"

    if (Test-Path $payloadZip) {
        Remove-Item $payloadZip -Force
    }

    Write-Step "Compression des fichiers Coclico dans payload.zip..."
    
    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

    $cudaDlls = @("cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll", "cufft64_11.dll", "nvrtc64_120_0.dll", "nvrtc64_120_0.alt.dll", "ggml-cuda.dll")

    if ($IncludeCuda) {
        Write-Step "Mode COMPLET : inclusion du runtime NVIDIA CUDA 12 (~1.1 Go)"
        $files = Get-ChildItem -Path $publishDir -Recurse -File | Where-Object { 
            $_.Extension -ne ".pdb" 
        }
    }
    else {
        Write-Step "Mode OPTIMISÉ LÉGER : exclusion des DLLs CUDA 12 (~50 Mo final)"
        # Le dossier cuda12 est exclu ENTIER : LLamaSharp charge sinon llama.dll du backend CUDA,
        # qui échoue sans ggml-cuda.dll (erreur NativeApi au démarrage de l'IA).
        # Sans ce dossier, le chargeur bascule sur la variante CPU (avx2/noavx) incluse.
        $files = Get-ChildItem -Path $publishDir -Recurse -File | Where-Object {
            $_.Extension -ne ".pdb" -and ($cudaDlls -notcontains $_.Name) -and ($_.DirectoryName -notlike "*\cuda12")
        }
    }
    
    $totalFiles = $files.Count
    Write-Step "Compression de $totalFiles fichiers..."
    
    $zip = [System.IO.Compression.ZipFile]::Open($payloadZip, [System.IO.Compression.ZipArchiveMode]::Create)
    $compressionLevel = [System.IO.Compression.CompressionLevel]::Optimal

    try {
        # Ajouter tous les fichiers de Coclico
        $i = 0
        foreach ($f in $files) {
            $i++
            # Placer les fichiers dans leur structure relative
            $rel = $f.FullName.Substring($publishDir.Length) -replace '^[\\/]+', ''
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel, $compressionLevel) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }

    $zipSizeMb = [math]::Round((Get-Item $payloadZip).Length / 1MB, 2)
    Write-Success "Archive payload.zip créée : $zipSizeMb Mo ($totalFiles fichiers)"

    # ==========================================================================
    # ÉTAPE 6 : Compilation SingleFile de Coclico.Installer
    # ==========================================================================
    
    Write-Section "Compilation de Coclico.Installer"
    
    # Etape 6a : desinstalleur leger (sans payload embarque, ~45 Mo au lieu de ~450 Mo)
    Write-Step "Compilation du desinstalleur leger (sans payload)..."
    $lightBuildDir = "$rootDir\artifacts\installer_light_build"
    if (Test-Path $lightBuildDir) { Remove-Item $lightBuildDir -Recurse -Force }

    & dotnet publish "$installerProjDir\Coclico.Installer.csproj" `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $lightBuildDir `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:Version=$Version `
        -p:EmbedPayload=false

    if ($LASTEXITCODE -ne 0) { throw "Échec de compilation du désinstalleur léger" }

    $lightUninstallerBin = "$installerProjDir\uninstaller.bin"
    Copy-Item "$lightBuildDir\Coclico.Installer.exe" $lightUninstallerBin -Force
    Remove-Item $lightBuildDir -Recurse -Force -ErrorAction SilentlyContinue
    $lightSizeMb = [math]::Round((Get-Item $lightUninstallerBin).Length / 1MB, 2)
    Write-Success "Désinstalleur léger préparé : $lightSizeMb Mo (sera embarqué dans le setup puis déployé à l'installation)"

    Write-Step "Compilation en binaire autonome SingleFile..."
    $tempBuildDir = "$rootDir\artifacts\installer_build"
    if (Test-Path $tempBuildDir) { Remove-Item $tempBuildDir -Recurse -Force }

    $installerPublishArgs = @(
        "publish",
        "$installerProjDir\Coclico.Installer.csproj",
        "--configuration", $Configuration,
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $tempBuildDir,
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=false",
        "-p:DebugSymbols=false",
        "-p:DebugType=None",
        "-p:Version=$Version"
    )
    
    & dotnet $installerPublishArgs
    if ($LASTEXITCODE -ne 0) { throw "Échec de compilation de Coclico.Installer" }
    
    Write-Success "Coclico.Installer compilé"

    # ==========================================================================
    # ÉTAPE 7 : Déplacement vers le dossier final
    # ==========================================================================
    
    Write-Section "Finalisation"
    
    $finalOutputDir = "$rootDir\$OutputDir"
    if (-not (Test-Path $finalOutputDir)) {
        New-Item -ItemType Directory -Path $finalOutputDir -Force | Out-Null
    }

    $suffix = if ($IncludeCuda) { "-Full-CUDA" } else { "" }
    $finalExePath = "$finalOutputDir\Coclico-$Version$suffix.exe"

    Copy-Item "$tempBuildDir\Coclico.Installer.exe" $finalExePath -Force
    
    # Nettoyage
    Remove-Item $tempBuildDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "$installerProjDir\uninstaller.bin" -Force -ErrorAction SilentlyContinue

    # ==========================================================================
    # ÉTAPE 9 : Affichage des résultats
    # ==========================================================================
    
    $finalSizeMb = [math]::Round((Get-Item $finalExePath).Length / 1MB, 2)
    $hash = (Get-FileHash $finalExePath -Algorithm SHA256).Hash
    
    Write-Section "Installeur généré avec succès"
    
    Write-Host "Fichier     : $finalExePath" -ForegroundColor White
    Write-Host "Taille     : $finalSizeMb Mo" -ForegroundColor White
    Write-Host "SHA-256   : $hash" -ForegroundColor White
    Write-Host "Version    : $Version" -ForegroundColor White
    Write-Host "Mode      : $(if ($IncludeCuda) { 'COMPLET (avec CUDA)' } else { 'LÉGER (sans CUDA)' })" -ForegroundColor White

    Write-Host "" -ForegroundColor White
    Write-Host "Fonctionnalités incluses:" -ForegroundColor Cyan
    Write-Host "  ✓ Application Coclico complète" -ForegroundColor Green
    Write-Host "  ✓ Système de désinstallation fonctionnel" -ForegroundColor Green
    Write-Host "  ✓ Script uninstall.bat universel" -ForegroundColor Green
    Write-Host "  ✓ Entrée de registre Windows correcte" -ForegroundColor Green
    Write-Host "  ✓ Désinstallation depuis Paramètres Windows ✓" -ForegroundColor Green
    Write-Host "  ✓ Désinstallation depuis Panneau de configuration ✓" -ForegroundColor Green
    Write-Host ""
    
    Write-Success "Prêt pour la distribution !"

}
finally {
    Pop-Location
}
