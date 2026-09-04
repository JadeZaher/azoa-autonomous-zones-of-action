// SPDX-License-Identifier: UNLICENSED

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>
/// The single egress point for every Identomat call. Centralising it is what makes
/// the ONE shared per-IP request budget enforceable: KYC session calls, AML
/// screening, and monitoring polls all pass through here, so nothing can bypass the
/// ceiling and get the node's IP banned for an hour.
/// </summary>
public interface IIdentomatHttpTransport
{
    /// <summary>
    /// POSTs JSON to <paramref name="baseUrl"/> + <paramref name="path"/> under the
    /// shared budget. Never surfaces a vendor error body or request URI to a caller.
    /// </summary>
    Task<AZOAResult<TResponse>> PostAsync<TRequest, TResponse>(
        string baseUrl,
        string path,
        TRequest body,
        string operation,
        CancellationToken ct = default)
        where TResponse : class;
}

/// <inheritdoc />
public sealed class IdentomatHttpTransport : IIdentomatHttpTransport
{
    /// <summary>Vendor rate-limit status; exceeding the ceiling bans the IP for an hour.</summary>
    public const int TooManyRequestsStatusCode = 529;

    /// <summary>Opaque dependency failure; deliberately carries no vendor detail.</summary>
    public const string DependencyMessage = "The identity provider is temporarily unavailable.";

    /// <summary>Shown when the shared budget or the ban circuit refuses the call.</summary>
    public const string RateLimitedMessage =
        "The identity provider request budget is exhausted; retry after the cooldown.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IIdentomatRateBudget _budget;
    private readonly IdentomatKycSettings _settings;
    private readonly ILogger<IdentomatHttpTransport> _logger;

    public IdentomatHttpTransport(
        IHttpClientFactory httpClientFactory,
        IIdentomatRateBudget budget,
        IOptions<KycSettings> options,
        ILogger<IdentomatHttpTransport>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _budget = budget;
        _settings = options.Value.Identomat;
        _logger = logger ?? NullLogger<IdentomatHttpTransport>.Instance;
    }

    public async Task<AZOAResult<TResponse>> PostAsync<TRequest, TResponse>(
        string baseUrl,
        string path,
        TRequest body,
        string operation,
        CancellationToken ct = default)
        where TResponse : class
    {
        if (!_budget.TryAcquire(operation))
        {
            _logger.LogWarning(
                "Identomat {Operation} was refused locally by the shared request budget.",
                operation);
            return AZOAResult<TResponse>.FailureWithCode(
                RateLimitedMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }

        try
        {
            var client = _httpClientFactory.CreateClient(IdentomatKycSettings.HttpClientName);
            var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));

            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(body, options: Json),
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.RequestTimeoutSeconds, 1, 120)));

            using var response = await client.SendAsync(request, cts.Token);
            if ((int)response.StatusCode == TooManyRequestsStatusCode
                || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _budget.NoteRateLimited();
                _logger.LogError(
                    "Identomat rate-limited the node on {Operation}; the shared budget is parked until the ban clears.",
                    operation);
                return AZOAResult<TResponse>.FailureWithCode(
                    RateLimitedMessage,
                    AzoaErrorCodes.DependencyUnavailable);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Status code only. A vendor error body can echo the request, which
                // for these endpoints means echoing the company key.
                _logger.LogError(
                    "Identomat {Operation} returned HTTP {StatusCode}.",
                    operation,
                    (int)response.StatusCode);
                return AZOAResult<TResponse>.FailureWithCode(
                    DependencyMessage,
                    AzoaErrorCodes.DependencyUnavailable);
            }

            var parsed = await response.Content.ReadFromJsonAsync<TResponse>(Json, cts.Token);
            if (parsed is null)
            {
                _logger.LogError("Identomat {Operation} returned an unreadable body.", operation);
                return AZOAResult<TResponse>.FailureWithCode(
                    DependencyMessage,
                    AzoaErrorCodes.DependencyUnavailable);
            }

            return AZOAResult<TResponse>.Success(parsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The exception message can carry the request URI; log it, never return it.
            _logger.LogError(exception, "Identomat {Operation} failed.", operation);
            return AZOAResult<TResponse>.FailureWithCode(
                DependencyMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }
    }
}
