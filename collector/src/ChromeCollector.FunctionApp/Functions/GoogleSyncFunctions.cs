using ChromeCollector.FunctionApp.Services;
using Microsoft.Azure.Functions.Worker;

namespace ChromeCollector.FunctionApp.Functions;

/// <summary>
/// Scheduled Google sync (UTC schedules). Each job does nothing until the Google settings are configured
/// (docs/google-sync.md). Run one on demand with the admin API:
///   POST https://&lt;app&gt;/admin/functions/GoogleDeviceSync  (header x-functions-key: &lt;master key&gt;, body {})
/// Disable one with the app setting AzureWebJobs.&lt;FunctionName&gt;.Disabled = true.
/// </summary>
public sealed class GoogleSyncFunctions(IGoogleSyncService sync)
{
    // Every 6 hours at :05. Google refreshes device data when devices check in, so more often adds little.
    [Function("GoogleDeviceSync")]
    public Task Devices([TimerTrigger("0 5 */6 * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        sync.SyncDevicesAsync(cancellationToken);

    // Daily at 02:20 UTC.
    [Function("GoogleUserSync")]
    public Task Users([TimerTrigger("0 20 2 * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        sync.SyncUsersAsync(cancellationToken);

    // Every 15 minutes: ChromeOS login/logout/failure events.
    [Function("GoogleChromeAuditSync")]
    public Task ChromeAudit([TimerTrigger("0 */15 * * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        sync.SyncAuditAsync(GoogleSyncOptions.AuditChrome, cancellationToken);

    // Every 15 minutes, offset from the Chrome job: Google account sign-ins (with source IP).
    [Function("GoogleLoginAuditSync")]
    public Task LoginAudit([TimerTrigger("0 7-59/15 * * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        sync.SyncAuditAsync(GoogleSyncOptions.AuditLogin, cancellationToken);
}
