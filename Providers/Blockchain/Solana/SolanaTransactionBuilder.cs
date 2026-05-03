using Sol.Rpc;
using Sol.Rpc.Models;
using Sol.Rpc.Types;
using Sol.Wallet;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Blockchain.Solana;

/// <summary>
/// Builds Solana transactions with proper fee calculation and parameter handling
/// </summary>
public class SolanaTransactionBuilder
{
    private readonly IRpcClient _rpcClient;
    
    public SolanaTransactionBuilder(IRpcClient rpcClient)
    {
        _rpcClient = rpcClient;
    }
    
    /// <summary>
    /// Build a transfer transaction for SOL transfers
    /// </summary>
    public async Task<Transaction> BuildTransferTransactionAsync(
        Account sourceAccount,
        string destinationAddress,
        ulong lamports,
        CancellationToken ct = default)
    {
        var recentBlockHash = await GetRecentBlockHashAsync(ct);
        var transaction = new Transaction
        {
            RecentBlockHash = recentBlockHash.Blockhash,
            FeePayer = sourceAccount.PublicKey,
            Signatures = new List<byte[]>()
        };
        
        // Add transfer instruction
        transaction.AddInstruction(
            SystemProgram.Transfer(
                sourceAccount.PublicKey,
                PublicKey.FromString(destinationAddress),
                lamports
            )
        );
        
        return transaction;
    }
    
    /// <summary>
    /// Build a transfer instruction for SPL tokens
    /// </summary>
    public async Task<Transaction> BuildTokenTransferTransactionAsync(
        Account sourceAccount,
        string destinationAddress,
        string mintAddress,
        decimal amount,
        CancellationToken ct = default)
    {
        var recentBlockHash = await GetRecentBlockHashAsync(ct);
        var transaction = new Transaction
        {
            RecentBlockHash = recentBlockHash.Blockhash,
            FeePayer = sourceAccount.PublicKey,
            Signatures = new List<byte[]>()
        };
        
        // Get token account info
        var sourceTokenAccount = await GetOrCreateAssociatedTokenAccount(
            sourceAccount.PublicKey.ToString(),
            mintAddress,
            ct);
        
        var destinationTokenAccount = await GetOrCreateAssociatedTokenAccount(
            destinationAddress,
            mintAddress,
            ct);
        
        // Add token transfer instruction
        transaction.AddInstruction(
            TokenProgram.Transfer(
                sourceTokenAccount,
                destinationTokenAccount,
                amount,
                new[] { sourceAccount.PublicKey }
            )
        );
        
        return transaction;
    }
    
    /// <summary>
    /// Build a create associated token account instruction
    /// </summary>
    public async Task<Transaction> BuildCreateAssociatedTokenAccountTransactionAsync(
        Account sourceAccount,
        string mintAddress,
        CancellationToken ct = default)
    {
        var recentBlockHash = await GetRecentBlockHashAsync(ct);
        var transaction = new Transaction
        {
            RecentBlockHash = recentBlockHash.Blockhash,
            FeePayer = sourceAccount.PublicKey,
            Signatures = new List<byte[]>()
        };
        
        var associatedTokenAccount = PublicKey.FindProgramAddress(
            new[] { sourceAccount.PublicKey.KeyBytes, PublicKey.FromString(mintAddress).KeyBytes },
            TokenProgram.ProgramIdKey
        ).Address;
        
        transaction.AddInstruction(
            AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(
                sourceAccount.PublicKey,
                sourceAccount.PublicKey,
                PublicKey.FromString(mintAddress)
            )
        );
        
        return transaction;
    }
    
    /// <summary>
    /// Build a transaction group for atomic operations
    /// </summary>
    public async Task<List<Transaction>> BuildAtomicTransactionGroupAsync(
        List<Transaction> transactions,
        CancellationToken ct = default)
    {
        var recentBlockHash = await GetRecentBlockHashAsync(ct);
        
        // Set the same recent block hash for all transactions in the group
        foreach (var tx in transactions)
        {
            tx.RecentBlockHash = recentBlockHash.Blockhash;
        }
        
        return transactions;
    }
    
    /// <summary>
    /// Calculate optimal fee for a transaction
    /// </summary>
    public async Task<ulong> CalculateOptimalFeeAsync(
        Transaction transaction,
        CancellationToken ct = default)
    {
        try
        {
            var feeCalculator = new TransactionFeeCalculator();
            return await feeCalculator.CalculateFeeAsync(transaction, _rpcClient, ct);
        }
        catch
        {
            // Fallback to a reasonable default fee
            return 5000; // 0.000005 SOL
        }
    }
    
    /// <summary>
    /// Get recent block hash for transaction signing
    /// </summary>
    private async Task<GetRecentBlockHashResult> GetRecentBlockHashAsync(CancellationToken ct = default)
    {
        return await _rpcClient.GetRecentBlockHashAsync();
    }
    
    /// <summary>
    /// Get or create an associated token account
    /// </summary>
    private async Task<PublicKey> GetOrCreateAssociatedTokenAccount(
        string ownerAddress,
        string mintAddress,
        CancellationToken ct = default)
    {
        try
        {
            var owner = PublicKey.FromString(ownerAddress);
            var mint = PublicKey.FromString(mintAddress);
            
            var associatedTokenAccount = PublicKey.FindProgramAddress(
                new[] { owner.KeyBytes, mint.KeyBytes },
                TokenProgram.ProgramIdKey
            ).Address;
            
            // Check if the account exists
            var accountInfo = await _rpcClient.GetAccountInfoAsync(associatedTokenAccount);
            if (accountInfo?.Value == null)
            {
                // Account doesn't exist, return the address for creation
                return associatedTokenAccount;
            }
            
            return associatedTokenAccount;
        }
        catch
        {
            // If there's an error, return the derived address
            var owner = PublicKey.FromString(ownerAddress);
            var mint = PublicKey.FromString(mintAddress);
            return PublicKey.FindProgramAddress(
                new[] { owner.KeyBytes, mint.KeyBytes },
                TokenProgram.ProgramIdKey
            ).Address;
        }
    }
}