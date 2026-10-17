using System.Security.Cryptography;
using System.Text;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Builds a seeded operation-owned wrapped SOL account with atomic owner-directed close.</summary>
internal sealed record SolanaSeededNativeAccount(string Address, string Seed, ulong Rent, ulong Funding, string Domain)
{
    internal static readonly PublicKey Mint = new("So11111111111111111111111111111111111111112");

    internal static SolanaSeededNativeAccount Create(string owner, string preparationKey, ulong rent, ulong funding, string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 128 || !domain.EndsWith(":", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(preparationKey) || preparationKey.Length > 512 || rent == 0)
            throw new InvalidDataException("Native token preparation requires an exact durable key and rent evidence.");
        _ = checked(rent + funding);
        var seed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(domain + preparationKey)))[..32].ToLowerInvariant();
        if (!PublicKey.TryCreateWithSeed(new PublicKey(owner), seed, TokenProgram.ProgramIdKey, out var account) || account is null)
            throw new InvalidDataException("Native token account derivation failed.");
        return new(account.Key, seed, rent, funding, domain);
    }

    internal IReadOnlyList<TransactionInstruction> Before(string owner, string preparationKey)
    {
        RequireExact(owner, preparationKey);
        var payer = new PublicKey(owner);
        var account = new PublicKey(Address);
        return
        [
            SystemProgram.CreateAccountWithSeed(payer, account, payer, Seed, checked(Rent + Funding), 165, TokenProgram.ProgramIdKey),
            TokenProgram.InitializeAccount(account, Mint, payer),
            TokenProgram.SyncNative(account)
        ];
    }

    internal TransactionInstruction After(string owner, string preparationKey)
    {
        RequireExact(owner, preparationKey);
        var participant = new PublicKey(owner);
        return TokenProgram.CloseAccount(new PublicKey(Address), participant, participant, TokenProgram.ProgramIdKey);
    }

    private void RequireExact(string owner, string preparationKey)
    {
        if (Create(owner, preparationKey, Rent, Funding, Domain) != this)
            throw new InvalidDataException("Native token account differs from its exact preparation identity.");
    }
}
