using System.Globalization;
using System.Text.Json;
using ChromeCollector.FunctionApp.Models;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Google.Apis.Admin.Reports.reports_v1.Data;
using Newtonsoft.Json.Linq;

namespace ChromeCollector.FunctionApp.Services;

/// <summary>Maps Admin SDK objects to SQL rows. Pure functions so they can be tested without Google.</summary>
public static class GoogleMapper
{
    public static GoogleDevice? MapDevice(ChromeOsDevice d, DateOnly activeSince)
    {
        if (string.IsNullOrWhiteSpace(d.DeviceId)) return null;

        var network = d.LastKnownNetwork?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.IpAddress) || !string.IsNullOrWhiteSpace(n.WanIpAddress));

        return new GoogleDevice
        {
            DirectoryDeviceId = Clamp(d.DeviceId.Trim(), 128)!,
            SerialNumber = Clamp(d.SerialNumber, 128),
            AssetId = Clamp(d.AnnotatedAssetId, 256),
            AnnotatedLocation = Clamp(d.AnnotatedLocation, 256),
            AnnotatedUser = Clamp(d.AnnotatedUser, 256),
            Notes = Clamp(d.Notes, 1000),
            OrgUnitPath = Clamp(d.OrgUnitPath, 512),
            Model = Clamp(d.Model, 256),
            OsVersion = Clamp(d.OsVersion, 64),
            PlatformVersion = Clamp(d.PlatformVersion, 128),
            FirmwareVersion = Clamp(d.FirmwareVersion, 128),
            BootMode = Clamp(d.BootMode, 32),
            GoogleStatus = Clamp(d.Status, 32),
            MacAddress = Clamp(d.MacAddress, 64),
            EthernetMacAddress = Clamp(d.EthernetMacAddress, 64),
            AutoUpdateThrough = Clamp(d.AutoUpdateThrough ?? FormatEpochDate(d.AutoUpdateExpiration), 32),
            FirstEnrollmentUtc = ParseUtc(d.FirstEnrollmentTime),
            LastEnrollmentUtc = Utc(d.LastEnrollmentTimeDateTimeOffset),
            GoogleLastSyncUtc = Utc(d.LastSyncDateTimeOffset),
            GoogleLastLanIp = Clamp(network?.IpAddress, 64),
            GoogleLastWanIp = Clamp(network?.WanIpAddress, 64),
            RecentUsers = (d.RecentUsers ?? [])
                .Select((u, i) => new GoogleRecentUser(i, Clamp(NormalizeEmail(u.Email), 320), Clamp(u.Type, 32)))
                .ToList(),
            ActiveDays = (d.ActiveTimeRanges ?? [])
                .Select(r => (Date: ParseDate(r.Date), Ms: r.ActiveTime ?? 0))
                .Where(r => r.Date is not null && r.Date >= activeSince)
                .GroupBy(r => r.Date!.Value)
                .Select(g => new GoogleActiveDay(g.Key, (int)Math.Round(g.Max(r => r.Ms) / 60_000.0, MidpointRounding.AwayFromZero)))
                .OrderBy(a => a.Date)
                .ToList(),
        };
    }

    public static GoogleUserRecord? MapUser(User u, string studentIdSource)
    {
        var email = NormalizeEmail(u.PrimaryEmail);
        if (email is null) return null;

        return new GoogleUserRecord
        {
            UserEmail = Clamp(email, 320)!,
            GoogleUserId = Clamp(u.Id, 64),
            OrgUnitPath = Clamp(u.OrgUnitPath, 512),
            StudentId = Clamp(ResolveStudentId(u, email, studentIdSource), 128),
            IsSuspended = u.Suspended,
            IsArchived = u.Archived,
            IsAdmin = u.IsAdmin,
            // Google reports 1970-01-01 for accounts that never signed in.
            LastLoginUtc = Utc(u.LastLoginTimeDateTimeOffset) is { Year: > 1970 } login ? login : null,
            CreatedInGoogleUtc = Utc(u.CreationTimeDateTimeOffset),
        };
    }

    /// <summary>
    /// Student ID from the configured source: none, emailLocalPart, externalId (first), externalId:&lt;type&gt;
    /// (matches type or customType, e.g. "organization" = Employee ID in the Admin console), customSchema:Schema.Field.
    /// </summary>
    public static string? ResolveStudentId(User u, string email, string source)
    {
        var (kind, arg) = source.IndexOf(':') is var i and >= 0 ? (source[..i].Trim(), source[(i + 1)..].Trim()) : (source.Trim(), "");

        if (kind.Equals("emailLocalPart", StringComparison.OrdinalIgnoreCase))
            return email.Split('@')[0];

        if (kind.Equals("externalId", StringComparison.OrdinalIgnoreCase))
        {
            var ids = u.ExternalIds ?? [];
            var match = arg.Length == 0
                ? ids.FirstOrDefault()
                : ids.FirstOrDefault(x => string.Equals(x.Type, arg, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(x.CustomType, arg, StringComparison.OrdinalIgnoreCase));
            return NullIfEmpty(match?.Value);
        }

        if (kind.Equals("customSchema", StringComparison.OrdinalIgnoreCase))
        {
            var dot = arg.IndexOf('.');
            if (dot <= 0 || u.CustomSchemas is null) return null;
            var schema = u.CustomSchemas.FirstOrDefault(s => s.Key.Equals(arg[..dot], StringComparison.OrdinalIgnoreCase)).Value;
            var field = schema?.FirstOrDefault(f => f.Key.Equals(arg[(dot + 1)..], StringComparison.OrdinalIgnoreCase)).Value;
            return NullIfEmpty(CustomFieldValue(field));
        }

        return null;
    }

    /// <summary>One row per event in the activity (an activity can carry several events).</summary>
    public static IEnumerable<GoogleAuditEvent> MapActivity(Activity a)
    {
        var time = Utc(a.Id?.TimeDateTimeOffset);
        var application = a.Id?.ApplicationName?.Trim().ToLowerInvariant();
        if (time is null || string.IsNullOrEmpty(application) || a.Events is null) yield break;

        var actorEmail = NormalizeEmail(a.Actor?.Email);
        var uniqueQualifier = a.Id!.UniqueQualifier?.ToString(CultureInfo.InvariantCulture) ?? "";

        foreach (var e in a.Events)
        {
            if (string.IsNullOrWhiteSpace(e.Name)) continue;
            var p = Parameters(e);

            yield return new GoogleAuditEvent
            {
                Application = Clamp(application, 32)!,
                UniqueQualifier = Clamp(uniqueQualifier, 64)!,
                EventTimeUtc = time.Value,
                EventName = Clamp(e.Name, 128)!,
                EventType = Clamp(e.Type, 128),
                // ChromeOS events name the signed-in user in DEVICE_USER; account events use the actor.
                UserEmail = Clamp(NormalizeEmail(Get(p, "DEVICE_USER")) ?? NormalizeEmail(Get(p, "affected_email_address")) ?? actorEmail, 320),
                ActorEmail = Clamp(actorEmail, 320),
                IpAddress = Clamp(a.IpAddress, 64),
                DirectoryDeviceId = Clamp(Get(p, "DIRECTORY_DEVICE_ID"), 128),
                DeviceName = Clamp(Get(p, "DEVICE_NAME"), 256),
                DevicePlatform = Clamp(Get(p, "DEVICE_PLATFORM"), 128),
                EventReason = Clamp(Get(p, "EVENT_REASON"), 256),
                FailureReason = Clamp(Get(p, "LOGIN_FAILURE_REASON") ?? Get(p, "login_failure_type"), 256),
                ParametersJson = p.Count == 0 ? null : JsonSerializer.Serialize(p),
            };
        }
    }

    private static Dictionary<string, string> Parameters(Activity.EventsData e)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in e.Parameters ?? [])
        {
            if (string.IsNullOrEmpty(p.Name)) continue;
            var value = p.Value
                ?? p.IntValue?.ToString(CultureInfo.InvariantCulture)
                ?? (p.BoolValue is bool b ? (b ? "true" : "false") : null)
                ?? (p.MultiValue is { Count: > 0 } mv ? string.Join(",", mv) : null)
                ?? (p.MultiIntValue is { Count: > 0 } mi ? string.Join(",", mi) : null);
            if (value is not null) result[p.Name] = value;
        }
        return result;
    }

    private static string? Get(Dictionary<string, string> p, string name) =>
        p.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    // Custom schema values arrive as JSON tokens; multi-valued fields are arrays of { "value": ... }.
    private static string? CustomFieldValue(object? value) => value switch
    {
        null => null,
        JArray array => array.Count == 0 ? null : CustomFieldValue(array[0]),
        JObject obj => obj["value"]?.ToString(),
        JValue v => v.Value is null ? null : Convert.ToString(v.Value, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string? NormalizeEmail(string? value) =>
        string.IsNullOrWhiteSpace(value) || !value.Contains('@') ? null : value.Trim().ToLowerInvariant();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? Utc(DateTimeOffset? value) => value?.UtcDateTime;

    private static DateTime? ParseUtc(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.UtcDateTime : null;

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string? FormatEpochDate(long? epochMs) =>
        epochMs is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs.Value).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static string? Clamp(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
