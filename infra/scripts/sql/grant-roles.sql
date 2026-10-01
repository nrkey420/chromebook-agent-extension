-- Adds Microsoft Entra groups to the database roles created by 004_security.sql.
-- Run as a person who is the SQL Entra admin (infra/scripts/grant-sql-roles.* runs this). Idempotent.
-- sqlcmd variables (each may be empty to skip that role; the name must match the Entra group's display name):
--   DeviceReadersGroup, InvestigatorsGroup, AuditReviewersGroup
--
-- Creating a user FROM EXTERNAL PROVIDER resolves the name in Entra ID; a service principal can only do that if the
-- SQL server has the "Directory Readers" role, so run this as a person.

SET NOCOUNT ON;
DECLARE @Map TABLE (RoleName sysname NOT NULL, GroupName nvarchar(128) NOT NULL);
INSERT @Map (RoleName, GroupName) VALUES
  (N'ChromebookDeviceReaders', LTRIM(RTRIM(N'$(DeviceReadersGroup)'))),
  (N'ChromebookInvestigators', LTRIM(RTRIM(N'$(InvestigatorsGroup)'))),
  (N'ChromebookAuditReviewers', LTRIM(RTRIM(N'$(AuditReviewersGroup)')));

IF EXISTS (SELECT 1 FROM @Map WHERE GroupName <> N'' AND DATABASE_PRINCIPAL_ID(RoleName) IS NULL)
  THROW 50010, 'The Chromebook roles do not exist yet: apply the schema first (infra/scripts/init-sql.*).', 1;

DECLARE @Role sysname, @Group nvarchar(128), @Sql nvarchar(max);
DECLARE groups CURSOR LOCAL FAST_FORWARD FOR SELECT RoleName, GroupName FROM @Map WHERE GroupName <> N'';
OPEN groups;
FETCH NEXT FROM groups INTO @Role, @Group;
WHILE @@FETCH_STATUS = 0
BEGIN
  IF DATABASE_PRINCIPAL_ID(@Group) IS NULL
  BEGIN
    SET @Sql = N'CREATE USER ' + QUOTENAME(@Group) + N' FROM EXTERNAL PROVIDER';
    EXEC (@Sql);
  END;
  SET @Sql = N'ALTER ROLE ' + QUOTENAME(@Role) + N' ADD MEMBER ' + QUOTENAME(@Group);
  EXEC (@Sql);
  PRINT CONCAT(N'Added ', @Group, N' to ', @Role);
  FETCH NEXT FROM groups INTO @Role, @Group;
END;
CLOSE groups;
DEALLOCATE groups;

-- Current membership of the Chromebook roles.
SELECT r.name AS RoleName, m.name AS Member, m.type_desc AS MemberType
FROM sys.database_role_members rm
JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
WHERE r.name LIKE N'Chromebook%'
ORDER BY r.name, m.name;
