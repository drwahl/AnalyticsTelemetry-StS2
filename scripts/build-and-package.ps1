#Requires -Version 5.1
$ErrorActionPreference = "Stop"

# Release build + zip under dist/ for manual Nexus upload (Windows).
# Expects this repo next to the game: ..\mods\AnalyticsTelemetry\

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ModFolder = Join-Path $RepoRoot "..\mods\AnalyticsTelemetry"
$Csproj = Join-Path $RepoRoot "AnalyticsTelemetry.csproj"

Set-Location $RepoRoot
dotnet build -c Release $Csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $ModFolder "AnalyticsTelemetry.dll"
if (-not (Test-Path -LiteralPath $dll)) {
    Write-Error "Expected $dll after build. Is the repo next to the game install (..\mods\)?"
    exit 1
}

$raw = Get-Content -LiteralPath $Csproj -Raw
if ($raw -notmatch '<Version>([^<]+)</Version>') {
    Write-Error "Could not parse <Version> from AnalyticsTelemetry.csproj"
    exit 1
}
$ver = $Matches[1].Trim()

$dist = Join-Path $RepoRoot "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip = Join-Path $dist "AnalyticsTelemetry-$ver.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

$modResolved = Resolve-Path -LiteralPath $ModFolder
Compress-Archive -Path (Join-Path $modResolved "*") -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Wrote $zip"
