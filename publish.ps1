# Builds dist\DbEmulator.exe: a single self-contained file (no .NET install needed) plus appsettings.json.
param(
    [string]$Output = "dist",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test DbEmulator.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

dotnet publish src/DbEmulator/DbEmulator.csproj -c Release -r win-x64 -o $Output
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Write-Host ""
Write-Host "Done: $(Resolve-Path $Output)\DbEmulator.exe" -ForegroundColor Green
