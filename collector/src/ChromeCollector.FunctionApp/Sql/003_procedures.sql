-- Investigation procedures. Times are entered in local time (ReportingSettings.ReportingTimeZone)
-- unless @TimesAreUtc = 1. Every run is recorded in dbo.InvestigationAudit (who, when, case, filters). Example:
--   EXEC dbo.usp_WhoWasOnIp @Ip = '10.20.30.40', @At = '2026-10-01 09:15', @CaseNumber = 'IR-2026-0142';
--   EXEC dbo.usp_DeviceTimeline @Device = '5CD1234XYZ', @From = '2026-10-01', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';
--   EXEC dbo.usp_UserTimeline @User = 'jdoe@district.org', @From = '2026-10-01', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';
--   EXEC dbo.usp_FindDevice @Search = '5CD1234XYZ';
--   EXEC dbo.usp_WebActivity @User = '123456', @From = '2026-09-28', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';
-- @CaseNumber is optional on the older procedures and required on usp_WebActivity.

-- Records one investigation run; returns the audit row ID so the caller can add the row count.
CREATE OR ALTER PROCEDURE dbo.usp_LogInvestigation
  @ProcedureName nvarchar(128),
  @CaseNumber nvarchar(64),
  @Parameters nvarchar(2000),
  @AuditId bigint = NULL OUTPUT
AS
BEGIN
  SET NOCOUNT ON;
  INSERT dbo.InvestigationAudit (CaseNumber, ProcedureName, Parameters)
  VALUES (NULLIF(LTRIM(RTRIM(@CaseNumber)), ''), @ProcedureName, LEFT(@Parameters, 2000));
  SET @AuditId = SCOPE_IDENTITY();
END;
GO

-- Who had this IP (internal or public) around a point in time?
-- Result 1: devices/users seen on the IP in the window. Result 2: Google account sign-ins from the IP.
CREATE OR ALTER PROCEDURE dbo.usp_WhoWasOnIp
  @Ip nvarchar(64),
  @At datetime2,
  @WindowMinutes int = 30,
  @TimesAreUtc bit = 0,
  @CaseNumber nvarchar(64) = NULL
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @AtUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @At ELSE dbo.fn_ToUtc(@At) END;
  DECLARE @FromUtc datetime2 = DATEADD(minute, -@WindowMinutes, @AtUtc);
  DECLARE @ToUtc datetime2 = DATEADD(minute, @WindowMinutes, @AtUtc);
  DECLARE @AuditParameters nvarchar(2000) = CONCAT('ip=', @Ip, ';atUtc=', CONVERT(nvarchar(30), @AtUtc, 126), ';windowMinutes=', @WindowMinutes);
  EXEC dbo.usp_LogInvestigation N'usp_WhoWasOnIp', @CaseNumber, @AuditParameters;

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
  @TimesAreUtc bit = 0,
  @CaseNumber nvarchar(64) = NULL
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @FromUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @From ELSE dbo.fn_ToUtc(@From) END;
  DECLARE @ToUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @To ELSE dbo.fn_ToUtc(@To) END;
  DECLARE @AuditParameters nvarchar(2000) = CONCAT('device=', @Device, ';fromUtc=', CONVERT(nvarchar(30), @FromUtc, 126), ';toUtc=', CONVERT(nvarchar(30), @ToUtc, 126));
  EXEC dbo.usp_LogInvestigation N'usp_DeviceTimeline', @CaseNumber, @AuditParameters;

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
  @TimesAreUtc bit = 0,
  @CaseNumber nvarchar(64) = NULL
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @FromUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @From ELSE dbo.fn_ToUtc(@From) END;
  DECLARE @ToUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @To ELSE dbo.fn_ToUtc(@To) END;
  DECLARE @AuditParameters nvarchar(2000) = CONCAT('user=', @User, ';fromUtc=', CONVERT(nvarchar(30), @FromUtc, 126), ';toUtc=', CONVERT(nvarchar(30), @ToUtc, 126));
  EXEC dbo.usp_LogInvestigation N'usp_UserTimeline', @CaseNumber, @AuditParameters;
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
  @Search nvarchar(320),
  @CaseNumber nvarchar(64) = NULL
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @AuditParameters nvarchar(2000) = CONCAT('search=', @Search);
  EXEC dbo.usp_LogInvestigation N'usp_FindDevice', @CaseNumber, @AuditParameters;
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

-- Web activity (page visits, searches, downloads) for a device and/or a user in a time window, for a case file.
-- @Device: serial number, asset ID or directory device ID. @User: email address or student ID.
-- Give either or both (both = that user on that device). @CaseNumber is required and recorded with the filters
-- and row count in dbo.InvestigationAudit. @Domain matches the domain and its subdomains ('example.com' also
-- matches 'www.example.com' and 'mail.example.com', not 'notexample.com').
-- Result 1: the activity, oldest first (at most @MaxRows rows; Truncated = 1 on every row if there were more).
-- Result 2: per-domain summary of the same filter (visits, searches, downloads, first/last seen).
CREATE OR ALTER PROCEDURE dbo.usp_WebActivity
  @Device nvarchar(256) = NULL,
  @User nvarchar(320) = NULL,
  @From datetime2,
  @To datetime2,
  @CaseNumber nvarchar(64),
  @Domain nvarchar(256) = NULL,
  @IncludeDownloads bit = 1,
  @TimesAreUtc bit = 0,
  @MaxRows int = 50000
AS
BEGIN
  SET NOCOUNT ON;
  SET @Device = NULLIF(LTRIM(RTRIM(@Device)), '');
  SET @User = NULLIF(LTRIM(RTRIM(@User)), '');
  SET @CaseNumber = NULLIF(LTRIM(RTRIM(@CaseNumber)), '');
  SET @Domain = LOWER(NULLIF(LTRIM(RTRIM(@Domain)), ''));
  IF @Domain LIKE 'www.%' SET @Domain = SUBSTRING(@Domain, 5, 256);

  IF @CaseNumber IS NULL THROW 50001, 'A case number is required (@CaseNumber).', 1;
  IF @Device IS NULL AND @User IS NULL THROW 50002, 'Specify @Device and/or @User.', 1;
  IF @From IS NULL OR @To IS NULL OR @From >= @To THROW 50003, '@From must be earlier than @To.', 1;
  IF @MaxRows IS NULL OR @MaxRows < 1 OR @MaxRows > 1000000 THROW 50004, '@MaxRows must be between 1 and 1000000.', 1;

  DECLARE @FromUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @From ELSE dbo.fn_ToUtc(@From) END;
  DECLARE @ToUtc datetime2 = CASE WHEN @TimesAreUtc = 1 THEN @To ELSE dbo.fn_ToUtc(@To) END;

  -- Emails are stored lower case; a student ID resolves through GoogleUsers.
  DECLARE @Email nvarchar(320) = NULL;
  IF @User IS NOT NULL
    SET @Email = COALESCE(
      (SELECT TOP 1 UserEmail FROM dbo.GoogleUsers WHERE UserEmail = LOWER(@User) OR StudentId = @User ORDER BY CASE WHEN UserEmail = LOWER(@User) THEN 0 ELSE 1 END),
      LOWER(@User));

  DECLARE @Ids TABLE (DirectoryDeviceId nvarchar(128) PRIMARY KEY);
  IF @Device IS NOT NULL
  BEGIN
    INSERT @Ids (DirectoryDeviceId)
    SELECT DirectoryDeviceId FROM dbo.Devices
    WHERE DirectoryDeviceId = @Device OR SerialNumber = @Device OR AssetId = @Device;
    -- Device may only be known from extension events.
    IF NOT EXISTS (SELECT 1 FROM @Ids) INSERT @Ids (DirectoryDeviceId) VALUES (@Device);
  END;

  -- Subdomain match with LIKE wildcards in the domain escaped.
  DECLARE @DomainSuffix nvarchar(300) = CASE WHEN @Domain IS NULL THEN NULL
    ELSE '%.' + REPLACE(REPLACE(REPLACE(@Domain, '[', '[[]'), '_', '[_]'), '%', '[%]') END;

  DECLARE @AuditId bigint;
  DECLARE @AuditParameters nvarchar(2000) = CONCAT(
    'device=', @Device, ';user=', @User, ';resolvedUser=', @Email,
    ';fromUtc=', CONVERT(nvarchar(30), @FromUtc, 126), ';toUtc=', CONVERT(nvarchar(30), @ToUtc, 126),
    ';domain=', @Domain, ';includeDownloads=', @IncludeDownloads, ';maxRows=', @MaxRows);
  EXEC dbo.usp_LogInvestigation N'usp_WebActivity', @CaseNumber, @AuditParameters, @AuditId OUTPUT;

  SELECT a.ActivityEventId, a.EventId, a.EventType, a.EventTimeUtc, a.SessionId, a.UserEmail, a.DirectoryDeviceId,
         a.Domain, a.Url, a.Title, a.Transition, a.SearchEngine, a.SearchQuery,
         a.DownloadFileName, a.DownloadMime, a.DownloadDanger, a.DownloadState, a.InternalIp, a.PublicIp
  INTO #Activity
  FROM dbo.ActivityEvents a
  WHERE a.EventTimeUtc >= @FromUtc AND a.EventTimeUtc < @ToUtc
    AND (a.EventType = 'NAVIGATION' OR (@IncludeDownloads = 1 AND a.EventType = 'DOWNLOAD'))
    AND (@Email IS NULL OR a.UserEmail = @Email)
    AND (@Device IS NULL OR a.DirectoryDeviceId IN (SELECT DirectoryDeviceId FROM @Ids))
    AND (@Domain IS NULL OR a.Domain = @Domain OR a.Domain LIKE @DomainSuffix)
  OPTION (RECOMPILE);

  DECLARE @Total int = @@ROWCOUNT;
  DECLARE @Returned int = CASE WHEN @Total > @MaxRows THEN @MaxRows ELSE @Total END;
  UPDATE dbo.InvestigationAudit SET RowsReturned = @Returned WHERE InvestigationAuditId = @AuditId;

  SELECT TOP (@MaxRows)
    a.EventTimeUtc,
    dbo.fn_ToLocal(a.EventTimeUtc) AS EventTimeLocal,
    a.EventType,
    a.UserEmail,
    u.StudentId,
    d.SerialNumber,
    d.AssetId,
    d.AnnotatedLocation,
    a.Domain,
    a.Url,
    a.Title,
    a.Transition,
    a.SearchEngine,
    a.SearchQuery,
    a.DownloadFileName,
    a.DownloadMime,
    a.DownloadDanger,
    a.DownloadState,
    a.InternalIp,
    a.PublicIp,
    a.DirectoryDeviceId,
    a.SessionId,
    a.EventId,
    CAST(CASE WHEN @Total > @MaxRows THEN 1 ELSE 0 END AS bit) AS Truncated,
    @CaseNumber AS CaseNumber
  FROM #Activity a
  LEFT JOIN dbo.GoogleUsers u ON u.UserEmail = a.UserEmail
  LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = a.DirectoryDeviceId
  ORDER BY a.EventTimeUtc, a.ActivityEventId;

  SELECT
    COALESCE(a.Domain, '(none)') AS Domain,
    SUM(CASE WHEN a.EventType = 'NAVIGATION' THEN 1 ELSE 0 END) AS Visits,
    SUM(CASE WHEN a.SearchQuery IS NOT NULL THEN 1 ELSE 0 END) AS Searches,
    SUM(CASE WHEN a.EventType = 'DOWNLOAD' THEN 1 ELSE 0 END) AS Downloads,
    MIN(a.EventTimeUtc) AS FirstSeenUtc,
    MAX(a.EventTimeUtc) AS LastSeenUtc,
    dbo.fn_ToLocal(MIN(a.EventTimeUtc)) AS FirstSeenLocal,
    dbo.fn_ToLocal(MAX(a.EventTimeUtc)) AS LastSeenLocal
  FROM #Activity a
  GROUP BY COALESCE(a.Domain, '(none)')
  ORDER BY Visits DESC, Domain;
END;
GO
