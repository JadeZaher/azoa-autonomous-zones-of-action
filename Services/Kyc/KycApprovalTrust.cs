// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Hosting;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>Creates and validates the versioned KYC approval provenance envelope.</summary>
public static class KycApprovalTrust
{
    public const string EnvelopeSchema = "azoa.kyc.approval-provenance/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static bool TryResolveCurrentProfile(
        IKycProviderService provider,
        KycSettings settings,
        IHostEnvironment environment,
        out KycProviderCapabilitiesModel capabilities,
        out KycApprovalProfile profile,
        out string failure)
    {
        profile = default!;
        capabilities = provider.GetCapabilities();
        var policy = settings.ApprovalPolicy;
        if (string.Equals(settings.Provider?.Trim(), "manual", StringComparison.OrdinalIgnoreCase)
            && (!environment.IsDevelopment() || !policy.AllowManualInDevelopment))
        {
            failure = "Manual KYC is allowed only by explicit Development policy.";
            return false;
        }

        if (!MatchesConfiguredProvider(settings.Provider, capabilities.Provider))
        {
            failure = "Kyc:Provider must explicitly select the active KYC provider.";
            return false;
        }
        if (!capabilities.Available)
        {
            failure = capabilities.UnavailableReason ?? "The configured KYC provider is unavailable.";
            return false;
        }

        var providerKey = NormalizeProviderKey(capabilities.ProviderKey);
        if (capabilities.Provider == KycProvider.UNAVAILABLE
            || capabilities.Provider != provider.Provider
            || !string.Equals(providerKey, NormalizeProviderKey(provider.ProviderKey), StringComparison.Ordinal))
        {
            failure = "The configured KYC provider returned inconsistent identity metadata.";
            return false;
        }

        var policyVersion = NormalizePolicyValue(policy.PolicyVersion);
        var assuranceLevel = NormalizePolicyValue(policy.AssuranceLevel);
        if (providerKey is null || policyVersion is null || assuranceLevel is null)
        {
            failure = "KYC approval policy version, assurance level, and provider key must be explicitly configured.";
            return false;
        }

        var trustedProviders = (policy.TrustedProviderKeys ?? Array.Empty<string>())
            .Select(NormalizeProviderKey)
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);
        if (!trustedProviders.Contains(providerKey))
        {
            failure = "The active KYC provider is not trusted by the configured approval policy.";
            return false;
        }

        if (capabilities.Provider == KycProvider.MANUAL
            && (!environment.IsDevelopment() || !policy.AllowManualInDevelopment))
        {
            failure = "Manual KYC is allowed only by explicit Development policy.";
            return false;
        }

        profile = new KycApprovalProfile(
            capabilities.Provider,
            providerKey,
            policyVersion,
            assuranceLevel);
        failure = string.Empty;
        return true;
    }

    public static string CreateEnvelope(KycApprovalProfile profile)
        => JsonSerializer.Serialize(new KycApprovalProvenance(
            EnvelopeSchema,
            profile.Provider,
            profile.ProviderKey,
            profile.PolicyVersion,
            profile.AssuranceLevel), JsonOptions);

    public static bool MatchesCurrentAttempt(
        KycSubmission submission,
        KycApprovalProfile profile,
        out string failure)
    {
        if (submission.Provider != profile.Provider
            || !string.Equals(
                NormalizeProviderKey(submission.ProviderKey),
                profile.ProviderKey,
                StringComparison.Ordinal))
        {
            failure = "The verification attempt belongs to a different KYC provider.";
            return false;
        }

        if (!TryReadEnvelope(submission.ProviderResult, out var provenance)
            || provenance.Provider != profile.Provider
            || !string.Equals(NormalizeProviderKey(provenance.ProviderKey), profile.ProviderKey, StringComparison.Ordinal)
            || !string.Equals(NormalizePolicyValue(provenance.PolicyVersion), profile.PolicyVersion, StringComparison.Ordinal)
            || !string.Equals(NormalizePolicyValue(provenance.AssuranceLevel), profile.AssuranceLevel, StringComparison.Ordinal))
        {
            failure = "The verification attempt does not satisfy the active KYC policy and assurance profile.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static bool IsCurrentApproval(
        KycSubmission submission,
        KycApprovalProfile profile,
        DateTimeOffset now,
        out string failure)
    {
        if (submission.Status != KycStatus.APPROVED)
        {
            failure = "The latest KYC attempt is not approved.";
            return false;
        }

        if (submission.ExpiresAt is not { } expiresAt || expiresAt <= now)
        {
            failure = "The KYC approval is expired or has no explicit expiry.";
            return false;
        }

        return MatchesCurrentAttempt(submission, profile, out failure);
    }

    public static bool IsProviderMatch(KycSubmission submission, KycApprovalProfile profile)
        => submission.Provider == profile.Provider
            && string.Equals(
                NormalizeProviderKey(submission.ProviderKey),
                profile.ProviderKey,
                StringComparison.Ordinal);

    private static bool TryReadEnvelope(string? value, out KycApprovalProvenance provenance)
    {
        provenance = default!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048)
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<KycApprovalProvenance>(value, JsonOptions);
            if (parsed is null || !string.Equals(parsed.Schema, EnvelopeSchema, StringComparison.Ordinal))
                return false;

            provenance = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? NormalizeProviderKey(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return IsSafeValue(normalized, 64)
            && normalized!.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.')
            ? normalized
            : null;
    }

    private static string? NormalizePolicyValue(string? value)
    {
        var normalized = value?.Trim();
        return IsSafeValue(normalized, 128) ? normalized : null;
    }

    private static bool IsSafeValue(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && value.All(character => !char.IsControl(character));

    private static bool MatchesConfiguredProvider(string? configured, KycProvider provider)
        => configured?.Trim().ToLowerInvariant() switch
        {
            "manual" => provider == KycProvider.MANUAL,
            "veriff" => provider == KycProvider.VERIFF,
            "hosted" or "generic-hosted" => provider == KycProvider.GENERIC_HOSTED,
            _ => false,
        };

    private sealed record KycApprovalProvenance(
        string Schema,
        KycProvider Provider,
        string ProviderKey,
        string PolicyVersion,
        string AssuranceLevel);
}

/// <summary>The normalized operator policy bound to the active KYC provider.</summary>
public sealed record KycApprovalProfile(
    KycProvider Provider,
    string ProviderKey,
    string PolicyVersion,
    string AssuranceLevel);
