using System.Globalization;
using System.Net;
using System.Security.Cryptography;
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

/// <summary>Veriff v1 hosted verification with signed authoritative decisions and durable admission.</summary>
public sealed class VeriffKycProviderService(IOptions<KycSettings> options, IHostedProviderAttemptStore attempts,
    IHostedSessionStore sessions, HostedIdentityHttp transport) : PreparedHostedKycProvider(attempts, sessions, transport)
{
    private readonly KycSettings _settings = options.Value;
    public override KycProvider Provider => KycProvider.VERIFF;
    public override string ProviderKey => "veriff";
    protected override string AdapterKey => "veriff";
    protected override string ProtocolVersion => "veriff-v1";
    protected override string Origin => HostedIdentityProtocol.Origin(_settings.VeriffBaseUrl, out var origin) ? origin : string.Empty;
    protected override bool PermanentIdempotency => false;

    /// <inheritdoc/>
    public override HostedSessionCapabilities GetHostedCapabilities()
    {
        var missing = new List<string>();
        if (Origin.Length == 0) missing.Add("Kyc__VeriffBaseUrl");
        if (string.IsNullOrWhiteSpace(_settings.VeriffApiKey)) missing.Add("Kyc__VeriffApiKey");
        if (_settings.VeriffSharedSecret is not { Length: >= 32 } || _settings.VeriffSharedSecret == _settings.VeriffApiKey) missing.Add("Kyc__VeriffSharedSecret");
        if (_settings.VeriffRedirectOrigins.Length == 0 || _settings.VeriffRedirectOrigins.Any(origin => !HostedIdentityProtocol.Origin(origin, out _))) missing.Add("Kyc__VeriffRedirectOrigins");
        if (!Uri.TryCreate(_settings.VeriffReturnUrl, UriKind.Absolute, out var returnUrl) || returnUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(returnUrl.UserInfo) || !string.IsNullOrEmpty(returnUrl.Fragment)
            || returnUrl.IsLoopback || IdentomatKycProviderService.IsInternalHost(returnUrl)) missing.Add("Kyc__VeriffReturnUrl");
        return new() { ProviderKey = ProviderKey, FlowKind = FlowKind, Available = missing.Count == 0,
            MissingConfigurationKeys = missing, UnavailableReason = missing.Count == 0 ? null : "Veriff identity verification is not fully configured." };
    }

    protected override string RequestBody(HostedSessionRequest request) => JsonSerializer.Serialize(new
    {
        verification = new { callback = _settings.VeriffReturnUrl, vendorData = request.SessionId!.Value.ToString("N"),
            endUserId = request.SubjectUserId.ToString("D"), timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
    });

    protected override async Task<AZOAResult<HostedSessionCreation>> SendCreationAsync(HostedProviderAttempt attempt, bool recovery, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "/v1/sessions", null);
        request.Content = new StringContent(attempt.RequestJson, System.Text.Encoding.UTF8, "application/json");
        var response = await Transport.SendAsync(request, ct);
        if (response.IsError || response.Result is null || response.Result.Status != HttpStatusCode.Created
            || !Authentic(response.Result)) return Invalid<HostedSessionCreation>();
        try
        {
            using var document = HostedIdentityProtocol.Parse(response.Result.Body);
            var root = document.RootElement;
            if (HostedIdentityProtocol.Text(root, "status") != "success" || !root.TryGetProperty("verification", out var verification)
                || !Guid.TryParse(HostedIdentityProtocol.Text(verification, "id"), out var id)
                || !HostedIdentityProtocol.Redirect(HostedIdentityProtocol.Text(verification, "url"), _settings.VeriffRedirectOrigins)
                || HostedIdentityProtocol.Text(verification, "vendorData") != attempt.Id
                || !Guid.TryParse(HostedIdentityProtocol.Text(verification, "endUserId"), out var subject) || subject.ToString("N") != attempt.SubjectId)
                return Invalid<HostedSessionCreation>();
            return AZOAResult<HostedSessionCreation>.Success(new() { ProviderSessionId = id.ToString("D"),
                RedirectUrl = HostedIdentityProtocol.Text(verification, "url")! });
        }
        catch (JsonException) { return Invalid<HostedSessionCreation>(); }
    }

    /// <inheritdoc/>
    public override AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        if (!GetHostedCapabilities().Available || !headers.TryGetValue("X-AUTH-CLIENT", out var client)
            || !headers.TryGetValue("X-HMAC-SIGNATURE", out var signature)
            || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(client), System.Text.Encoding.UTF8.GetBytes(_settings.VeriffApiKey!))
            || !HostedIdentityProtocol.VerifyHmac(signature, _settings.VeriffSharedSecret!, rawBody)) return Invalid<HostedCallbackTrigger>();
        try
        {
            using var document = HostedIdentityProtocol.Parse(rawBody);
            if (!document.RootElement.TryGetProperty("verification", out var verification)
                || !Guid.TryParse(HostedIdentityProtocol.Text(verification, "id"), out var id)) return Invalid<HostedCallbackTrigger>();
            Guid? attempt = Guid.TryParseExact(HostedIdentityProtocol.Text(verification, "vendorData"), "N", out var parsed) ? parsed : null;
            return AZOAResult<HostedCallbackTrigger>.Success(new() { ProviderSessionId = id.ToString("D"), AttemptId = attempt,
                EventId = HostedIdentityProtocol.Digest(rawBody) });
        }
        catch (JsonException) { return Invalid<HostedCallbackTrigger>(); }
    }

    /// <inheritdoc/>
    public override async Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(string providerSessionId, CancellationToken ct = default)
    {
        if (!GetHostedCapabilities().Available || !Guid.TryParse(providerSessionId, out var sessionId)) return Invalid<HostedSessionOutcome>();
        var canonical = sessionId.ToString("D");
        var attempt = await Attempts.GetByRemoteAsync(AdapterKey, Origin, canonical, ct);
        if (attempt is null) return Invalid<HostedSessionOutcome>();
        using var request = Request(HttpMethod.Get, $"/v1/sessions/{canonical}/decision", canonical);
        var response = await Transport.SendAsync(request, ct);
        if (response.IsError || response.Result is null || response.Result.Status != HttpStatusCode.OK || !Authentic(response.Result)) return Invalid<HostedSessionOutcome>();
        try
        {
            using var document = HostedIdentityProtocol.Parse(response.Result.Body);
            var root = document.RootElement;
            if (HostedIdentityProtocol.Text(root, "status") != "success" || !root.TryGetProperty("verification", out var verification)) return Invalid<HostedSessionOutcome>();
            if (verification.ValueKind == JsonValueKind.Null) return AZOAResult<HostedSessionOutcome>.Success(new() { SessionExists = true, Outcome = "PENDING" });
            if (!Guid.TryParse(HostedIdentityProtocol.Text(verification, "id"), out var id) || id != sessionId
                || HostedIdentityProtocol.Text(verification, "vendorData") != attempt.Id
                || !Guid.TryParse(HostedIdentityProtocol.Text(verification, "endUserId"), out var user) || user.ToString("N") != attempt.SubjectId
                || !verification.TryGetProperty("code", out var codeElement) || !codeElement.TryGetInt32(out var code)) return Invalid<HostedSessionOutcome>();
            var status = HostedIdentityProtocol.Text(verification, "status");
            if (status == "resubmission_requested" && code == 9103) return AZOAResult<HostedSessionOutcome>.Success(new() { SessionExists = true, Outcome = "RESUBMISSION_REQUESTED" });
            if ((status, code) is not (("approved", 9001) or ("declined", 9102) or ("expired", 9104) or ("abandoned", 9121))) return Invalid<HostedSessionOutcome>();
            var decisionAt = HostedIdentityProtocol.Text(verification, "decisionTime");
            var decisionAttempt = HostedIdentityProtocol.Text(verification, "attemptId");
            if (!Guid.TryParse(decisionAttempt, out _) || !HostedIdentityProtocol.UtcInstant(decisionAt, out var decided)
                || decided.Offset != TimeSpan.Zero || decided < attempt.CreatedAt.AddMinutes(-5) || decided > DateTimeOffset.UtcNow.AddMinutes(5)) return Invalid<HostedSessionOutcome>();
            HostedSessionSubjectAttributes? subject = null;
            DateTimeOffset? validUntil = null;
            if (status == "approved")
            {
                if (!verification.TryGetProperty("person", out var person) || !verification.TryGetProperty("document", out var identityDocument)) return Invalid<HostedSessionOutcome>();
                var first = HostedIdentityProtocol.Text(person, "firstName"); var last = HostedIdentityProtocol.Text(person, "lastName");
                var birth = HostedIdentityProtocol.Text(person, "dateOfBirth"); var country = HostedIdentityProtocol.Text(identityDocument, "country");
                var number = HostedIdentityProtocol.Text(identityDocument, "number");
                if (!HostedIdentityProtocol.SafeToken(first, 200) || !HostedIdentityProtocol.SafeToken(last, 200)
                    || !DateOnly.TryParseExact(birth, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birthday)
                    || birthday > DateOnly.FromDateTime(DateTime.UtcNow) || country is not { Length: 2 } || !country.All(char.IsAsciiLetter)
                    || !HostedIdentityProtocol.SafeToken(number, 128)) return Invalid<HostedSessionOutcome>();
                var documentExpiry = HostedIdentityProtocol.Text(identityDocument, "validUntil");
                if (documentExpiry is not null)
                {
                    if (!DateOnly.TryParseExact(documentExpiry, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) return Invalid<HostedSessionOutcome>();
                    validUntil = new DateTimeOffset(expiry.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
                }
                subject = new() { FirstName = first, LastName = last, Birthday = birth, IssuingState = country,
                    DocumentNumber = number, Citizenship = HostedIdentityProtocol.Text(person, "citizenship") };
            }
            var decisionId = decisionAttempt + ":" + decisionAt;
            var digest = HostedIdentityProtocol.Digest(JsonSerializer.Serialize(new { sessionId = canonical, attempt.Id, attempt.SubjectId,
                decisionAttempt, decisionAt, status, code, subject, validUntil }));
            if (!await Attempts.PinTerminalAsync(attempt.Id, canonical, decisionId, digest, ct)) return Invalid<HostedSessionOutcome>();
            return AZOAResult<HostedSessionOutcome>.Success(new() { SessionExists = true, IsTerminal = true, IsApproved = status == "approved",
                Outcome = status!.ToUpperInvariant(), Subject = subject, EvidenceValidUntil = validUntil });
        }
        catch (JsonException) { return Invalid<HostedSessionOutcome>(); }
    }

    /// <inheritdoc/>
    public override async Task<AZOAResult<HostedSessionCreation>> RecoverCandidateAsync(HostedSession session, string remoteId, CancellationToken ct)
    {
        var attempt = await Attempts.GetAsync(session.Id, ct);
        if (!GetHostedCapabilities().Available || attempt is null || attempt.AdapterKey != AdapterKey
            || attempt.Origin != Origin || attempt.ProviderKey != session.ProviderKey || attempt.SubjectId != session.SubjectUserId
            || attempt.TenantId != session.TenantId || attempt.SendStartedAt is null
            || attempt.RequestDigest != HostedIdentityProtocol.Digest(attempt.RequestJson)
            || !Guid.TryParse(remoteId, out var candidate)) return Invalid<HostedSessionCreation>();
        var canonical = candidate.ToString("D");
        if (attempt.RemoteSessionId is not null)
            return attempt.RemoteSessionId == canonical ? await RecoverCreationAsync(session, ct) : Invalid<HostedSessionCreation>();
        using var request = Request(HttpMethod.Get, $"/v1/sessions/{canonical}/decision", canonical);
        var response = await Transport.SendAsync(request, ct);
        if (response.IsError || response.Result is null || response.Result.Status != HttpStatusCode.OK || !Authentic(response.Result))
            return Invalid<HostedSessionCreation>();
        try
        {
            using var document = HostedIdentityProtocol.Parse(response.Result.Body);
            var root = document.RootElement;
            if (HostedIdentityProtocol.Text(root, "status") != "success" || !root.TryGetProperty("verification", out var verification)
                || !Guid.TryParse(HostedIdentityProtocol.Text(verification, "id"), out var id) || id != candidate
                || HostedIdentityProtocol.Text(verification, "vendorData") != attempt.Id
                || !Guid.TryParse(HostedIdentityProtocol.Text(verification, "endUserId"), out var subject) || subject.ToString("N") != attempt.SubjectId
                || !verification.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number)
                || (HostedIdentityProtocol.Text(verification, "status"), number) is not (("approved", 9001) or ("declined", 9102) or ("expired", 9104) or ("abandoned", 9121)))
                return Invalid<HostedSessionCreation>();
            using var original = HostedIdentityProtocol.Parse(attempt.RequestJson);
            var returnUrl = HostedIdentityProtocol.Text(original.RootElement.GetProperty("verification"), "callback");
            if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var redirect) || redirect.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(redirect.UserInfo) || !string.IsNullOrEmpty(redirect.Fragment)) return Invalid<HostedSessionCreation>();
            var lease = await Attempts.ClaimAsync(session, true, DateTimeOffset.UtcNow, ct);
            if (lease is null) return Invalid<HostedSessionCreation>();
            var creation = new HostedSessionCreation { ProviderSessionId = canonical, RedirectUrl = returnUrl!, ExpiresAt = DateTimeOffset.UtcNow };
            return await Attempts.SaveCreationAsync(lease, creation, ct) is null ? Invalid<HostedSessionCreation>()
                : AZOAResult<HostedSessionCreation>.Success(creation);
        }
        catch (JsonException) { return Invalid<HostedSessionCreation>(); }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? signedSession)
    {
        var request = new HttpRequestMessage(method, Origin + path);
        request.Headers.Add("X-AUTH-CLIENT", _settings.VeriffApiKey);
        if (signedSession is not null) request.Headers.Add("X-HMAC-SIGNATURE", HostedIdentityProtocol.Hmac(_settings.VeriffSharedSecret!, signedSession));
        return request;
    }

    private bool Authentic(HostedIdentityHttp.Response response)
        => response.Headers.TryGetValue("X-HMAC-SIGNATURE", out var signature)
            && HostedIdentityProtocol.VerifyHmac(signature, _settings.VeriffSharedSecret!, response.Body);
}
