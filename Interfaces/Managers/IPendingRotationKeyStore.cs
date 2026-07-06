using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// security-review HIGH-1: a durable, out-of-DB record that a wrapping-key rotation is
/// in flight, written BEFORE the batch mutates any wallet and cleared only after a
/// full post-rotation readability sweep passes. Its purpose is key-LOSS prevention: if
/// a rotation fails mid-batch and the operator wrongly concludes it aborted and
/// discards the new key candidate, any wallet already persisted under the new key would
/// become permanently unreadable. The pending record makes that scenario recoverable —
/// it survives a process crash and records a <em>verifier</em> for the new key so the
/// operator can prove the candidate they still hold is the one the rotation used.
/// <para>
/// The record NEVER stores the raw new key (a value-bearing secret stays with the
/// operator, out of the DB and out of any file we write). It stores only an AES-GCM
/// verifier token — a known sentinel encrypted under the new key — which proves key
/// identity without revealing the key. See Services/Custody/AGENTS.md §rotation.
/// </para>
/// </summary>
public interface IPendingRotationKeyStore
{
    /// <summary>
    /// Durably persist a pending-rotation marker BEFORE the batch begins. Overwrites any
    /// prior marker (a re-run supersedes the previous attempt). <paramref name="verifierToken"/>
    /// is a self-describing AES-GCM ciphertext of a fixed sentinel under the NEW key, so a
    /// later recovery can confirm a held key candidate matches this rotation.
    /// </summary>
    Task<AZOAResult<bool>> WritePendingAsync(string verifierToken, int walletsInScope, CancellationToken ct = default);

    /// <summary>Clear the pending marker after a rotation fully succeeds AND the
    /// post-rotation readability sweep passes. A no-op if none exists.</summary>
    Task<AZOAResult<bool>> ClearAsync(CancellationToken ct = default);

    /// <summary>Read the current pending marker, or a result with a null token when none
    /// exists. Used by recovery tooling to surface an interrupted rotation.</summary>
    Task<AZOAResult<PendingRotationRecord?>> ReadAsync(CancellationToken ct = default);
}

/// <summary>The durable pending-rotation marker — never contains raw key material.</summary>
public sealed class PendingRotationRecord
{
    /// <summary>When the rotation that wrote this marker began (UTC, ISO-8601).</summary>
    public string StartedUtc { get; init; } = string.Empty;

    /// <summary>AES-GCM ciphertext of a fixed sentinel under the NEW key — a key-identity
    /// verifier, not the key itself. A recovery holder re-encrypts the sentinel under
    /// their candidate key and compares plaintext round-trips to confirm a match.</summary>
    public string VerifierToken { get; init; } = string.Empty;

    /// <summary>Count of wallets the rotation intended to touch (for operator triage).</summary>
    public int WalletsInScope { get; init; }
}
