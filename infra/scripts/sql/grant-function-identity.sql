-- Grants the Function App's system-assigned managed identity access to the database.
-- Run as the SQL Microsoft Entra admin (init-sql.* does this after creating the schema).
-- sqlcmd variable: FunctionAppName = the Function App name (its managed identity has the same display name).
-- Idempotent: safe to re-run.
--
-- If this is run by a service principal (for example from CI) rather than a person, the SQL server
-- needs an identity with the Microsoft Entra "Directory Readers" role to resolve the name.

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(FunctionAppName)')
  EXEC (N'CREATE USER [$(FunctionAppName)] FROM EXTERNAL PROVIDER');
GO

-- The collector only reads and writes tables (INSERT/UPDATE/MERGE); it does not need schema rights.
ALTER ROLE db_datareader ADD MEMBER [$(FunctionAppName)];
ALTER ROLE db_datawriter ADD MEMBER [$(FunctionAppName)];
GO
