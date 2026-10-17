using System.Text.Json;
using AZOA.WebAPI.Helpers;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record JupiterState(JupiterRpcSnapshot Snapshot, JupiterRouteAccountRoles Roles,
        SolanaSplAssetCodec.Mint InputMint, SolanaSplAssetCodec.Mint OutputMint,
        WhirlpoolOracleState? Oracle, IReadOnlyList<SolanaLookupTableSnapshot> LookupTables,
        IReadOnlyDictionary<string, SolanaSplAssetCodec.Holding> Holdings, IReadOnlyList<string> MissingOwnerAccounts,
        string BuildHash, string Source, string Destination, WttSolanaGuard Guard, string JupiterHash, string WhirlpoolHash);

    private async Task<JupiterState> ReadJupiterStateAsync(JupiterBuildPlan build, string owner,
        string source, string destination, IReadOnlyList<string> extraAddresses, ulong minimumSlot,
        CancellationToken ct)
    {
        var prefix = $"AZOA:Solana:Dex:Networks:{ActiveNetwork}:";
        var jupiterHash = _config[prefix + "JupiterProgramDataHash"];
        var whirlpoolHash = _config[prefix + "WhirlpoolProgramDataHash"];
        if (!IsNativeHash(jupiterHash) || !IsNativeHash(whirlpoolHash))
            throw new InvalidDataException("The concrete Jupiter and Whirlpool deployments require explicit program-data pins.");
        var programs = new[] { JupiterRouteAccounts.Program, WhirlpoolAccountState.Program };
        var addresses = build.Swap.Accounts.Select(account => account.Address)
            .Concat(programs.Select(program => program.Key))
            .Concat(programs.Select(program => SolanaNativeLoader.ProgramData(program).Key))
            .Concat([SysVars.ClockKey.Key, build.InputMint, build.OutputMint,
                WhirlpoolAccountState.OracleAddress(build.Pool)])
            .Concat(extraAddresses).Distinct(StringComparer.Ordinal).ToArray();
        if (addresses.Length > 100) throw new InvalidDataException("Jupiter route exceeds the atomic account snapshot bound.");
        var response = await DurableRpcAsync("getMultipleAccounts", [addresses,
            new { encoding = "base64", commitment = "finalized", minContextSlot = minimumSlot }], ct);
        var snapshot = JupiterRpcSnapshot.Read(response, addresses, minimumSlot, SysVars.ClockKey.Key);
        var tables = await ReadLookupTablesAsync(build.LookupAddresses.Keys.ToArray(), snapshot.Slot, ct);
        var guard = await ReadSolanaCostGuardAsync(prefix, ct);
        return ValidateJupiterState(build, owner, source, destination, snapshot, tables, guard, jupiterHash!, whirlpoolHash!);
    }

    private static JupiterState ValidateJupiterState(JupiterBuildPlan build, string owner, string source, string destination,
        JupiterRpcSnapshot snapshot, IReadOnlyList<SolanaLookupTableSnapshot> tables, WttSolanaGuard guard,
        string jupiterHash, string whirlpoolHash)
    {
        var rebuilt = JupiterRpcSnapshot.Read(JsonSerializer.SerializeToElement(new
        {
            context = new { slot = snapshot.Slot }, value = snapshot.Accounts.Values.ToArray()
        }), snapshot.Accounts.Keys.ToArray(), snapshot.Slot, SysVars.ClockKey.Key);
        if (rebuilt.ChainTimestamp != snapshot.ChainTimestamp)
            throw new InvalidDataException("Saved Jupiter snapshot Clock does not match its recorded chain time.");
        var setup = JupiterSetupInstructions.ValidateSetup(build.Setup, owner, build.InputMint, build.OutputMint)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var mint in new[] { build.InputMint, build.OutputMint })
            setup.Add(AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(owner), new PublicKey(mint)).Key);
        snapshot.RequireProgram(JupiterRouteAccounts.Program, jupiterHash);
        snapshot.RequireProgram(WhirlpoolAccountState.Program, whirlpoolHash);
        var ownedWhirlpool = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (address, account) in snapshot.Accounts)
            if (account.ValueKind == JsonValueKind.Object && account.TryGetProperty("owner", out var accountOwner) &&
                accountOwner.ValueKind == JsonValueKind.String && accountOwner.GetString() == WhirlpoolAccountState.Program.Key)
                ownedWhirlpool.Add(address, snapshot.Owned(address, WhirlpoolAccountState.Program.Key));
        var roles = JupiterRouteAccounts.Validate(build.Route, build.Swap.Accounts.Select(account => account.Address).ToArray(),
            owner, source, destination, build.InputMint, build.OutputMint, build.Pool, ownedWhirlpool);
        JupiterRouteAuthority.ValidatePrivileges(build.Swap, roles);
        var holdings = JupiterRouteAuthority.ReadTokenEvidence(roles, snapshot.Accounts, setup);
        var missingOwnerAccounts = roles.TokenAccounts.Where(role => !holdings.ContainsKey(role.Address))
            .Select(role => role.Address).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var input = SolanaSplAssetCodec.ReadMint(snapshot.Owned(build.InputMint, TokenProgram.ProgramIdKey.Key));
        var output = SolanaSplAssetCodec.ReadMint(snapshot.Owned(build.OutputMint, TokenProgram.ProgramIdKey.Key));
        var oracle = snapshot.ReadOracle(roles.Pool);
        if (tables.Count != build.LookupAddresses.Count || tables.Select(table => table.Address).Distinct(StringComparer.Ordinal).Count() != tables.Count)
            throw new InvalidDataException("Saved Jupiter lookup evidence is incomplete or duplicated.");
        foreach (var table in tables)
            if (table.Slot < snapshot.Slot || !build.LookupAddresses.TryGetValue(table.Address, out var addresses) ||
                !table.ReadActiveAddresses().Select(address => address.Key).SequenceEqual(addresses))
                throw new InvalidDataException("Jupiter Build lookup addresses differ from authoritative active table contents.");
        return new(snapshot, roles, input, output, oracle, tables, holdings, missingOwnerAccounts,
            CanonicalJson.Hash(build), source, destination, guard, jupiterHash, whirlpoolHash);
    }
}
