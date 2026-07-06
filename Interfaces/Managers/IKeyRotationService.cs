using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Live wrapping-key rotation orchestration on top of the per-wallet
/// <see cref="IKeyCustodyService.RewrapAsync"/> primitive (final-hardening B5).
/// Batch re-wraps every stored wallet from the OLD <c>AZOA:WalletEncryptionKey</c>
/// to a NEW one, with a dual-key read window (either key decrypts during cutover),
/// idempotent/resumable re-runs, and all-or-nothing rollback on partial failure.
/// See Services/Custody/AGENTS.md §rotation.
/// </summary>
public interface IKeyRotationService
{
    /// <summary>
    /// Re-wrap every wallet's ciphertext from the OLD data-key to
    /// <paramref name="newEncryptionKey"/>. Idempotent: wallets already readable under
    /// the new key are skipped, so a re-run after a partial failure resumes. On any
    /// per-wallet failure the whole batch is rolled back to its pre-rotation ciphertext.
    /// </summary>
    /// <param name="newEncryptionKey">The new <c>AZOA:WalletEncryptionKey</c> secret to re-wrap under.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AZOAResult<KeyRotationReport>> RotateAllAsync(string newEncryptionKey, CancellationToken ct = default);
}

/// <summary>Outcome of a batch rotation — counts only, never any key material.</summary>
public sealed class KeyRotationReport
{
    /// <summary>Total wallets considered.</summary>
    public int Total { get; init; }

    /// <summary>Wallets re-wrapped under the new key in this run.</summary>
    public int Rewrapped { get; init; }

    /// <summary>Wallets already readable under the new key (idempotent skip).</summary>
    public int AlreadyRotated { get; init; }

    /// <summary>Wallets with no ciphertext to re-wrap (external wallets).</summary>
    public int Skipped { get; init; }

    /// <summary>True when a failure triggered a full rollback to pre-rotation state.</summary>
    public bool RolledBack { get; init; }
}
