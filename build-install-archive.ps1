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
$ProjectDir = Join-Path $PSScriptRoot 'FanaBridge'
$BuildOutput = Join-Path $ProjectDir "bin\$Configuration"

# ── 1. Build ──────────────────────────────────────────────────────────────────
Write-Host "Building $Configuration..." -ForegroundColor Cyan
dotnet build "$ProjectDir\FanaBridge.csproj" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

# ── 2. Stage files ────────────────────────────────────────────────────────────
$Staging = Join-Path $OutputPath 'staging'
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item $Staging -ItemType Directory -Force | Out-Null
New-Item (Join-Path $Staging 'DevicesLogos') -ItemType Directory -Force | Out-Null

# Plugin DLL (debug symbols are embedded)
Copy-Item (Join-Path $BuildOutput 'FanaBridge.dll') $Staging

# ── Device logo images ────────────────────────────────────────────────────────
# Copy pre-processed images from Resources/DeviceLogos/processed/.
# Run process-device-logos.ps1 first if source images have changed.
$ProcessedDir = Join-Path (Join-Path (Join-Path $ProjectDir 'Resources') 'DeviceLogos') 'processed'
$DestLogos = Join-Path $Staging 'DevicesLogos'

if (-not (Test-Path $ProcessedDir) -or (Get-ChildItem "$ProcessedDir\*.png" -ErrorAction SilentlyContinue).Count -eq 0) {
    Write-Warning "No processed logos found. Running process-device-logos.ps1..."
    & (Join-Path $PSScriptRoot 'process-device-logos.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Image processing failed."
        exit 1
    }
}

Get-ChildItem "$ProcessedDir\*.png" -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $DestLogos $_.Name)
    Write-Host "  $($_.Name) -> DevicesLogos\$($_.Name)" -ForegroundColor DarkGray
}

# ── 3. Create archive ─────────────────────────────────────────────────────────
# Use ProductVersion (InformationalVersion) for naming — strip +<sha> build metadata.
$Version = (Get-Item (Join-Path $Staging 'FanaBridge.dll')).VersionInfo.ProductVersion -replace '\+.*$', ''
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Item (Join-Path $Staging 'FanaBridge.dll')).VersionInfo.FileVersion -replace '\+.*$', ''
}
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = 'dev' }
if ($Configuration -eq 'Release') {
    $ArchiveName = "FanaBridge-$Version.zip"
} else {
    $ArchiveName = "FanaBridge-$Version-$Configuration.zip"
}
$ArchivePath = Join-Path $OutputPath $ArchiveName

if (Test-Path $ArchivePath) { Remove-Item $ArchivePath -Force }
Compress-Archive -Path (Join-Path $Staging '*') -DestinationPath $ArchivePath -Force

Write-Host ""
Write-Host "Archive created: $ArchivePath" -ForegroundColor Green
Write-Host "Install: extract directly into your SimHub directory." -ForegroundColor Green

# ── 4. Cleanup staging ────────────────────────────────────────────────────────
Remove-Item $Staging -Recurse -Force
