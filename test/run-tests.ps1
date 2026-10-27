<#
    Compiles the formatting and description sources into a console harness and runs it.

    These sources deliberately depend on nothing from Visual Studio, so they can be tested
    without SSMS in the loop. The grid plumbing (GridMonitor, ResultSetAccessor,
    CellInspectorControl) can only be exercised inside a running SSMS.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$out = Join-Path $root 'obj\test'

New-Item -ItemType Directory -Force -Path $out | Out-Null

$csc = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw 'Could not find a C# compiler.' }

$refDir = Join-Path $root 'tools\refs\.NETFramework\v4.7.2'
$refs = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Data.dll', 'System.Xml.dll',
          'System.Drawing.dll', 'System.Windows.Forms.dll') |
    ForEach-Object { '/r:' + (Join-Path $refDir $_) }

# CellSnapshot names GridControl (it holds the grid the cell came from), so the harness
# needs that reference too. Nothing in these tests constructs one.
$ssmsIde = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE'
$gridControl = Join-Path $ssmsIde 'Microsoft.SqlServer.GridControl.dll'
if (Test-Path $gridControl) { $refs += '/r:' + $gridControl }

$sources = @(
    (Join-Path $src 'Format\Token.cs'),
    (Join-Path $src 'Format\JsonPrettyPrinter.cs'),
    (Join-Path $src 'Format\XmlPrettyPrinter.cs'),
    (Join-Path $src 'Format\HexDump.cs'),
    (Join-Path $src 'Format\SqlTypeCatalog.cs'),
    (Join-Path $src 'Format\ValueDescriber.cs'),
    (Join-Path $src 'Format\CellRenderer.cs'),
    (Join-Path $src 'Format\SqlLiteral.cs'),
    (Join-Path $src 'Format\ScriptGenerator.cs'),
    (Join-Path $src 'Format\SqlTableFinder.cs'),
    (Join-Path $src 'Format\ValueListBuilder.cs'),
    (Join-Path $src 'Grid\CellSnapshot.cs'),
    (Join-Path $src 'Diagnostics.cs'),
    (Join-Path $PSScriptRoot 'FormatterTests.cs'),
    (Join-Path $PSScriptRoot 'ScriptTests.cs'),
    (Join-Path $PSScriptRoot 'TableFinderTests.cs'),
    (Join-Path $PSScriptRoot 'ValueListTests.cs'),
    (Join-Path $PSScriptRoot 'DiagnosticsTests.cs')
)

$exe = Join-Path $out 'FormatterTests.exe'

& $csc /nologo /nostdlib+ /noconfig /target:exe "/out:$exe" /langversion:8.0 /warn:4 $refs $sources
if ($LASTEXITCODE -ne 0) { throw "Test compile failed with exit code $LASTEXITCODE" }

& $exe
exit $LASTEXITCODE
