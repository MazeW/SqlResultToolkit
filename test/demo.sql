-- Fixture for driving SQL Cell Inspector end to end.
-- Two result sets, so the panel's multi-grid handling gets exercised too.

SELECT Id, Name, Payload, Doc, Blob, Amount, Ratio, Flag,
       CreatedAt, CreatedOffset, OnlyDate, OnlyTime, Ref, Notes, Ver
  FROM dbo.CellInspectorDemo
 ORDER BY Id;

SELECT TOP (3) name, database_id, create_date
  FROM sys.databases
 ORDER BY database_id;
