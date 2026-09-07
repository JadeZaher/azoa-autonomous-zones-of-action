// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Outcome of a soulbound-credential revocation (D19).
///
/// <para><b>A pending transaction is not a success.</b> <see cref="Status"/> is an
/// <see cref="OperationStatus"/> constant and is the ONLY authority on what happened:
/// <see cref="OperationStatus.Completed"/> means the clawback confirmed on chain,
/// <see cref="OperationStatus.PendingConfirmation"/> means it was broadcast but has
/// not confirmed (the credential may or may not have moved — reconcile against chain
/// truth), and <see cref="OperationStatus.Failed"/> means nothing landed. Callers
/// must branch on <see cref="Status"/>, never on "the call returned a result".</para>
/// </summary>
public sealed class CredentialRevokeResult
{
    /// <summary>The chain-native asset id (ASA id) of the revoked credential.</summary>
    public string AssetId { get; set; } = string.Empty;

    /// <summary>AZOA's ownership-row id for the credential.</summary>
    public Guid CredentialId { get; set; }

    /// <summary>The issuer that authorized this revocation — never the holder.</summary>
    public Guid IssuerUserId { get; set; }

    /// <summary>The user the credential was taken from.</summary>
    public Guid HolderUserId { get; set; }

    /// <summary>The on-chain address the credential was clawed back FROM.</summary>
    public string HolderAddress { get; set; } = string.Empty;

    /// <summary>The on-chain address the credential was clawed back TO (the issuer's wallet).</summary>
    public string RecipientAddress { get; set; } = string.Empty;

    /// <summary>The chain the clawback was submitted to.</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>
    /// An <see cref="OperationStatus"/> constant. <see cref="OperationStatus.Completed"/>
    /// ONLY after a real confirmation; <see cref="OperationStatus.PendingConfirmation"/>
    /// when broadcast but unconfirmed; <see cref="OperationStatus.Failed"/> otherwise.
    /// </summary>
    public string Status { get; set; } = OperationStatus.Failed;

    /// <summary>True only for <see cref="OperationStatus.Completed"/> — a confirmed clawback.</summary>
    public bool IsConfirmed => string.Equals(Status, OperationStatus.Completed, StringComparison.Ordinal);

    /// <summary>
    /// The broadcast transaction id, present whenever a transaction reached the
    /// network — including on the unconfirmed and failed-after-broadcast paths, so
    /// reconciliation can settle from chain truth instead of blind-retrying.
    /// </summary>
    public string? TxHash { get; set; }

    /// <summary>The <c>CredentialRevoke</c> operation row recording this move (D16 analytics).</summary>
    public Guid OperationId { get; set; }

    /// <summary>True when this response replays a prior revocation under the same idempotency key.</summary>
    public bool Replayed { get; set; }
}

/// <summary>
/// The subset of an <c>nft_ownership</c> row the credential seam needs, read straight
/// from the SurrealDB POCO. It exists because <c>IUserNFT</c> — the domain model the
/// ordinary <c>INftStore</c> maps to — carries no issuer, and the issuer is precisely
/// the field authorization turns on. See <c>Services/Actions/AGENTS.md</c> §credential-revoke.
/// </summary>
public sealed class CredentialOwnershipRecord
{
    /// <summary>AZOA's ownership-row id.</summary>
    public Guid Id { get; set; }

    /// <summary>The user currently HOLDING the credential. Never treat this as the issuer.</summary>
    public Guid HolderUserId { get; set; }

    /// <summary>The holder's on-chain address (the <c>current_owner</c> column).</summary>
    public string? HolderAddress { get; set; }

    /// <summary>
    /// The party that ISSUED the credential. Null on every row minted before D19 —
    /// and every revocation of such a row must fail closed rather than guess.
    /// </summary>
    public Guid? IssuerUserId { get; set; }

    /// <summary>Whether the credential is soulbound. Only soulbound assets are revocable here.</summary>
    public bool IsSoulbound { get; set; }

    /// <summary>The chain the credential lives on.</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>The chain-native asset id (ASA id).</summary>
    public string TokenId { get; set; } = string.Empty;
}
