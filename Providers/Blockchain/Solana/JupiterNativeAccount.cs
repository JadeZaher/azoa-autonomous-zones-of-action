using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Preserves the original Jupiter native-account domain and exact wire through the shared primitive.</summary>
internal sealed record JupiterNativeAccount(string Address, string Seed, ulong Rent, ulong Funding)
{
    private const string Domain = "azoa:swap:wsol:1:";
    internal static readonly PublicKey Mint = SolanaSeededNativeAccount.Mint;

    internal static JupiterNativeAccount Create(string owner, string preparationKey, ulong rent, ulong funding)
    {
        var account = SolanaSeededNativeAccount.Create(owner, preparationKey, rent, funding, Domain);
        return new(account.Address, account.Seed, account.Rent, account.Funding);
    }

    internal IReadOnlyList<TransactionInstruction> Before(string owner, string preparationKey)
        => Shared().Before(owner, preparationKey);

    internal TransactionInstruction After(string owner, string preparationKey)
        => Shared().After(owner, preparationKey);

    private SolanaSeededNativeAccount Shared() => new(Address, Seed, Rent, Funding, Domain);
}
