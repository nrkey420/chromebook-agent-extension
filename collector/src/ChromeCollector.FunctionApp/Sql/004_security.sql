-- Database roles for people. Idempotent: safe to re-run. Run after 001-003.
-- Entra groups are added to these roles by infra/scripts/grant-sql-roles.* (docs/sql-access.md).
--
--   ChromebookDeviceReaders   helpdesk / ops: devices, users, logins, sync and ingestion health. No web content.
--   ChromebookInvestigators   IR analysts: the above plus IP history, and every investigation procedure.
--                             Web content only through the audited procedures (usp_WebActivity, usp_*Timeline).
--   ChromebookAuditReviewers  IR lead / compliance: read dbo.InvestigationAudit.
--
-- The views and procedures are owned by dbo, so ownership chaining lets them read the tables without the caller
-- holding SELECT on those tables. The DENYs keep web content and the audit trail closed even if a member is later
-- given broad rights such as db_datareader. They do not restrict dbo (the SQL Entra admin group): keep that group
-- to the people who administer the database, never analysts.

IF DATABASE_PRINCIPAL_ID(N'ChromebookDeviceReaders') IS NULL CREATE ROLE ChromebookDeviceReaders;
IF DATABASE_PRINCIPAL_ID(N'ChromebookInvestigators') IS NULL CREATE ROLE ChromebookInvestigators;
IF DATABASE_PRINCIPAL_ID(N'ChromebookAuditReviewers') IS NULL CREATE ROLE ChromebookAuditReviewers;
GO

-- Device readers.
GRANT SELECT ON dbo.vw_Devices TO ChromebookDeviceReaders;
GRANT SELECT ON dbo.vw_Users TO ChromebookDeviceReaders;
GRANT SELECT ON dbo.vw_LoginHistory TO ChromebookDeviceReaders;
GRANT SELECT ON dbo.SyncState TO ChromebookDeviceReaders;
GRANT SELECT ON dbo.IngestionErrors TO ChromebookDeviceReaders;
GRANT EXECUTE ON dbo.usp_FindDevice TO ChromebookDeviceReaders;
GO

-- Investigators.
GRANT SELECT ON dbo.vw_Devices TO ChromebookInvestigators;
GRANT SELECT ON dbo.vw_Users TO ChromebookInvestigators;
GRANT SELECT ON dbo.vw_LoginHistory TO ChromebookInvestigators;
GRANT SELECT ON dbo.vw_IpHistory TO ChromebookInvestigators;
GRANT EXECUTE ON dbo.usp_FindDevice TO ChromebookInvestigators;
GRANT EXECUTE ON dbo.usp_WhoWasOnIp TO ChromebookInvestigators;
GRANT EXECUTE ON dbo.usp_DeviceTimeline TO ChromebookInvestigators;
GRANT EXECUTE ON dbo.usp_UserTimeline TO ChromebookInvestigators;
GRANT EXECUTE ON dbo.usp_WebActivity TO ChromebookInvestigators;
GO

-- Audit reviewers.
GRANT SELECT ON dbo.InvestigationAudit TO ChromebookAuditReviewers;
GO

-- Local-time helpers, for people's own queries.
GRANT EXECUTE ON dbo.fn_ToLocal TO ChromebookDeviceReaders;
GRANT EXECUTE ON dbo.fn_ToLocal TO ChromebookInvestigators;
GRANT EXECUTE ON dbo.fn_ToLocal TO ChromebookAuditReviewers;
GRANT EXECUTE ON dbo.fn_ToUtc TO ChromebookDeviceReaders;
GRANT EXECUTE ON dbo.fn_ToUtc TO ChromebookInvestigators;
GRANT EXECUTE ON dbo.fn_ToUtc TO ChromebookAuditReviewers;
GO

-- Web content: never read directly by any of these roles.
DENY SELECT ON dbo.ActivityEvents TO ChromebookDeviceReaders;
DENY SELECT ON dbo.ActivityEvents TO ChromebookInvestigators;
DENY SELECT ON dbo.ActivityEvents TO ChromebookAuditReviewers;
DENY SELECT ON dbo.vw_WebActivity TO ChromebookDeviceReaders;
DENY SELECT ON dbo.vw_WebActivity TO ChromebookInvestigators;
DENY SELECT ON dbo.vw_WebActivity TO ChromebookAuditReviewers;
DENY SELECT ON dbo.vw_SearchActivity TO ChromebookDeviceReaders;
DENY SELECT ON dbo.vw_SearchActivity TO ChromebookInvestigators;
DENY SELECT ON dbo.vw_SearchActivity TO ChromebookAuditReviewers;
DENY SELECT ON dbo.vw_Downloads TO ChromebookDeviceReaders;
DENY SELECT ON dbo.vw_Downloads TO ChromebookInvestigators;
DENY SELECT ON dbo.vw_Downloads TO ChromebookAuditReviewers;
DENY SELECT ON dbo.vw_InvestigationTimeline TO ChromebookDeviceReaders;
DENY SELECT ON dbo.vw_InvestigationTimeline TO ChromebookInvestigators;
DENY SELECT ON dbo.vw_InvestigationTimeline TO ChromebookAuditReviewers;
GO

-- The audit trail: written only by the procedures, never changed or forged by these roles.
DENY INSERT, UPDATE, DELETE ON dbo.InvestigationAudit TO ChromebookDeviceReaders;
DENY INSERT, UPDATE, DELETE ON dbo.InvestigationAudit TO ChromebookInvestigators;
DENY INSERT, UPDATE, DELETE ON dbo.InvestigationAudit TO ChromebookAuditReviewers;
DENY EXECUTE ON dbo.usp_LogInvestigation TO ChromebookDeviceReaders;
DENY EXECUTE ON dbo.usp_LogInvestigation TO ChromebookInvestigators;
DENY EXECUTE ON dbo.usp_LogInvestigation TO ChromebookAuditReviewers;
GO
