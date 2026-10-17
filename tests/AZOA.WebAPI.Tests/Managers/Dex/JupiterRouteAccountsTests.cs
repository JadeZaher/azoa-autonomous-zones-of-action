using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterRouteAccountsTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void ExactRouteAndCpiAccountGrammarBindsBothDirectionsAndAuthorityModes(bool shared, bool v2, bool aToB)
    {
        var fixture = new Fixture(shared, v2, aToB);
        var roles = fixture.Validate();
        roles.Signer.Should().Be(fixture.Owner);
        roles.Pool.Address.Should().Be(fixture.Pool.Address);
        roles.TokenAccounts.Should().Contain(role => role.Address == fixture.Source && role.Owner == fixture.Owner && role.Mint == fixture.Input);
        roles.TokenAccounts.Should().Contain(role => role.Address == fixture.Pool.VaultA && role.Owner == fixture.Pool.Address && role.Mint == fixture.Pool.MintA);
        roles.RequiredWritable.Should().Contain(fixture.Source).And.Contain(fixture.Destination).And.Contain(fixture.Pool.Address);
        roles.Readonly.Should().Contain(JupiterRouteAccounts.Program.Key).And.Contain(WhirlpoolAccountState.Program.Key);
        roles.TickArrays.Should().HaveCount(v2 ? 4 : 3);
        for (var index = 0; index < fixture.Accounts.Count; index++)
        {
            var changed = fixture.Accounts.ToArray(); changed[index] = Key(90);
            Action validate = () => fixture.Validate(changed);
            validate.Should().Throw<InvalidDataException>($"account position {index} must retain its exact role");
        }
        Action extra = () => fixture.Validate([.. fixture.Accounts, Key(91)]);
        extra.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ReplacingPoolAffiliationOrConflictingSharedTokenRolesIsRefused()
    {
        var fixture = new Fixture(true, true, true);
        var changed = fixture.Accounts.ToArray(); changed[4] = fixture.Source;
        Action alias = () => fixture.Validate(changed);
        alias.Should().Throw<InvalidDataException>();
        var tick = fixture.Accounts[fixture.Route.FixedAccountCount + 12];
        fixture.Data[tick][9956] ^= 1;
        Action wrongPool = () => fixture.Validate();
        wrongPool.Should().Throw<InvalidDataException>();
    }

    internal sealed class Fixture
    {
        internal readonly JupiterRouteData Route;
        internal readonly WhirlpoolPoolState Pool;
        internal readonly Dictionary<string, byte[]> Data = new(StringComparer.Ordinal);
        internal readonly List<string> Accounts = [];
        internal readonly string Owner = Key(10), Source = Key(11), Destination = Key(12);
        internal readonly string Input, Output;

        internal Fixture(bool shared, bool v2, bool aToB, bool ownerAtas = false, string? mintA = null, string? mintB = null,
            string? owner = null)
        {
            if (owner is not null) Owner = owner;
            var (address, bytes) = WhirlpoolAccountStateTests.Pool(mintA: mintA, mintB: mintB);
            Pool = WhirlpoolAccountState.ReadPool(address, bytes); Data[address] = bytes;
            Route = new(shared, 3, v2, aToB, v2 ? (byte)1 : (byte)0, 1000, 2000, 50);
            Input = aToB ? Pool.MintA : Pool.MintB; Output = aToB ? Pool.MintB : Pool.MintA;
            if (ownerAtas)
            {
                Source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(Owner), new PublicKey(Input)).Key;
                Destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(Owner), new PublicKey(Output)).Key;
            }
            var authority = shared ? JupiterRouteAccounts.Authority(3) : Owner;
            var source = shared ? Key(13) : Source; var destination = shared ? Key(14) : Destination;
            var program = JupiterRouteAccounts.Program.Key; var token = TokenProgram.ProgramIdKey.Key;
            Accounts.AddRange(shared
                ? [token, authority, Owner, Source, source, destination, Destination, Input, Output, program, program, JupiterRouteAccounts.EventAuthority(), program]
                : [token, Owner, Source, Destination, program, Output, program, JupiterRouteAccounts.EventAuthority(), program]);
            var ownerA = aToB ? source : destination; var ownerB = aToB ? destination : source;
            Accounts.AddRange(v2
                ? [WhirlpoolAccountState.Program.Key, token, token, MemoProgram.ProgramIdKeyV2.Key, authority, address,
                    Pool.MintA, Pool.MintB, ownerA, Pool.VaultA, ownerB, Pool.VaultB]
                : [WhirlpoolAccountState.Program.Key, token, authority, address, ownerA, Pool.VaultA, ownerB, Pool.VaultB]);
            Accounts.Add(Tick(0)); Accounts.Add(Tick(-5632)); Accounts.Add(Tick(-11264));
            Accounts.Add(WhirlpoolAccountState.OracleAddress(address));
            if (v2) Accounts.Add(Tick(-16896));
        }

        internal JupiterRouteAccountRoles Validate(IReadOnlyList<string>? accounts = null)
            => JupiterRouteAccounts.Validate(Route, accounts ?? Accounts, Owner, Source, Destination, Input, Output, Pool.Address, Data);

        private string Tick(int start)
        {
            var address = WhirlpoolAccountState.TickArrayAddress(Pool.Address, start);
            var bytes = new byte[9988];
            SHA256.HashData(Encoding.UTF8.GetBytes("account:TickArray")).AsSpan(0, 8).CopyTo(bytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), start);
            new PublicKey(Pool.Address).KeyBytes.CopyTo(bytes, 9956);
            Data[address] = bytes;
            return address;
        }
    }

    private static string Key(byte first) { var bytes = new byte[32]; bytes[0] = first; return new PublicKey(bytes).Key; }
}
