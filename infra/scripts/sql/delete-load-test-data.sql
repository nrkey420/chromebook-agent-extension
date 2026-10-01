-- Deletes everything the load simulator (tools/load-simulator) wrote: rows whose DirectoryDeviceId starts with
-- 'loadtest-' (and IngestionErrors rows tied to their requests are left alone: they carry no device ID).
-- Deletes in chunks so a large test does not fill the transaction log. Safe to re-run.
-- Run as the SQL Entra admin. Blob and Sentinel copies: tools/load-simulator/README.md, "Cleaning up".
SET NOCOUNT ON;
DECLARE @Chunk int = 50000, @Rows int, @Total bigint;

SET @Total = 0;
WHILE 1 = 1
BEGIN
  DELETE TOP (@Chunk) FROM dbo.ActivityEvents WHERE DirectoryDeviceId LIKE N'loadtest-%';
  SET @Rows = @@ROWCOUNT; SET @Total += @Rows;
  IF @Rows < @Chunk BREAK;
END;
PRINT CONCAT(N'ActivityEvents deleted: ', @Total);

SET @Total = 0;
WHILE 1 = 1
BEGIN
  DELETE TOP (@Chunk) FROM dbo.IpObservations WHERE DirectoryDeviceId LIKE N'loadtest-%';
  SET @Rows = @@ROWCOUNT; SET @Total += @Rows;
  IF @Rows < @Chunk BREAK;
END;
PRINT CONCAT(N'IpObservations deleted: ', @Total);

SET @Total = 0;
WHILE 1 = 1
BEGIN
  DELETE TOP (@Chunk) FROM dbo.Sessions WHERE DirectoryDeviceId LIKE N'loadtest-%';
  SET @Rows = @@ROWCOUNT; SET @Total += @Rows;
  IF @Rows < @Chunk BREAK;
END;
PRINT CONCAT(N'Sessions deleted: ', @Total);

DELETE FROM dbo.Devices WHERE DirectoryDeviceId LIKE N'loadtest-%';
PRINT CONCAT(N'Devices deleted: ', @@ROWCOUNT);
