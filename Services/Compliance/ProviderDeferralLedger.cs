// SPDX-License-Identifier: UNLICENSED

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Compliance;

/// <summary>
/// Tracks external provider capability deferrals (for example missing Identomat vendor credentials)
/// so the node starts cleanly, logs structured operator warnings, and reports explicit deferral entries.
/// </summary>
public interface IProviderDeferralLedger
{
    bool HasLiveIdentomatCredentials { get; }

    IReadOnlyList<string> GetMissingIdentomatKeys();

    ProviderDeferralEntry? EnsureIdentomatCredentialDeferralEvaluated();

    ProviderDeferralEntry RecordDeferral(
        string provider,
        string capability,
        string reasonCode,
        string message,
        IReadOnlyList<string>? missingConfigurationKeys = null);

    ProviderDeferralEntry? ResolveDeferral(string provider, string capability, string reasonCode);

    IReadOnlyList<ProviderDeferralEntry> ListDeferrals(string? provider = null);
}

public sealed class ProviderDeferralLedger : IProviderDeferralLedger
{
    public const string IdentomatProviderName = "Identomat";
    public const string IdentomatCapabilityName = "kyc_hosted_session";
    public const string IdentomatCredentialsPendingReasonCode = "IDENTOMAT_VENDOR_CREDENTIALS_PENDING";

    private readonly IOptions<KycSettings> _kycOptions;
    private readonly ILogger<ProviderDeferralLedger> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, ProviderDeferralEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ProviderDeferralLedger(
        IOptions<KycSettings> kycOptions,
        ILogger<ProviderDeferralLedger> logger,
        TimeProvider? clock = null)
    {
        _kycOptions = kycOptions ?? throw new ArgumentNullException(nameof(kycOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;

        EnsureIdentomatCredentialDeferralEvaluated();
    }

    public bool HasLiveIdentomatCredentials
    {
        get
        {
            var settings = _kycOptions.Value;
            return string.Equals(settings.Provider?.Trim(), "identomat", StringComparison.OrdinalIgnoreCase)
                && GetMissingIdentomatKeys().Count == 0;
        }
    }

    public IReadOnlyList<string> GetMissingIdentomatKeys()
    {
        var identomat = _kycOptions.Value.Identomat ?? new IdentomatKycSettings();
        var missing = new List<string>(3);

        if (string.IsNullOrWhiteSpace(identomat.CompanyKey))
            missing.Add(IdentomatKycSettings.CompanyKeyName);
        if (string.IsNullOrWhiteSpace(identomat.ConfigId))
            missing.Add(IdentomatKycSettings.ConfigIdName);
        if (string.IsNullOrWhiteSpace(identomat.CallbackApiKey))
            missing.Add(IdentomatKycSettings.CallbackApiKeyName);

        return missing;
    }

    public ProviderDeferralEntry? EnsureIdentomatCredentialDeferralEvaluated()
    {
        var settings = _kycOptions.Value;
        if (!string.Equals(settings.Provider?.Trim(), "identomat", StringComparison.OrdinalIgnoreCase))
            return null;

        var missing = GetMissingIdentomatKeys();
        if (missing.Count > 0)
        {
            var message =
                "Identomat KYC vendor credentials are not configured; hosted KYC session creation is deferred until credentials are supplied.";
            var key = CompositeKey(IdentomatProviderName, IdentomatCapabilityName, IdentomatCredentialsPendingReasonCode);
            var isNewOrStillDeferred = !_entries.TryGetValue(key, out var existing)
                || !string.Equals(existing.Status, ProviderDeferralStatuses.Deferred, StringComparison.Ordinal);

            var entry = RecordDeferral(
                IdentomatProviderName,
                IdentomatCapabilityName,
                IdentomatCredentialsPendingReasonCode,
                message,
                missing);

            if (isNewOrStillDeferred)
            {
                _logger.LogWarning(
                    "Provider deferral recorded: provider={Provider}, capability={Capability}, reasonCode={ReasonCode}, missingConfigurationKeys={MissingConfigurationKeys}",
                    entry.Provider,
                    entry.Capability,
                    entry.ReasonCode,
                    entry.MissingConfigurationKeys);
            }

            return entry;
        }

        return ResolveDeferral(
            IdentomatProviderName,
            IdentomatCapabilityName,
            IdentomatCredentialsPendingReasonCode);
    }

    public ProviderDeferralEntry RecordDeferral(
        string provider,
        string capability,
        string reasonCode,
        string message,
        IReadOnlyList<string>? missingConfigurationKeys = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var normalizedProvider = provider.Trim();
        var normalizedCapability = capability.Trim();
        var normalizedReasonCode = reasonCode.Trim();
        var key = CompositeKey(normalizedProvider, normalizedCapability, normalizedReasonCode);
        var keys = missingConfigurationKeys is null
            ? Array.Empty<string>()
            : missingConfigurationKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToArray();

        return _entries.AddOrUpdate(
            key,
            _ => new ProviderDeferralEntry(
                DeterministicId(key),
                normalizedProvider,
                normalizedCapability,
                normalizedReasonCode,
                message.Trim(),
                keys,
                _clock.GetUtcNow(),
                ProviderDeferralStatuses.Deferred),
            (_, existing) => existing with
            {
                Message = message.Trim(),
                MissingConfigurationKeys = keys,
                LoggedAt = string.Equals(existing.Status, ProviderDeferralStatuses.Deferred, StringComparison.Ordinal)
                    ? existing.LoggedAt
                    : _clock.GetUtcNow(),
                Status = ProviderDeferralStatuses.Deferred,
            });
    }

    public ProviderDeferralEntry? ResolveDeferral(string provider, string capability, string reasonCode)
    {
        var key = CompositeKey(provider.Trim(), capability.Trim(), reasonCode.Trim());
        if (!_entries.TryGetValue(key, out var existing))
            return null;

        var resolved = existing with
        {
            MissingConfigurationKeys = Array.Empty<string>(),
            Status = ProviderDeferralStatuses.Resolved,
        };
        _entries[key] = resolved;
        return resolved;
    }

    public IReadOnlyList<ProviderDeferralEntry> ListDeferrals(string? provider = null)
    {
        var query = _entries.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var filter = provider.Trim();
            query = query.Where(e => string.Equals(e.Provider, filter, StringComparison.OrdinalIgnoreCase));
        }

        return query.OrderByDescending(e => e.LoggedAt).ToArray();
    }

    private static string CompositeKey(string provider, string capability, string reasonCode)
        => $"{provider.ToLowerInvariant()}|{capability.ToLowerInvariant()}|{reasonCode.ToUpperInvariant()}";

    private static string DeterministicId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "def_" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }
}
