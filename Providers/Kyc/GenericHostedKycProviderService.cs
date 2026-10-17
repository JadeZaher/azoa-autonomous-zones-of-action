using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>Reviewed version-one hosted identity protocol with permanent request idempotency.</summary>
public sealed class GenericHostedKycProviderService(IOptions<KycSettings> options, IHostedProviderAttemptStore attempts,
    IHostedSessionStore sessions, HostedIdentityHttp transport) : PreparedHostedKycProvider(attempts, sessions, transport)
{
    private readonly HostedKycSettings _settings = options.Value.Hosted;
    public override KycProvider Provider => KycProvider.GENERIC_HOSTED;
    public override string ProviderKey => _settings.ProviderName?.Trim().ToLowerInvariant() ?? "hosted";
    protected override string AdapterKey => "generic-hosted";
    protected override string ProtocolVersion => "1";
    protected override string Origin => HostedIdentityProtocol.Origin(_settings.BaseUrl, out var origin) ? origin : string.Empty;
    protected override bool PermanentIdempotency => true;

    /// <inheritdoc/>
    public override HostedSessionCapabilities GetHostedCapabilities()
    {
        var missing = new List<string>();
        if (_settings.ProtocolVersion != "1") missing.Add("Kyc__Hosted__ProtocolVersion");
        if (!HostedIdentityProtocol.SafeToken(ProviderKey, 64) || !ProviderKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) missing.Add("Kyc__Hosted__ProviderName");
        if (Origin.Length == 0) missing.Add("Kyc__Hosted__BaseUrl");
        if (_settings.ApiKey is not { Length: >= 32 }) missing.Add("Kyc__Hosted__ApiKey");
        if (_settings.WebhookSecret is not { Length: >= 32 } || _settings.WebhookSecret == _settings.ApiKey) missing.Add("Kyc__Hosted__WebhookSecret");
        if (!HostedIdentityProtocol.SafeToken(_settings.WebhookKeyId, 64)) missing.Add("Kyc__Hosted__WebhookKeyId");
        if (_settings.RedirectOrigins.Length == 0 || _settings.RedirectOrigins.Any(origin => !HostedIdentityProtocol.Origin(origin, out _))) missing.Add("Kyc__Hosted__RedirectOrigins");
        if (!Uri.TryCreate(_settings.ReturnUrl, UriKind.Absolute, out var returnUrl) || returnUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(returnUrl.UserInfo) || !string.IsNullOrEmpty(returnUrl.Fragment)
            || returnUrl.IsLoopback || IdentomatKycProviderService.IsInternalHost(returnUrl)) missing.Add("Kyc__Hosted__ReturnUrl");
        var keyIds = _settings.RetiredWebhookKeys.Select(key => key.KeyId).Append(_settings.WebhookKeyId).ToArray();
        if (keyIds.Distinct(StringComparer.Ordinal).Count() != keyIds.Length || _settings.RetiredWebhookKeys.Any(key =>
                !HostedIdentityProtocol.SafeToken(key.KeyId, 64) || key.Secret.Length < 32 || key.Secret == _settings.ApiKey
                || key.AcceptUntil <= key.RetiredAt || key.AcceptUntil > key.RetiredAt.AddHours(24))) missing.Add("Kyc__Hosted__RetiredWebhookKeys");
        return new() { ProviderKey = ProviderKey, FlowKind = FlowKind, Available = missing.Count == 0,
            MissingConfigurationKeys = missing, UnavailableReason = missing.Count == 0 ? null : "The trusted hosted identity protocol is not fully configured." };
    }

    protected override string RequestBody(HostedSessionRequest request) => JsonSerializer.Serialize(new
    {
        protocolVersion = "1", provider = ProviderKey, attemptId = request.SessionId!.Value.ToString("N"),
        subjectId = request.SubjectUserId.ToString("N"), tenantId = request.TenantId?.ToString("N"),
        flow = "kyc", policyRevision = request.PolicyRevision, returnUrl = _settings.ReturnUrl,
    });

    protected override async Task<AZOAResult<HostedSessionCreation>> SendCreationAsync(HostedProviderAttempt attempt, bool recovery, CancellationToken ct)
    {
        using var original = HostedIdentityProtocol.Parse(attempt.RequestJson);
        if (!Correlation(original.RootElement, attempt)) return Invalid<HostedSessionCreation>();
        if (recovery)
        {
            using var lookup = Request(HttpMethod.Get, $"/azoa/identity/v1/attempts/{Uri.EscapeDataString(attempt.Id)}");
            var found = await Transport.SendAsync(lookup, ct);
            if (found.IsError || found.Result is null) return AZOAResult<HostedSessionCreation>.Failure(found.Message);
            if (found.Result.Status == HttpStatusCode.OK) return ParseCreation(found.Result.Body, attempt);
            if (found.Result.Status != HttpStatusCode.NotFound) return Invalid<HostedSessionCreation>();
        }
        using var request = Request(HttpMethod.Post, "/azoa/identity/v1/sessions");
        request.Headers.Add("Idempotency-Key", attempt.Id);
        request.Content = new StringContent(attempt.RequestJson, System.Text.Encoding.UTF8, "application/json");
        var response = await Transport.SendAsync(request, ct);
        return response.IsError || response.Result is null ? AZOAResult<HostedSessionCreation>.Failure(response.Message)
            : response.Result.Status is HttpStatusCode.OK or HttpStatusCode.Created ? ParseCreation(response.Result.Body, attempt)
            : Invalid<HostedSessionCreation>();
    }

    private AZOAResult<HostedSessionCreation> ParseCreation(string body, HostedProviderAttempt attempt)
    {
        try
        {
            using var document = HostedIdentityProtocol.Parse(body);
            var value = document.RootElement;
            if (!HostedIdentityProtocol.Fields(value, "protocolVersion", "provider", "attemptId", "subjectId", "tenantId", "flow", "policyRevision", "sessionId", "redirectUrl", "expiresAt")
                || !Correlation(value, attempt) || !HostedIdentityProtocol.SafeToken(HostedIdentityProtocol.Text(value, "sessionId"))
                || !HostedIdentityProtocol.Redirect(HostedIdentityProtocol.Text(value, "redirectUrl"), _settings.RedirectOrigins)
                || !Instant(value, "expiresAt", out var expiry) || expiry <= attempt.CreatedAt) return Invalid<HostedSessionCreation>();
            return AZOAResult<HostedSessionCreation>.Success(new() { ProviderSessionId = HostedIdentityProtocol.Text(value, "sessionId")!,
                RedirectUrl = HostedIdentityProtocol.Text(value, "redirectUrl")!, ExpiresAt = expiry });
        }
        catch (JsonException) { return Invalid<HostedSessionCreation>(); }
    }

    /// <inheritdoc/>
    public override AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        if (!GetHostedCapabilities().Available || !headers.TryGetValue("X-AZOA-Key-Id", out var keyId)
            || !headers.TryGetValue("X-AZOA-Timestamp", out var timestamp) || !headers.TryGetValue("X-AZOA-Signature", out var signature)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || Math.Abs((decimal)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds) > 300) return Invalid<HostedCallbackTrigger>();
        var now = DateTimeOffset.UtcNow;
        var secret = keyId == _settings.WebhookKeyId ? _settings.WebhookSecret
            : _settings.RetiredWebhookKeys.SingleOrDefault(key => key.KeyId == keyId && key.RetiredAt <= now && key.AcceptUntil >= now)?.Secret;
        if (secret is null || !HostedIdentityProtocol.VerifyHmac(signature, secret, timestamp + "." + rawBody)) return Invalid<HostedCallbackTrigger>();
        try
        {
            using var document = HostedIdentityProtocol.Parse(rawBody);
            var value = document.RootElement;
            if (!HostedIdentityProtocol.Fields(value, "protocolVersion", "provider", "eventId", "sessionId", "attemptId")
                || HostedIdentityProtocol.Text(value, "protocolVersion") != "1" || HostedIdentityProtocol.Text(value, "provider") != ProviderKey
                || !Guid.TryParseExact(HostedIdentityProtocol.Text(value, "attemptId"), "N", out var attempt)
                || !HostedIdentityProtocol.SafeToken(HostedIdentityProtocol.Text(value, "sessionId"))
                || !HostedIdentityProtocol.SafeToken(HostedIdentityProtocol.Text(value, "eventId"), 128)) return Invalid<HostedCallbackTrigger>();
            return AZOAResult<HostedCallbackTrigger>.Success(new() { ProviderSessionId = HostedIdentityProtocol.Text(value, "sessionId")!,
                AttemptId = attempt, EventId = HostedIdentityProtocol.Text(value, "eventId") });
        }
        catch (JsonException) { return Invalid<HostedCallbackTrigger>(); }
    }

    /// <inheritdoc/>
    public override async Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(string providerSessionId, CancellationToken ct = default)
    {
        if (!GetHostedCapabilities().Available || !HostedIdentityProtocol.SafeToken(providerSessionId)) return Invalid<HostedSessionOutcome>();
        var attempt = await Attempts.GetByRemoteAsync(AdapterKey, Origin, providerSessionId, ct);
        if (attempt is null) return Invalid<HostedSessionOutcome>();
        using var request = Request(HttpMethod.Get, $"/azoa/identity/v1/sessions/{Uri.EscapeDataString(providerSessionId)}/result");
        var response = await Transport.SendAsync(request, ct);
        if (response.IsError || response.Result is null || response.Result.Status != HttpStatusCode.OK) return Invalid<HostedSessionOutcome>();
        try
        {
            using var document = HostedIdentityProtocol.Parse(response.Result.Body);
            var value = document.RootElement;
            if (!Correlation(value, attempt) || HostedIdentityProtocol.Text(value, "sessionId") != providerSessionId
                || !HostedIdentityProtocol.Fields(value, "protocolVersion", "provider", "attemptId", "subjectId", "tenantId", "flow", "policyRevision", "sessionId", "status", "decisionId", "decidedAt", "verifiedAt", "validUntil", "subject", "evidencePayload", "evidenceDigest")) return Invalid<HostedSessionOutcome>();
            var status = HostedIdentityProtocol.Text(value, "status");
            if (status is "pending" or "review")
                return HostedIdentityProtocol.Fields(value, "protocolVersion", "provider", "attemptId", "subjectId", "tenantId", "flow", "policyRevision", "sessionId", "status")
                    ? AZOAResult<HostedSessionOutcome>.Success(new() { SessionExists = true, Outcome = status.ToUpperInvariant() })
                    : Invalid<HostedSessionOutcome>();
            if (status is not ("approved" or "rejected" or "expired")) return Invalid<HostedSessionOutcome>();
            var decision = HostedIdentityProtocol.Text(value, "decisionId");
            var payload = HostedIdentityProtocol.Text(value, "evidencePayload");
            var digest = HostedIdentityProtocol.Text(value, "evidenceDigest");
            if (!HostedIdentityProtocol.SafeToken(decision) || payload is null || digest is not { Length: 64 }
                || !Instant(value, "decidedAt", out var decided) || decided < attempt.CreatedAt || decided > DateTimeOffset.UtcNow.AddMinutes(5)) return Invalid<HostedSessionOutcome>();
            var evidenceBytes = Convert.FromBase64String(payload);
            var evidenceText = new System.Text.UTF8Encoding(false, true).GetString(evidenceBytes);
            if (HostedIdentityProtocol.Digest(evidenceText) != digest) return Invalid<HostedSessionOutcome>();
            using var evidenceDocument = HostedIdentityProtocol.Parse(evidenceText);
            var evidence = evidenceDocument.RootElement;
            if (!Correlation(evidence, attempt) || HostedIdentityProtocol.Text(evidence, "sessionId") != providerSessionId
                || HostedIdentityProtocol.Text(evidence, "status") != status || HostedIdentityProtocol.Text(evidence, "decisionId") != decision
                || HostedIdentityProtocol.Text(evidence, "decidedAt") != HostedIdentityProtocol.Text(value, "decidedAt")
                || !HostedIdentityProtocol.Fields(evidence, "protocolVersion", "provider", "attemptId", "subjectId", "tenantId", "flow", "policyRevision", "sessionId", "status", "decisionId", "decidedAt", "verifiedAt", "validUntil", "subject", "checks")) return Invalid<HostedSessionOutcome>();
            HostedSessionSubjectAttributes? subject = null;
            DateTimeOffset? evidenceValidUntil = null;
            if (status == "approved")
            {
                if (!Instant(value, "verifiedAt", out var verified) || !Instant(value, "validUntil", out var validUntil)
                    || verified < attempt.CreatedAt || verified > decided || validUntil <= decided
                    || HostedIdentityProtocol.Text(value, "verifiedAt") != HostedIdentityProtocol.Text(evidence, "verifiedAt")
                    || HostedIdentityProtocol.Text(value, "validUntil") != HostedIdentityProtocol.Text(evidence, "validUntil")
                    || !value.TryGetProperty("subject", out var outerSubject) || !evidence.TryGetProperty("subject", out var person)
                    || !evidence.TryGetProperty("checks", out var checks)
                    || !HostedIdentityProtocol.Fields(checks, "documentVerified", "biometricMatchVerified", "livenessVerified") || !True(checks, "documentVerified")
                    || !True(checks, "biometricMatchVerified") || !True(checks, "livenessVerified")) return Invalid<HostedSessionOutcome>();
                var names = new[] { "firstName", "lastName", "birthDate", "issuingCountry", "documentNumber", "citizenship" };
                if (!HostedIdentityProtocol.Fields(person, names) || !HostedIdentityProtocol.Fields(outerSubject, names)
                    || names.Any(name => HostedIdentityProtocol.Text(person, name) != HostedIdentityProtocol.Text(outerSubject, name))) return Invalid<HostedSessionOutcome>();
                var first = HostedIdentityProtocol.Text(person, "firstName"); var last = HostedIdentityProtocol.Text(person, "lastName");
                var birth = HostedIdentityProtocol.Text(person, "birthDate"); var country = HostedIdentityProtocol.Text(person, "issuingCountry");
                var number = HostedIdentityProtocol.Text(person, "documentNumber");
                if (!HostedIdentityProtocol.SafeToken(first, 200) || !HostedIdentityProtocol.SafeToken(last, 200)
                    || !DateOnly.TryParseExact(birth, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birthday)
                    || birthday > DateOnly.FromDateTime(DateTime.UtcNow) || country is not { Length: 2 } || !country.All(char.IsAsciiLetter)
                    || !HostedIdentityProtocol.SafeToken(number, 128)) return Invalid<HostedSessionOutcome>();
                var hosted = (await Sessions.GetByIdAsync(Guid.ParseExact(attempt.Id, "N"), ct)).Result;
                var appliedSame = hosted?.State == HostedSessionStates.Completed && hosted.ProviderDecisionId == decision && hosted.ProviderEvidenceDigest == digest;
                if (validUntil <= DateTimeOffset.UtcNow && !appliedSame) return Invalid<HostedSessionOutcome>();
                evidenceValidUntil = validUntil;
                subject = new() { FirstName = first, LastName = last, Birthday = birth, IssuingState = country,
                    DocumentNumber = number, Citizenship = HostedIdentityProtocol.Text(person, "citizenship") };
            }
            if (!await Attempts.PinTerminalAsync(attempt.Id, providerSessionId, decision!, digest, ct)) return Invalid<HostedSessionOutcome>();
            return AZOAResult<HostedSessionOutcome>.Success(new() { SessionExists = true, Outcome = status.ToUpperInvariant(), IsTerminal = true,
                IsApproved = status == "approved", Subject = subject, EvidenceValidUntil = evidenceValidUntil });
        }
        catch (JsonException) { return Invalid<HostedSessionOutcome>(); }
        catch (FormatException) { return Invalid<HostedSessionOutcome>(); }
        catch (System.Text.DecoderFallbackException) { return Invalid<HostedSessionOutcome>(); }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Origin + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        return request;
    }

    private bool Correlation(JsonElement value, HostedProviderAttempt attempt)
        => HostedIdentityProtocol.Text(value, "protocolVersion") == "1" && HostedIdentityProtocol.Text(value, "provider") == ProviderKey
            && HostedIdentityProtocol.Text(value, "attemptId") == attempt.Id && HostedIdentityProtocol.Text(value, "subjectId") == attempt.SubjectId
            && value.TryGetProperty("tenantId", out var tenant) && (attempt.TenantId is null ? tenant.ValueKind == JsonValueKind.Null : tenant.ValueKind == JsonValueKind.String && tenant.GetString() == attempt.TenantId)
            && HostedIdentityProtocol.Text(value, "flow") == "kyc" && HostedIdentityProtocol.Text(value, "policyRevision") == attempt.PolicyRevision;

    private static bool Instant(JsonElement value, string field, out DateTimeOffset instant)
        => HostedIdentityProtocol.UtcInstant(HostedIdentityProtocol.Text(value, field), out instant);

    private static bool True(JsonElement value, string field)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var result) && result.ValueKind == JsonValueKind.True;
}
