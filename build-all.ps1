param(
    [string]$Configuration = "Release",
    [string[]]$Runtimes = @("win-x64", "linux-x64", "osx-arm64")
)

$ErrorActionPreference = "Stop"

foreach ($rid in $Runtimes) {
    Write-Host "=== Publishing KMShare & KMShare.Updater ($rid - SingleFile) ===" -ForegroundColor Cyan

    dotnet publish "$PSScriptRoot\KMShare.csproj" `
        -c $Configuration `
        -r $rid `
        --self-contained false `
        -p:PublishSingleFile=true `
        -o "$PSScriptRoot\dist\$rid"

    if ($LASTEXITCODE -ne 0) { throw "Failed to publish KMShare for $rid" }

    dotnet publish "$PSScriptRoot\updater\KMShare.Updater\KMShare.Updater.csproj" `
        -c $Configuration `
        -r $rid `
        --self-contained false `
        -p:PublishSingleFile=true `
        -o "$PSScriptRoot\dist\$rid"

    if ($LASTEXITCODE -ne 0) { throw "Failed to publish KMShare.Updater for $rid" }
}

Write-Host "=== All Single-File builds succeeded! Output in ./dist/ ===" -ForegroundColor Green

