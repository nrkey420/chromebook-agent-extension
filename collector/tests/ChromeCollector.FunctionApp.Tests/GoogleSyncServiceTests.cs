using System.Runtime.CompilerServices;
using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using FluentAssertions;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Google.Apis.Admin.Reports.reports_v1.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChromeCollector.FunctionApp.Tests;

public class GoogleSyncServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<string, string?> ConfiguredSettings = new()
    {
        ["GOOGLE_SERVICE_ACCOUNT_JSON"] = "{\"type\":\"service_account\"}",
        ["GOOGLE_ADMIN_EMAIL"] = "sync-admin@district.org",
        ["GOOGLE_STUDENT_ID_SOURCE"] = "emailLocalPart",
    };

    [Theory]
    [InlineData(null, "admin@district.org")]
    [InlineData("{}", null)]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://kv.vault.azure.net/secrets/GoogleServiceAccountKey)", "admin@district.org")]
    public async Task NotConfigured_SkipsWithoutCallingGoogleOrSql(string? json, string? admin)
    {
        var (service, source, store) = Create(new() { ["GOOGLE_SERVICE_ACCOUNT_JSON"] = json, ["GOOGLE_ADMIN_EMAIL"] = admin });

        var result = await service.SyncDevicesAsync(CancellationToken.None);

        result.Status.Should().Be("SKIPPED");
        source.Calls.Should().BeEmpty();
        store.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Devices_WritesEveryPageAndRecordsSuccess()
    {
        var (service, source, store) = Create(ConfiguredSettings);
        source.DevicePages.Add([new ChromeOsDevice { DeviceId = "a" }, new ChromeOsDevice { DeviceId = null }]);
        source.DevicePages.Add([new ChromeOsDevice { DeviceId = "b" }]);

        var result = await service.SyncDevicesAsync(CancellationToken.None);

        result.Should().Be(new GoogleSyncResult(GoogleSyncService.DevicesSyncName, "SUCCESS", 2, Now));
        store.Devices.Select(d => d.DirectoryDeviceId).Should().Equal("a", "b");
        store.Runs.Should().ContainSingle().Which.Should().Be((GoogleSyncService.DevicesSyncName, "SUCCESS", (string?)null, 2, (DateTime?)Now));
    }

    [Fact]
    public async Task Users_UseConfiguredStudentIdSource()
    {
        var (service, source, store) = Create(ConfiguredSettings);
        source.UserPages.Add([new User { PrimaryEmail = "123456@district.org" }]);

        await service.SyncUsersAsync(CancellationToken.None);

        store.Users.Should().ContainSingle().Which.StudentId.Should().Be("123456");
    }

    [Fact]
    public async Task ChromeAudit_RequestsEachLoginEventAndKeepsOnlyThatEvent()
    {
        var (service, source, store) = Create(ConfiguredSettings);
        store.Watermarks[GoogleSyncService.AuditSyncName("chrome")] = Now.AddMinutes(-15);
        source.Activities = (_, eventName) =>
        [
            GoogleMapperTests.Activity("chrome", null, null,
                GoogleMapperTests.Event(eventName!, "CHROME_OS_LOGIN_LOGOUT_TYPE"),
                GoogleMapperTests.Event("SOME_OTHER_EVENT", null)),
        ];

        var result = await service.SyncAuditAsync("chrome", CancellationToken.None);

        source.Calls.Should().Equal(GoogleSyncOptions.DefaultChromeEventNames.Select(n => $"chrome:{n}:{Now.AddMinutes(-195):o}:{Now:o}"));
        store.AuditEvents.Select(e => e.EventName).Should().Equal(GoogleSyncOptions.DefaultChromeEventNames);
        result.Items.Should().Be(4);
        result.WatermarkUtc.Should().Be(Now);
    }

    [Fact]
    public async Task LoginAudit_WithoutEventNames_ListsAllEventsOnce()
    {
        var (service, source, store) = Create(ConfiguredSettings);
        source.Activities = (_, _) => [GoogleMapperTests.Activity("login", "a@district.org", "203.0.113.1",
            GoogleMapperTests.Event("login_success", "login"), GoogleMapperTests.Event("login_verification", "login"))];

        await service.SyncAuditAsync("login", CancellationToken.None);

        source.Calls.Should().ContainSingle().Which.Should().StartWith("login::");
        store.AuditEvents.Should().HaveCount(2);
    }

    [Fact]
    public async Task Failure_RecordsFailedRunKeepsWatermarkAndRethrows()
    {
        var (service, source, store) = Create(ConfiguredSettings);
        source.Activities = (_, _) => throw new InvalidOperationException("Google said no");

        var act = () => service.SyncAuditAsync("login", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.Runs.Should().ContainSingle().Which.Should().Be((GoogleSyncService.AuditSyncName("login"), "FAILED", "Google said no", 0, (DateTime?)null));
    }

    [Fact]
    public void AuditWindow_FirstRunBackfills_LaterRunsOverlap_AndNeverExceedGoogleRetention()
    {
        var options = new GoogleSyncOptions { AuditInitialDays = 7, AuditOverlapMinutes = 180 };

        GoogleSyncService.AuditWindow(null, Now, options).Should().Be((Now.AddDays(-7), Now));
        GoogleSyncService.AuditWindow(Now.AddMinutes(-15), Now, options).Should().Be((Now.AddMinutes(-195), Now));
        GoogleSyncService.AuditWindow(Now.AddDays(-400), Now, options).StartUtc.Should().Be(Now.AddDays(-179));
        GoogleSyncService.AuditWindow(Now.AddHours(5), Now, options with { AuditOverlapMinutes = 0 }).StartUtc.Should().Be(Now);
    }

    [Fact]
    public void Options_ParseListsAndClampNumbers()
    {
        var options = GoogleSyncOptions.FromConfiguration(Config(new()
        {
            ["GOOGLE_CHROME_EVENT_NAMES"] = " CHROME_OS_LOGIN_EVENT , ,CHROME_OS_LOGOUT_EVENT",
            ["GOOGLE_AUDIT_INITIAL_DAYS"] = "900",
            ["GOOGLE_CUSTOMER_ID"] = "  ",
        }));

        options.ChromeEventNames.Should().Equal("CHROME_OS_LOGIN_EVENT", "CHROME_OS_LOGOUT_EVENT");
        options.LoginEventNames.Should().BeEmpty();
        options.AuditInitialDays.Should().Be(180);
        options.CustomerId.Should().Be("my_customer");
        GoogleAdminSource.CustomSchemaName("customSchema:SIS.StudentId").Should().Be("SIS");
        GoogleAdminSource.CustomSchemaName("externalId:organization").Should().BeNull();
        GoogleAdminSource.Rfc3339(new DateTime(2026, 10, 1, 9, 5, 3, 120)).Should().Be("2026-10-01T09:05:03.120Z");
    }

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static (GoogleSyncService, FakeSource, FakeStore) Create(Dictionary<string, string?> settings)
    {
        var source = new FakeSource();
        var store = new FakeStore();
        var service = new GoogleSyncService(Config(settings), source, store, new FixedTime(Now), NullLogger<GoogleSyncService>.Instance);
        return (service, source, store);
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }

    private sealed class FakeSource : IGoogleAdminSource
    {
        public List<string> Calls { get; } = [];
        public List<IList<ChromeOsDevice>> DevicePages { get; } = [];
        public List<IList<User>> UserPages { get; } = [];
        public Func<string, string?, IList<Activity>> Activities { get; set; } = (_, _) => [];

        public async IAsyncEnumerable<IList<ChromeOsDevice>> ListDevicesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add("devices");
            foreach (var page in DevicePages) { await Task.Yield(); yield return page; }
        }

        public async IAsyncEnumerable<IList<User>> ListUsersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add("users");
            foreach (var page in UserPages) { await Task.Yield(); yield return page; }
        }

        public async IAsyncEnumerable<IList<Activity>> ListActivitiesAsync(string application, string? eventName, DateTime startUtc, DateTime endUtc,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add($"{application}:{eventName}:{startUtc:o}:{endUtc:o}");
            await Task.Yield();
            yield return Activities(application, eventName);
        }
    }

    private sealed class FakeStore : IGoogleSyncStore
    {
        public bool IsEnabled => true;
        public List<GoogleDevice> Devices { get; } = [];
        public List<GoogleUserRecord> Users { get; } = [];
        public List<GoogleAuditEvent> AuditEvents { get; } = [];
        public Dictionary<string, DateTime> Watermarks { get; } = [];
        public List<(string, string, string?, int, DateTime?)> Runs { get; } = [];

        public Task<int> UpsertDevicesAsync(IReadOnlyList<GoogleDevice> devices, DateTime refreshedUtc, CancellationToken cancellationToken)
        {
            Devices.AddRange(devices);
            return Task.FromResult(devices.Count);
        }

        public Task<int> UpsertUsersAsync(IReadOnlyList<GoogleUserRecord> users, DateTime refreshedUtc, CancellationToken cancellationToken)
        {
            Users.AddRange(users);
            return Task.FromResult(users.Count);
        }

        public Task<int> InsertAuditEventsAsync(IReadOnlyList<GoogleAuditEvent> events, CancellationToken cancellationToken)
        {
            AuditEvents.AddRange(events);
            return Task.FromResult(events.Count);
        }

        public Task<DateTime?> GetWatermarkAsync(string syncName, CancellationToken cancellationToken) =>
            Task.FromResult(Watermarks.TryGetValue(syncName, out var w) ? w : (DateTime?)null);

        public Task RecordRunAsync(string syncName, DateTime runUtc, string status, string? message, int itemsProcessed, DateTime? watermarkUtc, CancellationToken cancellationToken)
        {
            Runs.Add((syncName, status, message, itemsProcessed, watermarkUtc));
            return Task.CompletedTask;
        }
    }
}
