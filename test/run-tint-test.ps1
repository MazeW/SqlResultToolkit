<#
    Compiles the cell-highlighting sources against the REAL SSMS GridControl, paints a grid
    to a bitmap and samples the pixels. Verifies the tinting without SSMS in the loop.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$out = Join-Path $root 'obj\test'

New-Item -ItemType Directory -Force -Path $out | Out-Null

$csc = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'Could not find a C# compiler.' }

$ssmsIde = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE'
$refDir = Join-Path $root 'tools\refs\.NETFramework\v4.7.2'

$refs = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Data.dll', 'System.Xml.dll',
          'System.Drawing.dll', 'System.Windows.Forms.dll') |
    ForEach-Object { '/r:' + (Join-Path $refDir $_) }
$refs += '/r:' + (Join-Path $ssmsIde 'Microsoft.SqlServer.GridControl.dll')

$sources = @(
    (Join-Path $src 'Grid\PendingEdits.cs'),
    (Join-Path $src 'Grid\CellHighlighter.cs'),
    (Join-Path $src 'Grid\SelectionReader.cs'),
    (Join-Path $src 'Grid\ResultSetAccessor.cs'),
    (Join-Path $src 'Grid\CellSnapshot.cs'),
    (Join-Path $src 'Format\SqlTypeCatalog.cs'),
    (Join-Path $src 'Format\SqlLiteral.cs'),
    (Join-Path $src 'Format\ScriptGenerator.cs'),
    (Join-Path $PSScriptRoot 'TintHarness.cs')
)

$exe = Join-Path $out 'TintHarness.exe'

& $csc /nologo /nostdlib+ /noconfig /target:winexe "/out:$exe" /langversion:8.0 /warn:4 $refs $sources
if ($LASTEXITCODE -ne 0) { throw "Tint harness compile failed with exit code $LASTEXITCODE" }

# GridControl is not in the GAC, so it has to sit next to the harness for the CLR to bind it.
Copy-Item (Join-Path $ssmsIde 'Microsoft.SqlServer.GridControl.dll') $out -Force

# winexe has no console of its own, so capture what it writes.
$stdout = Join-Path $out 'tint-out.txt'
$p = Start-Process -FilePath $exe -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdout
Get-Content $stdout
exit $p.ExitCode
