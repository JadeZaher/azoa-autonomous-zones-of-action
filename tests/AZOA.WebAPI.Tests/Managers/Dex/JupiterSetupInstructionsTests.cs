using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterSetupInstructionsTests
{
    private static readonly string Owner = Key(1);
    private static readonly string Input = Key(2);
    private static readonly string Output = Key(3);

    [Fact]
    public void Exact_owner_ATA_setup_is_accepted_and_duplicates_are_refused()
    {
        var instruction = Setup();
        var generated = JupiterSetupInstructions.CreateOwnerAta(Owner, Output);
        generated.Accounts.Should().Equal(instruction.Accounts);
        generated.Data.Should().Equal((byte)1);
        JupiterSetupInstructions.ValidateSetup([instruction], Owner, Input, Output).Should().ContainSingle();
        var duplicate = () => JupiterSetupInstructions.ValidateSetup([instruction, instruction], Owner, Input, Output);
        duplicate.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("mint")]
    [InlineData("payer")]
    [InlineData("privilege")]
    [InlineData("opcode")]
    [InlineData("trailing")]
    public void Setup_rejects_changed_authority_or_instruction(string mutation)
    {
        var instruction = Setup();
        var accounts = instruction.Accounts.ToArray();
        if (mutation == "owner") accounts[2] = accounts[2] with { Address = Key(4) };
        if (mutation == "mint") accounts[3] = accounts[3] with { Address = Key(4) };
        if (mutation == "payer") accounts[0] = accounts[0] with { Address = Key(4) };
        if (mutation == "privilege") accounts[3] = accounts[3] with { Writable = true };
        instruction = instruction with { Accounts = accounts,
            Data = mutation == "opcode" ? [0] : mutation == "trailing" ? [1, 0] : [1] };
        var act = () => JupiterSetupInstructions.ValidateSetup([instruction], Owner, Input, Output);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Compute_requires_exact_bounded_nonduplicated_opcode_payloads()
    {
        var limit = new JupiterInstruction(SolanaComputeBudget.Program.Key, [], SolanaComputeBudget.Limit(200000).Data);
        var price = new JupiterInstruction(SolanaComputeBudget.Program.Key, [], SolanaComputeBudget.Price(10).Data);
        JupiterSetupInstructions.ValidateCompute([limit, price], 200000, 10).Should().Be(((uint?)200000, (ulong?)10));
        Action duplicate = () => JupiterSetupInstructions.ValidateCompute([limit, limit], 200000, 10);
        Action excessive = () => JupiterSetupInstructions.ValidateCompute([price], 200000, 9);
        Action trailing = () => JupiterSetupInstructions.ValidateCompute([price with { Data = [..price.Data, 0] }], 200000, 10);
        Action authority = () => JupiterSetupInstructions.ValidateCompute([price with { Accounts = [new(Owner, true, true)] }], 200000, 10);
        duplicate.Should().Throw<InvalidDataException>();
        excessive.Should().Throw<InvalidDataException>();
        trailing.Should().Throw<InvalidDataException>();
        authority.Should().Throw<InvalidDataException>();
    }

    private static JupiterInstruction Setup() => new(AssociatedTokenAccountProgram.ProgramIdKey.Key,
    [
        new(Owner, true, true),
        new(AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(Owner), new PublicKey(Output)).Key, false, true),
        new(Owner, false, false), new(Output, false, false),
        new(SystemProgram.ProgramIdKey.Key, false, false), new(TokenProgram.ProgramIdKey.Key, false, false)
    ], [1]);

    private static string Key(byte value) => new PublicKey(Enumerable.Repeat(value, 32).ToArray()).Key;
}
