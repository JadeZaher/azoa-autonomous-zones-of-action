using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Consent;

/// <summary>Records notifications for persisted expiry facts without changing signing authority.</summary>
public sealed class ConsentExpiryWorker(IServiceScopeFactory scopes, IOptions<WebhookOptions> configured,
    ILogger<ConsentExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, configured.Value.StartupDelaySeconds)), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunTickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { logger.LogError(exception, "Consent expiry observation failed; persisted facts remain eligible."); }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, configured.Value.PollSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task RunTickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SurrealConsentLifecycleStore>();
        var candidates = await store.ListUnnotifiedExpiryAsync(configured.Value.BatchSize, ct);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await store.RecordExpiryAsync(SurrealRecordGuid.Parse(candidate.Id), ct);
        }
    }
}
