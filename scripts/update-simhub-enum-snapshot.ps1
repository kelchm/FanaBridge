<#
.SYNOPSIS
    Regenerates the committed snapshot of SimHub.FanatecManaged.dll's public enums.

.DESCRIPTION
    Reads the public enum metadata of the shipped SimHub.FanatecManaged.dll via
    reflection and writes it to FanaBridge.Tests\Snapshots\SimHub.FanatecManaged.enums.txt.
    SimHubEnumSnapshotTests compares the DLL against this snapshot so a SimHub
    update that adds or renames wheel ids fails loudly instead of slipping by.

    The output format must stay identical to the C# generator in
    FanaBridge.Tests\SimHubEnumSnapshotTests.cs; the test itself catches drift
    between the two implementations.

.PARAMETER SimHubDir
    SimHub install directory. Defaults to C:\Program Files (x86)\SimHub\.

.PARAMETER OutFile
    Snapshot file to write. Defaults to the committed snapshot path.

.EXAMPLE
    .\update-simhub-enum-snapshot.ps1
#>
[CmdletBinding()]
param(
    [string]$SimHubDir = 'C:\Program Files (x86)\SimHub\',
    [string]$OutFile = (Join-Path (Split-Path $PSScriptRoot -Parent) 'tests\FanaBridge.Tests\Snapshots\SimHub.FanatecManaged.enums.txt')
)

$ErrorActionPreference = 'Stop'

# ReflectionOnlyLoadFrom exists only on .NET Framework; re-run under Windows PowerShell 5.1.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $forward = @('-SimHubDir', $SimHubDir, '-OutFile', $OutFile)
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @forward
    exit $LASTEXITCODE
}

$dllPath = Join-Path $SimHubDir 'SimHub.FanatecManaged.dll'
if (-not (Test-Path $dllPath)) {
    Write-Error "SimHub.FanatecManaged.dll not found at $dllPath. Install SimHub or pass -SimHubDir."
}

$resolver = [ResolveEventHandler] { param($s, $e) [Reflection.Assembly]::ReflectionOnlyLoad($e.Name) }
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolver)
try {
    # Reflection-only load: reads metadata without executing any assembly code.
    $asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom($dllPath)
    try {
        $types = $asm.GetTypes()
    }
    catch [Reflection.ReflectionTypeLoadException] {
        # Partial type load is expected for this assembly; enums resolve fine.
        $types = $_.Exception.Types | Where-Object { $null -ne $_ }
    }

    $sb = [Text.StringBuilder]::new()
    # Ordinal sorts to match the C# generator exactly (Sort-Object compares by culture).
    $enums = @($types | Where-Object { $_.IsEnum -and $_.IsPublic })
    [Array]::Sort($enums, [Comparison[Type]] { param($a, $b) [string]::CompareOrdinal($a.FullName, $b.FullName) })
    foreach ($type in $enums) {
        [void]$sb.Append($type.FullName).Append("`n")
        $members = @($type.GetFields([Reflection.BindingFlags]'Public,Static') |
            ForEach-Object { [pscustomobject]@{ Name = $_.Name; Value = [Convert]::ToInt64($_.GetRawConstantValue()) } })
        [Array]::Sort($members, [Comparison[object]] { param($a, $b)
                $c = $a.Value.CompareTo($b.Value)
                if ($c -ne 0) { $c } else { [string]::CompareOrdinal($a.Name, $b.Name) }
            })
        foreach ($member in $members) {
            [void]$sb.Append('  ').Append($member.Name).Append(' = ').Append($member.Value).Append("`n")
        }
    }
}
finally {
    [AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver)
}

if ($enums.Count -eq 0) {
    Write-Error "No public enums found in $dllPath."
}

# UTF-8 without BOM, LF line endings, for stable diffs.
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutFile))
[IO.File]::WriteAllText($OutFile, $sb.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($enums.Count) enum(s) from $dllPath"
Write-Host "  -> $OutFile"
Write-Host 'If members changed, review FanaBridgeVariantProvider.cs (StockWheelSuffixOverrides)'
Write-Host 'and FanatecDeviceTables.cs for new or renamed wheel ids.'
