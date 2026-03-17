-- Reporting system-of-record tables for Chromebook session attribution.
CREATE TABLE Devices (
  DeviceId bigint IDENTITY(1,1) PRIMARY KEY,
  DirectoryDeviceId nvarchar(128) NOT NULL UNIQUE,
  DeviceSerial nvarchar(128) NULL,
  AssetTag nvarchar(128) NULL,
  OrgUnit nvarchar(256) NULL,
  School nvarchar(256) NULL,
  DeviceType nvarchar(64) NOT NULL DEFAULT 'ChromeOS',
  FirstSeenUtc datetime2 NOT NULL,
  LastSeenUtc datetime2 NOT NULL,
  LastKnownInternalIp nvarchar(64) NULL,
  LastKnownPublicIp nvarchar(64) NULL,
  LastKnownUserEmail nvarchar(320) NULL
);
CREATE TABLE Sessions (
  SessionId uniqueidentifier PRIMARY KEY,
  DirectoryDeviceId nvarchar(128) NOT NULL,
  DeviceSerial nvarchar(128) NULL,
  UserEmail nvarchar(320) NULL,
  UserType nvarchar(32) NULL,
  LoginTimeUtc datetime2 NULL,
  LogoutTimeUtc datetime2 NULL,
  SessionStartTimeUtc datetime2 NOT NULL,
  SessionEndTimeUtc datetime2 NULL,
  LastSeenUtc datetime2 NOT NULL,
  InternalIp nvarchar(64) NULL,
  PublicIp nvarchar(64) NULL,
  AttributionConfidence nvarchar(16) NOT NULL,
  ExtensionVersion nvarchar(64) NULL,
  OrgUnit nvarchar(256) NULL,
  School nvarchar(256) NULL,
  IsActive bit NOT NULL DEFAULT 1,
  CreatedUtc datetime2 NOT NULL,
  UpdatedUtc datetime2 NOT NULL
);
CREATE TABLE ActivityEvents (
  ActivityEventId bigint IDENTITY(1,1) PRIMARY KEY,
  SessionId uniqueidentifier NOT NULL,
  DirectoryDeviceId nvarchar(128) NOT NULL,
  UserEmail nvarchar(320) NULL,
  EventType nvarchar(32) NOT NULL,
  EventTimeUtc datetime2 NOT NULL,
  Url nvarchar(2048) NULL,
  Domain nvarchar(512) NULL,
  Title nvarchar(1024) NULL,
  DownloadFileName nvarchar(512) NULL,
  DownloadMime nvarchar(256) NULL,
  DownloadDanger nvarchar(64) NULL,
  DownloadState nvarchar(64) NULL,
  InternalIp nvarchar(64) NULL,
  PublicIp nvarchar(64) NULL,
  IngestCorrelationId uniqueidentifier NOT NULL,
  CreatedUtc datetime2 NOT NULL,
  CONSTRAINT FK_ActivityEvents_Sessions FOREIGN KEY(SessionId) REFERENCES Sessions(SessionId)
);
CREATE TABLE SessionHeartbeats (
  SessionHeartbeatId bigint IDENTITY(1,1) PRIMARY KEY,
  SessionId uniqueidentifier NOT NULL,
  DirectoryDeviceId nvarchar(128) NOT NULL,
  UserEmail nvarchar(320) NULL,
  HeartbeatTimeUtc datetime2 NOT NULL,
  InternalIp nvarchar(64) NULL,
  PublicIp nvarchar(64) NULL,
  CreatedUtc datetime2 NOT NULL,
  CONSTRAINT FK_SessionHeartbeats_Sessions FOREIGN KEY(SessionId) REFERENCES Sessions(SessionId)
);
CREATE TABLE IngestionErrors (
  IngestionErrorId bigint IDENTITY(1,1) PRIMARY KEY,
  ErrorTimeUtc datetime2 NOT NULL,
  CorrelationId uniqueidentifier NOT NULL,
  Layer nvarchar(64) NOT NULL,
  ErrorCode nvarchar(128) NULL,
  ErrorMessage nvarchar(4000) NOT NULL,
  PayloadFragment nvarchar(max) NULL
);
