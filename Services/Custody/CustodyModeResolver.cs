using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AZOA.WebAPI.Services.Custody;

/// <summary>The custody modes this node implements; every other value fails closed.</summary>
public enum RecognisedCustodyMode
{
    /// <summary>The configured value names no mode this node implements.</summary>
    Unrecognised,

    /// <summary>Custody is switched off; no path on this node may hold a signing key.</summary>
    Disabled,

    /// <summary>Throwaway keys on a developer's simulated host only.</summary>
    DevelopmentOnly,

    /// <summary>Node-held custody under the boot-validated wallet encryption key.</summary>
    PlatformManaged,

    /// <summary>Reserved for an external KMS/HSM adapter; none is registered yet.</summary>
    KmsHsm
}

/// <summary>One custody answer plus the honest reason when it is negative.</summary>
public sealed record CustodyModeDecision(
    string ConfiguredMode,
    RecognisedCustodyMode Mode,
    bool Available,
    string? UnavailableReason);

/// <summary>
/// The single predicate deciding whether this node may hold a signing key right now.
/// Pure over <see cref="IConfiguration"/> + <see cref="IHostEnvironment"/>: no state, no
/// DI registration, so every caller inherits one answer instead of re-deriving it.
/// Custody <em>assurance</em> (boot guard, readiness probe, float caps) consumes this
/// resolver too rather than restating the predicate.
/// <see cref="Resolve"/> is deliberately the only entry point: a bool-only accessor
/// invites a caller to drop <see cref="CustodyModeDecision.UnavailableReason"/>, and an
/// unexplained refusal is what sends a sponsor into a wall with no route to a human.
/// </summary>
public static class CustodyModeResolver
{
    public const string CustodyModeConfigurationPath = "CustodialAccounts:CustodyMode";
    public const string WalletEncryptionKeyConfigurationPath = "AZOA:WalletEncryptionKey";
    public const string BlockchainModeConfigurationPath = "Blockchain:Mode";

    /// <summary>Mirrors the floor the tenant custodial projection has always applied.</summary>
    public const int MinimumWalletEncryptionKeyLength = 32;

    public const string DisabledMode = "Disabled";
    public const string DevelopmentOnlyMode = "DevelopmentOnly";
    public const string PlatformMode = "Platform";
    public const string NodeMode = "Node";
    public const string KmsHsmMode = "KmsHsm";
    public const string SimulatedBlockchainMode = "Simulated";

    public const string CustodyDisabledReason =
        "Custodial key management is disabled on this node.";

    public const string DevelopmentCustodyOutsideDevelopmentReason =
        "Development key custody is allowed only in Development with Blockchain:Mode=Simulated.";

    public const string KmsHsmAdapterMissingReason =
        "No production KMS/HSM custody adapter is registered.";

    public const string WalletEncryptionKeyUnusableReason =
        "The wallet encryption key (AZOA:WalletEncryptionKey) is missing or shorter than 32 characters.";

    public const string PlatformCustodyInSimulatedModeReason =
        "Node key custody requires a live blockchain mode; Blockchain:Mode is Simulated.";

    /// <summary>Names an unrecognised configured mode without echoing anything secret.</summary>
    public static string UnrecognisedModeReason(string configuredMode)
        => $"Custodial key management mode '{configuredMode}' is not recognised on this node.";

    /// <summary>
    /// Resolves the configured custody mode and whether it is usable here. A null
    /// <paramref name="environment"/> is treated as "not Development", so the
    /// development-only mode fails closed rather than opening on a missing host.
    /// </summary>
    public static CustodyModeDecision Resolve(IConfiguration configuration, IHostEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // An absent mode means off. Silence is never permission.
        var configuredMode = configuration[CustodyModeConfigurationPath]?.Trim();
        if (string.IsNullOrEmpty(configuredMode))
            configuredMode = DisabledMode;

        var mode = Recognise(configuredMode);
        var development = environment is not null && environment.IsDevelopment();
        var encryptionKey = configuration[WalletEncryptionKeyConfigurationPath];
        var encryptionKeyUsable = !string.IsNullOrWhiteSpace(encryptionKey)
            && encryptionKey.Length >= MinimumWalletEncryptionKeyLength;
        var simulated = string.Equals(
            configuration[BlockchainModeConfigurationPath],
            SimulatedBlockchainMode,
            StringComparison.OrdinalIgnoreCase);

        var unavailableReason = mode switch
        {
            RecognisedCustodyMode.DevelopmentOnly => DevelopmentOnlyReason(
                development, simulated, encryptionKeyUsable),
            RecognisedCustodyMode.PlatformManaged => PlatformManagedReason(
                simulated, encryptionKeyUsable),
            RecognisedCustodyMode.KmsHsm => KmsHsmAdapterMissingReason,
            RecognisedCustodyMode.Disabled => CustodyDisabledReason,
            _ => UnrecognisedModeReason(configuredMode)
        };

        return new CustodyModeDecision(
            configuredMode,
            mode,
            unavailableReason is null,
            unavailableReason);
    }

    private static string? DevelopmentOnlyReason(
        bool development, bool simulated, bool encryptionKeyUsable)
    {
        if (!development || !simulated)
            return DevelopmentCustodyOutsideDevelopmentReason;

        return encryptionKeyUsable ? null : WalletEncryptionKeyUnusableReason;
    }

    // The key itself is boot-validated for content outside Development/IntegrationTest
    // (Program.cs, "Fail-fast secret guards"); this only re-states the length floor.
    private static string? PlatformManagedReason(bool simulated, bool encryptionKeyUsable)
    {
        if (!encryptionKeyUsable)
            return WalletEncryptionKeyUnusableReason;

        return simulated ? PlatformCustodyInSimulatedModeReason : null;
    }

    private static RecognisedCustodyMode Recognise(string configuredMode)
    {
        if (Matches(configuredMode, DisabledMode))
            return RecognisedCustodyMode.Disabled;
        if (Matches(configuredMode, DevelopmentOnlyMode))
            return RecognisedCustodyMode.DevelopmentOnly;
        if (Matches(configuredMode, PlatformMode) || Matches(configuredMode, NodeMode))
            return RecognisedCustodyMode.PlatformManaged;
        if (Matches(configuredMode, KmsHsmMode))
            return RecognisedCustodyMode.KmsHsm;

        return RecognisedCustodyMode.Unrecognised;
    }

    private static bool Matches(string configuredMode, string knownMode)
        => string.Equals(configuredMode, knownMode, StringComparison.OrdinalIgnoreCase);
}
