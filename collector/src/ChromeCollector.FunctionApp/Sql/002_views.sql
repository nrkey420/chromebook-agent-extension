-- Investigation views: every view joins extension data with Google device and user data,
-- so investigators get serial/asset/location/student ID on every row. No names are stored or shown;
-- authorized staff resolve IDs to people in the student information system.
-- Local-time columns use ReportingSettings.ReportingTimeZone (Windows time zone name).

CREATE OR ALTER FUNCTION dbo.fn_ToLocal (@utc datetime2)
RETURNS datetime2
AS
BEGIN
  DECLARE @tz nvarchar(256) = COALESCE((SELECT SettingValue FROM dbo.ReportingSettings WHERE SettingName = 'ReportingTimeZone'), 'UTC');
  RETURN CAST((@utc AT TIME ZONE 'UTC') AT TIME ZONE @tz AS datetime2);
END;
GO

CREATE OR ALTER FUNCTION dbo.fn_ToUtc (@local datetime2)
RETURNS datetime2
AS
BEGIN
  DECLARE @tz nvarchar(256) = COALESCE((SELECT SettingValue FROM dbo.ReportingSettings WHERE SettingName = 'ReportingTimeZone'), 'UTC');
  RETURN CAST((@local AT TIME ZONE @tz) AT TIME ZONE 'UTC' AS datetime2);
END;
GO

-- Device inventory: Google inventory + latest extension check-in + extension health.
CREATE OR ALTER VIEW dbo.vw_Devices
AS
SELECT
  d.DirectoryDeviceId,
  d.SerialNumber,
  d.AssetId,
  d.AnnotatedLocation,
  d.AnnotatedUser,
  d.OrgUnitPath AS DeviceOrgUnit,
  d.Manufacturer,
  d.Model,
  d.OsVersion,
  d.AutoUpdateThrough,
  d.GoogleStatus,
  d.MacAddress,
  d.EthernetMacAddress,
  COALESCE(d.ExtLastUserEmail, ru.UserEmail) AS LastUserEmail,
  u.StudentId AS LastUserStudentId,
  d.GoogleLastSyncUtc,
  dbo.fn_ToLocal(d.GoogleLastSyncUtc) AS GoogleLastSyncLocal,
  d.GoogleLastLanIp,
  d.GoogleLastWanIp,
  d.ExtLastSeenUtc,
  dbo.fn_ToLocal(d.ExtLastSeenUtc) AS ExtLastSeenLocal,
  d.ExtLastInternalIp,
  d.ExtLastPublicIp,
  d.ExtLastMacAddress,
  d.ExtVersion,
  CASE
    WHEN d.ExtLastSeenUtc IS NULL THEN 'NEVER_REPORTED'
    WHEN d.GoogleLastSyncUtc IS NOT NULL AND d.ExtLastSeenUtc < DATEADD(day, -1, d.GoogleLastSyncUtc) THEN 'EXTENSION_SILENT'
    ELSE 'OK'
  END AS ExtensionReportingStatus
FROM dbo.Devices d
LEFT JOIN dbo.GoogleDeviceRecentUsers ru ON ru.DirectoryDeviceId = d.DirectoryDeviceId AND ru.Position = 0
LEFT JOIN dbo.GoogleUsers u ON u.UserEmail = COALESCE(d.ExtLastUserEmail, ru.UserEmail);
GO

-- Users with their most recent device seen by the extension.
CREATE OR ALTER VIEW dbo.vw_Users
AS
SELECT
  u.UserEmail,
  u.StudentId,
  u.OrgUnitPath AS UserOrgUnit,
  u.IsSuspended,
  u.IsArchived,
  u.LastLoginUtc AS GoogleLastLoginUtc,
  dbo.fn_ToLocal(u.LastLoginUtc) AS GoogleLastLoginLocal,
  ls.DirectoryDeviceId AS LastDeviceId,
  d.SerialNumber AS LastDeviceSerial,
  d.AssetId AS LastDeviceAssetId,
  ls.LastSeenUtc AS LastDeviceSeenUtc,
  dbo.fn_ToLocal(ls.LastSeenUtc) AS LastDeviceSeenLocal
FROM dbo.GoogleUsers u
OUTER APPLY (
  SELECT TOP 1 s.DirectoryDeviceId, s.LastSeenUtc
  FROM dbo.Sessions s
  WHERE s.UserEmail = u.UserEmail
  ORDER BY s.SessionStartUtc DESC
) ls
LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = ls.DirectoryDeviceId;
GO

-- Which device held which IP, and who was using it.
-- UserEmail comes from the extension when available; for Google-only observations the user is
-- inferred from the last ChromeOS login/logout event on that device before the observation.
CREATE OR ALTER VIEW dbo.vw_IpHistory
AS
SELECT
  o.ObservedUtc,
  dbo.fn_ToLocal(o.ObservedUtc) AS ObservedLocal,
  o.Source,
  o.InternalIp,
  o.InternalIpv6,
  o.PublicIp,
  o.MacAddress,
  o.DirectoryDeviceId,
  d.SerialNumber,
  d.AssetId,
  d.AnnotatedLocation,
  COALESCE(o.UserEmail, gl.UserEmail) AS UserEmail,
  CASE
    WHEN o.UserEmail IS NOT NULL THEN 'EXTENSION'
    WHEN gl.UserEmail IS NOT NULL THEN 'INFERRED_FROM_GOOGLE_LOGIN'
    ELSE NULL
  END AS UserSource,
  u.StudentId,
  o.SessionId
FROM dbo.IpObservations o
LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = o.DirectoryDeviceId
OUTER APPLY (
  SELECT TOP 1
    CASE WHEN g.EventName = 'CHROME_OS_LOGOUT_EVENT' THEN NULL ELSE COALESCE(g.UserEmail, g.ActorEmail) END AS UserEmail
  FROM dbo.GoogleAuditEvents g
  WHERE o.UserEmail IS NULL
    AND g.Application = 'chrome'
    AND g.DirectoryDeviceId = o.DirectoryDeviceId
    AND g.EventName IN ('CHROME_OS_LOGIN_EVENT', 'CHROME_OS_LOGOUT_EVENT')
    AND g.EventTimeUtc <= o.ObservedUtc
  ORDER BY g.EventTimeUtc DESC
) gl
LEFT JOIN dbo.GoogleUsers u ON u.UserEmail = COALESCE(o.UserEmail, gl.UserEmail);
GO

-- Every login/logout from every source, with device, student, and the IPs in use at that moment.
CREATE OR ALTER VIEW dbo.vw_LoginHistory
AS
WITH logins AS (
  SELECT
    'GOOGLE_CHROMEOS' AS Source,
    e.EventTimeUtc,
    CASE e.EventName
      WHEN 'CHROME_OS_LOGIN_EVENT' THEN 'LOGIN'
      WHEN 'CHROME_OS_LOGOUT_EVENT' THEN 'LOGOUT'
      WHEN 'CHROME_OS_LOGIN_FAILURE_EVENT' THEN 'LOGIN_FAILURE'
      WHEN 'CHROME_OS_LOGIN_LOGOUT_EVENT' THEN 'LOGIN_LOGOUT'
      ELSE e.EventName
    END AS LoginEvent,
    COALESCE(e.UserEmail, e.ActorEmail) AS UserEmail,
    e.DirectoryDeviceId,
    COALESCE(e.FailureReason, e.EventReason) AS Detail,
    e.IpAddress AS GoogleReportedIp,
    CAST(NULL AS uniqueidentifier) AS SessionId,
    CAST(NULL AS nvarchar(64)) AS ExtInternalIp,
    CAST(NULL AS nvarchar(64)) AS ExtPublicIp
  FROM dbo.GoogleAuditEvents e
  WHERE e.Application = 'chrome'
    AND e.EventName IN ('CHROME_OS_LOGIN_EVENT', 'CHROME_OS_LOGOUT_EVENT', 'CHROME_OS_LOGIN_FAILURE_EVENT', 'CHROME_OS_LOGIN_LOGOUT_EVENT')
  UNION ALL
  SELECT
    'GOOGLE_ACCOUNT',
    e.EventTimeUtc,
    UPPER(e.EventName),
    COALESCE(e.UserEmail, e.ActorEmail),
    NULL,
    e.FailureReason,
    e.IpAddress,
    NULL,
    NULL,
    NULL
  FROM dbo.GoogleAuditEvents e
  WHERE e.Application = 'login'
  UNION ALL
  SELECT
    'EXTENSION',
    COALESCE(s.LoginUtc, s.SessionStartUtc),
    'SESSION_START',
    s.UserEmail,
    s.DirectoryDeviceId,
    NULL,
    NULL,
    s.SessionId,
    s.FirstInternalIp,
    s.FirstPublicIp
  FROM dbo.Sessions s
  UNION ALL
  SELECT
    'EXTENSION',
    COALESCE(s.LogoutUtc, s.SessionEndUtc),
    'SESSION_END',
    s.UserEmail,
    s.DirectoryDeviceId,
    s.EndReason,
    NULL,
    s.SessionId,
    s.LastInternalIp,
    s.LastPublicIp
  FROM dbo.Sessions s
  WHERE COALESCE(s.LogoutUtc, s.SessionEndUtc) IS NOT NULL
)
SELECT
  l.EventTimeUtc,
  dbo.fn_ToLocal(l.EventTimeUtc) AS EventTimeLocal,
  l.Source,
  l.LoginEvent,
  l.UserEmail,
  u.StudentId,
  u.OrgUnitPath AS UserOrgUnit,
  l.DirectoryDeviceId,
  d.SerialNumber,
  d.AssetId,
  d.AnnotatedLocation,
  d.OrgUnitPath AS DeviceOrgUnit,
  COALESCE(l.ExtInternalIp, ip.InternalIp) AS InternalIp,
  COALESCE(l.ExtPublicIp, ip.PublicIp, l.GoogleReportedIp) AS PublicIp,
  l.GoogleReportedIp,
  CASE WHEN l.ExtInternalIp IS NOT NULL OR l.ExtPublicIp IS NOT NULL THEN 'EXTENSION' ELSE ip.Source END AS IpSource,
  l.Detail,
  l.SessionId
FROM logins l
LEFT JOIN dbo.GoogleUsers u ON u.UserEmail = l.UserEmail
LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = l.DirectoryDeviceId
OUTER APPLY (
  SELECT TOP 1 o.InternalIp, o.PublicIp, o.Source
  FROM dbo.IpObservations o
  WHERE l.ExtInternalIp IS NULL
    AND l.DirectoryDeviceId IS NOT NULL
    AND o.DirectoryDeviceId = l.DirectoryDeviceId
    AND o.ObservedUtc BETWEEN DATEADD(minute, -30, l.EventTimeUtc) AND DATEADD(minute, 30, l.EventTimeUtc)
  ORDER BY ABS(DATEDIFF(second, o.ObservedUtc, l.EventTimeUtc))
) ip;
GO

-- Device sign-ins in [@FromUtc, @ToUtc), for one user and/or one device (NULL = any). Two kinds of evidence:
--   GOOGLE_CHROMEOS  Google's ChromeOS login (and failed login) audit events, for every managed device and user.
--   EXTENSION        The extension's sessions that overlap the window (so a session that began earlier is included);
--                    only where the extension is installed for that user.
-- A sign-in Google saw but the extension did not usually means the extension is not installed for that user's OU.
-- Used by usp_UserDevices and usp_DeviceUsers; not granted to any role.
CREATE OR ALTER FUNCTION dbo.fn_SignIns (
  @FromUtc datetime2,
  @ToUtc datetime2,
  @UserEmail nvarchar(320),
  @DirectoryDeviceId nvarchar(128)
)
RETURNS TABLE
AS
RETURN
  SELECT
    'GOOGLE_CHROMEOS' AS Source,
    CASE e.EventName WHEN 'CHROME_OS_LOGIN_FAILURE_EVENT' THEN 'LOGIN_FAILURE' ELSE 'LOGIN' END AS SignInType,
    COALESCE(e.UserEmail, e.ActorEmail) AS UserEmail,
    e.DirectoryDeviceId,
    e.EventTimeUtc AS StartUtc,
    CAST(NULL AS datetime2) AS EndUtc,
    CAST(NULL AS nvarchar(64)) AS InternalIp,
    e.IpAddress AS PublicIp,
    CAST(NULL AS uniqueidentifier) AS SessionId
  FROM dbo.GoogleAuditEvents e
  WHERE e.Application = 'chrome'
    AND e.EventName IN ('CHROME_OS_LOGIN_EVENT', 'CHROME_OS_LOGIN_FAILURE_EVENT')
    AND e.DirectoryDeviceId IS NOT NULL
    AND e.EventTimeUtc >= @FromUtc AND e.EventTimeUtc < @ToUtc
    AND (@UserEmail IS NULL OR e.UserEmail = @UserEmail OR (e.UserEmail IS NULL AND e.ActorEmail = @UserEmail))
    AND (@DirectoryDeviceId IS NULL OR e.DirectoryDeviceId = @DirectoryDeviceId)
  UNION ALL
  SELECT
    'EXTENSION',
    'SESSION',
    s.UserEmail,
    s.DirectoryDeviceId,
    COALESCE(s.LoginUtc, s.SessionStartUtc),
    COALESCE(s.LogoutUtc, s.SessionEndUtc),
    COALESCE(s.LastInternalIp, s.FirstInternalIp),
    COALESCE(s.LastPublicIp, s.FirstPublicIp),
    s.SessionId
  FROM dbo.Sessions s
  WHERE s.SessionStartUtc < @ToUtc
    AND COALESCE(s.SessionEndUtc, s.LastSeenUtc) >= @FromUtc
    AND (@UserEmail IS NULL OR s.UserEmail = @UserEmail)
    AND (@DirectoryDeviceId IS NULL OR s.DirectoryDeviceId = @DirectoryDeviceId);
GO

-- Websites visited.
CREATE OR ALTER VIEW dbo.vw_WebActivity
AS
SELECT
  a.EventTimeUtc,
  dbo.fn_ToLocal(a.EventTimeUtc) AS EventTimeLocal,
  a.UserEmail,
  u.StudentId,
  u.OrgUnitPath AS UserOrgUnit,
  a.Domain,
  a.Url,
  a.Title,
  a.Transition,
  a.SearchEngine,
  a.SearchQuery,
  a.DirectoryDeviceId,
  d.SerialNumber,
  d.AssetId,
  a.InternalIp,
  a.PublicIp,
  a.SessionId
FROM dbo.ActivityEvents a
LEFT JOIN dbo.GoogleUsers u ON u.UserEmail = a.UserEmail
LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = a.DirectoryDeviceId
WHERE a.EventType = 'NAVIGATION';
GO

-- Searches (Google, Bing, YouTube, DuckDuckGo, Yahoo, etc.).
CREATE OR ALTER VIEW dbo.vw_SearchActivity
AS
SELECT
  a.EventTimeUtc,
  dbo.fn_ToLocal(a.EventTimeUtc) AS EventTimeLocal,
  a.UserEmail,
  u.StudentId,
  u.OrgUnitPath AS UserOrgUnit,
  a.SearchEngine,
  a.SearchQuery,
  a.Url,
  a.DirectoryDeviceId,
  d.SerialNumber,
  d.AssetId,
  a.InternalIp,
  a.PublicIp,
  a.SessionId
FROM dbo.ActivityEvents a
LEFT JOIN dbo.GoogleUsers u ON u.UserEmail = a.UserEmail
LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = a.DirectoryDeviceId
WHERE a.SearchQuery IS NOT NULL;
GO

-- Downloads.
CREATE OR ALTER VIEW dbo.vw_Downloads
AS
SELECT
  a.EventTimeUtc,
  dbo.fn_ToLocal(a.EventTimeUtc) AS EventTimeLocal,
  a.UserEmail,
  u.StudentId,
  a.DownloadFileName,
  a.DownloadMime,
  a.DownloadDanger,
  a.DownloadState,
  a.Url,
  a.Domain,
  a.DirectoryDeviceId,
  d.SerialNumber,
  d.AssetId,
  a.InternalIp,
  a.PublicIp
FROM dbo.ActivityEvents a
LEFT JOIN dbo.GoogleUsers u ON u.UserEmail = a.UserEmail
LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = a.DirectoryDeviceId
WHERE a.EventType = 'DOWNLOAD';
GO

-- One timeline across all sources (heartbeats excluded; see vw_IpHistory for those).
CREATE OR ALTER VIEW dbo.vw_InvestigationTimeline
AS
SELECT
  a.EventTimeUtc,
  dbo.fn_ToLocal(a.EventTimeUtc) AS EventTimeLocal,
  CAST('EXTENSION' AS nvarchar(32)) AS Source,
  CAST(a.EventType AS nvarchar(128)) AS EventType,
  a.UserEmail,
  u.StudentId,
  u.OrgUnitPath AS UserOrgUnit,
  a.DirectoryDeviceId,
  d.SerialNumber,
  d.AssetId,
  d.AnnotatedLocation,
  a.InternalIp,
  a.PublicIp,
  a.Domain,
  a.Url,
  a.Title,
  a.SearchQuery,
  CAST(COALESCE(a.DownloadFileName, a.Detail, a.Transition) AS nvarchar(1024)) AS Detail,
  a.SessionId
FROM dbo.ActivityEvents a
LEFT JOIN dbo.GoogleUsers u ON u.UserEmail = a.UserEmail
LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = a.DirectoryDeviceId
WHERE a.EventType <> 'HEARTBEAT'
UNION ALL
SELECT
  e.EventTimeUtc,
  dbo.fn_ToLocal(e.EventTimeUtc),
  CAST('GOOGLE_' + UPPER(e.Application) AS nvarchar(32)),
  e.EventName,
  COALESCE(e.UserEmail, e.ActorEmail),
  u.StudentId,
  u.OrgUnitPath,
  e.DirectoryDeviceId,
  d.SerialNumber,
  d.AssetId,
  d.AnnotatedLocation,
  NULL,
  e.IpAddress,
  NULL,
  NULL,
  NULL,
  NULL,
  CAST(COALESCE(e.FailureReason, e.EventReason) AS nvarchar(1024)),
  NULL
FROM dbo.GoogleAuditEvents e
LEFT JOIN dbo.GoogleUsers u ON u.UserEmail = COALESCE(e.UserEmail, e.ActorEmail)
LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = e.DirectoryDeviceId;
GO
