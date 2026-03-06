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

$SourceDir = Join-Path (Join-Path (Join-Path $PSScriptRoot 'FanaBridge') 'Resources') 'DeviceLogos'
$ProcessedDir = Join-Path $SourceDir 'processed'

# Verify ImageMagick is available
if (-not (Get-Command magick -ErrorAction SilentlyContinue)) {
    Write-Error "ImageMagick (magick) not found on PATH. Install from https://imagemagick.org"
    exit 1
}

# Clean and recreate output folder
if (Test-Path $ProcessedDir) { Remove-Item $ProcessedDir -Recurse -Force }
New-Item $ProcessedDir -ItemType Directory -Force | Out-Null

$Sources = Get-ChildItem "$SourceDir\*.png" -ErrorAction SilentlyContinue
if ($Sources.Count -eq 0) {
    Write-Warning "No source images found in $SourceDir"
    exit 0
}

foreach ($Src in $Sources) {
    $Dest = Join-Path $ProcessedDir $Src.Name

    # Trim transparent borders, resize to 512px wide (only shrinks), keep transparency
    & magick $Src.FullName -trim +repage -resize '512x>' $Dest
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to process $($Src.Name)"
        exit 1
    }

    # Report size change
    $SrcSize = $Src.Length
    $DestSize = (Get-Item $Dest).Length
    $SrcDim = & magick identify -format '%wx%h' $Src.FullName
    $DestDim = & magick identify -format '%wx%h' $Dest
    Write-Host "  $($Src.Name): $SrcDim ($([math]::Round($SrcSize/1KB))KB) -> $DestDim ($([math]::Round($DestSize/1KB))KB)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Processed $($Sources.Count) image(s) to $ProcessedDir" -ForegroundColor Green
