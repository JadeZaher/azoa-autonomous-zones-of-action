using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Blockchain.Simulated;
using Xunit;

namespace AZOA.WebAPI.Tests.Providers;

/// <summary>
/// db-only-null-provider track: the no-signer, no-network simulated provider.
/// Proves determinism, confirmation, ledger correctness, and the <c>sim:</c>
/// marker / no-collision guardrail.
/// </summary>
public class SimulatedBlockchainProviderTests
{
    private static SimulatedBlockchainProvider NewProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:Mode"] = "Simulated",
            })
            .Build();
        return new SimulatedBlockchainProvider(config, NullLogger<SimulatedBlockchainProvider>.Instance);
    }

    [Fact]
    public void ChainType_IsSimulated()
    {
        NewProvider().ChainType.Should().Be("Simulated");
    }

    // ─── Determinism ───

    [Fact]
    public async Task Mint_SameInputs_ProduceIdenticalHash()
    {
        var a = NewProvider();
        var b = NewProvider();

        var r1 = await a.MintAsync("ipfs://meta", 5, "NFT", "sim:algo:owner");
        var r2 = await b.MintAsync("ipfs://meta", 5, "NFT", "sim:algo:owner");

        r1.IsError.Should().BeFalse();
        r1.Result.Should().Be(r2.Result);
        r1.Result.Should().StartWith(SimulatedBlockchainProvider.SimTxPrefix);
    }

    [Fact]
    public async Task Mint_DifferentInputs_ProduceDifferentHashes()
    {
        var p = NewProvider();

        var r1 = await p.MintAsync("ipfs://meta", 5, "NFT", "sim:algo:owner");
        var r2 = await p.MintAsync("ipfs://meta", 6, "NFT", "sim:algo:owner");
        var r3 = await p.MintAsync("ipfs://other", 5, "NFT", "sim:algo:owner");

        r1.Result.Should().NotBe(r2.Result);
        r1.Result.Should().NotBe(r3.Result);
        r2.Result.Should().NotBe(r3.Result);
    }

    [Fact]
    public void SimTxHash_IsPureFunctionOfInputs()
    {
        var h1 = SimulatedBlockchainProvider.SimTxHash("transfer", "sim:algo:a", "tok", 10, "sim:algo:b");
        var h2 = SimulatedBlockchainProvider.SimTxHash("transfer", "sim:algo:a", "tok", 10, "sim:algo:b");
        var h3 = SimulatedBlockchainProvider.SimTxHash("transfer", "sim:algo:a", "tok", 11, "sim:algo:b");

        h1.Should().Be(h2);
        h1.Should().NotBe(h3);
    }

    // ─── Confirmation ───

    [Fact]
    public async Task GetTransactionStatus_OnSimHash_ReportsCompleted()
    {
        var p = NewProvider();
        var mint = await p.MintAsync("ipfs://meta", 1, "NFT", "sim:algo:owner");

        var status = await p.GetTransactionStatusAsync(mint.Result!);

        status.IsError.Should().BeFalse();
        status.Result!["confirmed"].Should().Be(true);
        status.Result["status"].Should().Be(OperationStatus.Completed);
        status.Result["simulated"].Should().Be(true);
    }

    [Fact]
    public async Task GetTransactionStatus_OnNonSimHash_IsRejected()
    {
        var p = NewProvider();
        // A plausible real Algorand tx id (52-char base32) must NOT be confirmed.
        var status = await p.GetTransactionStatusAsync("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRST");

        status.IsError.Should().BeTrue();
    }

    // ─── Balance ledger after mint / transfer / burn ───

    [Fact]
    public async Task Mint_Transfer_Burn_ReflectInLedger()
    {
        var p = NewProvider();
        const string owner = "sim:algo:owner";
        const string recipient = "sim:algo:recipient";

        var mint = await p.MintAsync("ipfs://meta", 100, "TOKEN", owner);
        var tokenId = mint.Result!; // mint uses the synthetic hash as the holding id

        (await p.GetBalanceAsync(owner, tokenId)).Result.Should().Be("100");

        await p.TransferAsync(tokenId, owner, recipient, 30);
        (await p.GetBalanceAsync(owner, tokenId)).Result.Should().Be("70");
        (await p.GetBalanceAsync(recipient, tokenId)).Result.Should().Be("30");

        await p.BurnAsync(tokenId, 20, recipient);
        (await p.GetBalanceAsync(recipient, tokenId)).Result.Should().Be("10");
    }

    [Fact]
    public async Task GetBalance_UnknownAddress_IsZero()
    {
        var p = NewProvider();
        (await p.GetBalanceAsync("sim:algo:nobody", "tok")).Result.Should().Be("0");
    }

    // ─── Address validation (no cross-contamination) ───

    [Fact]
    public async Task ValidateAddress_AcceptsSimPrefixed()
    {
        var p = NewProvider();
        var r = await p.ValidateAddressAsync("sim:algo:owner");
        r.IsError.Should().BeFalse();
        r.Result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAddress_RejectsRealLookingAddress()
    {
        var p = NewProvider();
        // A real 58-char base32 Algorand address must be rejected in sim mode.
        var r = await p.ValidateAddressAsync("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA234");
        r.IsError.Should().BeTrue();
        r.Result.Should().BeFalse();
    }

    // ─── Marker / no-collision guardrail ───

    [Fact]
    public void SimAddress_CarriesMarker_AndCannotCollideWithRealAddress()
    {
        var addr = SimulatedBlockchainProvider.SimAddress("algo", "owner-1");

        addr.Should().StartWith(SimulatedBlockchainProvider.SimPrefix);
        addr.Should().Contain(":"); // ':' is impossible in base32 / base58

        // A real Algorand address is exactly 58 base32 chars; a real Solana
        // address is base58. Neither alphabet contains ':' — so the marker is a
        // hard partition. Assert our value is not a bare 58-char base32 string.
        const string algoBase32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        addr.Length.Should().NotBe(58, "a marked address must never look like a 58-char Algorand address");
        addr.All(c => algoBase32.Contains(char.ToUpperInvariant(c)))
            .Should().BeFalse("the ':' separators ensure it cannot be pure base32");
    }

    [Fact]
    public async Task SimTxHash_CarriesMarker()
    {
        var p = NewProvider();
        var mint = await p.MintAsync("ipfs://meta", 1, "NFT", "sim:algo:owner");
        mint.Result.Should().StartWith("sim:tx:");
        SimulatedBlockchainProvider.IsSimulated(mint.Result).Should().BeTrue();
    }

    [Fact]
    public async Task GetChainInfo_IsClearlyMarkedSimulated()
    {
        var info = await NewProvider().GetChainInfoAsync();
        info.IsError.Should().BeFalse();
        info.Result!["simulated"].Should().Be(true);
        info.Result["chain"].Should().Be("Simulated");
    }

    // ─── No network I/O guardrail (signature-level) ───

    [Fact]
    public async Task WriteOps_NeverCallSigner_ReturnDeterministicSuccess()
    {
        // The provider takes no signer/HttpClient dependency in its ctor, so any
        // success here is by definition signer-free and network-free. Asserting a
        // success (not the base "not implemented" error) confirms the override.
        var p = NewProvider();
        (await p.MintAsync("u", 1, "NFT", "sim:algo:o")).IsError.Should().BeFalse();
        (await p.TransferAsync("t", "sim:algo:o", "sim:algo:r", 1)).IsError.Should().BeFalse();
        (await p.BurnAsync("t", 1, "sim:algo:o")).IsError.Should().BeFalse();
    }
}
