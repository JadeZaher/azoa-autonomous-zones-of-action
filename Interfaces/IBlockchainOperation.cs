namespace OASIS.WebAPI.Interfaces;

public interface IBlockchainOperation
{
    Guid Id { get; set; }
    Guid? AvatarId { get; set; }
    Guid? WalletId { get; set; }
    string OperationType { get; set; }
    string Status { get; set; }
    Dictionary<string, string> Parameters { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? CompletedDate { get; set; }
}
