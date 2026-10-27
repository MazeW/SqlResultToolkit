<#
    Creates dbo.ResultToolkitDemo in the LocalDB "test" database: one row per interesting
    SQL Server type, so the panel can be exercised end to end.

    Drop it again with:  .\setup-demo-db.ps1 -Drop
#>
[CmdletBinding()]
param(
    [string]$ConnectionString = 'Server=(localdb)\MSSQLLocalDB;Database=test;Integrated Security=true;Connect Timeout=30',
    [switch]$Drop
)

$ErrorActionPreference = 'Stop'

function Invoke-Sql([string]$sql) {
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $connection.Open()
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        $command.CommandTimeout = 60
        [void]$command.ExecuteNonQuery()
    } finally {
        $connection.Close()
    }
}

if ($Drop) {
    Invoke-Sql 'IF OBJECT_ID(''dbo.ResultToolkitDemo'') IS NOT NULL DROP TABLE dbo.ResultToolkitDemo;'
    Write-Host 'Dropped dbo.ResultToolkitDemo.'
    return
}

# A 1x1 red PNG, so the binary column has something the panel can render as an image.
$png = '0x89504E470D0A1A0A0000000D4948445200000001000000010806000000' +
       '1F15C4890000000D4944415478DA63F8CF0000030101002EA3B9EC0000000049454E44AE426082'

Invoke-Sql @"
IF OBJECT_ID('dbo.ResultToolkitDemo') IS NOT NULL DROP TABLE dbo.ResultToolkitDemo;

CREATE TABLE dbo.ResultToolkitDemo
(
    Id            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ResultToolkitDemo PRIMARY KEY,
    Name          nvarchar(100)   NULL,
    Payload       nvarchar(max)   NULL,
    Doc           xml             NULL,
    Blob          varbinary(max)  NULL,
    Amount        decimal(18,4)   NULL,
    Ratio         float           NULL,
    Flag          bit             NULL,
    CreatedAt     datetime2(7)    NULL,
    CreatedOffset datetimeoffset(7) NULL,
    OnlyDate      date            NULL,
    OnlyTime      time(7)         NULL,
    Ref           uniqueidentifier NULL,
    Notes         varchar(50)     NULL,
    Ver           rowversion      NOT NULL
);

INSERT INTO dbo.ResultToolkitDemo
    (Name, Payload, Doc, Blob, Amount, Ratio, Flag, CreatedAt, CreatedOffset, OnlyDate, OnlyTime, Ref, Notes)
VALUES
    (N'Ada Lovelace',
     N'{"haha":true,"xd":123,"string":"yes sir","nested":{"a":[1,2,3]}}',
     N'<order id="7"><line sku="A-1" qty="2"/><line sku="B-2" qty="1"/></order>',
     $png,
     12345.6700, 0.1, 1,
     '2026-10-25T14:30:15.1234567', '2026-10-25T14:30:15.1234567+03:00',
     '2026-10-25', '23:59:59.9999999',
     '8F14E45F-CEEA-467A-9C2B-1F2D3E4A5B6C',
     'O''Brien said "hi"'),

    (N'Grace Hopper',
     N'{"hi":123}',
     N'<order id="8"/>',
     0xDEADBEEF,
     -0.0001, 2.5, 0,
     '2000-01-01T00:00:00.0000000', '2000-01-01T00:00:00.0000000-08:00',
     '2000-01-01', '00:00:00.0000000',
     NEWID(),
     'plain'),

    (NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
"@

Write-Host 'Created dbo.ResultToolkitDemo with 3 rows.'
