namespace AZOA.WebAPI.Interfaces;

public interface IBlockchainOperation
{
    Guid Id { get; set; }
    Guid? AvatarId { get; set; }
    Guid? WalletId { get; set; }

    /// <summary>
    /// Opaque allocation-receipt correlation. When present, this is the
    /// lower-case SHA-256 correlation, never a raw client or ledger key.
    /// </summary>
    string? IdempotencyKey { get; set; }

    /// <summary>
    /// Authenticated avatar that initiated the operation, distinct from a target
    /// avatar that may receive value.
    /// </summary>
    Guid? InitiatorAvatarId { get; set; }

    /// <summary>
    /// Authenticated API-key record that initiated the operation. This is an ID
    /// only and never carries API-key material.
    /// </summary>
    Guid? InitiatorApiKeyId { get; set; }

    // tenant-consent-delegation AC4: the tenant that drove this value op via a child
    // credential (null = user-driven / platform-internal) + the signing scope the op
    // requires. Carried on the durable row so the signing seam's live consent check
    // survives the async saga-worker hop.
    Guid? ActingTenantId { get; set; }
    string? SigningScope { get; set; }

    string OperationType { get; set; }
    string Status { get; set; }
    Dictionary<string, string> Parameters { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? CompletedDate { get; set; }
}
