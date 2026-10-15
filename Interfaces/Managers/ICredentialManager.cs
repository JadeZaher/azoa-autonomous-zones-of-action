// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// The soulbound-credential revocation seam (D19). Revocation is an Algorand
/// <c>AssetClawback</c> signed by the ASA's clawback authority — a capability this
/// codebase has always minted onto its assets and never exercised.
///
/// <para><b>Authorization is the ISSUER, never the holder.</b> The credential's
/// recorded issuer is the only party that may revoke it; the holder is the party the
/// asset is taken FROM. A row with no recorded issuer is unrevocable and fails closed
/// — the issuer is never inferred from <c>CurrentOwner</c> (that is the holder, the
/// exact inversion) nor from an operation log's user id (a mint proxied through a
/// tenant credential stamps the wrong party).</para>
/// </summary>
public interface ICredentialManager
{
    /// <summary>
    /// Claws a soulbound credential back from its holder to the issuer's wallet.
    /// Idempotent on <paramref name="clientIdempotencyKey"/> (partitioned by caller AND
    /// by the asset), so a redelivered request never submits a second clawback and one
    /// credential's cached result can never be replayed as another's.
    ///
    /// <para>The returned <see cref="CredentialRevokeResult.Status"/> is the authority
    /// on what happened: a broadcast-but-unconfirmed transaction comes back as
    /// <c>PendingConfirmation</c> with its tx hash and is NOT a success. Such a result
    /// also carries <c>IsError = true</c> and
    /// <see cref="CredentialRevokeCodes.PendingConfirmation"/>, because
    /// <c>IsError</c> — not a nested status string — is what every AZOA client helper
    /// branches on.</para>
    /// </summary>
    /// <param name="assetId">Chain-native asset id (ASA id) of the credential.</param>
    /// <param name="request">Chain selector plus an optional recorded reason.</param>
    /// <param name="callerUserId">The authenticated caller; must BE the recorded issuer.</param>
    /// <param name="clientIdempotencyKey">Client key; null ⇒ a deterministic content key.</param>
    /// <param name="actingTenantId">Tenant driving this on the issuer's behalf, if any.</param>
    Task<AZOAResult<CredentialRevokeResult>> RevokeAsync(
        string assetId,
        CredentialRevokeRequest request,
        Guid callerUserId,
        string? clientIdempotencyKey,
        Guid? actingTenantId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Wire discriminators the credential seam sets on <c>AZOAResult.Code</c>. They live
/// here, beside the contract that produces them, so the manager and every transport
/// that maps them (HTTP status, SDK branching) name the SAME constant instead of two
/// string literals that can drift apart.
/// </summary>
public static class CredentialRevokeCodes
{
    /// <summary>
    /// The clawback reached the network but has NOT confirmed. Neither a success nor a
    /// failure: the credential may or may not have moved, and only chain truth decides.
    /// Carried with <c>IsError = true</c> so a client that branches on <c>IsError</c>
    /// — which is what AZOA client helpers do — cannot read it as a completed revocation.
    /// </summary>
    public const string PendingConfirmation = "REVOCATION_PENDING_CONFIRMATION";
}

/// <summary>
/// Read/write access to the D19 issuer column on <c>nft_ownership</c>, plus the
/// by-asset-id lookup the revoke path needs and the ownership move a confirmed
/// revocation must record.
/// </summary>
/// <remarks>
/// This is a second, deliberately narrow face on the same store that implements
/// <see cref="Stores.INftStore"/>. It exists because the <c>IUserNFT</c> domain model
/// <see cref="Stores.INftStore"/> maps to has no issuer property, and the issuer is
/// the field authorization turns on — routing it through the attributes bag (which
/// callers can write) would make the authorization decision forgeable by the holder.
/// Implemented by <c>SurrealNftStore</c>; registered alongside it.
/// </remarks>
public interface ICredentialIssuerStore
{
    /// <summary>
    /// Loads the live ownership row for one asset on one chain, including its issuer.
    /// A <c>null</c> result inside a non-error <see cref="AZOAResult{T}"/> means "no
    /// such live credential"; an error result means the lookup itself failed and the
    /// caller must fail closed rather than treat it as absent.
    /// </summary>
    Task<AZOAResult<CredentialOwnershipRecord?>> FindCurrentByAssetIdAsync(
        string chainType, string assetId, CancellationToken ct = default);

    /// <summary>
    /// Stamps the issuer onto an ownership row. The issuance path calls this once, at
    /// mint time, with the party that minted the credential — never with the holder.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the writer that makes revocation possible at all.</b>
    /// <c>issuer_user_id</c> is the single column the revoke authorization decision turns
    /// on, and no other write path touches it — so a mint that skips this call produces a
    /// credential that no party on earth can revoke, and the entire rail below it is dead
    /// code. Its production caller is <c>SurrealNftStore.UpsertUserNFTAsync</c>, on the
    /// creation of a soulbound row.</para>
    /// <para>Write-ONCE. Stamping an issuer over a different recorded issuer is refused
    /// (re-pointing it would hand revocation authority to another party); stamping the
    /// same issuer again succeeds, so an idempotent mint retry does not fail.</para>
    /// </remarks>
    Task<AZOAResult<bool>> SetIssuerUserIdAsync(
        Guid credentialId, Guid issuerUserId, CancellationToken ct = default);

    /// <summary>
    /// Records a CONFIRMED revocation against the ownership row: the credential now
    /// belongs to <paramref name="issuerUserId"/> at <paramref name="issuerAddress"/>.
    /// </summary>
    /// <remarks>
    /// Without this the chain and AZOA disagree after a successful clawback — the live
    /// <c>nft_ownership</c> row still names the original holder, so every AZOA read
    /// surface keeps reporting a revoked credential as held. Called ONLY after the
    /// clawback confirms; a pending or failed one leaves ownership exactly as it was.
    /// The recorded issuer is never rewritten: the party that issued a credential does
    /// not change by taking it back.
    /// </remarks>
    Task<AZOAResult<bool>> RecordCredentialRevokedAsync(
        Guid credentialId, Guid issuerUserId, string issuerAddress, CancellationToken ct = default);
}
