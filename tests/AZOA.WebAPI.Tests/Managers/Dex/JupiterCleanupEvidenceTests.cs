using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterCleanupEvidenceTests
{
    [Fact]
    public void Close_history_uses_inner_execution_order_and_ignores_failed_transactions()
    {
        var owner = Key(1); var mint = Key(2);
        var account = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);
        var wire = new TransactionBuilder().SetFeePayer(owner).SetRecentBlockHash(Key(3).Key)
            .AddInstruction(TokenProgram.TransferChecked(account, account, 0, 6, owner, mint)).CompileMessage();
        var parsed = SolanaMessageEnvelope.Read(wire);
        var keys = parsed.StaticKeys;
        var token = keys.ToList().FindIndex(key => key.Key == TokenProgram.ProgramIdKey.Key);
        var source = keys.ToList().FindIndex(key => key.Key == account.Key);
        var payer = keys.ToList().FindIndex(key => key.Key == owner.Key);
        var children = new[]
        {
            new { programIdIndex = token, accounts = new[] { source, payer, payer }, data = Encoders.Base58.EncodeData(new byte[] { 9 }) },
            new { programIdIndex = token, accounts = new[] { source, payer, payer }, data = Encoders.Base58.EncodeData(new byte[] { 9 }) }
        };
        var meta = JsonSerializer.SerializeToElement(new { err = (object?)null, innerInstructions = new[] { new { index = 0, instructions = children } } });
        var accounts = new SolanaObservedTransaction.Accounts(parsed, keys);
        JupiterCloseHistory.FirstClose(accounts, meta, account.Key).Should().Be(new JupiterClosePosition(0, 0));
        var failed = JsonSerializer.SerializeToElement(new { err = new { InstructionError = new object[] { 0, "InvalidAccountData" } } });
        JupiterCloseHistory.FirstClose(accounts, failed, account.Key).Should().BeNull();
        var missing = JsonSerializer.SerializeToElement(new { err = (object?)null, innerInstructions = (object?)null });
        Action missingHistory = () => JupiterCloseHistory.FirstClose(accounts, missing, account.Key);
        missingHistory.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("positive")]
    [InlineData("retained")]
    [InlineData("wrongRecipient")]
    [InlineData("missingToken")]
    [InlineData("unrelatedDebit")]
    public void Finalized_close_requires_zero_token_removal_and_exact_owner_return(string mutation)
    {
        var owner = Key(1); var mint = Key(2);
        var account = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);
        var wire = new TransactionBuilder().SetFeePayer(owner).SetRecentBlockHash(Key(3).Key)
            .AddInstruction(TokenProgram.CloseAccount(account, owner, owner)).CompileMessage();
        var parsed = SolanaMessageEnvelope.Read(wire);
        var keys = parsed.StaticKeys;
        var ownerIndex = keys.ToList().FindIndex(key => key.Key == owner.Key);
        var accountIndex = keys.ToList().FindIndex(key => key.Key == account.Key);
        var pre = keys.Select(_ => 0UL).ToArray(); var post = pre.ToArray();
        pre[ownerIndex] = 10_000_000; pre[accountIndex] = 2_039_280;
        post[ownerIndex] = 12_034_280;
        if (mutation == "retained") post[accountIndex] = 1;
        if (mutation == "wrongRecipient") post[ownerIndex]--;
        if (mutation == "unrelatedDebit") pre[keys.ToList().FindIndex(key => key.Key == TokenProgram.ProgramIdKey.Key)] = 1;
        var token = new
        {
            accountIndex, mint = mint.Key, owner = owner.Key, programId = TokenProgram.ProgramIdKey.Key,
            uiTokenAmount = new { amount = mutation == "positive" ? "1" : "0", decimals = 6 }
        };
        var meta = JsonSerializer.SerializeToElement(new
        {
            preBalances = pre, postBalances = post,
            preTokenBalances = mutation == "missingToken" ? Array.Empty<object>() : new object[] { token },
            postTokenBalances = Array.Empty<object>()
        });
        Func<ulong> read = () => JupiterCleanupEvidence.Read(owner.Key, mint.Key, 6,
            new SolanaObservedTransaction.Accounts(parsed, keys), meta, 5000);
        if (mutation == "valid") read().Should().Be(2_039_280);
        else read.Should().Throw<InvalidDataException>();
    }

    private static PublicKey Key(byte marker) { var bytes = new byte[32]; bytes[0] = marker; return new(bytes); }
}
