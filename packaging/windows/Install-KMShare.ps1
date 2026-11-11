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
Write-Host "[1/5] Preparando diretório de instalação: $targetDir" -ForegroundColor Yellow
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

$filesToCopy = @("KMShare.exe", "kmshare-updater.exe", "README.md", "LICENSE", "KMShare-CodeSigning.cer")
Write-Host "[2/5] Copiando arquivos essenciais..." -ForegroundColor Yellow

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

Write-Host "[3/5] Confiando certificado de assinatura digital (KMShare)..." -ForegroundColor Yellow
$certFile = Join-Path $targetDir "KMShare-CodeSigning.cer"
if (-not (Test-Path $certFile)) {
    $srcCert = Join-Path $SourceDir "KMShare-CodeSigning.cer"
    if (Test-Path $srcCert) {
        Copy-Item -Path $srcCert -Destination $certFile -Force
    }
}

if (Test-Path $certFile) {
    try {
        $cert = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile($certFile)
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new([System.Security.Cryptography.X509Certificates.StoreName]::Root, [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $exists = $store.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $cert.Thumbprint, $false).Count -gt 0
        if (-not $exists) {
            $store.Add($cert)
            Write-Host "  -> Certificado KMShare adicionado às Autoridades Raiz Confiáveis (CurrentUser)." -ForegroundColor Green
        } else {
            Write-Host "  -> Certificado KMShare já confiado nesta máquina." -ForegroundColor Green
        }
        $store.Close()

        $storePub = [System.Security.Cryptography.X509Certificates.X509Store]::new([System.Security.Cryptography.X509Certificates.StoreName]::TrustedPublisher, [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        $storePub.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        if ($storePub.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $cert.Thumbprint, $false).Count -eq 0) {
            $storePub.Add($cert)
        }
        $storePub.Close()
        Write-Host "  -> Cópia pública disponível para instalação manual em: $certFile" -ForegroundColor Cyan
    } catch {
        Write-Warning "Registro automático do certificado ignorado: $_. Você pode instalá-lo manualmente dando dois cliques em: $certFile"
    }
}

Write-Host "[4/5] Criando atalhos no Menu Iniciar e Desktop..." -ForegroundColor Yellow
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

Write-Host "[5/5] Adicionando ao PATH do usuário..." -ForegroundColor Yellow
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

