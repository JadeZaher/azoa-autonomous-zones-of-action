namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Result of a wallet top-up (faucet) operation on a dev / test network.
/// </summary>
public class WalletTopUpResult
{
    /// <summary>Transaction hash of the faucet dispense (chain-native id).</summary>
    public string TxHash { get; set; } = string.Empty;

    /// <summary>Amount of native test tokens dispensed.</summary>
    public decimal Amount { get; set; }

    /// <summary>Chain the wallet belongs to (e.g., "Algorand").</summary>
    public string Chain { get; set; } = string.Empty;

    /// <summary>Network the dispense happened on (e.g., "Devnet", "Testnet").</summary>
    public string Network { get; set; } = string.Empty;
}
