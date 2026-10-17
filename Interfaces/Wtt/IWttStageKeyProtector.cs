using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Versioned at-rest encryption only; callers must independently enforce live custody authority.</summary>
public interface IWttStageKeyProtector
{
    /// <summary>False when the <c>Wtt:StageKeys</c> keyring is absent or invalid; every protect/use then refuses (degrade).</summary>
    bool IsConfigured { get; }
    string? ConfigurationError { get; }
    string ActiveKeyId { get; }
    IReadOnlyCollection<string> AvailableKeyVersions { get; }
    /// <summary>Protects a 32-byte stage seed, binding the exact public key and operation identity.</summary>
    WttProtectedStageKey Protect(WttStageKeyBinding binding, string publicKey, ReadOnlySpan<byte> seed);
    /// <summary>Uses the protected seed within a callback and zeroes it even on failure; the callback must not retain or return it.</summary>
    T WithSeed<T>(WttStageKeyBinding binding, string publicKey, WttProtectedStageKey envelope, Func<byte[], T> use);
    /// <summary>Re-encrypts the same seed under the active key; caller persists by exact prior-envelope/version CAS.</summary>
    WttProtectedStageKey Rewrap(WttStageKeyBinding binding, string publicKey, WttProtectedStageKey envelope);
}
