<#
    Downloads the build tools this project needs and unpacks them into tools\.

    They are NOT committed: together they are about 176 MB of someone else's binaries.
    build.ps1 calls this automatically when they are missing, so a fresh clone just builds.

    What gets fetched, and why:

      Microsoft.VSSDK.BuildTools                 VSCT.exe (compiles the command table) and
                                                 CreatePkgDef.exe (generates the pkgdef from
                                                 the package's registration attributes).
                                                 Visual Studio Build Tools ships neither.

      Microsoft.NETFramework.ReferenceAssemblies .NET Framework 4.7.2 reference assemblies.
        .net472                                  Build Tools has no targeting pack, so
                                                 without these MSBuild fails with MSB3644.

    Everything else the build needs comes from the installed SSMS itself.
#>
[CmdletBinding()]
param(
    [string]$VssdkVersion = '18.9.820',
    [string]$ReferenceAssembliesVersion = '1.0.3',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$temp = Join-Path $env:TEMP 'SqlCellInspector-tools'
New-Item -ItemType Directory -Force -Path $temp | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Package {
    param([string]$Id, [string]$Version)

    $file = Join-Path $temp "$Id.$Version.nupkg"
    if (-not (Test-Path $file)) {
        $url = "https://api.nuget.org/v3-flatcontainer/$($Id.ToLowerInvariant())/$Version/$($Id.ToLowerInvariant()).$Version.nupkg"
        Write-Host "  downloading $Id $Version"
        Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
    }

    return $file
}

function Expand-Entries {
    param(
        [string]$Package,
        [scriptblock]$Filter,       # takes the entry path, returns $true to extract
        [scriptblock]$MapPath       # takes the entry path, returns the path under tools\
    )

    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $count = 0
        foreach ($entry in $zip.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) { continue }   # directory
            if (-not (& $Filter $entry.FullName)) { continue }

            $target = Join-Path $tools (& $MapPath $entry.FullName)
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            $count++
        }

        return $count
    } finally {
        $zip.Dispose()
    }
}

# ---- VSSDK build tools -------------------------------------------------------------
$vsctPath = Join-Path $tools 'vssdk\bin\VSCT.exe'
if ($Force -or -not (Test-Path $vsctPath)) {
    Write-Host 'Restoring VSSDK build tools'
    $package = Get-Package -Id 'Microsoft.VSSDK.BuildTools' -Version $VssdkVersion

    # Everything under tools/vssdk (any casing), minus the arm64 flavour. The first path
    # segment is normalised because the package mixes "tools/vssdk" and "tools/VSSDK".
    $n = Expand-Entries -Package $package `
        -Filter { param($p) $p.ToLowerInvariant().StartsWith('tools/vssdk/') -and $p.ToLowerInvariant() -notmatch '/arm64/' } `
        -MapPath { param($p) ($p -split '/' | Select-Object -Skip 1 | ForEach-Object { $_ }) -join '\' -replace '^[Vv][Ss][Ss][Dd][Kk]\\', 'vssdk\' }

    Write-Host "  extracted $n files"
} else {
    Write-Host 'VSSDK build tools already present'
}

# ---- .NET Framework 4.7.2 reference assemblies --------------------------------------
$mscorlib = Join-Path $tools 'refs\.NETFramework\v4.7.2\mscorlib.dll'
if ($Force -or -not (Test-Path $mscorlib)) {
    Write-Host 'Restoring .NET Framework 4.7.2 reference assemblies'
    $package = Get-Package -Id 'Microsoft.NETFramework.ReferenceAssemblies.net472' -Version $ReferenceAssembliesVersion

    $n = Expand-Entries -Package $package `
        -Filter { param($p) $p.ToLowerInvariant().StartsWith('build/') } `
        -MapPath { param($p) 'refs\' + (($p -split '/' | Select-Object -Skip 1) -join '\') }

    Write-Host "  extracted $n files"
} else {
    Write-Host 'Reference assemblies already present'
}

if (-not (Test-Path $vsctPath)) { throw "VSCT.exe missing after restore: $vsctPath" }
if (-not (Test-Path $mscorlib)) { throw "Reference assemblies missing after restore: $mscorlib" }

Write-Host 'Tools ready.' -ForegroundColor Green
