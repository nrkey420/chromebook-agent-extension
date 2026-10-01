namespace ChromeCollector.FunctionApp.Models;

// Rows written by the Google sync, already clamped to the SQL column sizes in 001_tables.sql.

/// <summary>A ChromeOS device from the Admin SDK Directory API (dbo.Devices Google columns).</summary>
public sealed record GoogleDevice
{
    public required string DirectoryDeviceId { get; init; }
    public string? SerialNumber { get; init; }
    public string? AssetId { get; init; }
    public string? AnnotatedLocation { get; init; }
    public string? AnnotatedUser { get; init; }
    public string? Notes { get; init; }
    public string? OrgUnitPath { get; init; }
    public string? Model { get; init; }
    public string? OsVersion { get; init; }
    public string? PlatformVersion { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? BootMode { get; init; }
    public string? GoogleStatus { get; init; }
    public string? MacAddress { get; init; }
    public string? EthernetMacAddress { get; init; }
    public string? AutoUpdateThrough { get; init; }
    public DateTime? FirstEnrollmentUtc { get; init; }
    public DateTime? LastEnrollmentUtc { get; init; }
    public DateTime? GoogleLastSyncUtc { get; init; }
    public string? GoogleLastLanIp { get; init; }
    public string? GoogleLastWanIp { get; init; }
    public IReadOnlyList<GoogleRecentUser> RecentUsers { get; init; } = [];
    public IReadOnlyList<GoogleActiveDay> ActiveDays { get; init; } = [];
}

/// <summary>One entry of a device's "recent users" list; Position 0 is the most recent.</summary>
public sealed record GoogleRecentUser(int Position, string? UserEmail, string? UserType);

/// <summary>Active minutes on a device for one day.</summary>
public sealed record GoogleActiveDay(DateOnly Date, int ActiveMinutes);

/// <summary>A Workspace user (dbo.GoogleUsers). No names are kept.</summary>
public sealed record GoogleUserRecord
{
    public required string UserEmail { get; init; }
    public string? GoogleUserId { get; init; }
    public string? OrgUnitPath { get; init; }
    public string? StudentId { get; init; }
    public bool? IsSuspended { get; init; }
    public bool? IsArchived { get; init; }
    public bool? IsAdmin { get; init; }
    public DateTime? LastLoginUtc { get; init; }
    public DateTime? CreatedInGoogleUtc { get; init; }
}

/// <summary>One event of a Reports API activity (dbo.GoogleAuditEvents).</summary>
public sealed record GoogleAuditEvent
{
    public required string Application { get; init; }
    public required string UniqueQualifier { get; init; }
    public required DateTime EventTimeUtc { get; init; }
    public required string EventName { get; init; }
    public string? EventType { get; init; }
    public string? UserEmail { get; init; }
    public string? ActorEmail { get; init; }
    public string? IpAddress { get; init; }
    public string? DirectoryDeviceId { get; init; }
    public string? DeviceName { get; init; }
    public string? DevicePlatform { get; init; }
    public string? EventReason { get; init; }
    public string? FailureReason { get; init; }
    public string? ParametersJson { get; init; }
}
