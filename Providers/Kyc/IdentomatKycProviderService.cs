// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>
/// Identomat hosted identity verification. Implements both the KYC provider seam
/// and the reusable <see cref="IHostedSessionProvider"/> lifecycle. See
/// <c>Providers/Kyc/AGENTS.md</c> §identomat for the contract this satisfies.
/// </summary>
public sealed class IdentomatKycProviderService : IKycProviderService, IHostedSessionProvider
{
    /// <summary>Stable registry key.</summary>
    public const string ProviderKeyValue = "identomat";

    /// <summary>Vendor status meaning "a human is still looking at it" — NON-TERMINAL.</summary>
    public const string ManualCheckOutcome = "MANUAL_CHECK";

    public const string ApprovedOutcome = "APPROVED";
    public const string RejectedOutcome = "REJECTED";

    /// <summary>camelCase JSON host path.</summary>
    private const string BeginPath = "/begin";

    /// <summary>
    /// Legacy snake_case widget host path. The host AND the casing differ from
    /// <see cref="BeginPath"/>; the adapter genuinely spans two base URLs.
    /// </summary>
    private const string ResultPath = "/external-api/result/";

    private const string UnconfiguredMessage =
        "The Identomat KYC adapter is not fully configured.";

    private static readonly JsonSerializerOptions CallbackJson = new(JsonSerializerDefaults.Web);

    private readonly IdentomatKycSettings _settings;
    private readonly IIdentomatHttpTransport _transport;
    private readonly IKycTenantQuota _quota;
    private readonly IHostedSessionFlow _flow;
    private readonly ILogger<IdentomatKycProviderService> _logger;

    public IdentomatKycProviderService(
        IOptions<KycSettings> options,
        IIdentomatHttpTransport transport,
        IKycTenantQuota quota,
        IHostedSessionFlow flow,
        ILogger<IdentomatKycProviderService>? logger = null)
    {
        _settings = options.Value.Identomat;
        _transport = transport;
        _quota = quota;
        _flow = flow;
        _logger = logger ?? NullLogger<IdentomatKycProviderService>.Instance;
    }

    public KycProvider Provider => KycProvider.IDENTOMAT;
    public string ProviderKey => ProviderKeyValue;
    public string FlowKind => HostedSessionFlowKinds.Kyc;

    // ── Capabilities ────────────────────────────────────────────────────────

    /// <summary>
    /// Available only when every key is configured over HTTPS. Unavailability
    /// reports the missing configuration key NAMES; a value never appears here.
    /// </summary>
    public KycProviderCapabilitiesModel GetCapabilities()
    {
        var missing = MissingConfigurationKeys();
        return new KycProviderCapabilitiesModel
        {
            Provider = Provider,
            ProviderKey = ProviderKey,
            Available = missing.Count == 0,
            HostedVerification = true,
            AcceptsDocumentReferences = false,
            UnavailableReason = missing.Count == 0
                ? null
                : $"{UnconfiguredMessage} Missing configuration: {string.Join(", ", missing)}.",
        };
    }

    public HostedSessionCapabilities GetHostedCapabilities()
    {
        var missing = MissingConfigurationKeys();
        return new HostedSessionCapabilities
        {
            ProviderKey = ProviderKey,
            FlowKind = FlowKind,
            Available = missing.Count == 0,
            MissingConfigurationKeys = missing,
            UnavailableReason = missing.Count == 0
                ? null
                : $"{UnconfiguredMessage} Missing configuration: {string.Join(", ", missing)}.",
        };
    }

    /// <summary>Configuration key NAMES that are absent or invalid. Never values.</summary>
    public IReadOnlyList<string> MissingConfigurationKeys()
    {
        var missing = new List<string>();
        if (!ValidHttpsOrigin(_settings.BeginBaseUrl))
            missing.Add(IdentomatKycSettings.BeginBaseUrlName);
        if (!ValidHttpsOrigin(_settings.ResultBaseUrl))
            missing.Add(IdentomatKycSettings.ResultBaseUrlName);
        if (string.IsNullOrWhiteSpace(_settings.CompanyKey))
            missing.Add(IdentomatKycSettings.CompanyKeyName);
        if (string.IsNullOrWhiteSpace(_settings.ConfigId))
            missing.Add(IdentomatKycSettings.ConfigIdName);
        if (!IsUsableCallbackSecret(_settings.CallbackApiKey))
            missing.Add(IdentomatKycSettings.CallbackApiKeyName);
        return missing;
    }

    /// <summary>
    /// The callback secret is the ONLY authentication on an anonymous, internet-facing
    /// endpoint, so a presence check is not a configuration check: <c>"test"</c> is
    /// present and guessable in a handful of attempts. Same floor
    /// <see cref="KycPersonKeyDeriver.MinimumSecretLength"/> already enforces for the
    /// person-key root secret, for the same reason.
    /// </summary>
    public static bool IsUsableCallbackSecret(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length >= KycPersonKeyDeriver.MinimumSecretLength;

    // ── IKycProviderService ─────────────────────────────────────────────────

    /// <summary>
    /// Begins verification through <see cref="IHostedSessionFlow"/>, so durable
    /// admission is claimed BEFORE the external create call. The returned
    /// <c>ProviderSessionId</c> is the AZOA hosted-session id, never Identomat's:
    /// <c>KycManager.ToSessionStart</c> projects that field to the tenant.
    /// </summary>
    public Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(
        Guid userId,
        CancellationToken ct = default)
        => BeginSessionAsync(userId, null, ct);

    /// <inheritdoc cref="BeginSessionAsync(Guid, CancellationToken)" />
    public async Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        var capabilities = GetCapabilities();
        if (!capabilities.Available)
            return AZOAResult<KycSessionStartModel>.Failure(capabilities.UnavailableReason!);

        if (!_quota.TryConsume(tenantId, KycQuotaOperation.BeginSession))
        {
            return new AZOAResult<KycSessionStartModel>
            {
                IsError = true,
                Message = "This tenant's KYC begin-session quota is exhausted.",
                Code = AzoaErrorCodes.OperationNotAllowed,
                RetryAfterSeconds = _quota.RetryAfterSeconds,
            };
        }

        var started = await _flow.BeginAsync(
            this,
            new HostedSessionRequest
            {
                FlowKind = FlowKind,
                SubjectUserId = userId,
                TenantId = tenantId,
                // One stable admission key per (subject, tenant): a retry resumes the
                // same claim instead of opening a second Identomat session.
                AdmissionKey = AdmissionKeyFor(userId, tenantId),
            },
            ct);
        if (started.IsError || started.Result is null)
        {
            return AZOAResult<KycSessionStartModel>.FailureWithCode(
                started.Message,
                started.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        return AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
        {
            Provider = Provider,
            ProviderKey = ProviderKey,
            HostedVerification = true,
            AcceptsDocumentReferences = false,
            DevelopmentSimulation = false,
            ProviderSessionId = started.Result.SessionId.ToString("N"),
            VerificationUrl = started.Result.RedirectUrl,
            ExpiresAt = started.Result.ExpiresAt?.UtcDateTime,
            Instructions = "Complete identity verification in the hosted provider window.",
        });
    }

    /// <summary>
    /// Not supported: Identomat is a hosted-redirect flow, so AZOA never brokers
    /// documents on the subject's behalf. Failing here keeps the document-reference
    /// path from silently creating a second provider session.
    /// </summary>
    public Task<AZOAResult<string>> CreateSessionAsync(
        Guid userId,
        IReadOnlyList<KycDocumentModel> documents,
        CancellationToken ct = default)
        => Task.FromResult(AZOAResult<string>.Failure(
            "Identomat verification is hosted; document references are not accepted."));

    /// <inheritdoc cref="CreateSessionAsync" />
    public Task<AZOAResult<bool>> ValidateDocumentsAsync(
        IReadOnlyList<SubmitKycDocumentModel> documents,
        CancellationToken ct = default)
        => Task.FromResult(AZOAResult<bool>.Failure(
            "Identomat verification is hosted; document references are not accepted."));

    /// <summary>
    /// Polls the authoritative result host. Note the host and casing differ from
    /// <c>/begin</c>: this is the legacy snake_case widget host, on purpose.
    /// </summary>
    public async Task<AZOAResult<KycStatus>> GetSessionStatusAsync(
        string providerSessionId,
        CancellationToken ct = default)
    {
        var outcome = await FetchAuthoritativeOutcomeAsync(providerSessionId, ct);
        if (outcome.IsError || outcome.Result is null)
            return AZOAResult<KycStatus>.FailureWithCode(outcome.Message, outcome.Code ?? AzoaErrorCodes.DependencyUnavailable);

        return AZOAResult<KycStatus>.Success(MapStatus(outcome.Result));
    }

    /// <summary>
    /// Verifies the callback secret in constant time, DISCARDS the payload's own
    /// <c>result</c>, and re-fetches <c>/result/</c> as the only authority.
    /// </summary>
    public async Task<AZOAResult<KycStatus>> HandleWebhookAsync(
        string payload,
        CancellationToken ct = default)
    {
        var handled = await _flow.HandleCallbackAsync(this, payload, ct);
        if (handled.IsError || handled.Result is null)
        {
            return AZOAResult<KycStatus>.FailureWithCode(
                handled.Message,
                handled.Code ?? AzoaErrorCodes.Forbidden);
        }

        return AZOAResult<KycStatus>.Success(handled.Result.State switch
        {
            HostedSessionStates.Completed => KycStatus.APPROVED,
            HostedSessionStates.Failed => KycStatus.REJECTED,
            HostedSessionStates.Abandoned => KycStatus.EXPIRED,
            _ => KycStatus.IN_REVIEW,
        });
    }

    // ── IHostedSessionProvider ──────────────────────────────────────────────

    public async Task<AZOAResult<HostedSessionCreation>> CreateHostedSessionAsync(
        HostedSessionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capabilities = GetHostedCapabilities();
        if (!capabilities.Available)
            return AZOAResult<HostedSessionCreation>.Failure(capabilities.UnavailableReason!);

        var body = new IdentomatBeginRequest
        {
            CompanyKey = _settings.CompanyKey!.Trim(),
            ConfigId = _settings.ConfigId!.Trim(),
            ClientUserId = request.SubjectUserId.ToString("N"),
        };

        var response = await _transport.PostAsync<IdentomatBeginRequest, IdentomatBeginResponse>(
            _settings.BeginBaseUrl!,
            BeginPath,
            body,
            "begin",
            ct);
        if (response.IsError || response.Result is null)
            return AZOAResult<HostedSessionCreation>.FailureWithCode(response.Message, response.Code ?? AzoaErrorCodes.DependencyUnavailable);

        var url = response.Result.Url;
        if (!ValidHttpsRedirect(url))
        {
            _logger.LogError("Identomat /begin returned a redirect that is not an acceptable HTTPS URL.");
            return AZOAResult<HostedSessionCreation>.FailureWithCode(
                IdentomatHttpTransport.DependencyMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }

        // Persist BOTH ids server-side. `id` is the parent session; the child
        // `additionalSessionId` is the session_token the legacy result host wants.
        var parentSessionId = response.Result.Id?.Trim();
        var childSessionId = response.Result.AdditionalSessionId?.Trim();
        if (string.IsNullOrWhiteSpace(parentSessionId) && string.IsNullOrWhiteSpace(childSessionId))
        {
            _logger.LogError("Identomat /begin returned no usable session correlation.");
            return AZOAResult<HostedSessionCreation>.FailureWithCode(
                IdentomatHttpTransport.DependencyMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }

        return AZOAResult<HostedSessionCreation>.Success(new HostedSessionCreation
        {
            ProviderSessionId = parentSessionId ?? childSessionId!,
            ProviderCorrelationId = childSessionId,
            RedirectUrl = url!,
            ExpiresAt = null,
        });
    }

    /// <summary>
    /// Constant-time comparison of the body-borne shared secret. The comparison is
    /// length-independent because both sides are hashed first, so neither the
    /// secret's length nor its content leaks through timing. Nothing about the
    /// secret is logged or returned on either branch.
    /// </summary>
    public AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody)
    {
        var expected = _settings.CallbackApiKey;
        if (string.IsNullOrWhiteSpace(expected))
            return AZOAResult<HostedCallbackTrigger>.Failure(UnconfiguredMessage);

        IdentomatCallbackPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<IdentomatCallbackPayload>(rawBody, CallbackJson);
        }
        catch (JsonException)
        {
            return CallbackRejected();
        }

        if (payload is null)
            return CallbackRejected();

        if (!ConstantTimeEquals(payload.CallbackApiKey, expected))
        {
            // Deliberately identical to every other rejection: no oracle.
            _logger.LogWarning("Rejected an Identomat callback whose shared secret did not match.");
            return CallbackRejected();
        }

        var sessionId = payload.SessionId?.Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
            return CallbackRejected();

        // payload.UntrustedResult is intentionally NOT read. The trigger carries
        // correlation only; the verdict comes from the /result/ re-fetch.
        return AZOAResult<HostedCallbackTrigger>.Success(new HostedCallbackTrigger
        {
            ProviderSessionId = sessionId,
            EventType = payload.Type,
        });
    }

    /// <summary>
    /// The ONLY authority for a verdict. <c>MANUAL_CHECK</c> maps to a NON-TERMINAL
    /// outcome so it can never satisfy a value gate.
    /// </summary>
    public async Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(
        string providerSessionId,
        CancellationToken ct = default)
    {
        var capabilities = GetHostedCapabilities();
        if (!capabilities.Available)
            return AZOAResult<HostedSessionOutcome>.Failure(capabilities.UnavailableReason!);
        if (string.IsNullOrWhiteSpace(providerSessionId))
            return AZOAResult<HostedSessionOutcome>.Failure("A provider session id is required.");

        var response = await _transport.PostAsync<IdentomatResultRequest, IdentomatResultResponse>(
            _settings.ResultBaseUrl!,
            ResultPath,
            new IdentomatResultRequest
            {
                CompanyKey = _settings.CompanyKey!.Trim(),
                SessionToken = providerSessionId.Trim(),
            },
            "result",
            ct);
        if (response.IsError || response.Result is null)
            return AZOAResult<HostedSessionOutcome>.FailureWithCode(response.Message, response.Code ?? AzoaErrorCodes.DependencyUnavailable);

        return AZOAResult<HostedSessionOutcome>.Success(MapOutcome(response.Result));
    }

    /// <summary>
    /// Maps a raw vendor result onto the outcome contract. Anything that is not an
    /// explicit approval or rejection — including <c>MANUAL_CHECK</c> and any future
    /// value the vendor adds — is non-terminal, which is the fail-closed direction.
    /// </summary>
    public static HostedSessionOutcome MapOutcome(IdentomatResultResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var raw = response.Result?.Trim().ToUpperInvariant() ?? string.Empty;
        var terminal = raw is ApprovedOutcome or RejectedOutcome;
        var approved = raw == ApprovedOutcome;

        return new HostedSessionOutcome
        {
            Outcome = raw.Length == 0 ? "UNKNOWN" : raw,
            IsTerminal = terminal,
            IsApproved = approved,
            Detail = approved ? null : Truncate(response.RejectReason?.Value, 256),
            Subject = terminal && approved && response.Person is not null
                ? new HostedSessionSubjectAttributes
                {
                    FirstName = response.Person.FirstName,
                    LastName = response.Person.LastName,
                    Birthday = response.Person.Birthday,
                    Citizenship = response.Person.Citizenship,
                    DocumentNumber = response.Person.DocumentNumber,
                    IssuingState = response.Person.IssuingState,
                }
                : null,
        };
    }

    /// <summary>Projects an outcome onto the KYC lifecycle; non-terminal stays IN_REVIEW.</summary>
    public static KycStatus MapStatus(HostedSessionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!outcome.IsTerminal)
            return KycStatus.IN_REVIEW;
        return outcome.IsApproved ? KycStatus.APPROVED : KycStatus.REJECTED;
    }

    /// <summary>Stable per-(subject, tenant) admission key; no secret material in it.</summary>
    public static string AdmissionKeyFor(Guid userId, Guid? tenantId)
        => $"kyc:{ProviderKeyValue}:{userId:N}:{(tenantId is null || tenantId == Guid.Empty ? "direct" : tenantId.Value.ToString("N"))}";

    /// <summary>
    /// Hash-then-compare so the comparison never short-circuits on length. A plain
    /// length check before a fixed-time compare would leak the secret's length.
    /// </summary>
    public static bool ConstantTimeEquals(string? candidate, string? expected)
    {
        if (candidate is null || expected is null)
            return false;
        var candidateHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidate));
        var expectedHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected.Trim()));
        return CryptographicOperations.FixedTimeEquals(candidateHash, expectedHash);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AZOAResult<HostedCallbackTrigger> CallbackRejected()
        => AZOAResult<HostedCallbackTrigger>.FailureWithCode(
            HostedSessionFlow.OpaqueCallbackFailure,
            AzoaErrorCodes.Forbidden);

    private static string? Truncate(string? value, int maximum)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maximum ? value : value[..maximum];

    /// <summary>Mirrors the origin validation used by the generic hosted scaffold.</summary>
    private static bool ValidHttpsOrigin(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && !uri.IsLoopback
            && !IsInternalHost(uri);

    /// <summary>
    /// The redirect legitimately carries a query (the session token), so the origin
    /// rules are relaxed for the query only — credentials, loopback, and internal
    /// address space stay banned.
    /// </summary>
    private static bool ValidHttpsRedirect(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !uri.IsLoopback
            && !IsInternalHost(uri);

    /// <summary>
    /// Refuses private, link-local, and unique-local literals. A vendor origin is
    /// operator configuration rather than attacker input, so this is hardening rather
    /// than a boundary — but <c>169.254.169.254</c> is a cloud metadata endpoint, and
    /// an adapter that will POST a company key at whatever origin it is handed should
    /// not be willing to POST it there.
    /// </summary>
    public static bool IsInternalHost(Uri uri)
    {
        if (!System.Net.IPAddress.TryParse(uri.IdnHost, out var address))
            return false;

        if (System.Net.IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            return octets[0] switch
            {
                10 => true,
                127 => true,
                169 when octets[1] == 254 => true,
                172 when octets[1] is >= 16 and <= 31 => true,
                192 when octets[1] == 168 => true,
                0 => true,
                _ => false,
            };
        }

        return address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6UniqueLocal
            || address.Equals(System.Net.IPAddress.IPv6Any);
    }
}
