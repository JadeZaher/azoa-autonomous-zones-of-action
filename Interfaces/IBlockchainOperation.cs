namespace AZOA.WebAPI.Interfaces;

public interface IBlockchainOperation
{
    Guid Id { get; set; }
    Guid? AvatarId { get; set; }
    Guid? WalletId { get; set; }

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
