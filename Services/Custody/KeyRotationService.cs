using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Custody;

// ─── DI registration (orchestrator applies to Program.cs — REPORTED for reconciliation) ───
//   builder.Services.AddScoped<IKeyRotationService, KeyRotationService>();
// Scoped to match IWalletStore / IKeyCustodyService. See Services/Custody/AGENTS.md §rotation.

/// <summary>
/// Live <c>AZOA:WalletEncryptionKey</c> rotation orchestration on top of
/// <see cref="IKeyCustodyService.RewrapAsync"/> (final-hardening B5). Batch re-wraps
/// every stored wallet from the current (OLD) data-key to a NEW one.
/// <para>
/// Safety properties (the reviewer's checklist): (1) <b>dual-key read window</b> —
/// each wallet is probed under the new key first, so mixed old/new state stays
/// readable while a rotation is in flight; (2) <b>idempotent / resumable</b> —
/// wallets already readable under the new key are skipped, so a re-run after a crash
/// mid-batch resumes without double-wrapping; (3) <b>all-or-nothing rollback</b> — the
/// original ciphertext of every mutated wallet is snapshotted before the batch and
/// restored if any wallet fails, so the store is never left half-rotated; (4) no key,
/// seed, or cleartext is ever logged — the report carries counts only.
/// </para>
/// </summary>
public sealed class KeyRotationService : IKeyRotationService
{
    private readonly IWalletStore _walletStore;
    private readonly IKeyCustodyService _custody;
    private readonly IPendingRotationKeyStore _pendingKeys;
    private readonly IConfiguration _config;
    private readonly ILogger<KeyRotationService> _logger;

    /// <summary>
    /// Fixed sentinel encrypted under the NEW key to produce the pending-rotation
    /// verifier token (security-review HIGH-1). It is a public constant — the security
    /// comes from the key, not the plaintext: only a holder of the correct new key can
    /// produce a token that decrypts back to this exact value.
    /// </summary>
    private const string VerifierSentinel = "azoa-rotation-key-verifier-v1";

    public KeyRotationService(
        IWalletStore walletStore,
        IKeyCustodyService custody,
        IPendingRotationKeyStore pendingKeys,
        IConfiguration config,
        ILogger<KeyRotationService> logger)
    {
        _walletStore = walletStore;
        _custody = custody;
        _pendingKeys = pendingKeys;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<KeyRotationReport>> RotateAllAsync(string newEncryptionKey, CancellationToken ct = default)
    {
        var result = new AZOAResult<KeyRotationReport>();

        if (string.IsNullOrWhiteSpace(newEncryptionKey))
        {
            result.IsError = true;
            result.Message = "A non-empty new encryption key is required to rotate.";
            return result;
        }

        var oldKey = _config.GetValue<string>("AZOA:WalletEncryptionKey");
        if (string.IsNullOrWhiteSpace(oldKey))
        {
            result.IsError = true;
            result.Message = "No current AZOA:WalletEncryptionKey configured — nothing to rotate from.";
            return result;
        }

        // Refuse a no-op / dangerous same-key rotation: the dual-key probe cannot
        // distinguish old from new if they are identical, which would defeat the
        // idempotency skip. Fail closed.
        if (string.Equals(oldKey, newEncryptionKey, StringComparison.Ordinal))
        {
            result.IsError = true;
            result.Message = "New encryption key is identical to the current key; rotation would be a no-op.";
            return result;
        }

        // Two key services: one bound to the OLD data-key (decrypts existing
        // ciphertext), one to the NEW (re-encrypts + probes readability).
        var oldSvc = KeyServiceFor(oldKey);
        var newSvc = KeyServiceFor(newEncryptionKey);

        var listed = await _walletStore.GetAllAsync(ct);
        if (listed.IsError || listed.Result is null)
        {
            result.IsError = true;
            result.Message = $"Failed to load wallets for rotation: {listed.Message}";
            return result;
        }

        var wallets = listed.Result.ToList();

        // security-review HIGH-1: durably record that a rotation is in flight BEFORE we
        // mutate any wallet. The marker carries a verifier token (the sentinel encrypted
        // under the NEW key) — never the raw key — so a discarded-new-key scenario is
        // recoverable: the operator can prove a held candidate matches this rotation. If
        // we cannot write the marker, we refuse to start: an unrecorded rotation is
        // exactly the key-loss hazard this defends against.
        string verifierToken;
        try
        {
            verifierToken = newSvc.EncryptSeedPhrase(VerifierSentinel);
        }
        catch (Exception ex)
        {
            result.IsError = true;
            result.Message = $"Failed to build the new-key verifier token; rotation not started: {ex.Message}";
            return result;
        }

        var pending = await _pendingKeys.WritePendingAsync(verifierToken, wallets.Count, ct);
        if (pending.IsError)
        {
            result.IsError = true;
            result.Message = $"Failed to persist the pending-rotation marker; refusing to start rotation: {pending.Message}";
            return result;
        }

        // Snapshot pre-rotation ciphertext for EVERY wallet we may mutate, so a
        // partial failure can be rolled back deterministically.
        var snapshots = new Dictionary<Guid, (string? pk, string? seed)>();
        var mutated = new List<IWallet>();

        int rewrapped = 0, alreadyRotated = 0, skipped = 0;

        try
        {
            foreach (var wallet in wallets)
            {
                ct.ThrowIfCancellationRequested();

                // Nothing to re-wrap (external wallet, no stored key).
                if (string.IsNullOrEmpty(wallet.EncryptedPrivateKey)
                    && string.IsNullOrEmpty(wallet.EncryptedSeedPhrase))
                {
                    skipped++;
                    continue;
                }

                // Idempotency / dual-key read window: if it already decrypts under the
                // NEW key, it was rotated in a prior (possibly crashed) run — skip.
                if (IsReadableUnder(newSvc, wallet))
                {
                    alreadyRotated++;
                    continue;
                }

                // Guard: it MUST be readable under the OLD key, else the store is in an
                // unexpected mixed state we will not silently paper over. Fail → rollback.
                if (!IsReadableUnder(oldSvc, wallet))
                    throw new InvalidOperationException(
                        $"Wallet {wallet.Id} is unreadable under both the old and new keys; aborting rotation.");

                snapshots[wallet.Id] = (wallet.EncryptedPrivateKey, wallet.EncryptedSeedPhrase);

                var rewrap = _custody.RewrapAsync(wallet, oldSvc, newSvc);
                if (rewrap.IsError || rewrap.Result is null)
                    throw new InvalidOperationException($"Re-wrap failed for wallet {wallet.Id}: {rewrap.Message}");

                var saved = await _walletStore.UpsertAsync(rewrap.Result, ct);
                if (saved.IsError)
                    throw new InvalidOperationException($"Persist failed for wallet {wallet.Id}: {saved.Message}");

                mutated.Add(wallet);
                rewrapped++;
            }

            // security-review HIGH-1: post-rotation readability assertion sweep. Re-read
            // EVERY wallet from the store and prove it is readable under the INTENDED
            // final key before we declare success. Any wallet that carries ciphertext yet
            // does NOT decrypt under the new key means the store is in an unrecoverable-
            // looking state — fail LOUD and keep the pending marker so recovery can use
            // the verifier token. This is the safety net beneath the all-or-nothing
            // rollback: it catches a wallet that was persisted but is somehow unreadable
            // (e.g. a torn write) that the per-wallet path did not surface.
            var finalWallets = await _walletStore.GetAllAsync(ct);
            if (finalWallets.IsError || finalWallets.Result is null)
                throw new InvalidOperationException(
                    $"Post-rotation readability sweep could not reload wallets: {finalWallets.Message}");

            foreach (var w in finalWallets.Result)
            {
                if (string.IsNullOrEmpty(w.EncryptedPrivateKey) && string.IsNullOrEmpty(w.EncryptedSeedPhrase))
                    continue; // external / no stored key — nothing to read.
                if (!IsReadableUnder(newSvc, w))
                    throw new InvalidOperationException(
                        $"Post-rotation readability sweep FAILED: wallet {w.Id} is not readable under the intended final key.");
            }

            // Rotation verified end-to-end — the new key is now the source of truth for
            // every wallet. Only now is it safe to clear the pending marker. A failure to
            // clear is non-fatal (the rotation itself succeeded) but is logged loudly so
            // an operator removes the stale marker.
            var cleared = await _pendingKeys.ClearAsync(ct);
            if (cleared.IsError)
                _logger.LogWarning(
                    "Rotation succeeded but the pending-rotation marker could not be cleared: {Message}. " +
                    "Remove it manually; the new key is now authoritative.", cleared.Message);

            result.Result = new KeyRotationReport
            {
                Total = wallets.Count,
                Rewrapped = rewrapped,
                AlreadyRotated = alreadyRotated,
                Skipped = skipped,
                RolledBack = false
            };
            result.Message = $"Rotation complete: {rewrapped} re-wrapped, {alreadyRotated} already rotated, {skipped} skipped.";
            _logger.LogInformation(
                "Wallet key rotation complete: total={Total} rewrapped={Rewrapped} alreadyRotated={Already} skipped={Skipped}",
                wallets.Count, rewrapped, alreadyRotated, skipped);
            return result;
        }
        catch (Exception ex)
        {
            // Roll back every wallet we already persisted under the new key, restoring
            // the snapshotted ciphertext, so no wallet is left half-rotated.
            var rollbackOk = await RollbackAsync(mutated, snapshots, ct);

            result.IsError = true;
            result.Result = new KeyRotationReport
            {
                Total = wallets.Count,
                Rewrapped = 0,
                AlreadyRotated = alreadyRotated,
                Skipped = skipped,
                RolledBack = rollbackOk
            };
            result.Message = rollbackOk
                ? $"Rotation aborted and rolled back to pre-rotation state: {ex.Message}"
                : $"Rotation aborted; ROLLBACK INCOMPLETE — manual review required: {ex.Message}";
            _logger.LogError(ex,
                "Wallet key rotation aborted after {Mutated} wallets; rollbackOk={RollbackOk}",
                mutated.Count, rollbackOk);
            return result;
        }
    }

    private WalletKeyService KeyServiceFor(string encryptionKey)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = encryptionKey,
            })
            .Build();
        return new WalletKeyService(cfg);
    }

    /// <summary>True if the wallet's ciphertext decrypts cleanly under <paramref name="svc"/>.</summary>
    private static bool IsReadableUnder(WalletKeyService svc, IWallet wallet)
    {
        try
        {
            if (!string.IsNullOrEmpty(wallet.EncryptedPrivateKey))
            {
                var probe = svc.DecryptPrivateKeyBytes(wallet.EncryptedPrivateKey);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(probe);
            }
            else if (!string.IsNullOrEmpty(wallet.EncryptedSeedPhrase))
            {
                // Seed-only wallet: prove readability via the string decrypt (no raw-key surface).
                _ = svc.DecryptSeedPhrase(wallet.EncryptedSeedPhrase);
            }
            return true;
        }
        catch
        {
            // Wrong key ⇒ AuthenticationTagMismatch ⇒ not readable under this key.
            return false;
        }
    }

    private async Task<bool> RollbackAsync(
        List<IWallet> mutated,
        Dictionary<Guid, (string? pk, string? seed)> snapshots,
        CancellationToken ct)
    {
        var allOk = true;
        foreach (var wallet in mutated)
        {
            if (!snapshots.TryGetValue(wallet.Id, out var snap))
                continue;

            wallet.EncryptedPrivateKey = snap.pk;
            wallet.EncryptedSeedPhrase = snap.seed;

            try
            {
                // Best-effort restore even if the batch was cancelled: use a fresh
                // token so rollback is not itself cancelled mid-way.
                var restore = await _walletStore.UpsertAsync(wallet, CancellationToken.None);
                if (restore.IsError) allOk = false;
            }
            catch
            {
                allOk = false;
            }
        }
        return allOk;
    }
}
