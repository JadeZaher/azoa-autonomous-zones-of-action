using System.Text;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

internal sealed record JupiterTokenRole(string Address, string Mint, string Owner);
internal sealed record JupiterRouteAccountRoles(WhirlpoolPoolState Pool, IReadOnlyList<JupiterTokenRole> TokenAccounts,
    IReadOnlySet<string> RequiredWritable, IReadOnlySet<string> OptionalWritable, IReadOnlySet<string> Readonly,
    string Signer, IReadOnlyList<string> TickArrays);

/// <summary>Binds the pinned Jupiter fixed accounts and exact Whirlpool CPI account sequence.</summary>
internal static class JupiterRouteAccounts
{
    internal static readonly PublicKey Program = new("JUP6LkbZbjS1jKKwapdHNy74zcZ3tLUZoi5QNyVTaV4");

    internal static JupiterRouteAccountRoles Validate(JupiterRouteData route, IReadOnlyList<string> accounts,
        string owner, string source, string destination, string inputMint, string outputMint, string poolAddress,
        IReadOnlyDictionary<string, byte[]> whirlpoolAccounts)
    {
        var ammCount = (route.WhirlpoolV2 ? 15 : 11) + route.SupplementalTickArrays;
        if (accounts.Count != route.FixedAccountCount + 1 + ammCount || source == destination || inputMint == outputMint ||
            !whirlpoolAccounts.TryGetValue(poolAddress, out var poolBytes))
            throw new InvalidDataException("Jupiter route account count or immutable roles differ.");
        foreach (var account in accounts)
            if (new PublicKey(account).Key != account) throw new InvalidDataException("Jupiter account identity is not canonical.");
        var pool = WhirlpoolAccountState.ReadPool(poolAddress, poolBytes);
        if ((route.AToB ? pool.MintA : pool.MintB) != inputMint || (route.AToB ? pool.MintB : pool.MintA) != outputMint)
            throw new InvalidDataException("Jupiter direction differs from the proven Whirlpool mint pair.");
        var writable = new HashSet<string>(StringComparer.Ordinal);
        var optionalWritable = new HashSet<string>(StringComparer.Ordinal);
        var readOnly = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new Dictionary<string, JupiterTokenRole>(StringComparer.Ordinal);
        var tokenProgram = TokenProgram.ProgramIdKey.Key;
        var authority = route.SharedAccounts ? Authority(route.AuthorityId) : owner;
        var ammSource = route.SharedAccounts ? accounts[4] : source;
        var ammDestination = route.SharedAccounts ? accounts[5] : destination;
        var header = route.SharedAccounts
            ? new[] { tokenProgram, authority, owner, source, ammSource, ammDestination, destination,
                inputMint, outputMint, Program.Key, Program.Key, EventAuthority(), Program.Key }
            : new[] { tokenProgram, owner, source, destination, Program.Key, outputMint, Program.Key, EventAuthority(), Program.Key };
        if (!accounts.Take(header.Length).SequenceEqual(header))
            throw new InvalidDataException("Jupiter fixed accounts differ from their original user, mint or PDA roles.");
        AddToken(source, inputMint, owner);
        AddToken(destination, outputMint, owner);
        AddToken(ammSource, inputMint, authority);
        AddToken(ammDestination, outputMint, authority);
        AddToken(pool.VaultA, pool.MintA, poolAddress);
        AddToken(pool.VaultB, pool.MintB, poolAddress);
        var ownerA = route.AToB ? ammSource : ammDestination;
        var ownerB = route.AToB ? ammDestination : ammSource;
        var prefix = route.WhirlpoolV2
            ? new[] { WhirlpoolAccountState.Program.Key, tokenProgram, tokenProgram, MemoProgram.ProgramIdKeyV2.Key,
                authority, poolAddress, pool.MintA, pool.MintB, ownerA, pool.VaultA, ownerB, pool.VaultB }
            : new[] { WhirlpoolAccountState.Program.Key, tokenProgram, authority, poolAddress, ownerA, pool.VaultA, ownerB, pool.VaultB };
        if (!accounts.Skip(header.Length).Take(prefix.Length).SequenceEqual(prefix))
            throw new InvalidDataException("Jupiter AMM prefix or Whirlpool owner/vault accounts differ.");
        var tickOffset = header.Length + prefix.Length;
        var oracle = WhirlpoolAccountState.OracleAddress(poolAddress);
        if (accounts[tickOffset + 3] != oracle)
            throw new InvalidDataException("Whirlpool oracle is not the exact pool PDA.");
        var ticks = accounts.Skip(tickOffset).Take(3)
            .Concat(accounts.Skip(tickOffset + 4).Take(route.SupplementalTickArrays)).ToArray();
        foreach (var address in ticks)
        {
            if (!whirlpoolAccounts.TryGetValue(address, out var data)) throw new InvalidDataException("Whirlpool tick-array evidence is missing.");
            WhirlpoolAccountState.ReadTickArray(address, data, pool);
            writable.Add(address);
        }
        writable.Add(poolAddress);
        if (route.WhirlpoolV2 || pool.FeeTierIndex != pool.TickSpacing) writable.Add(oracle);
        else optionalWritable.Add(oracle);
        readOnly.UnionWith([tokenProgram, Program.Key, WhirlpoolAccountState.Program.Key, EventAuthority(), inputMint, outputMint]);
        if (route.WhirlpoolV2) readOnly.Add(MemoProgram.ProgramIdKeyV2.Key);
        if (route.SharedAccounts) readOnly.Add(authority);
        if (readOnly.Overlaps(writable) || readOnly.Contains(owner) || writable.Contains(owner) || optionalWritable.Contains(owner))
            throw new InvalidDataException("Jupiter writable, signer and program roles collide.");
        return new(pool, tokens.Values.ToArray(), writable, optionalWritable, readOnly, owner, ticks);

        void AddToken(string address, string mint, string tokenOwner)
        {
            var role = new JupiterTokenRole(address, mint, tokenOwner);
            if (address == tokenOwner || tokens.TryGetValue(address, out var prior) && prior != role)
                throw new InvalidDataException("Jupiter token account roles alias incompatible authorities.");
            tokens[address] = role;
            writable.Add(address);
        }
    }

    internal static string Authority(byte id)
    {
        if (id > 7) throw new InvalidDataException("Unsupported Jupiter authority index.");
        return Derive([Encoding.UTF8.GetBytes("authority"), [id]]);
    }

    internal static string EventAuthority() => Derive([Encoding.UTF8.GetBytes("__event_authority")]);

    private static string Derive(IEnumerable<byte[]> seeds)
    {
        if (!PublicKey.TryFindProgramAddress(seeds, Program, out var address, out _))
            throw new InvalidDataException("Jupiter PDA derivation failed.");
        return address.Key;
    }
}
