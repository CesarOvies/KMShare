param(
    [string]$PublishDir = "$PSScriptRoot\..\..\publish\osx-arm64",
    [string]$OutputDir = "$PSScriptRoot\..\..\dist",
    [string]$Version = "1.0.1"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PublishDir)) {
    throw "Diretório de publicação osx-arm64 não encontrado: $PublishDir"
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$appZipName = "KMShare-v$Version-osx-arm64-app.zip"
$appZipPath = Join-Path $OutputDir $appZipName

Write-Host "=== Construindo Pacote macOS App Bundle ($appZipName) ===" -ForegroundColor Cyan

$tempStage = Join-Path ([System.IO.Path]::GetTempPath()) "kmshare_macos_$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempStage -Force | Out-Null

try {
    $bundleDir = Join-Path $tempStage "KMShare.app"
    $macOsDir = Join-Path $bundleDir "Contents\MacOS"
    $resDir = Join-Path $bundleDir "Contents\Resources"

    New-Item -ItemType Directory -Path $macOsDir -Force | Out-Null
    New-Item -ItemType Directory -Path $resDir -Force | Out-Null

    # Copiar binários
    Copy-Item (Join-Path $PublishDir "KMShare") (Join-Path $macOsDir "KMShare") -Force
    if (Test-Path (Join-Path $PublishDir "kmshare-updater")) {
        Copy-Item (Join-Path $PublishDir "kmshare-updater") (Join-Path $macOsDir "kmshare-updater") -Force
    }

    # Copiar Info.plist com versão
    $plistTemplate = Get-Content (Join-Path $PSScriptRoot "Info.plist") -Raw
    $plistUpdated = $plistTemplate.Replace("1.0.0", $Version)
    [System.IO.File]::WriteAllText((Join-Path $bundleDir "Contents\Info.plist"), $plistUpdated, [System.Text.Encoding]::UTF8)

    # Copiar ícone
    $iconPng = Join-Path $PSScriptRoot "..\..\assets\logo.png"
    if (Test-Path $iconPng) {
        Copy-Item $iconPng (Join-Path $resDir "KMShare.png") -Force
    }

    # Copiar instalador script e docs para o pacote
    Copy-Item (Join-Path $PSScriptRoot "KMShare-installer.sh") (Join-Path $tempStage "KMShare-installer.sh") -Force
    Copy-Item (Join-Path $PSScriptRoot "..\..\README.md") (Join-Path $tempStage "README.md") -Force
    Copy-Item (Join-Path $PSScriptRoot "..\..\LICENSE") (Join-Path $tempStage "LICENSE") -Force

    # Compactar ZIP do App Bundle macOS
    Compress-Archive -Path "$tempStage\*" -DestinationPath $appZipPath -Force
    Write-Host "  -> Pacote macOS App Bundle gerado com sucesso: $appZipPath" -ForegroundColor Green
} finally {
    if (Test-Path $tempStage) {
        Remove-Item -Path $tempStage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

