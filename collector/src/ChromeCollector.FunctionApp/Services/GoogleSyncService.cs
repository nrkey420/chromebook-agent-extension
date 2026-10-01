using ChromeCollector.FunctionApp.Models;
using Google;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChromeCollector.FunctionApp.Services;

public interface IGoogleSyncService
{
    Task<GoogleSyncResult> SyncDevicesAsync(CancellationToken cancellationToken);
    Task<GoogleSyncResult> SyncUsersAsync(CancellationToken cancellationToken);
    Task<GoogleSyncResult> SyncAuditAsync(string application, CancellationToken cancellationToken);
}

public sealed record GoogleSyncResult(string SyncName, string Status, int Items, DateTime? WatermarkUtc = null, string? Message = null);

/// <summary>
/// Pulls Google device inventory, users and sign-in audit events into SQL. Every run is recorded in dbo.SyncState
/// (status, items, message); audit runs also keep a watermark there and re-read an overlap window each time,
/// because the Reports API can deliver events hours late. Duplicates are skipped by the store.
/// </summary>
public sealed class GoogleSyncService(
    IConfiguration configuration,
    IGoogleAdminSource source,
    IGoogleSyncStore store,
    TimeProvider timeProvider,
    ILogger<GoogleSyncService> logger) : IGoogleSyncService
{
    public const string DevicesSyncName = "GoogleDevices";
    public const string UsersSyncName = "GoogleUsers";
    public static string AuditSyncName(string application) => $"GoogleAudit:{application}";

    // The Reports API only serves the last 180 days.
    private static readonly TimeSpan MaxAuditLookback = TimeSpan.FromDays(179);

    public Task<GoogleSyncResult> SyncDevicesAsync(CancellationToken cancellationToken) =>
        RunAsync(DevicesSyncName, async (options, runUtc, ct) =>
        {
            var activeSince = DateOnly.FromDateTime(runUtc).AddDays(-options.ActiveTimeDays);
            var count = 0;
            await foreach (var page in source.ListDevicesAsync(ct))
            {
                var rows = page.Select(d => GoogleMapper.MapDevice(d, activeSince)).OfType<GoogleDevice>().ToList();
                count += await store.UpsertDevicesAsync(rows, runUtc, ct);
                logger.LogInformation("Google device sync: {count} devices so far", count);
            }
            return (count, (DateTime?)runUtc, (string?)null);
        }, cancellationToken);

    public Task<GoogleSyncResult> SyncUsersAsync(CancellationToken cancellationToken) =>
        RunAsync(UsersSyncName, async (options, runUtc, ct) =>
        {
            var count = 0;
            await foreach (var page in source.ListUsersAsync(ct))
            {
                var rows = page.Select(u => GoogleMapper.MapUser(u, options.StudentIdSource)).OfType<GoogleUserRecord>().ToList();
                count += await store.UpsertUsersAsync(rows, runUtc, ct);
                logger.LogInformation("Google user sync: {count} users so far", count);
            }
            return (count, (DateTime?)runUtc, (string?)null);
        }, cancellationToken);

    public Task<GoogleSyncResult> SyncAuditAsync(string application, CancellationToken cancellationToken) =>
        RunAsync(AuditSyncName(application), async (options, runUtc, ct) =>
        {
            var (startUtc, endUtc) = AuditWindow(await store.GetWatermarkAsync(AuditSyncName(application), ct), runUtc, options);
            var eventNames = application == GoogleSyncOptions.AuditChrome ? options.ChromeEventNames : options.LoginEventNames;

            var inserted = 0;
            var unknown = new List<string>();
            // No names = every event of the application, in one listing.
            foreach (var eventName in eventNames.Count == 0 ? [null] : eventNames.Cast<string?>())
            {
                try
                {
                    await foreach (var page in source.ListActivitiesAsync(application, eventName, startUtc, endUtc, ct))
                    {
                        var rows = page.SelectMany(GoogleMapper.MapActivity)
                            // A name-filtered request returns the whole activity; keep only the requested events.
                            .Where(e => eventName is null || string.Equals(e.EventName, eventName, StringComparison.Ordinal))
                            .ToList();
                        inserted += await store.InsertAuditEventsAsync(rows, ct);
                    }
                }
                catch (GoogleApiException ex) when (eventName is not null && IsUnknownEventName(ex))
                {
                    // One bad name (renamed by Google, or a typo in GOOGLE_*_EVENT_NAMES) must not stop the others.
                    logger.LogWarning("Google {app} audit sync: Google does not accept event name {eventName}; skipped. {message}", application, eventName, ex.Message);
                    unknown.Add(eventName);
                }
            }
            logger.LogInformation("Google {app} audit sync {from:o}..{to:o}: {count} new events", application, startUtc, endUtc, inserted);
            var message = unknown.Count == 0 ? null
                : $"Skipped event names Google does not accept: {string.Join(", ", unknown)}. Remove them from the GOOGLE_*_EVENT_NAMES setting.";
            return (inserted, (DateTime?)endUtc, message);
        }, cancellationToken);

    /// <summary>The Reports API answers 400 "Event X not found in manifest" for an event name it does not know.</summary>
    public static bool IsUnknownEventName(GoogleApiException ex) =>
        ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest
        && ex.Message.Contains("not found in manifest", StringComparison.OrdinalIgnoreCase);

    /// <summary>Start = previous watermark minus the overlap (or the initial backfill), capped to what Google retains.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) AuditWindow(DateTime? watermarkUtc, DateTime runUtc, GoogleSyncOptions options)
    {
        var start = watermarkUtc is { } w
            ? w - TimeSpan.FromMinutes(options.AuditOverlapMinutes)
            : runUtc - TimeSpan.FromDays(options.AuditInitialDays);
        var earliest = runUtc - MaxAuditLookback;
        if (start < earliest) start = earliest;
        if (start > runUtc) start = runUtc;
        return (start, runUtc);
    }

    private async Task<GoogleSyncResult> RunAsync(
        string syncName,
        Func<GoogleSyncOptions, DateTime, CancellationToken, Task<(int Items, DateTime? Watermark, string? Message)>> work,
        CancellationToken cancellationToken)
    {
        var options = GoogleSyncOptions.FromConfiguration(configuration);
        if (!options.IsConfigured || !store.IsEnabled)
        {
            logger.LogInformation("{sync} skipped: Google sync not configured (GOOGLE_SERVICE_ACCOUNT_JSON, GOOGLE_ADMIN_EMAIL, SQL_CONNECTION_STRING).", syncName);
            return new GoogleSyncResult(syncName, "SKIPPED", 0);
        }

        var runUtc = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var (items, watermark, message) = await work(options, runUtc, cancellationToken);
            await store.RecordRunAsync(syncName, runUtc, "SUCCESS", message, items, watermark, cancellationToken);
            return new GoogleSyncResult(syncName, "SUCCESS", items, watermark, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{sync} failed", syncName);
            try
            {
                // The watermark is left unchanged, so the next run retries the same window.
                await store.RecordRunAsync(syncName, runUtc, "FAILED", ex.Message, 0, null, CancellationToken.None);
            }
            catch (Exception recordEx)
            {
                logger.LogWarning(recordEx, "Could not record failed {sync} run in SyncState", syncName);
            }
            throw;
        }
    }
}
