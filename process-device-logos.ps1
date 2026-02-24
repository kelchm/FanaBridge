<#
.SYNOPSIS
    Processes device logo source images for distribution.

.DESCRIPTION
    Reads source images from Resources/DeviceLogos/, trims transparent
    borders and resizes to a standard 512px width, then writes the
    processed results to Resources/DeviceLogos/processed/.

    Run this whenever source images change. Both the build archive
    script and the dev install (csproj) copy from the processed folder.

    Requires ImageMagick 7+ (magick) on PATH.

.EXAMPLE
    .\process-device-logos.ps1
#>
$ErrorActionPreference = 'Stop'

$sourceDir = Join-Path (Join-Path (Join-Path $PSScriptRoot 'FanaBridge') 'Resources') 'DeviceLogos'
$processedDir = Join-Path $sourceDir 'processed'

# Verify ImageMagick is available
if (-not (Get-Command magick -ErrorAction SilentlyContinue)) {
    Write-Error "ImageMagick (magick) not found on PATH. Install from https://imagemagick.org"
    exit 1
}

# Clean and recreate output folder
if (Test-Path $processedDir) { Remove-Item $processedDir -Recurse -Force }
New-Item $processedDir -ItemType Directory -Force | Out-Null

$sources = Get-ChildItem "$sourceDir\*.png" -ErrorAction SilentlyContinue
if ($sources.Count -eq 0) {
    Write-Warning "No source images found in $sourceDir"
    exit 0
}

foreach ($src in $sources) {
    $dest = Join-Path $processedDir $src.Name

    # Trim transparent borders, resize to 512px wide (only shrinks), keep transparency
    & magick $src.FullName -trim +repage -resize '512x>' $dest
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to process $($src.Name)"
        exit 1
    }

    # Report size change
    $srcSize = $src.Length
    $destSize = (Get-Item $dest).Length
    $srcDim = & magick identify -format '%wx%h' $src.FullName
    $destDim = & magick identify -format '%wx%h' $dest
    Write-Host "  $($src.Name): $srcDim ($([math]::Round($srcSize/1KB))KB) -> $destDim ($([math]::Round($destSize/1KB))KB)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Processed $($sources.Count) image(s) to $processedDir" -ForegroundColor Green
