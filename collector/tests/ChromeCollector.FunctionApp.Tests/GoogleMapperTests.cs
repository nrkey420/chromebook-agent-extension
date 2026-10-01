using ChromeCollector.FunctionApp.Services;
using FluentAssertions;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Google.Apis.Admin.Reports.reports_v1.Data;
using Newtonsoft.Json.Linq;

namespace ChromeCollector.FunctionApp.Tests;

public class GoogleMapperTests
{
    private static readonly DateOnly ActiveSince = new(2026, 9, 1);

    [Fact]
    public void MapDevice_MapsInventoryNetworkAndUsers()
    {
        var device = new ChromeOsDevice
        {
            DeviceId = "dev-1",
            SerialNumber = "5CD1234XYZ",
            AnnotatedAssetId = "A-100",
            AnnotatedLocation = "Room 204",
            AnnotatedUser = "cart 3",
            OrgUnitPath = "/Students/HS",
            Model = "Acer Chromebook 311",
            OsVersion = "128.0.6613.0",
            Status = "ACTIVE",
            MacAddress = "aabbccddeeff",
            AutoUpdateThrough = "2030-06-01",
            FirstEnrollmentTime = "2024-08-15T12:00:00.000Z",
            LastSyncDateTimeOffset = new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero),
            LastKnownNetwork = [new ChromeOsDevice.LastKnownNetworkData { IpAddress = "10.1.2.3", WanIpAddress = "203.0.113.9" }],
            RecentUsers =
            [
                new ChromeOsDevice.RecentUsersData { Email = "123456@District.org", Type = "USER_TYPE_MANAGED" },
                new ChromeOsDevice.RecentUsersData { Email = null, Type = "USER_TYPE_UNMANAGED" },
            ],
            ActiveTimeRanges =
            [
                new ChromeOsDevice.ActiveTimeRangesData { Date = "2026-08-31", ActiveTime = 600_000 },
                new ChromeOsDevice.ActiveTimeRangesData { Date = "2026-09-30", ActiveTime = 5_430_000 },
                new ChromeOsDevice.ActiveTimeRangesData { Date = "not-a-date", ActiveTime = 1 },
            ],
        };

        var row = GoogleMapper.MapDevice(device, ActiveSince)!;

        row.DirectoryDeviceId.Should().Be("dev-1");
        row.AssetId.Should().Be("A-100");
        row.GoogleStatus.Should().Be("ACTIVE");
        row.FirstEnrollmentUtc.Should().Be(new DateTime(2024, 8, 15, 12, 0, 0, DateTimeKind.Utc));
        row.GoogleLastSyncUtc.Should().Be(new DateTime(2026, 10, 1, 13, 0, 0));
        row.GoogleLastLanIp.Should().Be("10.1.2.3");
        row.GoogleLastWanIp.Should().Be("203.0.113.9");
        row.RecentUsers.Should().Equal(
            new Models.GoogleRecentUser(0, "123456@district.org", "USER_TYPE_MANAGED"),
            new Models.GoogleRecentUser(1, null, "USER_TYPE_UNMANAGED"));
        // Days before activeSince and unparseable dates are dropped; milliseconds become minutes.
        row.ActiveDays.Should().Equal(new Models.GoogleActiveDay(new DateOnly(2026, 9, 30), 91));
    }

    [Fact]
    public void MapDevice_WithoutDeviceId_IsSkipped()
    {
        GoogleMapper.MapDevice(new ChromeOsDevice { SerialNumber = "X" }, ActiveSince).Should().BeNull();
    }

    [Fact]
    public void MapDevice_FallsBackToAutoUpdateExpirationAndClampsLongValues()
    {
        var row = GoogleMapper.MapDevice(new ChromeOsDevice
        {
            DeviceId = "dev-2",
            AutoUpdateExpiration = new DateTimeOffset(2029, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Notes = new string('n', 5000),
        }, ActiveSince)!;

        row.AutoUpdateThrough.Should().Be("2029-06-01");
        row.Notes.Should().HaveLength(1000);
    }

    [Theory]
    [InlineData("externalId:organization", "S-777")]
    [InlineData("externalId:studentNumber", "STU-9")]
    [InlineData("externalId", "S-777")]
    [InlineData("emailLocalPart", "123456")]
    [InlineData("customSchema:SIS.StudentId", "998877")]
    [InlineData("customSchema:SIS.Missing", null)]
    [InlineData("none", null)]
    public void MapUser_ResolvesStudentIdFromConfiguredSource(string source, string? expected)
    {
        var user = new User
        {
            Id = "g-1",
            PrimaryEmail = "123456@District.org",
            OrgUnitPath = "/Students",
            ExternalIds =
            [
                new UserExternalId { Type = "organization", Value = "S-777" },
                new UserExternalId { Type = "custom", CustomType = "studentNumber", Value = "STU-9" },
            ],
            CustomSchemas = new Dictionary<string, IDictionary<string, object>>
            {
                ["SIS"] = new Dictionary<string, object> { ["StudentId"] = new JValue("998877") },
            },
        };

        GoogleMapper.MapUser(user, source)!.StudentId.Should().Be(expected);
    }

    [Fact]
    public void MapUser_ReadsMultiValuedCustomSchemaField()
    {
        var user = new User
        {
            PrimaryEmail = "a@district.org",
            CustomSchemas = new Dictionary<string, IDictionary<string, object>>
            {
                ["SIS"] = new Dictionary<string, object> { ["Ids"] = JArray.Parse("""[{ "type": "work", "value": "42" }]""") },
            },
        };

        GoogleMapper.MapUser(user, "customSchema:SIS.Ids")!.StudentId.Should().Be("42");
    }

    [Fact]
    public void MapUser_NormalizesEmailAndTreatsEpochLastLoginAsNever()
    {
        var row = GoogleMapper.MapUser(new User
        {
            Id = "g-2",
            PrimaryEmail = " Teacher@District.ORG ",
            Suspended = true,
            LastLoginTimeDateTimeOffset = DateTimeOffset.UnixEpoch,
            CreationTimeDateTimeOffset = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero),
        }, "none")!;

        row.UserEmail.Should().Be("teacher@district.org");
        row.IsSuspended.Should().BeTrue();
        row.LastLoginUtc.Should().BeNull();
        row.CreatedInGoogleUtc.Should().Be(new DateTime(2020, 1, 2, 3, 4, 5));
    }

    [Fact]
    public void MapUser_WithoutEmail_IsSkipped()
    {
        GoogleMapper.MapUser(new User { Id = "x" }, "none").Should().BeNull();
    }

    [Fact]
    public void MapActivity_ChromeOsLogin_TakesUserAndDeviceFromParameters()
    {
        var activity = Activity("chrome", "admin@district.org", "198.51.100.7",
            Event("CHROME_OS_LOGIN_FAILURE_EVENT", "CHROME_OS_LOGIN_LOGOUT_TYPE",
                ("DEVICE_USER", "123456@District.org"),
                ("DIRECTORY_DEVICE_ID", "dev-1"),
                ("DEVICE_NAME", "5CD1234XYZ"),
                ("DEVICE_PLATFORM", "ChromeOS 128"),
                ("EVENT_REASON", "USER_LOGIN"),
                ("LOGIN_FAILURE_REASON", "AUTHENTICATION_ERROR")));

        var row = GoogleMapper.MapActivity(activity).Single();

        row.Application.Should().Be("chrome");
        row.UniqueQualifier.Should().Be("-4242");
        row.EventTimeUtc.Should().Be(new DateTime(2026, 10, 1, 13, 15, 0));
        row.EventName.Should().Be("CHROME_OS_LOGIN_FAILURE_EVENT");
        row.UserEmail.Should().Be("123456@district.org");
        row.ActorEmail.Should().Be("admin@district.org");
        row.IpAddress.Should().Be("198.51.100.7");
        row.DirectoryDeviceId.Should().Be("dev-1");
        row.DeviceName.Should().Be("5CD1234XYZ");
        row.EventReason.Should().Be("USER_LOGIN");
        row.FailureReason.Should().Be("AUTHENTICATION_ERROR");
        row.ParametersJson.Should().Contain("\"DIRECTORY_DEVICE_ID\":\"dev-1\"");
    }

    [Fact]
    public void MapActivity_AccountLogin_UsesActorAndFailureType_OneRowPerEvent()
    {
        var activity = Activity("login", "123456@district.org", "203.0.113.50",
            Event("login_failure", "login", ("login_failure_type", "login_failure_invalid_password"), ("is_suspicious", null)),
            Event("login_challenge", "login"));
        activity.Events[0].Parameters.Add(new Activity.EventsData.ParametersData { Name = "is_second_factor", BoolValue = false });

        var rows = GoogleMapper.MapActivity(activity).ToList();

        rows.Should().HaveCount(2);
        rows[0].UserEmail.Should().Be("123456@district.org");
        rows[0].FailureReason.Should().Be("login_failure_invalid_password");
        rows[0].ParametersJson.Should().Contain("\"is_second_factor\":\"false\"").And.NotContain("is_suspicious");
        rows[1].EventName.Should().Be("login_challenge");
        rows[1].ParametersJson.Should().BeNull();
    }

    [Fact]
    public void MapActivity_WithoutTime_IsSkipped()
    {
        var activity = Activity("chrome", null, null, Event("CHROME_OS_LOGIN_EVENT", null));
        activity.Id.TimeDateTimeOffset = null;
        GoogleMapper.MapActivity(activity).Should().BeEmpty();
    }

    internal static Activity Activity(string app, string? actor, string? ip, params Activity.EventsData[] events) => new()
    {
        Id = new Activity.IdData
        {
            ApplicationName = app,
            UniqueQualifier = -4242,
            TimeDateTimeOffset = new DateTimeOffset(2026, 10, 1, 13, 15, 0, TimeSpan.Zero),
        },
        Actor = new Activity.ActorData { Email = actor },
        IpAddress = ip,
        Events = events.ToList(),
    };

    internal static Activity.EventsData Event(string name, string? type, params (string Name, string? Value)[] parameters) => new()
    {
        Name = name,
        Type = type,
        Parameters = parameters.Select(p => new Activity.EventsData.ParametersData { Name = p.Name, Value = p.Value }).ToList(),
    };
}
