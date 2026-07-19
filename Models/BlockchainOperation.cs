using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models;

public class BlockchainOperation : IBlockchainOperation, IMintOperation, IExchangeOperation, ITransferOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? AvatarId { get; set; }
    public Guid? WalletId { get; set; }

    public string? IdempotencyKey { get; set; }
    public Guid? InitiatorAvatarId { get; set; }
    public Guid? InitiatorApiKeyId { get; set; }

    // tenant-consent-delegation AC4 — see IBlockchainOperation.
    public Guid? ActingTenantId { get; set; }
    public string? SigningScope { get; set; }

    public string OperationType { get; set; } = string.Empty;
    public string Status { get; set; } = OperationStatus.Pending;
    public Dictionary<string, string> Parameters { get; set; } = new();
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedDate { get; set; }

    // IMintOperation
    public string? TokenUri { get; set; }
    public ulong Amount { get; set; }
    public string? AssetType { get; set; }

    // IExchangeOperation
    public Guid? SourceHolonId { get; set; }
    public Guid? TargetHolonId { get; set; }
    public string? ExchangeRate { get; set; }

    // ITransferOperation
    public string? RecipientAddress { get; set; }
}
