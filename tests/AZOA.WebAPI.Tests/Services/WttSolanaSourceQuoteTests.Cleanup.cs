using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttSolanaSourceQuoteTests
{
    [Fact]
    public async Task CleanupUsesFreshDirectConsentAndReplaysLostCommitWithoutNewKeyOrCustody()
    {
        using var rpc = new SourceRpc(); var intent = await rpc.IntentAsync();
        var originalQuote = (await rpc.Provider.QuoteWttSourceAsync(intent)).Result!;
        using var signing = new SigningFixture(rpc, intent.SourceAuthority.Context);
        var original = (await signing.Provider.PrepareWttSourceAsync(intent, originalQuote, "Transfer", 0)).Result!;
        rpc.FinalizePublication(intent, original, "valid", 400, 475); rpc.SetCleanupBalance(original, 700);
        var quote = (await signing.Provider.QuoteWttCleanupAsync(intent, originalQuote, null, original)).Result!;
        var consent = new WttCleanupConsent(1, "cleanup-one", quote.QuoteId, quote.NetworkFee);
        var authority = intent.SourceAuthority with { Context = intent.SourceAuthority.Context with
            { Scope = AzoaScopes.WttCleanupSign, ActingTenantId = Guid.Empty, GrantorUserId = Guid.Empty } };
        var priorCalls = signing.CustodyCalls;
        var oldAuthority = await signing.Provider.PrepareWttCleanupAsync(intent, originalQuote, null, original, quote, consent,
            intent.SourceAuthority, 0);
        Assert.True(oldAuthority.IsError); Assert.Equal(priorCalls, signing.CustodyCalls);
        signing.AdditionalContext = authority.Context; signing.LoseCommittedResponse = true;
        await Assert.ThrowsAsync<IOException>(() => signing.Provider.PrepareWttCleanupAsync(intent, originalQuote, null,
            original, quote, consent, authority, 0));
        Assert.Equal(2, signing.Keys.Count); Assert.Equal(2, signing.Preparations.Count);
        signing.AllowCustody = false; priorCalls = signing.CustodyCalls;
        var replay = await signing.Provider.PrepareWttCleanupAsync(intent, originalQuote, null, original, quote, consent, authority, 0);
        Assert.False(replay.IsError, replay.Message); Assert.Equal(priorCalls, signing.CustodyCalls);
        rpc.LoseSendResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => signing.Provider.SubmitWttCleanupAsync(intent, quote, consent, replay.Result!));
        await signing.Provider.SubmitWttCleanupAsync(intent, quote, consent, replay.Result!);
        Assert.Equal(2, rpc.Sent.Count); Assert.Equal(rpc.Sent[0], rpc.Sent[1]);
        rpc.FinalizeCleanup(replay.Result!, quote, 20);
        var observed = await signing.Provider.ObserveWttCleanupAsync(intent, quote, consent, replay.Result!);
        Assert.False(observed.IsError, observed.Message); Assert.Equal("Confirmed", observed.Result!.Stage.Verdict);
        Assert.Equal("700", observed.Result.ReturnedAmount); Assert.Equal("75", observed.Result.OriginalAttributedDepositReturned);
        Assert.Equal("20", observed.Result.RemainingStageBalance);
    }

    [Theory]
    [InlineData("valid", 700UL, false, "75")]
    [InlineData("valid", 474UL, true, null)]
    [InlineData("wrong-custody-delta", 700UL, true, null)]
    [InlineData("missing-history", 700UL, true, null)]
    [InlineData("failed", 700UL, false, "0")]
    public async Task CleanupQuoteRequiresOriginalFinalityAndSeparatesUnrelatedCredits(string mutation, ulong current,
        bool rejected, string? attributed)
    {
        using var rpc = new SourceRpc(); var intent = await rpc.IntentAsync();
        var quote = (await rpc.Provider.QuoteWttSourceAsync(intent)).Result!;
        using var signing = new SigningFixture(rpc, intent.SourceAuthority.Context);
        var prepared = (await signing.Provider.PrepareWttSourceAsync(intent, quote, "Transfer", 0)).Result!;
        rpc.FinalizePublication(intent, prepared, mutation, 400, mutation == "failed" ? 400UL : 475UL);
        rpc.SetCleanupBalance(prepared, current);
        var custodyBefore = signing.CustodyCalls; signing.AllowCustody = false;
        var result = await signing.Provider.QuoteWttCleanupAsync(intent, quote, null, prepared);
        Assert.Equal(rejected, result.IsError); Assert.Equal(custodyBefore, signing.CustodyCalls); Assert.Empty(rpc.Sent);
        if (rejected) return;
        Assert.Equal(current.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Result!.ReturnAmount);
        Assert.Equal(attributed, result.Result.OriginalAttributedDepositReturn);
        Assert.Equal(intent.SourceAuthority.Address, result.Result.DestinationAddress);
        Assert.Equal(64, result.Result.OriginalObservationHash.Length);
        Assert.Equal("5000", result.Result.NetworkFee);
    }

    private sealed partial class SourceRpc
    {
        private string? cleanupPayer;
        private ulong cleanupBalance;

        public void SetCleanupBalance(WttStagePreparation prepared, ulong balance)
        {
            using var recovery = JsonDocument.Parse(prepared.RecoveryMetadata);
            var keys = JsonSerializer.Deserialize<Dictionary<string, WttStageKeyBinding>>(recovery.RootElement.GetProperty("StageKeys").GetRawText())!;
            cleanupPayer = keys.Single(pair => pair.Value.StageRole == "Source:Transfer:payer").Key;
            cleanupBalance = balance;
        }

        private object? CleanupSnapshot(JsonElement args)
        {
            if (cleanupPayer is null || args[0].GetArrayLength() != 2 || args[0][0].GetString() != cleanupPayer) return null;
            Assert.Equal(Owner.Key, args[0][1].GetString()); Assert.Equal(100UL, args[1].GetProperty("minContextSlot").GetUInt64());
            object Account(ulong lamports) => new { owner = SystemProgram.ProgramIdKey.Key, executable = false,
                data = new[] { "", "base64" }, lamports };
            return new { context = new { slot = 101UL }, value = new[] { Account(cleanupBalance), Account(1000000) } };
        }

        public void FinalizeCleanup(WttStagePreparation prepared, WttCleanupQuote quote, ulong remaining)
        {
            using var recovery = JsonDocument.Parse(prepared.RecoveryMetadata);
            var message = SolanaMessageEnvelope.Read(Convert.FromBase64String(recovery.RootElement.GetProperty("MessageBase64").GetString()!));
            Assert.Empty(message.Lookups); Assert.Equal(2, message.RequiredSignatures);
            var keys = message.StaticKeys.Select(key => key.Key).ToArray();
            var pre = new ulong[keys.Length]; var post = new ulong[keys.Length];
            var payer = Array.IndexOf(keys, quote.StageAccount); var destination = Array.IndexOf(keys, quote.DestinationAddress);
            pre[payer] = ulong.Parse(quote.ReturnAmount, System.Globalization.CultureInfo.InvariantCulture) + remaining; post[payer] = remaining;
            pre[destination] = 1000000; post[destination] = pre[destination] + ulong.Parse(quote.ReturnAmount, System.Globalization.CultureInfo.InvariantCulture) - 5000;
            publicationSignature = prepared.TransactionId; observationMutation = "valid";
            FinalizedTransaction = new { slot = 102UL, transaction = new[] { prepared.SignedTransactionBase64, "base64" },
                meta = new { err = (object?)null, fee = 5000UL, preBalances = pre, postBalances = post,
                    loadedAddresses = new { writable = Array.Empty<string>(), @readonly = Array.Empty<string>() } } };
        }
    }
}
