using System.Runtime.CompilerServices;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Google.Apis.Admin.Reports.reports_v1;
using Google.Apis.Admin.Reports.reports_v1.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;

namespace ChromeCollector.FunctionApp.Services;

/// <summary>Reads pages from the Google Admin SDK. Each page is written to SQL before the next is fetched.</summary>
public interface IGoogleAdminSource
{
    IAsyncEnumerable<IList<ChromeOsDevice>> ListDevicesAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<IList<User>> ListUsersAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<IList<Activity>> ListActivitiesAsync(string application, string? eventName, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken);
}

public sealed class GoogleAdminSource(IConfiguration configuration) : IGoogleAdminSource
{
    private static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/admin.directory.device.chromeos.readonly",
        "https://www.googleapis.com/auth/admin.directory.user.readonly",
        "https://www.googleapis.com/auth/admin.reports.audit.readonly",
    ];

    // Only the fields the sync stores; FULL projection is needed for recentUsers and activeTimeRanges.
    private const string DeviceFields =
        "nextPageToken,chromeosdevices(deviceId,serialNumber,annotatedAssetId,annotatedLocation,annotatedUser,notes,orgUnitPath," +
        "model,osVersion,platformVersion,firmwareVersion,bootMode,status,macAddress,ethernetMacAddress,autoUpdateThrough," +
        "autoUpdateExpiration,firstEnrollmentTime,lastEnrollmentTime,lastSync,lastKnownNetwork,recentUsers,activeTimeRanges)";

    private const string UserFields =
        "nextPageToken,users(id,primaryEmail,orgUnitPath,suspended,archived,isAdmin,lastLoginTime,creationTime,externalIds,customSchemas)";

    private readonly Lazy<(DirectoryService Directory, ReportsService Reports)> _clients = new(() => CreateClients(configuration));
    private GoogleSyncOptions Options => GoogleSyncOptions.FromConfiguration(configuration);

    public async IAsyncEnumerable<IList<ChromeOsDevice>> ListDevicesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = Options;
        string? pageToken = null;
        do
        {
            var request = _clients.Value.Directory.Chromeosdevices.List(options.CustomerId);
            request.Projection = ChromeosdevicesResource.ListRequest.ProjectionEnum.FULL;
            request.MaxResults = 200;
            request.Fields = DeviceFields;
            request.PageToken = pageToken;
            if (options.DeviceOrgUnitPath is not null)
            {
                request.OrgUnitPath = options.DeviceOrgUnitPath;
                request.IncludeChildOrgunits = true;
            }

            var page = await request.ExecuteAsync(cancellationToken);
            if (page.Chromeosdevices is { Count: > 0 }) yield return page.Chromeosdevices;
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    public async IAsyncEnumerable<IList<User>> ListUsersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = Options;
        var customSchema = CustomSchemaName(options.StudentIdSource);
        string? pageToken = null;
        do
        {
            var request = _clients.Value.Directory.Users.List();
            request.Customer = options.CustomerId;
            request.MaxResults = 500;
            request.Fields = UserFields;
            request.PageToken = pageToken;
            request.Query = options.UserQuery;
            if (customSchema is not null)
            {
                request.Projection = UsersResource.ListRequest.ProjectionEnum.Custom;
                request.CustomFieldMask = customSchema;
            }

            var page = await request.ExecuteAsync(cancellationToken);
            if (page.UsersValue is { Count: > 0 }) yield return page.UsersValue;
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    public async IAsyncEnumerable<IList<Activity>> ListActivitiesAsync(
        string application, string? eventName, DateTime startUtc, DateTime endUtc, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var app = application switch
        {
            GoogleSyncOptions.AuditChrome => ActivitiesResource.ListRequest.ApplicationNameEnum.Chrome,
            GoogleSyncOptions.AuditLogin => ActivitiesResource.ListRequest.ApplicationNameEnum.Login,
            _ => throw new ArgumentOutOfRangeException(nameof(application), application, "Unsupported audit application."),
        };

        string? pageToken = null;
        do
        {
            var request = _clients.Value.Reports.Activities.List("all", app);
            request.CustomerId = Options.CustomerId == "my_customer" ? null : Options.CustomerId;
            request.StartTime = Rfc3339(startUtc);
            request.EndTime = Rfc3339(endUtc);
            request.EventName = eventName;
            request.MaxResults = 1000;
            request.PageToken = pageToken;

            var page = await request.ExecuteAsync(cancellationToken);
            if (page.Items is { Count: > 0 }) yield return page.Items;
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    public static string Rfc3339(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static string? CustomSchemaName(string studentIdSource)
    {
        const string prefix = "customSchema:";
        if (!studentIdSource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var name = studentIdSource[prefix.Length..].Split('.')[0].Trim();
        return name.Length == 0 ? null : name;
    }

    private static (DirectoryService, ReportsService) CreateClients(IConfiguration configuration)
    {
        var options = GoogleSyncOptions.FromConfiguration(configuration);
        if (!options.IsConfigured) throw new InvalidOperationException("Google sync is not configured (GOOGLE_SERVICE_ACCOUNT_JSON, GOOGLE_ADMIN_EMAIL).");

        // Validates that the JSON really is a service account key before it is used.
        var credential = CredentialFactory.FromJson<ServiceAccountCredential>(options.ServiceAccountJson!)
            .ToGoogleCredential()
            .CreateScoped(Scopes)
            .CreateWithUser(options.AdminEmail!);

        var initializer = new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "chromebook-session-attribution",
            // Retry 503s and transport errors with exponential backoff.
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.Exception | ExponentialBackOffPolicy.UnsuccessfulResponse503,
        };
        return (new DirectoryService(initializer), new ReportsService(initializer));
    }
}
