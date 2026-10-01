using Microsoft.Extensions.Configuration;

namespace ChromeCollector.FunctionApp.Services;

/// <summary>
/// Google sync settings (app settings). The sync is off until GOOGLE_SERVICE_ACCOUNT_JSON and GOOGLE_ADMIN_EMAIL are set.
/// Setup: docs/google-sync.md.
/// </summary>
public sealed record GoogleSyncOptions
{
    public const string AuditChrome = "chrome";
    public const string AuditLogin = "login";

    // ChromeOS sign-in events; the Reports API filters by one event name per request.
    public static readonly string[] DefaultChromeEventNames =
        ["CHROME_OS_LOGIN_EVENT", "CHROME_OS_LOGOUT_EVENT", "CHROME_OS_LOGIN_FAILURE_EVENT", "CHROME_OS_LOGIN_LOGOUT_EVENT"];

    /// <summary>Service account key JSON (Key Vault reference in Azure).</summary>
    public string? ServiceAccountJson { get; init; }

    /// <summary>Workspace admin the service account impersonates (domain-wide delegation).</summary>
    public string? AdminEmail { get; init; }

    public string CustomerId { get; init; } = "my_customer";

    /// <summary>Limit the device sync to one OU and its children (e.g. the pilot OU). Empty = whole domain.</summary>
    public string? DeviceOrgUnitPath { get; init; }

    /// <summary>Optional Directory API users.list query, e.g. "orgUnitPath='/Students'".</summary>
    public string? UserQuery { get; init; }

    /// <summary>Where the student ID comes from: none, emailLocalPart, externalId[:type], customSchema:Schema.Field.</summary>
    public string StudentIdSource { get; init; } = "externalId:organization";

    /// <summary>Days of per-day active time kept per device on each sync.</summary>
    public int ActiveTimeDays { get; init; } = 30;

    /// <summary>First audit run reads this many days back.</summary>
    public int AuditInitialDays { get; init; } = 7;

    /// <summary>Each audit run re-reads this many minutes before the watermark, because Google reports events late.</summary>
    public int AuditOverlapMinutes { get; init; } = 180;

    public IReadOnlyList<string> ChromeEventNames { get; init; } = DefaultChromeEventNames;

    /// <summary>Empty = all events of the 'login' application.</summary>
    public IReadOnlyList<string> LoginEventNames { get; init; } = [];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServiceAccountJson)
        // An unresolved Key Vault reference (secret not created yet) arrives as the literal reference text.
        && !ServiceAccountJson.TrimStart().StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(AdminEmail);

    public static GoogleSyncOptions FromConfiguration(IConfiguration c) => new()
    {
        ServiceAccountJson = c["GOOGLE_SERVICE_ACCOUNT_JSON"],
        AdminEmail = Trimmed(c["GOOGLE_ADMIN_EMAIL"]),
        CustomerId = Trimmed(c["GOOGLE_CUSTOMER_ID"]) ?? "my_customer",
        DeviceOrgUnitPath = Trimmed(c["GOOGLE_DEVICE_ORG_UNIT"]),
        UserQuery = Trimmed(c["GOOGLE_USER_QUERY"]),
        StudentIdSource = Trimmed(c["GOOGLE_STUDENT_ID_SOURCE"]) ?? "externalId:organization",
        ActiveTimeDays = Int(c["GOOGLE_ACTIVE_TIME_DAYS"], 30, 1, 365),
        AuditInitialDays = Int(c["GOOGLE_AUDIT_INITIAL_DAYS"], 7, 1, 180),
        AuditOverlapMinutes = Int(c["GOOGLE_AUDIT_OVERLAP_MINUTES"], 180, 0, 1440),
        ChromeEventNames = List(c["GOOGLE_CHROME_EVENT_NAMES"]) ?? DefaultChromeEventNames,
        LoginEventNames = List(c["GOOGLE_LOGIN_EVENT_NAMES"]) ?? [],
    };

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int Int(string? value, int fallback, int min, int max) =>
        int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private static string[]? List(string? value)
    {
        var items = value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items is { Length: > 0 } ? items : null;
    }
}
