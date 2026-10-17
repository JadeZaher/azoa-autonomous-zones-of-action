using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Tests.Signing;
using FluentAssertions;
using Nethereum.Util;
using Xunit;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed class EthereumNativeOperationTests
{
    [Fact]
    public async Task Quote_is_read_only_and_increased_cost_is_rejected_before_nonce_or_signing()
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var intent = await Quote(fixture);
        fixture.Store.Snapshot.Should().BeEmpty();
        fixture.SigningCalls.Should().Be(0);
        fixture.Rpc.GasEstimate = "0x100000";
        var result = await fixture.Provider.PrepareNativeStageAsync(intent, "Execute", 0);
        result.IsError.Should().BeTrue();
        fixture.Store.Snapshot.Should().BeEmpty();
        fixture.SigningCalls.Should().Be(0);
        fixture.Rpc.Wires.Should().BeEmpty();
    }

    [Fact]
    public async Task Lost_preparation_response_reuses_one_nonce_and_does_not_authorize_worker_egress()
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var intent = await Quote(fixture);
        var prepared = await fixture.Provider.PrepareNativeStageAsync(intent, "Execute", 0);
        prepared.IsError.Should().BeFalse(prepared.Message);
        var replay = await fixture.NewProvider().PrepareNativeStageAsync(intent, "Execute", 0);
        replay.Result.Should().Be(prepared.Result);
        fixture.Store.Snapshot.Should().ContainSingle();
        fixture.SigningCalls.Should().Be(1);
        await fixture.NewProvider().ReplayAuthorizedAsync(default);
        fixture.Rpc.Wires.Should().BeEmpty();
    }

    [Fact]
    public async Task Lost_send_response_replays_exact_wire_and_only_finalized_matching_code_completes()
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var intent = await Quote(fixture);
        var prepared = (await fixture.Provider.PrepareNativeStageAsync(intent, "Execute", 0)).Result!;
        fixture.Rpc.FailNextSend = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Provider.SubmitNativeStageAsync(intent, prepared));
        await fixture.NewProvider().SubmitNativeStageAsync(intent, prepared);
        fixture.Rpc.Wires.Should().HaveCount(2).And.OnlyContain(wire => wire == fixture.Rpc.Wires[0]);
        (await fixture.Provider.ObserveNativeStageAsync(intent, prepared)).Result!.Verdict.Should().Be("Pending");
        fixture.Rpc.Finalized = true;
        fixture.Rpc.RuntimeCode = "0x6001";
        (await fixture.Provider.ObserveNativeStageAsync(intent, prepared)).IsError.Should().BeTrue();
        fixture.Rpc.RuntimeCode = "0x6000";
        var observed = await fixture.Provider.ObserveNativeStageAsync(intent, prepared);
        observed.Result!.Verdict.Should().Be("Confirmed");
        var completed = await fixture.Provider.GetNextStageAsync(intent, [new(prepared, observed.Result)]);
        completed.Result!.Complete.Should().BeTrue();
        completed.Result.ResourceId.Should().Be("0x2222222222222222222222222222222222222222");
    }

    [Fact]
    public async Task Modified_authority_value_quote_or_wire_cannot_reuse_admitted_operation()
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var intent = await Quote(fixture);
        var prepared = (await fixture.Provider.PrepareNativeStageAsync(intent, "Execute", 0)).Result!;
        var changed = intent with { Budget = intent.Budget with { MaximumValue = "1" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.SubmitNativeStageAsync(changed, prepared));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.SubmitNativeStageAsync(intent,
            prepared with { SignedTransactionBase64 = prepared.SignedTransactionBase64 + "AAAA" }));
        var platform = intent with { Authorities = [new(fixture.Sender, SigningContext.PlatformFor("Ethereum"))] };
        (await fixture.Provider.QuoteNativeOperationAsync(platform)).IsError.Should().BeTrue();
        fixture.Rpc.Wires.Should().BeEmpty();
    }

    [Theory]
    [InlineData("0x__$0123456789012345678901234567890123$__", "0x6000")]
    [InlineData("0x6000", "0x")]
    [InlineData("0x6000", "0x600")]
    public async Task Creation_requires_resolved_exact_bytecode(string init, string runtime)
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var artifact = new NativeArtifact("evm-contract/1", JsonSerializer.SerializeToElement(new EvmContractArtifact(init, runtime)), "solc/fixture");
        (await fixture.Provider.ValidateArtifactAsync(artifact)).IsError.Should().BeTrue();
        fixture.SigningCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("0x1")]
    [InlineData("0x0")]
    public async Task Finalized_cancellation_proves_original_failure_with_its_own_receipt_and_fee(string status)
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var intent = await Quote(fixture);
        var prepared = (await fixture.Provider.PrepareNativeStageAsync(intent, "Execute", 0)).Result!;
        var canceled = await fixture.Provider.RecoverPreparationAsync(fixture.Store.Snapshot.Single().Id,
            intent.Authorities[0].SigningContext.UserId, Guid.Empty, false, true, "100000000000",
            "withdraw native execution", new HashSet<string> { AzoaScopes.ContractCallSign }, default);
        canceled.IsError.Should().BeFalse(canceled.Message);
        var pending = await fixture.Provider.ObserveNativeStageAsync(intent, prepared);
        pending.Result!.Verdict.Should().Be("Unknown");
        fixture.Rpc.Finalized = true;
        fixture.Rpc.ReceiptStatus = status;
        var observed = await fixture.NewProvider().ObserveNativeStageAsync(intent, prepared);
        observed.IsError.Should().BeFalse(observed.Message);
        observed.Result!.Verdict.Should().Be("FailedOnChain");
        observed.Result.TransactionId.Should().Be(prepared.TransactionId);
        observed.Result.ResourceId.Should().BeNull();
        observed.Result.Accounting!.NetworkFeePaid.Should().Be("21000000000000");
        using var proof = JsonDocument.Parse(observed.Result.EvidenceJson!);
        proof.RootElement.GetProperty("canceledOriginal").GetString().Should().Be(prepared.TransactionId);
        proof.RootElement.GetProperty("winningNonceTransaction").GetString().Should().NotBe(prepared.TransactionId);
        proof.RootElement.GetProperty("cancellationSucceeded").GetBoolean().Should().Be(status == "0x1");
        fixture.Rpc.Wires.Should().ContainSingle();
        fixture.Rpc.ReceiptGasUsed = null;
        (await fixture.Provider.ObserveNativeStageAsync(intent, prepared)).IsError.Should().BeTrue();
        fixture.Rpc.ReceiptGasUsed = "0x5208";
        fixture.Rpc.CorruptCanonicalHash = true;
        (await fixture.Provider.ObserveNativeStageAsync(intent, prepared)).Result!.Verdict.Should().Be("Unknown");
    }

    private static async Task<NativeOperationIntent> Quote(EthereumPreparedRecoveryTests.Fixture fixture)
    {
        var actor = Guid.NewGuid();
        var authority = new NativeAuthority(fixture.Sender, new SigningContext(actor, Guid.NewGuid(), false,
            GrantorUserId: actor, Scope: AzoaScopes.ContractCallSign, ChainType: "Ethereum"));
        var artifact = new NativeArtifact("evm-call/1", JsonSerializer.SerializeToElement(new EvmCallArtifact(
            "0x2222222222222222222222222222222222222222", EthereumHex.Data(new Sha3Keccack().CalculateHash(new byte[] { 0x60, 0x00 })),
            "set(uint256)", [JsonSerializer.SerializeToElement("1")])), "solc/fixture");
        var intent = new NativeOperationIntent(1, Guid.NewGuid().ToString("N"), "Call", "Ethereum", "Devnet", "", CanonicalJson.Hash(artifact),
            JsonSerializer.SerializeToElement(new EvmNativeInput(artifact, fixture.Sender)), [authority], new("10000000000000000", "0", "0", ""));
        var quoted = await fixture.Provider.QuoteNativeOperationAsync(intent);
        quoted.IsError.Should().BeFalse(quoted.Message);
        intent = intent with { ProtocolIdentity = quoted.Result!.ProtocolIdentity };
        var quote = quoted.Result with { QuoteId = Guid.NewGuid().ToString("N"), IntentHash = NativeOperationAuthorization.IntentHash(intent) };
        return intent with { Quote = quote, Budget = intent.Budget with { QuoteId = quote.QuoteId } };
    }
}
