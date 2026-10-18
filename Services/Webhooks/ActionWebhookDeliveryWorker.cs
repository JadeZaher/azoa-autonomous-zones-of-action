using AZOA.WebAPI.Core.Webhooks;
using AZOA.WebAPI.Interfaces.Stores;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>Delivers immutable accepted events under bounded leases; see AGENTS.md.</summary>
public sealed class ActionWebhookDeliveryWorker(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory,
    WebhookSsrfGuard ssrfGuard, WebhookHmacSigner signer, WebhookSecretProtector secrets,
    ILogger<ActionWebhookDeliveryWorker> logger, IOptions<WebhookOptions> configured) : BackgroundService
{
    private readonly WebhookOptions _options = configured.Value;
    private readonly LeasedWebhookSender _sender = new(httpClientFactory, ssrfGuard, signer, secrets);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds)), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunTickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { logger.LogError(exception, "Action webhook delivery tick failed; unfinished leases will expire."); }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task RunTickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IActionWebhookDeliveryStore>();
        var maximumAttempts = _options.BoundedMaxAttempts;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.HttpTimeoutSeconds, 1, 120));
        var candidates = await store.ListCandidatesAsync(_options.BatchSize, ct);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var id = SurrealRecordGuid.BareId(candidate.Id);
            var lease = await store.TryClaimAsync(id, candidate.Version, timeout + TimeSpan.FromSeconds(30), maximumAttempts, ct);
            if (lease is null)
            {
                await store.SuspendUnavailableAsync(id, candidate.Version, maximumAttempts, ct);
                continue;
            }
            var completion = await _sender.SendAsync(lease, _options, WebhookOptions.ActionHttpClientName, ct);
            if (completion is not null)
                await store.FinishAsync(lease, completion.Outcome, completion.RetryDelay, completion.ErrorCode, ct);
        }
    }

}
