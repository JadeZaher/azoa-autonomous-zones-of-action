using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Models;

public class BlockchainOperation : IBlockchainOperation, IMintOperation, IExchangeOperation, ITransferOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? AvatarId { get; set; }
    public Guid? WalletId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public Dictionary<string, string> Parameters { get; set; } = new();
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedDate { get; set; }

    // IMintOperation
    public string? TokenUri { get; set; }
    public int Amount { get; set; }
    public string? AssetType { get; set; }

    // IExchangeOperation
    public Guid? SourceHolonId { get; set; }
    public Guid? TargetHolonId { get; set; }
    public string? ExchangeRate { get; set; }

    // ITransferOperation
    public string? RecipientAddress { get; set; }
}
