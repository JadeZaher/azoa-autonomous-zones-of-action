namespace AZOA.WebAPI.Models.Requests;

public sealed record EthereumRecoveryRequest(bool Cancel, string MaximumFeePerGas, string Reason);
public sealed record EthereumSignerActivationRequest(Guid WalletId);
public sealed record EthereumPlatformActivationRequest(string Sender);
