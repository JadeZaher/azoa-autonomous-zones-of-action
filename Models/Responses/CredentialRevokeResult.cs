// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Responses;

/// <summary>Durable credential revocation outcome; completion requires confirmed lifecycle evidence.</summary>
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

    /// <summary>Confirmed lifecycle status or an explicit pending/failed state.</summary>
    public string Status { get; set; } = OperationStatus.Failed;

    /// <summary>True only for <see cref="OperationStatus.Completed"/> — a confirmed clawback.</summary>
    public bool IsConfirmed => string.Equals(Status, OperationStatus.Completed, StringComparison.Ordinal);

    /// <summary>Final revocation transaction identity when available; intermediate acceptance hashes are omitted.</summary>
    public string? TxHash { get; set; }

    /// <summary>The durable NFT lifecycle identifier represented as a GUID.</summary>
    public Guid OperationId { get; set; }

    /// <summary>The durable NFT lifecycle used for refresh and participant acceptance.</summary>
    public string? LifecycleId { get; set; }

    public string? LifecycleState { get; set; }

    /// <summary>True when this response replays a prior revocation under the same idempotency key.</summary>
    public bool Replayed { get; set; }
}

/// <summary>Authoritative current credential identity and verified issuance provenance.</summary>
public sealed class CredentialOwnershipRecord
{
    /// <summary>True only when persisted confirmed issuance evidence matches this exact asset and issuer.</summary>
    public bool HasVerifiedIssuance { get; set; }
    public string Network { get; set; } = string.Empty;
    public string ProtocolIdentity { get; set; } = string.Empty;
    public string ContractAddress { get; set; } = string.Empty;

    /// <summary>AZOA's ownership-row id.</summary>
    public Guid Id { get; set; }

    /// <summary>The user currently HOLDING the credential. Never treat this as the issuer.</summary>
    public Guid HolderUserId { get; set; }

    /// <summary>The holder's on-chain address (the <c>current_owner</c> column).</summary>
    public string? HolderAddress { get; set; }

    /// <summary>Original authenticated issuer, absent when issuance provenance is unavailable.</summary>
    public Guid? IssuerUserId { get; set; }

    /// <summary>Whether the credential is soulbound. Only soulbound assets are revocable here.</summary>
    public bool IsSoulbound { get; set; }

    /// <summary>The chain the credential lives on.</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>The chain-native asset id (ASA id).</summary>
    public string TokenId { get; set; } = string.Empty;
}
