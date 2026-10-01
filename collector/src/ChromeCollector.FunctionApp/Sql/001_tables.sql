-- Chromebook investigation database: tables and indexes.
-- Idempotent: safe to re-run. Run in order: 001, 002, 003, 004.
--
-- Data sources:
--   EXTENSION        events sent by the force-installed Chrome extension (via the collector)
--   GOOGLE_DIRECTORY Google Admin SDK Directory API (device inventory, users)
--   GOOGLE_AUDIT     Google Admin SDK Reports API (ChromeOS login/logout, account sign-ins)
-- All timestamps are stored in UTC. Views expose local time using ReportingSettings.ReportingTimeZone.

IF OBJECT_ID('dbo.ReportingSettings', 'U') IS NULL
CREATE TABLE dbo.ReportingSettings (
  SettingName nvarchar(64) NOT NULL PRIMARY KEY,
  SettingValue nvarchar(256) NOT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM dbo.ReportingSettings WHERE SettingName = 'ReportingTimeZone')
  INSERT dbo.ReportingSettings (SettingName, SettingValue) VALUES ('ReportingTimeZone', 'Eastern Standard Time');
GO

-- One row per ChromeOS device. Google columns are written by the Google sync;
-- Ext* columns are written by the collector from extension check-ins.
IF OBJECT_ID('dbo.Devices', 'U') IS NULL
CREATE TABLE dbo.Devices (
  DirectoryDeviceId nvarchar(128) NOT NULL PRIMARY KEY,
  SerialNumber nvarchar(128) NULL,
  AssetId nvarchar(256) NULL,
  AnnotatedLocation nvarchar(256) NULL,
  AnnotatedUser nvarchar(256) NULL,
  Notes nvarchar(1000) NULL,
  OrgUnitPath nvarchar(512) NULL,
  Model nvarchar(256) NULL,
  Manufacturer nvarchar(256) NULL,
  OsVersion nvarchar(64) NULL,
  PlatformVersion nvarchar(128) NULL,
  FirmwareVersion nvarchar(128) NULL,
  BootMode nvarchar(32) NULL,
  GoogleStatus nvarchar(32) NULL,
  MacAddress nvarchar(64) NULL,
  EthernetMacAddress nvarchar(64) NULL,
  AutoUpdateThrough nvarchar(32) NULL,
  FirstEnrollmentUtc datetime2 NULL,
  LastEnrollmentUtc datetime2 NULL,
  GoogleLastSyncUtc datetime2 NULL,
  GoogleLastLanIp nvarchar(64) NULL,
  GoogleLastWanIp nvarchar(64) NULL,
  GoogleRefreshedUtc datetime2 NULL,
  ExtFirstSeenUtc datetime2 NULL,
  ExtLastSeenUtc datetime2 NULL,
  ExtLastUserEmail nvarchar(320) NULL,
  ExtLastInternalIp nvarchar(64) NULL,
  ExtLastInternalIpv6 nvarchar(64) NULL,
  ExtLastPublicIp nvarchar(64) NULL,
  ExtLastMacAddress nvarchar(64) NULL,
  ExtVersion nvarchar(32) NULL,
  ExtHostname nvarchar(256) NULL,
  ExtChromeVersion nvarchar(64) NULL,
  ExtPlatformVersion nvarchar(64) NULL,
  CreatedUtc datetime2 NOT NULL CONSTRAINT DF_Devices_CreatedUtc DEFAULT SYSUTCDATETIME(),
  UpdatedUtc datetime2 NOT NULL CONSTRAINT DF_Devices_UpdatedUtc DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Devices_SerialNumber')
  CREATE INDEX IX_Devices_SerialNumber ON dbo.Devices (SerialNumber);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Devices_AssetId')
  CREATE INDEX IX_Devices_AssetId ON dbo.Devices (AssetId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Devices_ExtLastUserEmail')
  CREATE INDEX IX_Devices_ExtLastUserEmail ON dbo.Devices (ExtLastUserEmail);
GO

-- Google's "recent users" list for each device (most recent first). Replaced on every sync.
IF OBJECT_ID('dbo.GoogleDeviceRecentUsers', 'U') IS NULL
CREATE TABLE dbo.GoogleDeviceRecentUsers (
  DirectoryDeviceId nvarchar(128) NOT NULL,
  Position int NOT NULL,
  UserEmail nvarchar(320) NULL,
  UserType nvarchar(32) NULL,
  RefreshedUtc datetime2 NOT NULL,
  CONSTRAINT PK_GoogleDeviceRecentUsers PRIMARY KEY (DirectoryDeviceId, Position)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_GoogleDeviceRecentUsers_UserEmail')
  CREATE INDEX IX_GoogleDeviceRecentUsers_UserEmail ON dbo.GoogleDeviceRecentUsers (UserEmail);
GO

-- Google's per-day active usage for each device.
IF OBJECT_ID('dbo.GoogleDeviceActiveTime', 'U') IS NULL
CREATE TABLE dbo.GoogleDeviceActiveTime (
  DirectoryDeviceId nvarchar(128) NOT NULL,
  ActiveDate date NOT NULL,
  ActiveMinutes int NOT NULL,
  RefreshedUtc datetime2 NOT NULL,
  CONSTRAINT PK_GoogleDeviceActiveTime PRIMARY KEY (DirectoryDeviceId, ActiveDate)
);
GO

-- Google Workspace users (students and staff), keyed by the account email, which is an internal ID number.
-- No names are stored here: authorized staff resolve an ID to a person in the student information system.
IF OBJECT_ID('dbo.GoogleUsers', 'U') IS NULL
CREATE TABLE dbo.GoogleUsers (
  UserEmail nvarchar(320) NOT NULL PRIMARY KEY,
  GoogleUserId nvarchar(64) NULL,
  OrgUnitPath nvarchar(512) NULL,
  StudentId nvarchar(128) NULL,
  IsSuspended bit NULL,
  IsArchived bit NULL,
  IsAdmin bit NULL,
  LastLoginUtc datetime2 NULL,
  CreatedInGoogleUtc datetime2 NULL,
  RefreshedUtc datetime2 NOT NULL
);
GO
-- Remove name columns from databases created by an earlier version of this script.
IF COL_LENGTH('dbo.GoogleUsers', 'FullName') IS NOT NULL ALTER TABLE dbo.GoogleUsers DROP COLUMN FullName;
IF COL_LENGTH('dbo.GoogleUsers', 'GivenName') IS NOT NULL ALTER TABLE dbo.GoogleUsers DROP COLUMN GivenName;
IF COL_LENGTH('dbo.GoogleUsers', 'FamilyName') IS NOT NULL ALTER TABLE dbo.GoogleUsers DROP COLUMN FamilyName;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_GoogleUsers_StudentId')
  CREATE INDEX IX_GoogleUsers_StudentId ON dbo.GoogleUsers (StudentId);
GO

-- Google audit events: ChromeOS login/logout/login failure (application 'chrome')
-- and Google account sign-ins with source IP (application 'login').
IF OBJECT_ID('dbo.GoogleAuditEvents', 'U') IS NULL
CREATE TABLE dbo.GoogleAuditEvents (
  AuditEventId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
  Application nvarchar(32) NOT NULL,
  UniqueQualifier nvarchar(64) NOT NULL,
  EventTimeUtc datetime2 NOT NULL,
  EventType nvarchar(128) NULL,
  EventName nvarchar(128) NOT NULL,
  UserEmail nvarchar(320) NULL,
  ActorEmail nvarchar(320) NULL,
  IpAddress nvarchar(64) NULL,
  DirectoryDeviceId nvarchar(128) NULL,
  DeviceName nvarchar(256) NULL,
  DevicePlatform nvarchar(128) NULL,
  EventReason nvarchar(256) NULL,
  FailureReason nvarchar(256) NULL,
  ParametersJson nvarchar(max) NULL,
  IngestedUtc datetime2 NOT NULL CONSTRAINT DF_GoogleAuditEvents_IngestedUtc DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_GoogleAuditEvents_Identity')
  CREATE UNIQUE INDEX UX_GoogleAuditEvents_Identity
    ON dbo.GoogleAuditEvents (Application, UniqueQualifier, EventName, EventTimeUtc) WITH (IGNORE_DUP_KEY = ON);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_GoogleAuditEvents_Device_Time')
  CREATE INDEX IX_GoogleAuditEvents_Device_Time ON dbo.GoogleAuditEvents (DirectoryDeviceId, EventTimeUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_GoogleAuditEvents_User_Time')
  CREATE INDEX IX_GoogleAuditEvents_User_Time ON dbo.GoogleAuditEvents (UserEmail, EventTimeUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_GoogleAuditEvents_Ip_Time')
  CREATE INDEX IX_GoogleAuditEvents_Ip_Time ON dbo.GoogleAuditEvents (IpAddress, EventTimeUtc DESC);
GO

-- Extension sessions: one row per user session on a device.
IF OBJECT_ID('dbo.Sessions', 'U') IS NULL
CREATE TABLE dbo.Sessions (
  SessionId uniqueidentifier NOT NULL PRIMARY KEY,
  DirectoryDeviceId nvarchar(128) NOT NULL,
  SerialNumber nvarchar(128) NULL,
  UserEmail nvarchar(320) NULL,
  SessionStartUtc datetime2 NOT NULL,
  SessionEndUtc datetime2 NULL,
  LoginUtc datetime2 NULL,
  LogoutUtc datetime2 NULL,
  LastSeenUtc datetime2 NOT NULL,
  EndReason nvarchar(64) NULL,
  FirstInternalIp nvarchar(64) NULL,
  LastInternalIp nvarchar(64) NULL,
  FirstPublicIp nvarchar(64) NULL,
  LastPublicIp nvarchar(64) NULL,
  MacAddress nvarchar(64) NULL,
  ExtensionVersion nvarchar(32) NULL,
  IsActive bit NOT NULL,
  CreatedUtc datetime2 NOT NULL CONSTRAINT DF_Sessions_CreatedUtc DEFAULT SYSUTCDATETIME(),
  UpdatedUtc datetime2 NOT NULL CONSTRAINT DF_Sessions_UpdatedUtc DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Sessions_Device_Start')
  CREATE INDEX IX_Sessions_Device_Start ON dbo.Sessions (DirectoryDeviceId, SessionStartUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Sessions_User_Start')
  CREATE INDEX IX_Sessions_User_Start ON dbo.Sessions (UserEmail, SessionStartUtc DESC);
GO

-- Every extension event: navigation, search, download, session lifecycle, lock/unlock, heartbeat.
IF OBJECT_ID('dbo.ActivityEvents', 'U') IS NULL
CREATE TABLE dbo.ActivityEvents (
  ActivityEventId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
  EventId uniqueidentifier NULL,
  SessionId uniqueidentifier NULL,
  DirectoryDeviceId nvarchar(128) NOT NULL,
  UserEmail nvarchar(320) NULL,
  EventType nvarchar(32) NOT NULL,
  EventTimeUtc datetime2 NOT NULL,
  Url nvarchar(2048) NULL,
  Domain nvarchar(256) NULL,
  Title nvarchar(1024) NULL,
  SearchEngine nvarchar(32) NULL,
  SearchQuery nvarchar(1024) NULL,
  Transition nvarchar(64) NULL,
  Detail nvarchar(1024) NULL,
  DownloadFileName nvarchar(512) NULL,
  DownloadMime nvarchar(256) NULL,
  DownloadDanger nvarchar(64) NULL,
  DownloadState nvarchar(64) NULL,
  InternalIp nvarchar(64) NULL,
  InternalIpv6 nvarchar(64) NULL,
  PublicIp nvarchar(64) NULL,
  MacAddress nvarchar(64) NULL,
  IngestCorrelationId nvarchar(64) NULL,
  ReceivedUtc datetime2 NOT NULL CONSTRAINT DF_ActivityEvents_ReceivedUtc DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ActivityEvents_EventId')
  CREATE UNIQUE INDEX UX_ActivityEvents_EventId ON dbo.ActivityEvents (EventId) WHERE EventId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActivityEvents_Device_Time')
  CREATE INDEX IX_ActivityEvents_Device_Time ON dbo.ActivityEvents (DirectoryDeviceId, EventTimeUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActivityEvents_User_Time')
  CREATE INDEX IX_ActivityEvents_User_Time ON dbo.ActivityEvents (UserEmail, EventTimeUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActivityEvents_Session_Time')
  CREATE INDEX IX_ActivityEvents_Session_Time ON dbo.ActivityEvents (SessionId, EventTimeUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActivityEvents_Domain_Time')
  CREATE INDEX IX_ActivityEvents_Domain_Time ON dbo.ActivityEvents (Domain, EventTimeUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActivityEvents_Search_Time')
  CREATE INDEX IX_ActivityEvents_Search_Time ON dbo.ActivityEvents (EventTimeUtc DESC) INCLUDE (SearchQuery, UserEmail) WHERE SearchQuery IS NOT NULL;
GO

-- Point-in-time record of which device (and user) held which IP address.
-- Written by the collector (extension heartbeats, session start, network changes)
-- and by the Google sync (device's last known LAN/WAN IP at its last Google check-in).
IF OBJECT_ID('dbo.IpObservations', 'U') IS NULL
CREATE TABLE dbo.IpObservations (
  IpObservationId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
  ObservedUtc datetime2 NOT NULL,
  Source nvarchar(32) NOT NULL,
  DirectoryDeviceId nvarchar(128) NOT NULL,
  UserEmail nvarchar(320) NULL,
  SessionId uniqueidentifier NULL,
  InternalIp nvarchar(64) NULL,
  InternalIpv6 nvarchar(64) NULL,
  PublicIp nvarchar(64) NULL,
  MacAddress nvarchar(64) NULL,
  CreatedUtc datetime2 NOT NULL CONSTRAINT DF_IpObservations_CreatedUtc DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IpObservations_InternalIp_Time')
  CREATE INDEX IX_IpObservations_InternalIp_Time ON dbo.IpObservations (InternalIp, ObservedUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IpObservations_PublicIp_Time')
  CREATE INDEX IX_IpObservations_PublicIp_Time ON dbo.IpObservations (PublicIp, ObservedUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IpObservations_Device_Time')
  CREATE INDEX IX_IpObservations_Device_Time ON dbo.IpObservations (DirectoryDeviceId, ObservedUtc);
GO

-- Watermarks and last-run status for the Google sync jobs.
IF OBJECT_ID('dbo.SyncState', 'U') IS NULL
CREATE TABLE dbo.SyncState (
  SyncName nvarchar(64) NOT NULL PRIMARY KEY,
  WatermarkUtc datetime2 NULL,
  LastRunUtc datetime2 NULL,
  LastStatus nvarchar(16) NULL,
  LastMessage nvarchar(2000) NULL,
  ItemsProcessed int NULL
);
GO

IF OBJECT_ID('dbo.IngestionErrors', 'U') IS NULL
CREATE TABLE dbo.IngestionErrors (
  IngestionErrorId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
  ErrorTimeUtc datetime2 NOT NULL,
  CorrelationId nvarchar(64) NOT NULL,
  Layer nvarchar(64) NOT NULL,
  ErrorCode nvarchar(128) NULL,
  ErrorMessage nvarchar(4000) NOT NULL,
  BlobPath nvarchar(1024) NULL
);
GO

-- Who ran which investigation procedure, for which case, with which filters, and how many rows came back.
-- Written by the usp_* procedures themselves; investigators get EXECUTE on the procedures, not write access here.
IF OBJECT_ID('dbo.InvestigationAudit', 'U') IS NULL
CREATE TABLE dbo.InvestigationAudit (
  InvestigationAuditId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
  RunUtc datetime2 NOT NULL CONSTRAINT DF_InvestigationAudit_RunUtc DEFAULT SYSUTCDATETIME(),
  RunBy nvarchar(256) NOT NULL CONSTRAINT DF_InvestigationAudit_RunBy DEFAULT ORIGINAL_LOGIN(),
  CaseNumber nvarchar(64) NULL,
  ProcedureName nvarchar(128) NOT NULL,
  Parameters nvarchar(2000) NULL,
  RowsReturned int NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InvestigationAudit_Case')
  CREATE INDEX IX_InvestigationAudit_Case ON dbo.InvestigationAudit (CaseNumber, RunUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InvestigationAudit_RunBy_Time')
  CREATE INDEX IX_InvestigationAudit_RunBy_Time ON dbo.InvestigationAudit (RunBy, RunUtc);
GO
