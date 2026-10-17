using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Core.Webhooks;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>Sends one frozen lease without reading or mutating delivery persistence.</summary>
public sealed class LeasedWebhookSender(IHttpClientFactory clients, WebhookSsrfGuard guard,
    WebhookHmacSigner signer, WebhookSecretProtector secrets)
{
    public sealed record Completion(WebhookAttemptOutcome Outcome, TimeSpan RetryDelay, string? ErrorCode);

    /// <summary>Returns no completion if insufficient lease lifetime remains to start HTTP.</summary>
    public async Task<Completion?> SendAsync(WebhookDeliveryLease lease, WebhookOptions options,
        string clientName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.HttpTimeoutSeconds, 1, 120));
        string? error = Encoding.UTF8.GetByteCount(lease.BodyJson) > WebhookHttpTransport.MaximumBodyBytes
            ? "BODY_TOO_LARGE" : !guard.IsAllowed(lease.Url, out _) ? "DESTINATION_BLOCKED" : null;
        if (error is not null) return new(WebhookAttemptOutcome.DeadLetter, TimeSpan.Zero, error);
        string signature;
        var timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        try { signature = signer.Sign(lease.BodyJson, timestamp, secrets.Unprotect(lease.TenantId,
            lease.RegistrationId, lease.SecretRevision, lease.EncryptedSecret)); }
        catch (CryptographicException)
        {
            return new(WebhookAttemptOutcome.DeadLetter, TimeSpan.Zero, "SECRET_AUTHENTICATION_FAILED");
        }
        if (DateTimeOffset.UtcNow + timeout >= lease.ExpiresAt) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var client = clients.CreateClient(clientName);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Post, lease.Url)
            { Content = new StringContent(lease.BodyJson, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Azoa-Signature", signature);
        request.Headers.TryAddWithoutValidation("X-Azoa-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Azoa-Idempotency-Id", lease.IdempotencyId);
        request.Headers.TryAddWithoutValidation("X-Azoa-Secret-Revision", lease.SecretRevision.ToString(CultureInfo.InvariantCulture));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.IsSuccessStatusCode) return new(WebhookAttemptOutcome.Delivered, TimeSpan.Zero, null);
            error = "HTTP_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { error = "HTTP_TIMEOUT"; }
        catch (HttpRequestException) { error = "HTTP_TRANSPORT_FAILED"; }
        var exhausted = lease.CycleAttemptCount >= options.BoundedMaxAttempts;
        var baseSeconds = Math.Clamp(options.BaseBackoffSeconds, 1, 86400);
        var maximumSeconds = Math.Clamp(options.MaxBackoffSeconds, baseSeconds, 86400);
        var delay = TimeSpan.FromSeconds(Math.Min(maximumSeconds,
            baseSeconds * Math.Pow(2, Math.Clamp(lease.CycleAttemptCount - 1, 0, 30))));
        return new(exhausted ? WebhookAttemptOutcome.DeadLetter : WebhookAttemptOutcome.Retry,
            exhausted ? TimeSpan.Zero : delay, error);
    }
}
