CREATE VIEW vw_CurrentActiveSessions AS
SELECT SessionId, UserEmail, DirectoryDeviceId, DeviceSerial, LoginTimeUtc, LastSeenUtc, InternalIp, PublicIp, AttributionConfidence, OrgUnit, School
FROM Sessions WHERE IsActive = 1;
GO
CREATE VIEW vw_DeviceLoginHistory AS
SELECT DirectoryDeviceId, DeviceSerial, UserEmail, SessionStartTimeUtc, SessionEndTimeUtc, LoginTimeUtc, LogoutTimeUtc, InternalIp, PublicIp, AttributionConfidence
FROM Sessions;
GO
CREATE VIEW vw_UserDeviceAttribution AS
SELECT UserEmail, DirectoryDeviceId, MAX(DeviceSerial) DeviceSerial, MIN(SessionStartTimeUtc) FirstSeenUtc, MAX(LastSeenUtc) LastSeenUtc,
COUNT(*) SessionCount, COUNT(DISTINCT InternalIp) DistinctInternalIpCount, COUNT(DISTINCT PublicIp) DistinctPublicIpCount
FROM Sessions GROUP BY UserEmail, DirectoryDeviceId;
GO
CREATE VIEW vw_DomainUsageByUser AS
SELECT CAST(EventTimeUtc AS date) UsageDate, UserEmail, Domain, COUNT(*) VisitCount, COUNT(DISTINCT DirectoryDeviceId) DistinctDeviceCount,
MIN(EventTimeUtc) FirstVisitUtc, MAX(EventTimeUtc) LastVisitUtc
FROM ActivityEvents WHERE EventType='NAVIGATION' GROUP BY CAST(EventTimeUtc AS date), UserEmail, Domain;
GO
CREATE VIEW vw_SessionTimeline AS
SELECT SessionId, UserEmail, DirectoryDeviceId, EventType, EventTimeUtc, Url, Domain, Title, DownloadFileName, DownloadDanger
FROM ActivityEvents;
GO
CREATE VIEW vw_DeviceUsageSummaryDaily AS
SELECT CAST(ae.EventTimeUtc AS date) UsageDate, ae.DirectoryDeviceId, MAX(s.DeviceSerial) DeviceSerial,
COUNT(DISTINCT ae.UserEmail) DistinctUserCount, COUNT(DISTINCT ae.SessionId) SessionCount,
SUM(CASE WHEN ae.EventType='NAVIGATION' THEN 1 ELSE 0 END) NavigationCount,
SUM(CASE WHEN ae.EventType='DOWNLOAD' THEN 1 ELSE 0 END) DownloadCount,
MIN(ae.EventTimeUtc) FirstSeenUtc, MAX(ae.EventTimeUtc) LastSeenUtc
FROM ActivityEvents ae LEFT JOIN Sessions s ON ae.SessionId=s.SessionId
GROUP BY CAST(ae.EventTimeUtc AS date), ae.DirectoryDeviceId;
