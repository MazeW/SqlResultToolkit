<#
    Builds SqlResultToolkit.vsix.

    Needs only:
      - MSBuild (Visual Studio Build Tools; no VS SDK workload required)
      - tools\vssdk\bin\VSCT.exe and CreatePkgDef.exe (committed alongside this script)
      - An installed SSMS 22, whose assemblies are the compile-time references

    Steps: compile the command table, build the assembly, generate the pkgdef from the
    package attributes, then lay out and zip the VSIX.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$SsmsIde = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$obj = Join-Path $root 'obj'
$dist = Join-Path $root 'dist'
$layout = Join-Path $dist 'SqlResultToolkit'

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $paths = & $vswhere -products * -requires Microsoft.Component.MSBuild -format value -property installationPath 2>$null
        foreach ($p in $paths) {
            foreach ($rel in @('MSBuild\Current\Bin\amd64\MSBuild.exe', 'MSBuild\Current\Bin\MSBuild.exe')) {
                $candidate = Join-Path $p $rel
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }

    $fallback = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
    if (Test-Path $fallback) { return $fallback }

    throw 'Could not find MSBuild. Install Visual Studio Build Tools.'
}

if (-not (Test-Path $SsmsIde)) {
    throw "SSMS not found at '$SsmsIde'. Pass -SsmsIde with the correct path."
}

$msbuild = Find-MSBuild
Write-Host "MSBuild:  $msbuild"
Write-Host "SSMS IDE: $SsmsIde"

# VSCT.exe and the .NET Framework reference assemblies are not committed (176 MB of other
# people's binaries). Fetch them on a fresh clone so the build is still one command.
if (-not (Test-Path (Join-Path $root 'tools\vssdk\bin\VSCT.exe')) -or
    -not (Test-Path (Join-Path $root 'tools\refs\.NETFramework\v4.7.2\mscorlib.dll'))) {
    Write-Host ''
    & (Join-Path $root 'tools\restore-tools.ps1')
}

New-Item -ItemType Directory -Force -Path $obj, $dist | Out-Null
if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
New-Item -ItemType Directory -Force -Path $layout | Out-Null

# 1. Command table -> .cto (embedded by VSPackage.resx as Menus.ctmenu)
$vsct = Join-Path $root 'tools\vssdk\bin\VSCT.exe'
$ctoPath = Join-Path $obj 'SqlResultToolkit.cto'
$vsctInc = Join-Path $root 'tools\VSSDK\inc'
Write-Host "`n[1/4] Compiling command table"
# VSCT wants the include path attached to the switch: "-I<path>", not "-I <path>"
# and not "-I:<path>" (which it mis-splits on the drive-letter colon).
& $vsct (Join-Path $src 'SqlResultToolkit.vsct') $ctoPath "-I$vsctInc" "-I$(Join-Path $src 'Resources')" -nologo
if ($LASTEXITCODE -ne 0) { throw "VSCT failed with exit code $LASTEXITCODE" }

# 2. Assembly
Write-Host "`n[2/4] Building assembly"
& $msbuild (Join-Path $src 'SqlResultToolkit.csproj') `
    "/p:Configuration=$Configuration" `
    "/p:SsmsIde=$SsmsIde" `
    /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }

$assembly = Join-Path $obj 'bin\SqlResultToolkit.dll'
if (-not (Test-Path $assembly)) { throw "Build produced no assembly at $assembly" }

# 3. pkgdef, generated from the package's registration attributes
Write-Host "`n[3/4] Generating pkgdef"
$createPkgDef = Join-Path $root 'tools\vssdk\bin\CreatePkgDef.exe'
$pkgdef = Join-Path $obj 'SqlResultToolkit.pkgdef'
if (Test-Path $pkgdef) { Remove-Item -Force $pkgdef }

# /i is required: CreatePkgDef loads the assembly to read its registration attributes and
# needs the shell assemblies on hand to resolve them. Its exit code is not a reliable
# success signal (it returns non-zero even after printing SUCCEEDED), so check the output.
$includes = @(
    (Join-Path $SsmsIde 'PublicAssemblies'),
    $SsmsIde,
    (Join-Path $SsmsIde 'PrivateAssemblies')
) -join ';'

& $createPkgDef "/out=$pkgdef" /codebase "/i=$includes" $assembly | Out-Null
if (-not (Test-Path $pkgdef)) { throw 'CreatePkgDef produced no pkgdef.' }

# 4. Lay out and zip. A VSIX is an OPC zip: the manifest, a content-types part,
#    and the payload files named by the manifest's Assets.
Write-Host "`n[4/4] Packaging VSIX"
Copy-Item $assembly $layout
Copy-Item $pkgdef $layout
Copy-Item (Join-Path $src 'source.extension.vsixmanifest') (Join-Path $layout 'extension.vsixmanifest')

$pdb = [IO.Path]::ChangeExtension($assembly, '.pdb')
if (Test-Path $pdb) { Copy-Item $pdb $layout }

$contentTypes = @'
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="dll" ContentType="application/octet-stream" />
  <Default Extension="pdb" ContentType="application/octet-stream" />
  <Default Extension="pkgdef" ContentType="text/plain" />
  <Default Extension="vsixmanifest" ContentType="text/xml" />
  <Default Extension="png" ContentType="image/png" />
  <Default Extension="txt" ContentType="text/plain" />
</Types>
'@

# WriteAllText rather than Set-Content: the bracketed file name is a wildcard to
# PowerShell's provider cmdlets, and this avoids any encoding-parameter differences.
[IO.File]::WriteAllText(
    [IO.Path]::Combine($layout, '[Content_Types].xml'),
    $contentTypes,
    (New-Object Text.UTF8Encoding $false))

$vsixPath = Join-Path $dist 'SqlResultToolkit.vsix'
if (Test-Path $vsixPath) { Remove-Item -Force $vsixPath }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($layout, $vsixPath)

Write-Host "`nBuilt $vsixPath" -ForegroundColor Green
Get-ChildItem $layout | Select-Object Name, Length | Format-Table -AutoSize

if ($Install) {
    $installer = Join-Path $SsmsIde 'VSIXInstaller.exe'
    Write-Host "Launching $installer"
    & $installer $vsixPath
}
