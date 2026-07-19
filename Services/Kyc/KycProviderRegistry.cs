using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Services.Admin;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Kyc;

public static class KycProviderReadinessCodes
{
    public const string Ready = "READY";
    public const string Disabled = "DISABLED";
    public const string ProfileNotConfigured = "PROFILE_NOT_CONFIGURED";
    public const string AdapterUnavailable = "ADAPTER_UNAVAILABLE";
    public const string SecretsNotConfigured = "SECRETS_NOT_CONFIGURED";
    public const string PolicyInvalid = "POLICY_INVALID";
    public const string SelectionRequired = "SELECTION_REQUIRED";
}

/// <summary>Resolves persisted tenant choices only through allowlisted runtime adapters.</summary>
public sealed class KycProviderRegistry : IKycProviderRegistry
{
    private readonly IKycControlStore _store;
    private readonly KycSettings _nodeSettings;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IReadOnlyDictionary<string, IKycProviderService> _adapters;

    public KycProviderRegistry(
        IKycControlStore store,
        ManualKycProviderService manual,
        VeriffKycProviderService veriff,
        GenericHostedKycProviderService hosted,
        IOptions<KycSettings> settings,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _store = store;
        _nodeSettings = settings.Value;
        _environment = environment;
        _configuration = configuration;
        _adapters = new Dictionary<string, IKycProviderService>(StringComparer.Ordinal)
        {
            ["manual"] = manual,
            ["veriff"] = veriff,
            ["generic-hosted"] = hosted,
        };
    }

    public async Task<AZOAResult<KycProviderResolution>> ResolveTenantAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || tenantId == NodeOperatorIdentity.AvatarId)
            return PolicyFailure<KycProviderResolution>(KycProviderReadinessCodes.SelectionRequired);
        var selection = await _store.GetSelectionAsync(tenantId, ct);
        if (selection.IsError)
            return DependencyFailure<KycProviderResolution>();
        if (selection.Result is null)
            return PolicyFailure<KycProviderResolution>(KycProviderReadinessCodes.SelectionRequired);

        var profileResult = await _store.GetProfileAsync(selection.Result.ProviderKey, ct);
        if (profileResult.IsError)
            return DependencyFailure<KycProviderResolution>();
        if (profileResult.Result is null)
            return PolicyFailure<KycProviderResolution>(KycProviderReadinessCodes.ProfileNotConfigured);

        return Resolve(profileResult.Result, tenantId, selection.Result.SelectionVersion);
    }

    public AZOAResult<KycProviderResolution> ResolveNodeDefault()
    {
        var adapterKey = NormalizeAdapter(_nodeSettings.Provider);
        if (adapterKey is null || !_adapters.TryGetValue(adapterKey, out var provider))
            return PolicyFailure<KycProviderResolution>(KycProviderReadinessCodes.ProfileNotConfigured);

        var capabilities = provider.GetCapabilities();
        if (capabilities.Provider == KycProvider.MANUAL
            && !KycRuntimeSafety.IsManualSimulationAllowed(_environment, _configuration))
        {
            return PolicyFailure<KycProviderResolution>(KycRuntimeSafety.ManualSimulationUnavailable);
        }
        var settings = CloneSettings(
            provider.ProviderKey,
            adapterKey,
            _nodeSettings.ApprovalPolicy.PolicyVersion,
            _nodeSettings.ApprovalPolicy.AssuranceLevel);
        if (!KycApprovalTrust.TryResolveCurrentProfile(
                provider, settings, _environment, out _, out _, out var failure))
        {
            return PolicyFailure<KycProviderResolution>(failure);
        }

        return AZOAResult<KycProviderResolution>.Success(new KycProviderResolution(
            provider,
            settings,
            null,
            0,
            0,
            capabilities.ProviderKey));
    }

    public async Task<AZOAResult<IReadOnlyList<KycProviderProfileResponse>>> ListProfilesAsync(
        CancellationToken ct = default)
    {
        var profiles = await _store.ListProfilesAsync(ct);
        if (profiles.IsError)
            return DependencyFailure<IReadOnlyList<KycProviderProfileResponse>>();
        var persisted = profiles.Result!.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        foreach (var builtIn in BuiltInProfiles())
            persisted.TryAdd(builtIn.Id, builtIn);
        return AZOAResult<IReadOnlyList<KycProviderProfileResponse>>.Success(
            persisted.Values
                .OrderBy(profile => profile.DisplayName, StringComparer.Ordinal)
                .Select(Evaluate)
                .ToList());
    }

    public async Task<AZOAResult<IReadOnlyList<TenantKycProviderChoiceResponse>>> ListTenantChoicesAsync(
        CancellationToken ct = default)
    {
        var profiles = await ListProfilesAsync(ct);
        if (profiles.IsError)
            return AZOAResult<IReadOnlyList<TenantKycProviderChoiceResponse>>.FailureWithCode(
                profiles.Message,
                profiles.Code ?? AzoaErrorCodes.DependencyUnavailable);

        return AZOAResult<IReadOnlyList<TenantKycProviderChoiceResponse>>.Success(
            profiles.Result!
                .Where(profile => profile.Enabled
                    && profile.Available
                    && profile.ReadinessCode == KycProviderReadinessCodes.Ready)
                .Select(profile =>
                {
                    var adapter = _adapters[profile.AdapterKey].GetCapabilities();
                    return new TenantKycProviderChoiceResponse
                    {
                        ProviderKey = profile.ProviderKey,
                        DisplayName = profile.DisplayName,
                        AssuranceLevel = profile.AssuranceLevel,
                        HostedVerification = adapter.HostedVerification,
                        AcceptsDocumentReferences = adapter.AcceptsDocumentReferences,
                    };
                })
                .ToList());
    }

    public async Task<AZOAResult<KycProviderProfileResponse>> EvaluateProfileAsync(
        string providerKey,
        CancellationToken ct = default)
    {
        var profile = await _store.GetProfileAsync(NormalizeProviderKey(providerKey), ct);
        if (profile.IsError)
            return DependencyFailure<KycProviderProfileResponse>();
        return profile.Result is null
            ? PolicyFailure<KycProviderProfileResponse>(KycProviderReadinessCodes.ProfileNotConfigured)
            : AZOAResult<KycProviderProfileResponse>.Success(Evaluate(profile.Result));
    }

    public KycProviderProfileResponse EvaluateCandidate(KycProviderProfile profile)
        => Evaluate(profile);

    private AZOAResult<KycProviderResolution> Resolve(
        KycProviderProfile profile,
        Guid tenantId,
        long selectionVersion)
    {
        var evaluated = Evaluate(profile);
        if (evaluated.ReadinessCode != KycProviderReadinessCodes.Ready
            || !_adapters.TryGetValue(profile.AdapterKey, out var adapter))
        {
            return PolicyFailure<KycProviderResolution>(evaluated.ReadinessCode);
        }

        var provider = new ProfileBoundKycProviderService(profile.Id, adapter);
        var settings = CloneSettings(
            profile.Id,
            profile.AdapterKey,
            profile.PolicyVersion,
            profile.AssuranceLevel);
        if (!KycApprovalTrust.TryResolveCurrentProfile(
                provider, settings, _environment, out _, out _, out var failure))
        {
            return PolicyFailure<KycProviderResolution>(failure);
        }

        return AZOAResult<KycProviderResolution>.Success(new KycProviderResolution(
            provider,
            settings,
            tenantId,
            selectionVersion,
            profile.TrustRevision,
            profile.DisplayName));
    }

    private KycProviderProfileResponse Evaluate(KycProviderProfile profile)
    {
        var adapterFound = _adapters.TryGetValue(profile.AdapterKey, out var provider);
        var capabilities = provider?.GetCapabilities();
        var adapterAvailable = capabilities?.Available == true
            && (profile.AdapterKey != "manual"
                || KycRuntimeSafety.IsManualSimulationAllowed(_environment, _configuration));
        var secretsConfigured = SecretsConfigured(profile.AdapterKey);
        var policyValid = IsSafe(profile.PolicyVersion, 128) && IsSafe(profile.AssuranceLevel, 128);
        var readiness = !profile.Enabled
            ? KycProviderReadinessCodes.Disabled
            : !adapterFound || !adapterAvailable
                ? KycProviderReadinessCodes.AdapterUnavailable
                : !secretsConfigured
                    ? KycProviderReadinessCodes.SecretsNotConfigured
                    : !policyValid
                        ? KycProviderReadinessCodes.PolicyInvalid
                        : KycProviderReadinessCodes.Ready;
        var needsApiKey = profile.AdapterKey is "veriff" or "generic-hosted";
        var needsWebhook = profile.AdapterKey == "generic-hosted";
        var requiredKeys = RequiredConfigurationKeys(profile.AdapterKey);
        var missingKeys = MissingConfigurationKeys(profile.AdapterKey);
        return new KycProviderProfileResponse
        {
            ProviderKey = profile.Id,
            DisplayName = profile.DisplayName,
            AdapterKey = profile.AdapterKey,
            Enabled = profile.Enabled,
            Available = readiness == KycProviderReadinessCodes.Ready,
            ApiKeyConfigured = !needsApiKey || ApiKeyConfigured(profile.AdapterKey),
            WebhookSecretConfigured = !needsWebhook || !string.IsNullOrWhiteSpace(_nodeSettings.Hosted.WebhookSecret),
            ReadinessCode = readiness,
            RequiredConfigurationKeys = requiredKeys,
            MissingConfigurationKeys = missingKeys,
            PolicyVersion = profile.PolicyVersion,
            AssuranceLevel = profile.AssuranceLevel,
            Version = profile.Version,
            TrustRevision = profile.TrustRevision,
            UpdatedAt = profile.UpdatedAt,
        };
    }

    private IEnumerable<KycProviderProfile> BuiltInProfiles()
    {
        var policyVersion = IsSafe(_nodeSettings.ApprovalPolicy.PolicyVersion, 128)
            ? _nodeSettings.ApprovalPolicy.PolicyVersion
            : "unconfigured";
        var assuranceLevel = IsSafe(_nodeSettings.ApprovalPolicy.AssuranceLevel, 128)
            ? _nodeSettings.ApprovalPolicy.AssuranceLevel
            : "unconfigured";
        yield return BuiltIn("manual", "Manual development review", policyVersion, assuranceLevel);
        yield return BuiltIn("veriff", "Veriff", policyVersion, assuranceLevel);
        yield return BuiltIn("generic-hosted", "Generic hosted KYC", policyVersion, assuranceLevel);
    }

    private static KycProviderProfile BuiltIn(
        string key,
        string displayName,
        string policyVersion,
        string assuranceLevel)
        => new()
        {
            Id = key,
            DisplayName = displayName,
            AdapterKey = key,
            Enabled = false,
            PolicyVersion = policyVersion,
            AssuranceLevel = assuranceLevel,
            Version = 0,
            TrustRevision = 0,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

    private KycSettings CloneSettings(
        string providerKey,
        string adapterKey,
        string policyVersion,
        string assuranceLevel)
        => new()
        {
            Provider = adapterKey,
            VeriffApiKey = _nodeSettings.VeriffApiKey,
            VeriffBaseUrl = _nodeSettings.VeriffBaseUrl,
            Hosted = _nodeSettings.Hosted,
            SubmissionExpiryDays = _nodeSettings.SubmissionExpiryDays,
            SessionExpiryMinutes = _nodeSettings.SessionExpiryMinutes,
            ApprovalPolicy = new KycApprovalPolicySettings
            {
                PolicyVersion = policyVersion,
                AssuranceLevel = assuranceLevel,
                TrustedProviderKeys = [providerKey],
                AllowManualInDevelopment = adapterKey == "manual",
            },
        };

    private bool SecretsConfigured(string adapterKey)
        => adapterKey switch
        {
            "manual" => true,
            "veriff" => !string.IsNullOrWhiteSpace(_nodeSettings.VeriffApiKey)
                && ValidHttpsOrigin(_nodeSettings.VeriffBaseUrl),
            "generic-hosted" => !string.IsNullOrWhiteSpace(_nodeSettings.Hosted.ApiKey)
                && !string.IsNullOrWhiteSpace(_nodeSettings.Hosted.WebhookSecret)
                && ValidHttpsOrigin(_nodeSettings.Hosted.BaseUrl),
            _ => false,
        };

    private bool ApiKeyConfigured(string adapterKey)
        => adapterKey == "veriff"
            ? !string.IsNullOrWhiteSpace(_nodeSettings.VeriffApiKey)
            : adapterKey == "generic-hosted"
                && !string.IsNullOrWhiteSpace(_nodeSettings.Hosted.ApiKey);

    private static IReadOnlyList<string> RequiredConfigurationKeys(string adapterKey)
        => adapterKey switch
        {
            "veriff" => ["Kyc__VeriffApiKey", "Kyc__VeriffBaseUrl"],
            "generic-hosted" => [
                "Kyc__Hosted__ApiKey",
                "Kyc__Hosted__WebhookSecret",
                "Kyc__Hosted__BaseUrl"],
            _ => Array.Empty<string>(),
        };

    private IReadOnlyList<string> MissingConfigurationKeys(string adapterKey)
    {
        var missing = new List<string>();
        if (adapterKey == "veriff")
        {
            if (string.IsNullOrWhiteSpace(_nodeSettings.VeriffApiKey)) missing.Add("Kyc__VeriffApiKey");
            if (!ValidHttpsOrigin(_nodeSettings.VeriffBaseUrl)) missing.Add("Kyc__VeriffBaseUrl");
        }
        else if (adapterKey == "generic-hosted")
        {
            if (string.IsNullOrWhiteSpace(_nodeSettings.Hosted.ApiKey)) missing.Add("Kyc__Hosted__ApiKey");
            if (string.IsNullOrWhiteSpace(_nodeSettings.Hosted.WebhookSecret)) missing.Add("Kyc__Hosted__WebhookSecret");
            if (!ValidHttpsOrigin(_nodeSettings.Hosted.BaseUrl)) missing.Add("Kyc__Hosted__BaseUrl");
        }

        return missing;
    }

    public static string NormalizeProviderKey(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;

    public static string? NormalizeAdapter(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "manual" => "manual",
            "veriff" => "veriff",
            "hosted" or "generic-hosted" => "generic-hosted",
            _ => null,
        };

    public static bool IsSafeProviderKey(string value)
        => value.Length is >= 2 and <= 64
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');

    private static bool IsSafe(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximum
            && value.All(character => !char.IsControl(character));

    private static bool ValidHttpsOrigin(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && !uri.IsLoopback;

    private static AZOAResult<T> DependencyFailure<T>()
        => AZOAResult<T>.FailureWithCode(
            "KYC control persistence is temporarily unavailable.",
            AzoaErrorCodes.DependencyUnavailable);

    private static AZOAResult<T> PolicyFailure<T>(string message)
        => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.PolicyUnavailable);

    private sealed class ProfileBoundKycProviderService(
        string providerKey,
        IKycProviderService inner) : IKycProviderService
    {
        public KycProvider Provider => inner.Provider;
        public string ProviderKey => providerKey;

        public KycProviderCapabilitiesModel GetCapabilities()
        {
            var source = inner.GetCapabilities();
            return new KycProviderCapabilitiesModel
            {
                Provider = source.Provider,
                ProviderKey = providerKey,
                Available = source.Available,
                HostedVerification = source.HostedVerification,
                AcceptsDocumentReferences = source.AcceptsDocumentReferences,
                DevelopmentSimulation = source.DevelopmentSimulation,
                UnavailableReason = source.UnavailableReason,
            };
        }

        public async Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(Guid avatarId, CancellationToken ct = default)
        {
            var result = await inner.BeginSessionAsync(avatarId, ct);
            if (result.IsError || result.Result is null)
                return result;
            result.Result.ProviderKey = providerKey;
            return result;
        }
        public Task<AZOAResult<string>> CreateSessionAsync(Guid avatarId, IReadOnlyList<KycDocumentModel> documents, CancellationToken ct = default)
            => inner.CreateSessionAsync(avatarId, documents, ct);
        public Task<AZOAResult<KycStatus>> GetSessionStatusAsync(string providerSessionId, CancellationToken ct = default)
            => inner.GetSessionStatusAsync(providerSessionId, ct);
        public Task<AZOAResult<KycStatus>> HandleWebhookAsync(string payload, CancellationToken ct = default)
            => inner.HandleWebhookAsync(payload, ct);
        public Task<AZOAResult<bool>> ValidateDocumentsAsync(IReadOnlyList<SubmitKycDocumentModel> documents, CancellationToken ct = default)
            => inner.ValidateDocumentsAsync(documents, ct);
    }
}
