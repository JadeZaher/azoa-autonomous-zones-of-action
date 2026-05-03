using Algorand.Algod.V2;
using Algorand.V2;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Blockchain.Algorand;

/// <summary>
/// Builds Algorand transactions with proper fee calculation and parameter handling
/// </summary>
public class AlgorandTransactionBuilder
{
    private readonly AlgodClient _algodClient;
    
    public AlgorandTransactionBuilder(AlgodClient algodClient)
    {
        _algodClient = algodClient;
    }
    
    /// <summary>
    /// Build a payment transaction for ALGO transfers
    /// </summary>
    public async Task<Transaction> BuildPaymentTransactionAsync(
        string fromAddress, 
        string toAddress, 
        ulong amount, 
        CancellationToken ct = default)
    {
        var suggestedParams = await GetSuggestedParamsAsync();
        var fromAddr = Address.FromPublicKey(fromAddress);
        var toAddr = Address.FromPublicKey(toAddress);
        
        return Transaction.CreatePaymentTransaction(
            fromAddr,
            toAddr,
            amount,
            suggestedParams);
    }
    
    /// <summary>
    /// Build an asset transfer transaction for ASA transfers
    /// </summary>
    public async Task<Transaction> BuildAssetTransferTransactionAsync(
        string fromAddress,
        string toAddress,
        ulong assetId,
        ulong amount,
        CancellationToken ct = default)
    {
        var suggestedParams = await GetSuggestedParamsAsync();
        var fromAddr = Address.FromPublicKey(fromAddress);
        var toAddr = Address.FromPublicKey(toAddress);
        
        return Transaction.CreateAssetTransferTransaction(
            fromAddr,
            toAddr,
            assetId,
            amount,
            0, // Close amount to receiver
            0, // Close amount to sender
            suggestedParams);
    }
    
    /// <asset>
    /// Build an ASA creation transaction
    /// </summary>
    public async Task<Transaction> BuildAssetCreationTransactionAsync(
        string fromAddress,
        string name,
        string unitName,
        ulong total,
        int decimals,
        string managerAddress,
        string reserveAddress,
        string freezeAddress,
        string clawbackAddress,
        CancellationToken ct = default)
    {
        var suggestedParams = await GetSuggestedParamsAsync();
        var fromAddr = Address.FromPublicKey(fromAddress);
        
        // Convert addresses to Algorand Address objects
        var manager = Address.FromPublicKey(managerAddress);
        var reserve = Address.FromPublicKey(reserveAddress);
        var freeze = Address.FromPublicKey(freezeAddress);
        var clawback = Address.FromPublicKey(clawbackAddress);
        
        return Transaction.CreateAssetTransaction(
            fromAddr,
            suggestedParams,
            total,
            (ulong)decimals,
            name,
            unitName,
            manager,
            reserve,
            freeze,
            clawback);
    }
    
    /// <summary>
    /// Build an opt-in transaction for an ASA
    /// </summary>
    public async Task<Transaction> BuildAssetOptInTransactionAsync(
        string address,
        ulong assetId,
        CancellationToken ct = default)
    {
        var suggestedParams = await GetSuggestedParamsAsync();
        var addr = Address.FromPublicKey(address);
        
        return Transaction.CreateAssetTransferTransaction(
            addr,
            addr,
            assetId,
            0,
            0,
            0,
            suggestedParams);
    }
    
    /// <summary>
    /// Build a transaction group for atomic operations
    /// </summary>
    public async Task<List<Transaction>> BuildAtomicTransactionGroupAsync(
        List<Transaction> transactions,
        CancellationToken ct = default)
    {
        var suggestedParams = await GetSuggestedParamsAsync();
        
        // Set the same suggested params for all transactions in the group
        foreach (var tx in transactions)
        {
            tx.SuggestedParams = suggestedParams;
        }
        
        return transactions;
    }
    
    /// <summary>
    /// Calculate optimal fee for a transaction
    /// </summary>
    public async Task<ulong> CalculateOptimalFeeAsync(CancellationToken ct = default)
    {
        var suggestedParams = await GetSuggestedParamsAsync();
        return suggestedParams.Fee;
    }
    
    /// <summary>
    /// Get suggested transaction parameters with current network conditions
    /// </summary>
    private async Task<SuggestedParams> GetSuggestedParamsAsync()
    {
        return await _algodClient.SuggestedTransactionParamsAsync();
    }
}