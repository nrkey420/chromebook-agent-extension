-- Investigation procedures. Times are entered in local time (ReportingSettings.ReportingTimeZone)
-- unless @TimesAreUtc = 1. Example:
--   EXEC dbo.usp_WhoWasOnIp @Ip = '10.20.30.40', @At = '2026-10-01 09:15';
--   EXEC dbo.usp_DeviceTimeline @Device = '5CD1234XYZ', @From = '2026-10-01', @To = '2026-10-02';
--   EXEC dbo.usp_UserTimeline @User = 'jdoe@district.org', @From = '2026-10-01', @To = '2026-10-02';
--   EXEC dbo.usp_FindDevice @Search = '5CD1234XYZ';

-- Who had this IP (internal or public) around a point in time?
-- Result 1: devices/users seen on the IP in the window. Result 2: Google account sign-ins from the IP.
CREATE OR ALTER PROCEDURE dbo.usp_WhoWasOnIp
  @Ip nvarchar(64),
  @At datetime2,
  @WindowMinutes int = 30,
  @TimesAreUtc bit = 0
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @AtUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @At ELSE dbo.fn_ToUtc(@At) END;
  DECLARE @FromUtc datetime2 = DATEADD(minute, -@WindowMinutes, @AtUtc);
  DECLARE @ToUtc datetime2 = DATEADD(minute, @WindowMinutes, @AtUtc);

  SELECT
    CASE WHEN h.InternalIp = @Ip THEN 'INTERNAL' ELSE 'PUBLIC' END AS MatchedOn,
    h.DirectoryDeviceId,
    h.SerialNumber,
    h.AssetId,
    h.AnnotatedLocation,
    h.UserEmail,
    h.StudentId,
    h.UserSource,
    h.Source AS IpSource,
    dbo.fn_ToLocal(MIN(h.ObservedUtc)) AS FirstSeenLocal,
    dbo.fn_ToLocal(MAX(h.ObservedUtc)) AS LastSeenLocal,
    MIN(h.ObservedUtc) AS FirstSeenUtc,
    MAX(h.ObservedUtc) AS LastSeenUtc,
    COUNT(*) AS Observations,
    MIN(ABS(DATEDIFF(second, h.ObservedUtc, @AtUtc))) AS ClosestSecondsFromTarget
  FROM dbo.vw_IpHistory h
  WHERE (h.InternalIp = @Ip OR h.PublicIp = @Ip)
    AND h.ObservedUtc BETWEEN @FromUtc AND @ToUtc
  GROUP BY
    CASE WHEN h.InternalIp = @Ip THEN 'INTERNAL' ELSE 'PUBLIC' END,
    h.DirectoryDeviceId, h.SerialNumber, h.AssetId, h.AnnotatedLocation,
    h.UserEmail, h.StudentId, h.UserSource, h.Source
  ORDER BY ClosestSecondsFromTarget;

  SELECT l.*
  FROM dbo.vw_LoginHistory l
  WHERE l.Source = 'GOOGLE_ACCOUNT'
    AND l.GoogleReportedIp = @Ip
    AND l.EventTimeUtc BETWEEN @FromUtc AND @ToUtc
  ORDER BY l.EventTimeUtc;
END;
GO

-- Everything that happened on a device. @Device may be the serial number, asset ID, or directory device ID.
-- Result 1: device profile. Result 2: logins. Result 3: full timeline. Result 4: IP history.
CREATE OR ALTER PROCEDURE dbo.usp_DeviceTimeline
  @Device nvarchar(256),
  @From datetime2,
  @To datetime2,
  @TimesAreUtc bit = 0
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @FromUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @From ELSE dbo.fn_ToUtc(@From) END;
  DECLARE @ToUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @To ELSE dbo.fn_ToUtc(@To) END;

  DECLARE @Ids TABLE (DirectoryDeviceId nvarchar(128) PRIMARY KEY);
  INSERT @Ids (DirectoryDeviceId)
  SELECT DirectoryDeviceId FROM dbo.Devices
  WHERE DirectoryDeviceId = @Device OR SerialNumber = @Device OR AssetId = @Device;

  -- Device may only be known from extension events (not yet synced from Google).
  IF NOT EXISTS (SELECT 1 FROM @Ids)
    INSERT @Ids (DirectoryDeviceId) VALUES (@Device);

  SELECT v.* FROM dbo.vw_Devices v WHERE v.DirectoryDeviceId IN (SELECT DirectoryDeviceId FROM @Ids);

  SELECT l.* FROM dbo.vw_LoginHistory l
  WHERE l.DirectoryDeviceId IN (SELECT DirectoryDeviceId FROM @Ids)
    AND l.EventTimeUtc BETWEEN @FromUtc AND @ToUtc
  ORDER BY l.EventTimeUtc;

  SELECT t.* FROM dbo.vw_InvestigationTimeline t
  WHERE t.DirectoryDeviceId IN (SELECT DirectoryDeviceId FROM @Ids)
    AND t.EventTimeUtc BETWEEN @FromUtc AND @ToUtc
  ORDER BY t.EventTimeUtc;

  SELECT h.* FROM dbo.vw_IpHistory h
  WHERE h.DirectoryDeviceId IN (SELECT DirectoryDeviceId FROM @Ids)
    AND h.ObservedUtc BETWEEN @FromUtc AND @ToUtc
  ORDER BY h.ObservedUtc;
END;
GO

-- Everything a user did. @User may be the email address or student ID.
-- Result 1: user profile. Result 2: logins. Result 3: full timeline. Result 4: searches.
CREATE OR ALTER PROCEDURE dbo.usp_UserTimeline
  @User nvarchar(320),
  @From datetime2,
  @To datetime2,
  @TimesAreUtc bit = 0
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @FromUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @From ELSE dbo.fn_ToUtc(@From) END;
  DECLARE @ToUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @To ELSE dbo.fn_ToUtc(@To) END;
  DECLARE @Email nvarchar(320) = COALESCE(
    (SELECT TOP 1 UserEmail FROM dbo.GoogleUsers WHERE UserEmail = @User OR StudentId = @User),
    @User);

  SELECT v.* FROM dbo.vw_Users v WHERE v.UserEmail = @Email;

  SELECT l.* FROM dbo.vw_LoginHistory l
  WHERE l.UserEmail = @Email AND l.EventTimeUtc BETWEEN @FromUtc AND @ToUtc
  ORDER BY l.EventTimeUtc;

  SELECT t.* FROM dbo.vw_InvestigationTimeline t
  WHERE t.UserEmail = @Email AND t.EventTimeUtc BETWEEN @FromUtc AND @ToUtc
  ORDER BY t.EventTimeUtc;

  SELECT s.* FROM dbo.vw_SearchActivity s
  WHERE s.UserEmail = @Email AND s.EventTimeUtc BETWEEN @FromUtc AND @ToUtc
  ORDER BY s.EventTimeUtc;
END;
GO

-- Find a device by serial, asset ID, directory ID, MAC address, last known IP, or last user.
CREATE OR ALTER PROCEDURE dbo.usp_FindDevice
  @Search nvarchar(320)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @Mac nvarchar(64) = LOWER(REPLACE(REPLACE(@Search, ':', ''), '-', ''));

  SELECT v.* FROM dbo.vw_Devices v
  WHERE v.DirectoryDeviceId = @Search
     OR v.SerialNumber = @Search
     OR v.AssetId = @Search
     OR LOWER(REPLACE(REPLACE(v.MacAddress, ':', ''), '-', '')) = @Mac
     OR LOWER(REPLACE(REPLACE(v.EthernetMacAddress, ':', ''), '-', '')) = @Mac
     OR LOWER(REPLACE(REPLACE(v.ExtLastMacAddress, ':', ''), '-', '')) = @Mac
     OR v.GoogleLastLanIp = @Search
     OR v.GoogleLastWanIp = @Search
     OR v.ExtLastInternalIp = @Search
     OR v.LastUserEmail = @Search;
END;
GO
