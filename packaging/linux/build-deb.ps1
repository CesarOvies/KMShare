param(
    [string]$PublishDir = "$PSScriptRoot\..\..\publish\linux-x64",
    [string]$OutputDir = "$PSScriptRoot\..\..\dist",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PublishDir)) {
    throw "Diretório de publicação linux-x64 não encontrado: $PublishDir"
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$debPackageName = "kmshare"
$debFileName = "KMShare-v$Version-linux-x64.deb"
$debFilePath = Join-Path $OutputDir $debFileName

Write-Host "=== Construindo Pacote Debian ($debFileName) ===" -ForegroundColor Cyan

$tempStage = Join-Path ([System.IO.Path]::GetTempPath()) "kmshare_deb_$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempStage -Force | Out-Null

try {
    # 1. Estrutura de dados
    $dataDir = Join-Path $tempStage "data"
    $binDir = Join-Path $dataDir "usr/bin"
    $appsDir = Join-Path $dataDir "usr/share/applications"
    $iconsDir = Join-Path $dataDir "usr/share/pixmaps"
    $docDir = Join-Path $dataDir "usr/share/doc/kmshare"

    New-Item -ItemType Directory -Path $binDir -Force | Out-Null
    New-Item -ItemType Directory -Path $appsDir -Force | Out-Null
    New-Item -ItemType Directory -Path $iconsDir -Force | Out-Null
    New-Item -ItemType Directory -Path $docDir -Force | Out-Null

    # Copiar binários
    Copy-Item (Join-Path $PublishDir "KMShare") (Join-Path $binDir "kmshare") -Force
    if (Test-Path (Join-Path $PublishDir "kmshare-updater")) {
        Copy-Item (Join-Path $PublishDir "kmshare-updater") (Join-Path $binDir "kmshare-updater") -Force
    }

    # Copiar desktop e ícone
    $desktopSrc = Join-Path $PSScriptRoot "kmshare.desktop"
    if (Test-Path $desktopSrc) {
        Copy-Item $desktopSrc (Join-Path $appsDir "kmshare.desktop") -Force
    }
    $iconSrc = Join-Path $PSScriptRoot "..\..\assets\logo.png"
    if (Test-Path $iconSrc) {
        Copy-Item $iconSrc (Join-Path $iconsDir "kmshare.png") -Force
    }

    # Copiar licença
    $licenseSrc = Join-Path $PSScriptRoot "..\..\LICENSE"
    if (Test-Path $licenseSrc) {
        Copy-Item $licenseSrc (Join-Path $docDir "copyright") -Force
    }

    # 2. Estrutura de controle
    $controlDir = Join-Path $tempStage "control"
    New-Item -ItemType Directory -Path $controlDir -Force | Out-Null

    $installedSizeKb = [math]::Ceiling((Get-ChildItem -Path $dataDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1024)

    $controlContent = @"
Package: $debPackageName
Version: $Version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: Cesar Ovies <https://github.com/CesarOvies/KMShare>
Installed-Size: $installedSizeKb
Description: Intranet Virtual KVM & Secure Peer-to-Peer File Transfer
 KMShare is a high-performance zero-dependency virtual KVM (mouse and keyboard
 boundary crossing) and encrypted peer-to-peer file transfer system built on .NET 10.
"@.Trim() + "`n"

    $controlFile = Join-Path $controlDir "control"
    [System.IO.File]::WriteAllText($controlFile, $controlContent.Replace("`r`n", "`n"), [System.Text.Encoding]::UTF8)

    # 3. Gerar control.tar.gz e data.tar.gz via tar
    $controlTar = Join-Path $tempStage "control.tar.gz"
    $dataTar = Join-Path $tempStage "data.tar.gz"
    $debianBinaryFile = Join-Path $tempStage "debian-binary"
    [System.IO.File]::WriteAllText($debianBinaryFile, "2.0`n", [System.Text.Encoding]::ASCII)

    tar -czf $controlTar -C $controlDir control
    tar -czf $dataTar -C $dataDir usr

    # 4. Empacotar formato ar do Debian
    $controlBytes = [System.IO.File]::ReadAllBytes($controlTar)
    $dataBytes = [System.IO.File]::ReadAllBytes($dataTar)
    $debBinBytes = [System.IO.File]::ReadAllBytes($debianBinaryFile)

    $arStream = [System.IO.File]::Create($debFilePath)
    try {
        $magic = [System.Text.Encoding]::ASCII.GetBytes("!<arch>`n")
        $arStream.Write($magic, 0, $magic.Length)

        $entries = @(
            @{ Name = "debian-binary"; Bytes = $debBinBytes },
            @{ Name = "control.tar.gz"; Bytes = $controlBytes },
            @{ Name = "data.tar.gz"; Bytes = $dataBytes }
        )

        foreach ($entry in $entries) {
            $namePadded = ("{0,-16}" -f $entry.Name).Substring(0, 16)
            $headerStr = "{0}{1,-12}{2,-6}{3,-6}{4,-8}{5,-10}" -f $namePadded, "0", "0", "0", "100644", $entry.Bytes.Length
            $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($headerStr)
            $arStream.Write($headerBytes, 0, $headerBytes.Length)
            $arStream.WriteByte(0x60)
            $arStream.WriteByte(0x0A)
            $arStream.Write($entry.Bytes, 0, $entry.Bytes.Length)
            if ($entry.Bytes.Length % 2 -ne 0) {
                $arStream.WriteByte(0x0A)
            }
        }
    } finally {
        $arStream.Close()
    }

    Write-Host "  -> Pacote Debian gerado com sucesso: $debFilePath" -ForegroundColor Green
} finally {
    if (Test-Path $tempStage) {
        Remove-Item -Path $tempStage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

