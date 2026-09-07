// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Soulbound-credential revocation (D19): an on-chain ASA clawback authorized to the
/// credential's ISSUER. See <c>Services/Actions/AGENTS.md</c> §credential-revoke.
/// </summary>
/// <remarks>
/// <para><b>The issuer, never the holder.</b> Authorization compares the authenticated
/// caller against the credential's recorded <c>IssuerUserId</c>. A row with no recorded
/// issuer is unrevocable and fails closed: the issuer is never inferred from
/// <c>CurrentOwner</c> (that is the holder — the exact inversion D19 forbids), nor from
/// an operation log's user id (a mint proxied through a tenant credential stamps the
/// wrong party).</para>
/// <para><b>No fabricated success.</b> Every precondition is proven by the provider
/// against the node before signing, and the outcome is read from the provider's result:
/// a broadcast that did not confirm becomes <see cref="OperationStatus.PendingConfirmation"/>
/// carrying its tx hash, never <see cref="OperationStatus.Completed"/> — and it is
/// returned with <c>IsError = true</c> plus
/// <see cref="CredentialRevokeCodes.PendingConfirmation"/>, because <c>IsError</c> is
/// what AZOA client helpers branch on and a nested status string is not.</para>
/// <para><b>Ownership follows chain truth.</b> Only a CONFIRMED clawback re-points the
/// <c>nft_ownership</c> row at the issuer. Recording the operation row alone would leave
/// every AZOA read surface reporting a revoked credential as still held.</para>
/// <para><b>Operation record.</b> The move is recorded as an ordinary
/// <c>CredentialRevoke</c> operation row so it lands in the D16 analytics surface. It is
/// written through <see cref="IBlockchainOperationStore"/> rather than
/// <c>IBlockchainOperationManager.ExecuteAsync</c> because that method is the EXECUTOR:
/// it strips <c>Parameters["TxHash"]</c> and, having no <c>CredentialRevoke</c> case,
/// would record the row as <see cref="OperationStatus.Unknown"/> with the tx hash
/// discarded — losing exactly the evidence reconciliation needs.</para>
/// </remarks>
public sealed class CredentialManager : ICredentialManager
{
    /// <summary>Operation type recorded on the row; keyed by <c>AzoaScopes.IsScopeValidForOperation</c>.</summary>
    public const string OperationTypeName = "CredentialRevoke";

    /// <summary>A credential is a single indivisible unit; a revoke moves exactly one.</summary>
    private static readonly RailAmount OneCredential = RailAmount.OfChainAsset(1);

    private const string UnexpectedFailureMessage = "Credential revocation failed unexpectedly.";

    private readonly ICredentialIssuerStore _credentials;
    private readonly IWalletStore _walletStore;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly IBlockchainOperationStore _operationStore;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IValueAccessService _valueAccess;

    public CredentialManager(
        ICredentialIssuerStore credentials,
        IWalletStore walletStore,
        IBlockchainProviderFactory providerFactory,
        IBlockchainOperationStore operationStore,
        IIdempotencyStore idempotencyStore,
        IValueAccessService valueAccess)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _walletStore = walletStore ?? throw new ArgumentNullException(nameof(walletStore));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _valueAccess = valueAccess ?? throw new ArgumentNullException(nameof(valueAccess));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<CredentialRevokeResult>> RevokeAsync(
        string assetId,
        CredentialRevokeRequest request,
        Guid callerUserId,
        string? clientIdempotencyKey,
        Guid? actingTenantId = null,
        CancellationToken ct = default)
    {
        if (request is null)
            return Fail("A revocation request is required.");
        if (string.IsNullOrWhiteSpace(assetId))
            return Fail("An asset id is required to revoke a credential.");
        if (string.IsNullOrWhiteSpace(request.ChainType))
            return Fail("ChainType is required.");
        if (callerUserId == Guid.Empty)
            return Fail("An authenticated caller is required to revoke a credential.");

        var chainType = request.ChainType.Trim();
        var normalizedAssetId = assetId.Trim();

        // ── Idempotency claim BEFORE any irreversible effect ──────────────────
        // A clawback cannot be undone, so a redelivered request must replay rather
        // than submit a second one. The key is partitioned by the caller so two
        // issuers reusing one human-friendly key cannot collide.
        var idempotencyKey = BuildIdempotencyKey(callerUserId, chainType, normalizedAssetId, clientIdempotencyKey);
        var claim = await _idempotencyStore.TryClaimAsync(idempotencyKey, OperationTypeName, ct);
        if (!claim.Won)
            return ReplayFromRecord(claim.Record, idempotencyKey, normalizedAssetId);

        var effectMayHaveStarted = false;
        try
        {
            // ── Readiness gate (fail-closed) ─────────────────────────────────
            var gate = await _valueAccess.RequireValueAccessAsync(callerUserId, actingTenantId, ct);
            if (gate.IsError)
                return await FailClaim(idempotencyKey, gate.Message, ct);

            // ── Resolve the credential ───────────────────────────────────────
            var lookup = await _credentials.FindCurrentByAssetIdAsync(chainType, normalizedAssetId, ct);
            if (lookup.IsError)
                return await FailClaim(idempotencyKey,
                    $"Credential lookup failed: {lookup.Message}", ct);
            if (lookup.Result is not { } credential)
                return await FailClaim(idempotencyKey,
                    $"No live credential {normalizedAssetId} is recorded on {chainType}.", ct);

            // Only soulbound assets are revoked here. A transferable asset moves by
            // Transfer/Refund and has an owner who can sign for themselves; reaching
            // for the clawback authority on one would be a silent escalation.
            if (!credential.IsSoulbound)
                return await FailClaim(idempotencyKey,
                    $"Credential {normalizedAssetId} is not soulbound; use a transfer, not a revocation.", ct);

            // ── Authorization: the ISSUER, never the holder ──────────────────
            if (credential.IssuerUserId is not { } issuerUserId || issuerUserId == Guid.Empty)
                return await FailClaim(idempotencyKey,
                    $"Credential {normalizedAssetId} has no recorded issuer, so no party can be proven " +
                    "entitled to revoke it. Refusing to infer an issuer from the holder or from an operation log.", ct);

            if (issuerUserId != callerUserId)
                return await FailClaim(idempotencyKey,
                    $"Only the issuer of credential {normalizedAssetId} may revoke it.", ct,
                    AzoaErrorCodes.Forbidden);

            // ── Addresses: from (the proven holder) and to (the issuer) ──────
            if (string.IsNullOrWhiteSpace(credential.HolderAddress))
                return await FailClaim(idempotencyKey,
                    $"Credential {normalizedAssetId} has no recorded holder address; " +
                    "refusing to guess which account to claw it back from.", ct);

            var (recipientAddress, walletId, walletError) = await ResolveIssuerDestinationAsync(issuerUserId, chainType, ct);
            if (recipientAddress is null)
                return await FailClaim(idempotencyKey, walletError, ct);

            // ── Resolve the ASA capability module ────────────────────────────
            if (!TryGetAsaModule(chainType, out var asa, out var moduleError))
                return await FailClaim(idempotencyKey, moduleError, ct);

            // ── Record the operation as Pending BEFORE broadcasting ──────────
            // A crash between here and the provider call must leave evidence that a
            // revocation was attempted, not a silent gap.
            var operation = BuildOperation(
                issuerUserId, callerUserId, walletId, chainType, normalizedAssetId,
                credential, credential.HolderAddress!, recipientAddress, request.Reason,
                actingTenantId, idempotencyKey);
            await _operationStore.UpsertAsync(operation, ct);

            // AC4b: a revocation is platform-SIGNED (the clawback authority) but may be
            // tenant-DRIVEN. When a tenant drives it, carry the acting tenant + scope so
            // the custody seam runs the live consent check before any key decrypt.
            var signingContext = actingTenantId is { } tenant && tenant != Guid.Empty
                ? SigningContext.PlatformFor(chainType).ActingAs(tenant, AzoaScopes.CredentialRevokeSign, issuerUserId)
                : SigningContext.PlatformFor(chainType);

            effectMayHaveStarted = true;
            var chainResult = await asa!.ClawbackAssetAsync(
                normalizedAssetId,
                holderAddress: credential.HolderAddress!,
                recipientAddress: recipientAddress,
                amount: OneCredential,
                signingContext: signingContext,
                clawbackAddress: null,
                ct: ct);

            // ── Map chain truth onto the record. Three distinct outcomes. ────
            var result = new CredentialRevokeResult
            {
                AssetId = normalizedAssetId,
                CredentialId = credential.Id,
                IssuerUserId = issuerUserId,
                HolderUserId = credential.HolderUserId,
                HolderAddress = credential.HolderAddress!,
                RecipientAddress = recipientAddress,
                ChainType = chainType,
                OperationId = operation.Id,
            };

            if (chainResult.IsError)
            {
                var error = string.IsNullOrWhiteSpace(chainResult.Message)
                    ? "Revocation failed." : chainResult.Message;
                operation.Status = OperationStatus.Failed;
                operation.Parameters["Error"] = error;
                await _operationStore.UpsertAsync(operation, ct);
                await _idempotencyStore.FailAsync(idempotencyKey, error, ct);

                result.Status = OperationStatus.Failed;
                return AZOAResult<CredentialRevokeResult>.Failure(error, result);
            }

            var txId = chainResult.Result;
            result.TxHash = string.IsNullOrWhiteSpace(txId) ? null : txId;
            operation.Parameters["TxHash"] = result.TxHash ?? string.Empty;

            // A broadcast that did not confirm is NOT a revocation. The provider signals
            // it with the PendingConfirmation marker on an otherwise-successful result;
            // record it as such, keep the tx hash, and leave the idempotency claim
            // InProgress so a duplicate replays "in progress" instead of re-broadcasting.
            if (IsPendingConfirmation(chainResult.Message))
            {
                result.Status = OperationStatus.PendingConfirmation;

                operation.Status = OperationStatus.PendingConfirmation;
                // ReconciliationService settles a stranded claim from the operation row and
                // will NOT reconstruct a key it cannot read (it refuses to guess). Without
                // these two parameters this claim stays InProgress forever and every later
                // revoke under the same key replays "already in progress" — the credential
                // becomes permanently un-revocable by its own idempotency record.
                operation.Parameters[IdempotencyParameterNames.ResultPayload] = SerializeForReplay(result);
                await _operationStore.UpsertAsync(operation, ct);

                // IsError, not a nested status string. Every AZOA client helper branches on
                // IsError, so a broadcast that has not confirmed must not come back as a
                // plain success — the credential may or may not have moved, and only chain
                // truth decides. The Code lets the transport map it to 202, not 400.
                return AZOAResult<CredentialRevokeResult>.FailureWithCode(
                    $"Revocation of credential {normalizedAssetId} was submitted (tx {result.TxHash}) but has " +
                    "NOT confirmed. It is neither a completed revocation nor a failure: reconcile against " +
                    "chain truth before treating the credential as revoked, and do NOT re-submit.",
                    CredentialRevokeCodes.PendingConfirmation,
                    result);
            }

            if (result.TxHash is null)
            {
                // A "success" with no transaction id is not evidence of anything. Refuse
                // to promote it rather than record a revocation that may never have been sent.
                operation.Status = OperationStatus.Unknown;
                await _operationStore.UpsertAsync(operation, ct);
                await _idempotencyStore.FailAsync(idempotencyKey, "Revocation reported no transaction id.", ct);

                result.Status = OperationStatus.Unknown;
                return AZOAResult<CredentialRevokeResult>.Failure(
                    $"Revocation of credential {normalizedAssetId} reported success without a transaction id; " +
                    "treating the outcome as unknown rather than confirmed.", result);
            }

            // ── Confirmed. AZOA's ownership record must follow chain truth. ──
            // The clawback moved the asset to the issuer. Until the nft_ownership row is
            // re-pointed, its live (is_current = true) row still names the original
            // holder, so every AZOA read surface keeps reporting the credential as held
            // by the party it was just taken from.
            var ownership = await _credentials.RecordCredentialRevokedAsync(
                credential.Id, issuerUserId, recipientAddress, ct);

            operation.Status = OperationStatus.Completed;
            operation.CompletedDate = DateTime.UtcNow;
            if (ownership.IsError)
                operation.Parameters["OwnershipUpdateError"] = ownership.Message;
            await _operationStore.UpsertAsync(operation, ct);

            result.Status = OperationStatus.Completed;
            await _idempotencyStore.CompleteAsync(idempotencyKey, SerializeForReplay(result), ct);

            // A confirmed clawback is irreversible, so a failed bookkeeping write cannot
            // turn the outcome back into a failure — a retry would attempt a SECOND
            // clawback of an asset the holder no longer has. Report the confirmed
            // revocation and make the ledger drift loud instead of silent.
            var confirmedMessage = $"Credential {normalizedAssetId} revoked (tx {result.TxHash}).";
            return new AZOAResult<CredentialRevokeResult>
            {
                Result = result,
                Message = ownership.IsError
                    ? confirmedMessage +
                      " WARNING: the clawback confirmed on chain but AZOA's ownership record could not be " +
                      $"updated ({ownership.Message}), so this credential still reads as held until it is " +
                      "reconciled. The chain, not that record, is the truth."
                    : confirmedMessage,
            };
        }
        catch (Exception ex)
        {
            // Only release the claim when nothing could have reached the chain; a claim
            // released after a possible broadcast would permit a second clawback.
            if (!effectMayHaveStarted)
                await IdempotencyClaimRecovery.TryFailAsync(
                    _idempotencyStore, idempotencyKey, UnexpectedFailureMessage, ex);
            throw;
        }
    }

    // ── Destination resolution ────────────────────────────────────────────────

    /// <summary>
    /// Resolves the issuer's wallet address on the credential's chain — the account a
    /// revoked credential returns to. Fails closed rather than defaulting to the
    /// platform account: a credential must land somewhere the issuer actually controls.
    /// </summary>
    private async Task<(string? Address, Guid? WalletId, string Error)> ResolveIssuerDestinationAsync(
        Guid issuerUserId, string chainType, CancellationToken ct)
    {
        var wallets = await _walletStore.GetByUserAsync(issuerUserId, ct);
        if (wallets.IsError)
            return (null, null, $"Issuer wallet lookup failed: {wallets.Message}");

        var match = wallets.Result?.FirstOrDefault(w =>
            string.Equals(w.ChainType, chainType, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(w.Address));

        return match is null
            ? (null, null, $"The issuer has no {chainType} wallet to receive the revoked credential.")
            : (match.Address, match.Id, string.Empty);
    }

    /// <summary>
    /// Resolves the Algorand ASA capability module for the chain. Fails closed when the
    /// chain does not expose it, so the claim settles Failed before any side effect.
    /// </summary>
    private bool TryGetAsaModule(string chainType, out IAlgorandASAModule? module, out string error)
    {
        module = null;
        error = string.Empty;
        try
        {
            var provider = _providerFactory.GetProvider(chainType, _providerFactory.DefaultNetwork);
            if (!provider.TryGetModule<IAlgorandASAModule>(out module) || module is null)
            {
                error = $"Chain '{chainType}' does not expose the ASA clawback capability required to revoke a credential.";
                return false;
            }
            return true;
        }
        catch (BlockchainProviderNotFoundException)
        {
            error = $"No provider is available for chain '{chainType}'.";
            return false;
        }
    }

    // ── Operation record ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds the <c>CredentialRevoke</c> operation row. It is an ordinary value-move
    /// record — same shape as a Transfer or Mint — so the D16 analytics surface reads it
    /// without a special case. It reads back UNPRICED (nothing implements
    /// <c>IValueRateSource</c>), which is correct: an invented rate on a financial record
    /// is worse than a visibly absent one.
    /// </summary>
    private static BlockchainOperation BuildOperation(
        Guid issuerUserId, Guid callerUserId, Guid? walletId, string chainType, string assetId,
        CredentialOwnershipRecord credential, string holderAddress, string recipientAddress,
        string? reason, Guid? actingTenantId, string idempotencyKey)
    {
        var operation = new BlockchainOperation
        {
            // The op belongs to the issuer (the authority), while InitiatorUserId records
            // who actually called. They differ when a tenant drives the revocation.
            UserId = issuerUserId,
            WalletId = walletId,
            InitiatorUserId = callerUserId,
            OperationType = OperationTypeName,
            Status = OperationStatus.Pending,
            Amount = OneCredential,
            AssetType = "Credential",
            RecipientAddress = recipientAddress,
            IdempotencyKey = IdempotencyReplay.ContentHash(idempotencyKey),
        };

        if (actingTenantId is { } tenant && tenant != Guid.Empty)
        {
            operation.ActingTenantId = tenant;
            operation.SigningScope = AzoaScopes.CredentialRevokeSign;
        }

        // The RAW claim key, from the first write onward. ReconciliationService settles a
        // stranded idempotency claim by reading exactly this parameter and passing it to
        // IIdempotencyStore.GetAsync; the IdempotencyKey COLUMN above is a content hash
        // and is not the store's key. Stamped in the constructor of the row rather than
        // on the pending branch so a crash mid-flight still leaves it recoverable.
        operation.Parameters["IdempotencyKey"] = idempotencyKey;
        operation.Parameters["ChainType"] = chainType;
        operation.Parameters["AssetId"] = assetId;
        operation.Parameters["TokenId"] = credential.TokenId;
        operation.Parameters["CredentialId"] = credential.Id.ToString();
        operation.Parameters["HolderUserId"] = credential.HolderUserId.ToString();
        operation.Parameters["HolderAddress"] = holderAddress;
        operation.Parameters["RecipientAddress"] = recipientAddress;
        operation.Parameters["Amount"] = OneCredential.ToBaseUnitString();
        if (!string.IsNullOrWhiteSpace(reason))
            operation.Parameters["Reason"] = reason!.Trim();

        return operation;
    }

    /// <summary>
    /// True when the provider signalled "broadcast, not yet confirmed" — a success
    /// result whose message carries <see cref="OperationStatus.PendingConfirmationMarker"/>.
    /// </summary>
    private static bool IsPendingConfirmation(string? message) =>
        message?.Contains(OperationStatus.PendingConfirmationMarker, StringComparison.Ordinal) == true;

    // ── Idempotency helpers (mirror FungibleTokenManager) ─────────────────────

    /// <summary>
    /// The claim key. It names the CREDENTIAL as well as the caller, because the tail is
    /// whatever the client sent in its <c>Idempotency-Key</c> header: a key that omitted
    /// the asset let a revoke of a DIFFERENT credential under the same header replay the
    /// first one's Completed result, reporting an asset revoked that was never touched.
    /// The asset id is part of the key itself, not merely of the fallback content hash.
    /// </summary>
    private static string BuildIdempotencyKey(
        Guid callerUserId, string chainType, string assetId, string? clientIdempotencyKey)
    {
        var tail = !string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? clientIdempotencyKey.Trim()
            : IdempotencyReplay.ContentHash(string.Join('|',
                callerUserId.ToString("N"), chainType.ToLowerInvariant(), assetId));
        return $"credential-revoke:{callerUserId:N}:{chainType.ToLowerInvariant()}:{assetId}:{tail}";
    }

    /// <summary>
    /// Replays a settled claim, then re-checks the one fact the key encodes. Belt and
    /// braces behind <see cref="BuildIdempotencyKey"/>: handing back some OTHER
    /// credential's revocation is a wrong answer, not a duplicate, so a mismatch is
    /// refused rather than served.
    /// </summary>
    private static AZOAResult<CredentialRevokeResult> ReplayFromRecord(
        IdempotencyRecord record, string idempotencyKey, string expectedAssetId)
    {
        var replay = IdempotencyReplay.ReplayFromRecord<CredentialRevokeResult>(
            record,
            DeserializeForReplay,
            r => r.Replayed = true,
            "Duplicate request: returning the result of the original revocation (not re-executed).",
            "Duplicate request: original revocation result could not be replayed.",
            "Original revocation failed.",
            $"Revocation for key '{idempotencyKey}' is already in progress; " +
            "retry once the original request settles.");

        if (replay.Result is { } cached
            && !string.Equals(cached.AssetId, expectedAssetId, StringComparison.Ordinal))
            return AZOAResult<CredentialRevokeResult>.FailureWithCode(
                $"Idempotency key '{idempotencyKey}' already recorded a revocation of credential " +
                $"{cached.AssetId}, not {expectedAssetId}. Refusing to replay one credential's result " +
                "as another's.",
                AzoaErrorCodes.Conflict);

        return replay;
    }

    private static string SerializeForReplay(CredentialRevokeResult result)
        => IdempotencyReplay.SerializeForReplay(result);

    private static CredentialRevokeResult? DeserializeForReplay(string payload)
        => IdempotencyReplay.DeserializeForReplay<CredentialRevokeResult>(payload);

    private static AZOAResult<CredentialRevokeResult> Fail(string message)
        => AZOAResult<CredentialRevokeResult>.Failure(message);

    /// <summary>Settles the claim Failed (nothing was broadcast) and returns the refusal.</summary>
    private async Task<AZOAResult<CredentialRevokeResult>> FailClaim(
        string idempotencyKey, string message, CancellationToken ct, string? code = null)
    {
        await _idempotencyStore.FailAsync(idempotencyKey, message, ct);
        return code is null
            ? AZOAResult<CredentialRevokeResult>.Failure(message)
            : AZOAResult<CredentialRevokeResult>.FailureWithCode(message, code);
    }
}
