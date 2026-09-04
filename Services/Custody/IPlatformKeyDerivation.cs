namespace AZOA.WebAPI.Services.Custody;

/// <summary>
/// One chain's rule for turning its configured platform secret into the raw signing
/// key bytes that chain's signer expects. See <c>Services/Custody/AGENTS.md</c>
/// §platform-key-derivation.
/// </summary>
public interface IPlatformKeyDerivation
{
    /// <summary>Canonical chain name this derivation serves (matched case-insensitively).</summary>
    string ChainType { get; }

    /// <summary>Configuration path holding this chain's platform secret. Never the value.</summary>
    string PlatformKeyConfigPath { get; }

    /// <summary>Human-readable shape of the configured secret, safe to put in an error message.</summary>
    string SecretDescription { get; }

    /// <summary>Exact byte length of the derived raw signing key for this chain.</summary>
    int KeyLength { get; }

    /// <summary>
    /// Derives the raw signing key from the configured secret. Total and non-throwing:
    /// a malformed secret returns <c>false</c> with an empty buffer, never a partial or
    /// wrong-length key. Implementations MUST NOT log, wrap, or re-surface the secret —
    /// chain SDKs echo mnemonic words in their exception messages.
    /// </summary>
    bool TryDerive(string configuredSecret, out byte[] key);
}
