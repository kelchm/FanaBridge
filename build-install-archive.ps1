<#
.SYNOPSIS
    Builds a distributable install archive for FanaBridge.

.DESCRIPTION
    Constructs a zip file containing:
      - FanaBridge.dll (and .pdb if present)
      - Processed device logo images from Resources/DeviceLogos/processed/

    The archive can be extracted directly into the SimHub installation directory.
    Run process-device-logos.ps1 first if source images have changed.

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release.

.PARAMETER OutputPath
    Directory to place the final archive. Default: ./dist

.EXAMPLE
    .\build-install-archive.ps1
    .\build-install-archive.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputPath = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$projectDir = Join-Path $PSScriptRoot 'FanaBridge'
$buildOutput = Join-Path $projectDir "bin\$Configuration"

# ── 1. Build ──────────────────────────────────────────────────────────────────
Write-Host "Building $Configuration..." -ForegroundColor Cyan
dotnet build "$projectDir\FanaBridge.csproj" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

# ── 2. Stage files ────────────────────────────────────────────────────────────
$staging = Join-Path $OutputPath 'staging'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item $staging -ItemType Directory -Force | Out-Null
New-Item (Join-Path $staging 'DevicesLogos') -ItemType Directory -Force | Out-Null

# Plugin DLL (debug symbols are embedded)
Copy-Item (Join-Path $buildOutput 'FanaBridge.dll') $staging

# ── Device logo images ────────────────────────────────────────────────────────
# Copy pre-processed images from Resources/DeviceLogos/processed/.
# Run process-device-logos.ps1 first if source images have changed.
$processedDir = Join-Path (Join-Path (Join-Path $projectDir 'Resources') 'DeviceLogos') 'processed'
$destLogos = Join-Path $staging 'DevicesLogos'

if (-not (Test-Path $processedDir) -or (Get-ChildItem "$processedDir\*.png" -ErrorAction SilentlyContinue).Count -eq 0) {
    Write-Warning "No processed logos found. Running process-device-logos.ps1..."
    & (Join-Path $PSScriptRoot 'process-device-logos.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Image processing failed."
        exit 1
    }
}

Get-ChildItem "$processedDir\*.png" -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $destLogos $_.Name)
    Write-Host "  $($_.Name) -> DevicesLogos\$($_.Name)" -ForegroundColor DarkGray
}

# ── 3. Create archive ─────────────────────────────────────────────────────────
$version = (Get-Item (Join-Path $staging 'FanaBridge.dll')).VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($version)) { $version = 'dev' }
$archiveName = "FanaBridge-$version-$Configuration.zip"
$archivePath = Join-Path $OutputPath $archiveName

if (Test-Path $archivePath) { Remove-Item $archivePath -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archivePath -Force

Write-Host ""
Write-Host "Archive created: $archivePath" -ForegroundColor Green
Write-Host "Install: extract directly into your SimHub directory." -ForegroundColor Green

# ── 4. Cleanup staging ────────────────────────────────────────────────────────
Remove-Item $staging -Recurse -Force
