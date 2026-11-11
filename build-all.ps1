param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [string[]]$Runtimes = @("win-x64", "linux-x64", "osx-arm64"),
    [switch]$PackageAll = $false,
    [switch]$ScanDefender = $false,
    [bool]$SelfContained = $true
)

$ErrorActionPreference = "Stop"

$publishBase = Join-Path $PSScriptRoot "publish"
$distBase = Join-Path $PSScriptRoot "dist"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  KMShare - Pipeline de Compilacao Multiplataforma (.NET 10)" -ForegroundColor Cyan
Write-Host "  Versao: $Version | Config: $Configuration | Self-Contained: $SelfContained" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Compilacao Single-File para cada RID
foreach ($rid in $Runtimes) {
    $outDir = Join-Path $publishBase $rid
    Write-Host "`n[1/3] Publicando KMShare e KMShare.Updater ($rid)..." -ForegroundColor Yellow

    dotnet publish "$PSScriptRoot\KMShare.csproj" `
        -c $Configuration `
        -r $rid `
        --self-contained $SelfContained `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        -o $outDir

    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar KMShare para $rid" }

    dotnet publish "$PSScriptRoot\updater\KMShare.Updater\KMShare.Updater.csproj" `
        -c $Configuration `
        -r $rid `
        --self-contained $SelfContained `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        -o $outDir

    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar KMShare.Updater para $rid" }

    Write-Host "  -> Concluido com sucesso para $rid" -ForegroundColor Green
}

# Assinatura de código Windows (se certificado fornecido via env)
$certPfxBase64 = $env:CERT_BASE64
$certPass = $env:CERT_PASS
if ($Runtimes -contains "win-x64" -and -not [string]::IsNullOrWhiteSpace($certPfxBase64) -and -not [string]::IsNullOrWhiteSpace($certPass)) {
    Write-Host "`n--- Assinando binarios do Windows (win-x64) ---" -ForegroundColor Cyan
    $winPub = Join-Path $publishBase "win-x64"
    $kmshareExe = Join-Path $winPub "KMShare.exe"
    $updaterExe = Join-Path $winPub "kmshare-updater.exe"

    $tempPfx = Join-Path ([System.IO.Path]::GetTempPath()) "kmshare_cert_$([System.Guid]::NewGuid().ToString('N')).pfx"
    try {
        [System.IO.File]::WriteAllBytes($tempPfx, [Convert]::FromBase64String($certPfxBase64))
        
        $publicCerPath = Join-Path $PSScriptRoot "packaging\windows\KMShare-CodeSigning.cer"
        if (-not (Test-Path $publicCerPath)) {
            $pfxCert = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile($tempPfx, $certPass)
            [System.IO.File]::WriteAllBytes($publicCerPath, $pfxCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        }

        $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
        if (-not $signtool) {
            $signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending |
                Select-Object -ExpandProperty FullName -First 1
        }

        foreach ($targetExe in @($kmshareExe, $updaterExe)) {
            if (Test-Path $targetExe) {
                if ($signtool) {
                    & $signtool sign /f $tempPfx /p $certPass /fd SHA256 /tr "http://timestamp.digicert.com" /td SHA256 /d "KMShare" /du "https://github.com/CesarOvies/KMShare" $targetExe
                } else {
                    $securePass = ConvertTo-SecureString -String $certPass -Force -AsPlainText
                    $cert = Import-PfxCertificate -FilePath $tempPfx -CertStoreLocation "Cert:\CurrentUser\My" -Password $securePass
                    $null = Set-AuthenticodeSignature -FilePath $targetExe -Certificate $cert -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
                }
                Write-Host "  -> Assinado: $(Split-Path $targetExe -Leaf)" -ForegroundColor Green
            }
        }
    } catch {
        Write-Warning "Falha ao assinar binarios: $_"
    } finally {
        Remove-Item $tempPfx -Force -ErrorAction SilentlyContinue
    }
}

# 2. Empacotamento de Portables e Instaladores (se solicitado)
if ($PackageAll) {
    Write-Host "`n[2/3] Gerando pacotes de distribuicao (Portables e Instaladores)..." -ForegroundColor Yellow
    if (-not (Test-Path $distBase)) {
        New-Item -ItemType Directory -Path $distBase -Force | Out-Null
    }

    # --- WINDOWS (win-x64) ---
    if ($Runtimes -contains "win-x64") {
        Write-Host "`n--- Empacotando Windows (win-x64) ---" -ForegroundColor Cyan
        $winPub = Join-Path $publishBase "win-x64"

        # Portable ZIP
        $winZipName = "KMShare-v$Version-win-x64-portable.zip"
        $winZipPath = Join-Path $distBase $winZipName
        $winTemp = Join-Path ([System.IO.Path]::GetTempPath()) "kmshare_win_pkg_$([System.Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $winTemp -Force | Out-Null

        Copy-Item (Join-Path $winPub "KMShare.exe") $winTemp -Force
        Copy-Item (Join-Path $winPub "kmshare-updater.exe") $winTemp -Force
        Copy-Item "$PSScriptRoot\README.md" $winTemp -Force
        Copy-Item "$PSScriptRoot\LICENSE" $winTemp -Force
        Copy-Item "$PSScriptRoot\packaging\windows\Install-KMShare.ps1" $winTemp -Force

        $certSrc = "$PSScriptRoot\packaging\windows\KMShare-CodeSigning.cer"
        if (Test-Path $certSrc) {
            Copy-Item $certSrc $winTemp -Force
            Copy-Item $certSrc (Join-Path $distBase "KMShare-CodeSigning.cer") -Force
        }

        Compress-Archive -Path "$winTemp\*" -DestinationPath $winZipPath -Force
        Remove-Item -Path $winTemp -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  -> Portable ZIP criado: $winZipName" -ForegroundColor Green

        # Script Instalador User-Space
        Copy-Item "$PSScriptRoot\packaging\windows\Install-KMShare.ps1" (Join-Path $distBase "Install-KMShare.ps1") -Force

        # Inno Setup Installer (se compiler disponivel)
        $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
        if (-not $iscc) {
            $isccCandidates = @(
                "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
                "${env:ProgramFiles}\Inno Setup 6\iscc.exe",
                "${env:LOCALAPPDATA}\Programs\Inno Setup 6\iscc.exe"
            )
            foreach ($candidate in $isccCandidates) {
                if (Test-Path $candidate) { $iscc = $candidate; break }
            }
        }

        if ($iscc) {
            Write-Host "  -> Compilando Instalador Windows com Inno Setup..." -ForegroundColor Cyan
            $issFile = Join-Path $PSScriptRoot "packaging\windows\KMShare.iss"
            & $iscc "/DMyAppVersion=$Version" "/O$distBase" $issFile
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  -> Instalador Windows (.exe) gerado com sucesso via Inno Setup!" -ForegroundColor Green
            } else {
                Write-Warning "Falha ao compilar com Inno Setup (codigo $LASTEXITCODE)."
            }
        } else {
            Write-Host "  -> [INFO] Inno Setup (iscc.exe) nao localizado. Instalador standalone Install-KMShare.ps1 disponivel em dist." -ForegroundColor Yellow
        }
    }

    # --- LINUX (linux-x64) ---
    if ($Runtimes -contains "linux-x64") {
        Write-Host "`n--- Empacotando Linux (linux-x64) ---" -ForegroundColor Cyan
        $linuxPub = Join-Path $publishBase "linux-x64"

        # Portable TAR.GZ
        $linuxTarName = "KMShare-v$Version-linux-x64-portable.tar.gz"
        $linuxTarPath = Join-Path $distBase $linuxTarName
        $linuxTemp = Join-Path ([System.IO.Path]::GetTempPath()) "kmshare_linux_pkg_$([System.Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $linuxTemp -Force | Out-Null

        Copy-Item (Join-Path $linuxPub "KMShare") $linuxTemp -Force
        Copy-Item (Join-Path $linuxPub "kmshare-updater") $linuxTemp -Force
        Copy-Item "$PSScriptRoot\README.md" $linuxTemp -Force
        Copy-Item "$PSScriptRoot\LICENSE" $linuxTemp -Force
        Copy-Item "$PSScriptRoot\packaging\linux\kmshare.desktop" $linuxTemp -Force
        Copy-Item "$PSScriptRoot\packaging\linux\KMShare-installer.sh" $linuxTemp -Force
        Copy-Item "$PSScriptRoot\assets\logo.svg" (Join-Path $linuxTemp "kmshare.svg") -Force

        tar -czf $linuxTarPath -C $linuxTemp .
        Remove-Item -Path $linuxTemp -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  -> Portable TAR.GZ criado: $linuxTarName" -ForegroundColor Green

        # Script Instalador Standalone
        $linuxInstallerName = "KMShare-v$Version-linux-x64-installer.sh"
        Copy-Item "$PSScriptRoot\packaging\linux\KMShare-installer.sh" (Join-Path $distBase $linuxInstallerName) -Force

        # Pacote Debian (.deb)
        & "$PSScriptRoot\packaging\linux\build-deb.ps1" -PublishDir $linuxPub -OutputDir $distBase -Version $Version
    }

    # --- MACOS (osx-arm64) ---
    if ($Runtimes -contains "osx-arm64") {
        Write-Host "`n--- Empacotando macOS (osx-arm64) ---" -ForegroundColor Cyan
        $macPub = Join-Path $publishBase "osx-arm64"

        # Portable TAR.GZ
        $macTarName = "KMShare-v$Version-osx-arm64-portable.tar.gz"
        $macTarPath = Join-Path $distBase $macTarName
        $macTemp = Join-Path ([System.IO.Path]::GetTempPath()) "kmshare_mac_pkg_$([System.Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $macTemp -Force | Out-Null

        Copy-Item (Join-Path $macPub "KMShare") $macTemp -Force
        Copy-Item (Join-Path $macPub "kmshare-updater") $macTemp -Force
        Copy-Item "$PSScriptRoot\README.md" $macTemp -Force
        Copy-Item "$PSScriptRoot\LICENSE" $macTemp -Force

        tar -czf $macTarPath -C $macTemp .
        Remove-Item -Path $macTemp -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  -> Portable TAR.GZ criado: $macTarName" -ForegroundColor Green

        # Script Instalador macOS Standalone
        $macInstallerName = "KMShare-v$Version-osx-arm64-installer.sh"
        Copy-Item "$PSScriptRoot\packaging\macos\KMShare-installer.sh" (Join-Path $distBase $macInstallerName) -Force

        # macOS App Bundle Package (.zip)
        & "$PSScriptRoot\packaging\macos\build-macos.ps1" -PublishDir $macPub -OutputDir $distBase -Version $Version
    }

    # 3. Geracao de Checksums Criptograficos (SHA-256 e SHA-512)
    Write-Host "`nCalculando Checksums Criptograficos de todos os pacotes..." -ForegroundColor Cyan
    $distFiles = Get-ChildItem -Path $distBase -File | Where-Object { $_.Extension -ne ".sha256" -and $_.Extension -ne ".sha512" }

    $sha256Lines = @()
    $sha512Lines = @()

    foreach ($file in $distFiles) {
        $hash256 = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLower()
        $hash512 = (Get-FileHash -Path $file.FullName -Algorithm SHA512).Hash.ToLower()

        $sha256Lines += "$hash256  $($file.Name)"
        $sha512Lines += "$hash512  $($file.Name)"

        Set-Content -Path "$($file.FullName).sha256" -Value "$hash256  $($file.Name)" -NoNewline
    }

    Set-Content -Path (Join-Path $distBase "CHECKSUMS.sha256") -Value ($sha256Lines -join "`n") -NoNewline
    Set-Content -Path (Join-Path $distBase "CHECKSUMS.sha512") -Value ($sha512Lines -join "`n") -NoNewline
    Write-Host "  -> Checksums gravados em CHECKSUMS.sha256 e CHECKSUMS.sha512" -ForegroundColor Green
}

# 4. Scan de Antivirus com Microsoft Defender (se solicitado)
if ($ScanDefender) {
    Write-Host "`n[3/3] Executando Scan de Seguranca com Microsoft Defender Antivirus..." -ForegroundColor Yellow
    $mpCmdRun = "$env:ProgramFiles\Windows Defender\MpCmdRun.exe"
    $scanTarget = (Resolve-Path $distBase).Path

    if (Test-Path $mpCmdRun) {
        Write-Host "Iniciando varredura profunda no diretorio $scanTarget..." -ForegroundColor Cyan
        & $mpCmdRun -Scan -ScanType 3 -File $scanTarget
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  [OK] Microsoft Defender Antivirus: 0 ameacas detectadas em todos os binarios e instaladores!" -ForegroundColor Green
        } else {
            throw "Microsoft Defender detectou ameaca de seguranca ou erro durante o scan! Codigo de saida: $LASTEXITCODE"
        }
    } else {
        Write-Warning "MpCmdRun.exe nao encontrado em '$mpCmdRun'. Scanner Defender local indisponivel."
    }
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "  Build e empacotamento concluidos com sucesso!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
