# Builds the Velopack installer locally: Releases\RAM.AccountManager-win-Setup.exe
# Usage: .\tools\pack.ps1 -Version 1.0.0
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    if (Test-Path publish) { Remove-Item -Recurse -Force publish }
    dotnet publish RAM.csproj -c Release -r win-x64 --self-contained false "-p:Version=$Version" -o publish
    if ($LASTEXITCODE) { throw "publish failed" }
    vpk pack --packId RAM.AccountManager --packVersion $Version --packDir publish --mainExe RAM.exe `
        --packTitle "R.A.M" --packAuthors "R.A.M" --icon Assets\ram.ico `
        --splashImage Assets\splash.png --splashProgressColor "#D97757" `
        --framework net8.0-x64-desktop --outputDir Releases
    if ($LASTEXITCODE) { throw "vpk pack failed" }
}
finally {
    Pop-Location
}
