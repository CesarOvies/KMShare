<#
.SYNOPSIS
    Instalador User-Space (sem Admin) do KMShare para Windows x64.
.DESCRIPTION
    Instala KMShare.exe e kmshare-updater.exe no diretório de programas do usuário (%LOCALAPPDATA%\Programs\KMShare),
    cria atalhos no Menu Iniciar e na Área de Trabalho, e registra no PATH do usuário.
#>
param(
    [string]$SourceDir = $PSScriptRoot,
    [switch]$LaunchAfterInstall = $true
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  KMShare — Instalador User-Space para Windows (asInvoker)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$targetDir = Join-Path $env:LOCALAPPDATA "Programs\KMShare"
Write-Host "[1/4] Preparando diretório de instalação: $targetDir" -ForegroundColor Yellow
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

$filesToCopy = @("KMShare.exe", "kmshare-updater.exe", "README.md", "LICENSE")
Write-Host "[2/4] Copiando arquivos essenciais..." -ForegroundColor Yellow

foreach ($file in $filesToCopy) {
    $src = Join-Path $SourceDir $file
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination (Join-Path $targetDir $file) -Force
        Write-Host "  -> Instalado: $file" -ForegroundColor Green
    }
}

$assetsDir = Join-Path $targetDir "assets"
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null }
$iconSrc = Join-Path $SourceDir "assets\KMShare.ico"
if (-not (Test-Path $iconSrc)) { $iconSrc = Join-Path $SourceDir "KMShare.ico" }
if (Test-Path $iconSrc) {
    Copy-Item -Path $iconSrc -Destination (Join-Path $assetsDir "KMShare.ico") -Force
}

Write-Host "[3/4] Criando atalhos no Menu Iniciar e Desktop..." -ForegroundColor Yellow
try {
    $wsh = New-Object -ComObject WScript.Shell
    $programsMenu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    $shortcutPath = Join-Path $programsMenu "KMShare.lnk"
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $targetDir "KMShare.exe"
    $shortcut.Arguments = "--gui"
    $shortcut.WorkingDirectory = $targetDir
    $shortcut.Description = "KMShare - KVM Virtual & Compartilhamento P2P"
    $shortcut.IconLocation = "$targetDir\KMShare.exe,0"
    $shortcut.Save()
    Write-Host "  -> Atalho criado no Menu Iniciar: $shortcutPath" -ForegroundColor Green

    $desktopPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)
    $desktopShortcutPath = Join-Path $desktopPath "KMShare.lnk"
    $desktopShortcut = $wsh.CreateShortcut($desktopShortcutPath)
    $desktopShortcut.TargetPath = Join-Path $targetDir "KMShare.exe"
    $desktopShortcut.Arguments = "--gui"
    $desktopShortcut.WorkingDirectory = $targetDir
    $desktopShortcut.IconLocation = "$targetDir\KMShare.exe,0"
    $desktopShortcut.Save()
    Write-Host "  -> Atalho criado na Área de Trabalho: $desktopShortcutPath" -ForegroundColor Green
} catch {
    Write-Warning "Não foi possível criar atalhos automaticamente: $_"
}

Write-Host "[4/4] Adicionando ao PATH do usuário..." -ForegroundColor Yellow
try {
    $userPath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::User)
    if ($userPath -notlike "*$targetDir*") {
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$targetDir", [EnvironmentVariableTarget]::User)
        Write-Host "  -> $targetDir adicionado ao PATH do usuário." -ForegroundColor Green
    }
} catch {
    Write-Warning "Falha ao registrar PATH: $_"
}

Write-Host "`nKMShare instalado com sucesso no User-Space!" -ForegroundColor Green

if ($LaunchAfterInstall) {
    $exePath = Join-Path $targetDir "KMShare.exe"
    if (Test-Path $exePath) {
        Write-Host "Iniciando KMShare..." -ForegroundColor Cyan
        Start-Process -FilePath $exePath -ArgumentList "--gui"
    }
}

