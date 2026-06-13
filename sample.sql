-- Sample T-SQL exercising the relation classifier (discussion #580).
-- Shapes mirror a typical production T-SQL script: schema-qualified procs, mixed DML.

CREATE PROCEDURE dbo.usp_GetProperty
    @id INT
AS
BEGIN
    -- reads_table: dbo.Property, dbo.PropertyAddress (JOIN source)
    SELECT p.Id, p.Name, a.City
    FROM dbo.Property AS p
    INNER JOIN dbo.PropertyAddress AS a ON a.PropertyId = p.Id
    WHERE p.Id = @id;
END
GO

CREATE PROCEDURE dbo.usp_RecordBooking
    @propertyId INT,
    @guest NVARCHAR(200)
AS
BEGIN
    -- writes_table: dbo.Booking ; reads_table: dbo.Property (INSERT ... SELECT)
    INSERT INTO dbo.Booking (PropertyId, Guest, CreatedUtc)
    SELECT @propertyId, @guest, SYSUTCDATETIME()
    FROM dbo.Property
    WHERE Id = @propertyId;

    -- writes_table: dbo.Property ; reads_table: dbo.Booking (UPDATE ... FROM)
    UPDATE p
    SET p.LastBookedUtc = SYSUTCDATETIME()
    FROM dbo.Property AS p
    INNER JOIN dbo.Booking AS b ON b.PropertyId = p.Id
    WHERE b.PropertyId = @propertyId;

    -- calls_proc: dbo.usp_GetProperty
    EXEC dbo.usp_GetProperty @propertyId;
END
GO

CREATE PROCEDURE dbo.usp_SyncRates
AS
BEGIN
    -- writes_table: dbo.Rate ; reads_table: staging.RateImport (MERGE target + source)
    MERGE dbo.Rate AS tgt
    USING staging.RateImport AS srcdata ON tgt.PropertyId = srcdata.PropertyId
    WHEN MATCHED THEN UPDATE SET tgt.Amount = srcdata.Amount
    WHEN NOT MATCHED THEN INSERT (PropertyId, Amount) VALUES (srcdata.PropertyId, srcdata.Amount);

    -- dynamic SQL: flagged unresolved, never silently dropped
    DECLARE @sql NVARCHAR(400) = N'DELETE FROM dbo.Rate WHERE Amount < 0';
    EXEC sp_executesql @sql;
END
GO
