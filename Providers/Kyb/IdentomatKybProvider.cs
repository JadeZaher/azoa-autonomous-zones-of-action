using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Providers.Kyb;

/// <summary>Identomat business verification using the documented modern begin/result contracts.</summary>
public sealed class IdentomatKybProvider(IOptions<KycSettings> kycOptions, IOptions<KybSettings> options,
    IIdentomatHttpTransport transport) : IHostedSessionProvider
{
    private readonly IdentomatKycSettings _credentials = kycOptions.Value.Identomat;
    private readonly KybSettings _settings = options.Value;

    /// <inheritdoc/>
    public string FlowKind => HostedSessionFlowKinds.Kyb;
    /// <inheritdoc/>
    public string ProviderKey => "identomat";

    /// <summary>Policy/configuration identity captured on every attempt.</summary>
    public string AuthorityRevision => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new[] { _settings.PolicyVersion, _settings.ConfigId,
            _credentials.BeginBaseUrl, _credentials.CompanyKey }))));

    /// <inheritdoc/>
    public HostedSessionCapabilities GetHostedCapabilities()
    {
        var missing = new List<string>();
        if (!ValidPublicUrl(_credentials.BeginBaseUrl, true)) missing.Add("Kyc__Identomat__BeginBaseUrl");
        if (string.IsNullOrWhiteSpace(_credentials.CompanyKey)) missing.Add("Kyc__Identomat__CompanyKey");
        if (string.IsNullOrWhiteSpace(_settings.ConfigId)) missing.Add("Kyb__ConfigId");
        if (string.IsNullOrWhiteSpace(_settings.PolicyVersion)) missing.Add("Kyb__PolicyVersion");
        if (!IdentomatKycProviderService.IsUsableCallbackSecret(_credentials.CallbackApiKey)) missing.Add("Kyc__Identomat__CallbackApiKey");
        if (_settings.ReturnUrl is not null && !ValidPublicUrl(_settings.ReturnUrl, false)) missing.Add("Kyb__ReturnUrl");
        return new HostedSessionCapabilities
        {
            ProviderKey = ProviderKey, FlowKind = FlowKind, Available = missing.Count == 0,
            MissingConfigurationKeys = missing,
            UnavailableReason = missing.Count == 0 ? null : "Business verification is not configured.",
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSessionCreation>> CreateHostedSessionAsync(HostedSessionRequest request, CancellationToken ct = default)
    {
        if (!GetHostedCapabilities().Available || request.FlowKind != FlowKind || request.TenantId is null
            || request.SubjectUserId != request.TenantId.Value)
            return AZOAResult<HostedSessionCreation>.FailureWithCode("Business verification is unavailable.", HostedSessionFlow.RequestNotSentCode);
        var body = new Dictionary<string, object>
        {
            ["companyKey"] = _credentials.CompanyKey!.Trim(), ["configId"] = _settings.ConfigId!.Trim(),
            ["clientUserId"] = request.AdmissionKey,
        };
        if (_settings.ReturnUrl is not null)
            body["general"] = new Dictionary<string, string> { ["returnUrl"] = _settings.ReturnUrl };
        var response = await transport.PostAsync<Dictionary<string, object>, IdentomatBeginResponse>(
            _credentials.BeginBaseUrl!, "/begin", body, "kyb-begin", ct);
        if (response.IsError || response.Result is null)
            return AZOAResult<HostedSessionCreation>.FailureWithCode("Business verification could not start.", response.Code ?? AzoaErrorCodes.DependencyUnavailable);
        var value = response.Result;
        var redirect = value.AdditionalSessionUrl ?? value.Url;
        if (string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 256 || value.AdditionalSessionId?.Length > 256
            || !ValidPublicUrl(redirect, false))
            return AZOAResult<HostedSessionCreation>.FailureWithCode("Business verification could not start.", AzoaErrorCodes.DependencyUnavailable);
        return AZOAResult<HostedSessionCreation>.Success(new HostedSessionCreation
        {
            ProviderSessionId = value.Id, ProviderCorrelationId = value.AdditionalSessionId, RedirectUrl = redirect!,
        });
    }

    /// <inheritdoc/>
    public AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody)
    {
        if (!GetHostedCapabilities().Available) return Rejected();
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("callbackApiKey", out var key) || key.ValueKind != JsonValueKind.String
                || !IdentomatKycProviderService.ConstantTimeEquals(key.GetString(), _credentials.CallbackApiKey)
                || !root.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 256)
                return Rejected();
            return AZOAResult<HostedCallbackTrigger>.Success(new HostedCallbackTrigger { ProviderSessionId = id.GetString()! });
        }
        catch (JsonException) { return Rejected(); }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(string providerSessionId, CancellationToken ct = default)
    {
        if (!GetHostedCapabilities().Available || string.IsNullOrWhiteSpace(providerSessionId))
            return AZOAResult<HostedSessionOutcome>.FailureWithCode("Business verification is unavailable.", AzoaErrorCodes.DependencyUnavailable);
        var response = await transport.PostAsync<Dictionary<string, string>, JsonDocument>(
            _credentials.BeginBaseUrl!, "/result", new()
            {
                ["companyKey"] = _credentials.CompanyKey!.Trim(), ["sessionId"] = providerSessionId,
            }, "kyb-result", ct);
        if (response.IsError || response.Result is null)
            return AZOAResult<HostedSessionOutcome>.FailureWithCode("Business verification could not be refreshed.", AzoaErrorCodes.DependencyUnavailable);
        using var document = response.Result;
        return AZOAResult<HostedSessionOutcome>.Success(IdentomatKybResultParser.Parse(document.RootElement));
    }

    private static AZOAResult<HostedCallbackTrigger> Rejected()
        => AZOAResult<HostedCallbackTrigger>.FailureWithCode(HostedSessionFlow.OpaqueCallbackFailure, AzoaErrorCodes.Forbidden);

    private static bool ValidPublicUrl(string? value, bool origin)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo) && !uri.IsLoopback && !IdentomatKycProviderService.IsInternalHost(uri)
            && string.IsNullOrEmpty(uri.Fragment) && (!origin || string.IsNullOrEmpty(uri.Query));
}
