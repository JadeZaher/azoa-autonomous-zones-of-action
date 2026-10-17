using System.Text.Json;
using Solnet.Programs;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Checks route privilege unions and authenticated legacy-token account roles.</summary>
internal static class JupiterRouteAuthority
{
    internal static void ValidatePrivileges(JupiterInstruction instruction, JupiterRouteAccountRoles roles)
    {
        if (instruction.Program != JupiterRouteAccounts.Program.Key)
            throw new InvalidDataException("Jupiter route invokes another program.");
        var union = instruction.Accounts.GroupBy(account => account.Address, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new { Signer = group.Any(account => account.Signer), Writable = group.Any(account => account.Writable) }, StringComparer.Ordinal);
        foreach (var (address, role) in union)
        {
            if (role.Signer != (address == roles.Signer) ||
                role.Writable && address != roles.Signer && !roles.RequiredWritable.Contains(address) && !roles.OptionalWritable.Contains(address) ||
                !role.Writable && roles.RequiredWritable.Contains(address) ||
                address != roles.Signer && !roles.RequiredWritable.Contains(address) && !roles.OptionalWritable.Contains(address) && !roles.Readonly.Contains(address))
                throw new InvalidDataException("Jupiter account privilege union differs from the validated roles.");
        }
        if (!union.TryGetValue(roles.Signer, out var signer) || !signer.Signer || roles.RequiredWritable.Any(address => !union.ContainsKey(address)))
            throw new InvalidDataException("Jupiter route lacks an exact signer or mutable account role.");
    }

    internal static IReadOnlyDictionary<string, SolanaSplAssetCodec.Holding> ReadTokenEvidence(JupiterRouteAccountRoles roles,
        IReadOnlyDictionary<string, JsonElement> accounts, IReadOnlySet<string>? allowedOwnerSetup = null)
    {
        var result = new Dictionary<string, SolanaSplAssetCodec.Holding>(StringComparer.Ordinal);
        foreach (var role in roles.TokenAccounts)
        {
            if (!accounts.TryGetValue(role.Address, out var account)) throw new InvalidDataException("Jupiter token account evidence is missing.");
            if (allowedOwnerSetup?.Contains(role.Address) == true && role.Owner == roles.Signer &&
                AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new Solnet.Wallet.PublicKey(role.Owner),
                    new Solnet.Wallet.PublicKey(role.Mint)).Key == role.Address && IsUninitialized(account))
                continue;
            var holding = SolanaSplAssetCodec.ReadHolding(SolanaAccountData.ReadOwned(account, TokenProgram.ProgramIdKey.Key, false));
            if (holding.Mint != role.Mint || holding.Owner != role.Owner || holding.Frozen ||
                holding.Native != (role.Mint == JupiterNativeAccount.Mint.Key) || holding.Delegate is not null ||
                holding.DelegatedAmount != 0 || holding.CloseAuthority is not null && holding.CloseAuthority != role.Owner)
                throw new InvalidDataException("Jupiter token account mint, owner, delegate or close authority differs.");
            result.Add(role.Address, holding);
        }
        return result;
    }

    private static bool IsUninitialized(JsonElement account)
    {
        if (account.ValueKind == JsonValueKind.Null) return true;
        return account.ValueKind == JsonValueKind.Object && account.TryGetProperty("owner", out var owner) &&
            owner.ValueKind == JsonValueKind.String && owner.GetString() == SystemProgram.ProgramIdKey.Key &&
            SolanaAccountData.ReadOwned(account, SystemProgram.ProgramIdKey.Key, false).Length == 0;
    }
}
