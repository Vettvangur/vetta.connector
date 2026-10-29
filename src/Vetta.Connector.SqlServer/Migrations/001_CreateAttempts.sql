-- Applied automatically by Vetta.Connector.SqlServer in the CUSTOMER WEBSITE database.
-- This script does not connect to or change Vetta's central database.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @result int;
EXEC @result = sys.sp_getapplock @Resource=N'VettaConnectorAttempts.Schema',
    @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
IF @result < 0 THROW 51000, 'Could not acquire the schema lock.', 1;
IF OBJECT_ID(N'dbo.VettaConnectorAttempts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VettaConnectorAttempts (
        Handle varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
        BrowserHash varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        UpstreamId uniqueidentifier NOT NULL,
        Expires bigint NOT NULL,
        Kind nvarchar(32) NOT NULL,
        Origin nvarchar(2048) NULL,
        Phase int NOT NULL CHECK (Phase BETWEEN 0 AND 6),
        Redirect nvarchar(2048) NULL,
        Lease bigint NOT NULL
    );
    CREATE INDEX IX_VettaConnectorAttempts_Browser ON dbo.VettaConnectorAttempts(BrowserHash);
    CREATE INDEX IX_VettaConnectorAttempts_Expires ON dbo.VettaConnectorAttempts(Expires);
END;
COMMIT;
