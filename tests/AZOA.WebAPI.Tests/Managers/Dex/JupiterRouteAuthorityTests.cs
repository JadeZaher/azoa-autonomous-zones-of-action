using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterRouteAuthorityTests
{
    [Fact]
    public void Missing_account_is_not_fabricated_and_requires_exact_owner_ATA_setup()
    {
        var roles = new JupiterRouteAccountsTests.Fixture(true, true, true).Validate();
        var mint = roles.TokenAccounts[0].Mint;
        var address = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(roles.Signer), new PublicKey(mint)).Key;
        roles = roles with { TokenAccounts = [new(address, mint, roles.Signer)] };
        var accounts = new Dictionary<string, JsonElement> { [address] = JsonSerializer.SerializeToElement<object?>(null) };
        JupiterRouteAuthority.ReadTokenEvidence(roles, accounts, new HashSet<string> { address }).Should().BeEmpty();
        Action omittedSetup = () => JupiterRouteAuthority.ReadTokenEvidence(roles, accounts);
        omittedSetup.Should().Throw<InvalidDataException>();
        var foreign = roles with { TokenAccounts = [new(address, mint, JupiterRouteAccounts.Program.Key)] };
        Action changedOwner = () => JupiterRouteAuthority.ReadTokenEvidence(foreign, accounts, new HashSet<string> { address });
        changedOwner.Should().Throw<InvalidDataException>();
        accounts[address] = Account(roles.TokenAccounts[0], "frozen");
        Action initializedWrongState = () => JupiterRouteAuthority.ReadTokenEvidence(roles, accounts, new HashSet<string> { address });
        initializedWrongState.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void DuplicateAccountAliasesMustHaveOnlyTheApprovedPrivilegeUnion()
    {
        var fixture = new JupiterRouteAccountsTests.Fixture(true, true, true);
        var roles = fixture.Validate();
        var instruction = new JupiterInstruction(JupiterRouteAccounts.Program.Key, fixture.Accounts.Select(address =>
            new JupiterInstructionAccount(address, address == roles.Signer, roles.RequiredWritable.Contains(address))).ToArray(), []);
        JupiterRouteAuthority.ValidatePrivileges(instruction, roles);
        foreach (var change in new[] { "program-write", "extra-signer", "readonly-vault", "no-signer" })
        {
            var changed = instruction with { Accounts = instruction.Accounts.Select(account => change switch
            {
                "program-write" when account.Address == JupiterRouteAccounts.Program.Key => account with { Writable = true },
                "extra-signer" when account.Address == roles.Pool.Address => account with { Signer = true },
                "readonly-vault" when account.Address == roles.Pool.VaultA => account with { Writable = false },
                "no-signer" when account.Address == roles.Signer => account with { Signer = false },
                _ => account,
            }).ToArray() };
            Action validate = () => JupiterRouteAuthority.ValidatePrivileges(changed, roles);
            validate.Should().Throw<InvalidDataException>();
        }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("mint")]
    [InlineData("delegate")]
    [InlineData("frozen")]
    [InlineData("close")]
    [InlineData("program")]
    [InlineData("executable")]
    public void TokenEvidenceRequiresActualProgramOwnerAndExactUnencumberedRole(string change)
    {
        var fixture = new JupiterRouteAccountsTests.Fixture(true, true, true);
        var roles = fixture.Validate();
        var accounts = roles.TokenAccounts.ToDictionary(role => role.Address, role => Account(role), StringComparer.Ordinal);
        JupiterRouteAuthority.ReadTokenEvidence(roles, accounts).Count.Should().Be(roles.TokenAccounts.Count);
        var target = roles.TokenAccounts[0];
        accounts[target.Address] = Account(target, change);
        Action validate = () => JupiterRouteAuthority.ReadTokenEvidence(roles, accounts);
        validate.Should().Throw<InvalidDataException>();
    }

    internal static JsonElement Account(JupiterTokenRole role, string? change = null)
    {
        var bytes = new byte[165];
        new PublicKey(role.Mint).KeyBytes.CopyTo(bytes, 0);
        new PublicKey(role.Owner).KeyBytes.CopyTo(bytes, 32);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), 10000);
        bytes[108] = 1;
        if (role.Mint == JupiterNativeAccount.Mint.Key)
        {
            bytes[109] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(113), 2039280);
        }
        switch (change)
        {
            case "mint": bytes[0] ^= 1; break;
            case "owner": bytes[32] ^= 1; break;
            case "delegate": bytes[72] = 1; new PublicKey(role.Owner).KeyBytes.CopyTo(bytes, 76); break;
            case "frozen": bytes[108] = 2; break;
            case "close": bytes[129] = 1; new PublicKey(role.Mint).KeyBytes.CopyTo(bytes, 133); break;
        }
        return JsonSerializer.SerializeToElement(new
        {
            owner = change == "program" ? JupiterRouteAccounts.Program.Key : TokenProgram.ProgramIdKey.Key,
            executable = change == "executable", data = new[] { Convert.ToBase64String(bytes), "base64" },
        });
    }
}
