using System.Net;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Services.Wormhole;
using AZOA.WebAPI.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.Util;
using Nethereum.Signer;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttEthereumAssetInspectionTests
{
    [Theory]
    [InlineData("exact", "Confirmed")]
    [InlineData("missing-debit", "Unknown")]
    [InlineData("duplicate", "Unknown")]
    [InlineData("missing-fee", "Unknown")]
    [InlineData("revert", "FailedOnChain")]
    public async Task TargetRedemptionRequiresCanonicalBodyConsumptionAndExactRecipientEffect(string evidence, string verdict)
    {
        using var rpc = new AssetRpc("pipeline");
        var (intent, proofs) = await TargetCase(rpc);
        var quote = await rpc.Provider.QuoteWttTargetAsync(intent, proofs);
        Assert.False(quote.IsError, quote.Message);
        var prepared = await rpc.Provider.PrepareWttTargetAsync(intent, proofs, quote.Result!, "Redeem", 0);
        Assert.False(prepared.IsError, prepared.Message);
        rpc.FinalizedIntent = intent; rpc.ReceiptEvidence = evidence;
        var observed = await rpc.Provider.ObserveWttTargetAsync(intent, proofs, quote.Result!, prepared.Result!);
        Assert.False(observed.IsError, observed.Message);
        Assert.Equal(verdict, observed.Result!.Verdict);
        if (verdict == "Confirmed")
        {
            using var proof = JsonDocument.Parse(observed.Result.EvidenceJson!);
            Assert.Equal("1000000000000", proof.RootElement.GetProperty("recipientAmount").GetString());
            Assert.Equal(proofs.Transfer.Vaa.BodyDigest, proof.RootElement.GetProperty("bodyDigest").GetString());
        }
        Assert.Empty(rpc.Submissions);
    }

    [Fact]
    public async Task TargetRechecksGuardianProofBeforeNewNonceOrCustody()
    {
        using var rpc = new AssetRpc("pipeline");
        var (intent, proofs) = await TargetCase(rpc);
        var quote = await rpc.Provider.QuoteWttTargetAsync(intent, proofs);
        Assert.False(quote.IsError, quote.Message);
        rpc.GuardianSignatureValid = false;
        var prepared = await rpc.Provider.PrepareWttTargetAsync(intent, proofs, quote.Result!, "Redeem", 0);
        Assert.True(prepared.IsError);
        Assert.Empty(rpc.Preparations.Snapshot);
        Assert.Equal(0, rpc.SignCount);
    }

    private static async Task<(WttOperationIntent Intent, WttTargetProofs Proofs)> TargetCase(AssetRpc rpc)
    {
        rpc.TargetSigning = true;
        var inspected = await rpc.Provider.InspectWttSourceAsync(AssetRpc.Token, false);
        Assert.False(inspected.IsError, inspected.Message);
        var source = new WttNetworkDescriptor("Solana", ChainNetwork.Devnet, "solana:fixture", "Devnet", 1, "core", "token", "code",
            new(1, new string('a', 64), new string('b', 64), new string('c', 64)), new string('d', 64));
        var authority = new WttSigningAuthority(rpc.SignerAddress, new SigningContext(rpc.Actor, rpc.Wallet, false,
            Scope: AzoaScopes.WttTargetSign, ChainType: "Ethereum"));
        var asset = inspected.Result.Asset with { TokenAddress = "solana-wrapper", Decimals = 8, OriginDecimals = null, BehaviorIdentity = "source-wrapper" };
        var recipient = "0x4444444444444444444444444444444444444444";
        var intent = new WttOperationIntent(1, Guid.NewGuid().ToString("N"), rpc.Actor, null, source, inspected.Result.Network,
            asset, authority, authority, recipient, new string('0', 24) + recipient[2..], AssetRpc.Token, 18,
            WttPayloadCodec.Normalize("100", "0", 8), 42, 1, false, false, new("0", "0", "0"), new("2000000", "0", "0"));
        var payload = WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, asset.OriginAddress, asset.OriginChain,
            intent.RecipientProtocolAddress, 2, "0"));
        var wire = new byte[123 + payload.Length]; wire[0] = 1; wire[5] = 1; wire[7] = 1; wire[39] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(76), 42);
        wire[81] = 1; Convert.FromHexString(source.EmitterAddressHex).CopyTo(wire, 82);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(wire.AsSpan(114), 17);
        wire[122] = 1; payload.CopyTo(wire, 123);
        return (intent, new(new(WttVaaCodec.Parse(wire), null!), null));
    }

    [Theory]
    [InlineData("exact", "Confirmed")]
    [InlineData("missing-debit", "Unknown")]
    public async Task NativePublicationRequiresExactBridgeOwnedWethDeposit(string evidence, string verdict)
    {
        using var rpc = new AssetRpc("pipeline");
        var intent = await PipelineIntent(rpc, native: true);
        var quote = await rpc.Provider.QuoteWttSourceAsync(intent);
        Assert.False(quote.IsError, quote.Message);
        var prepared = await rpc.Provider.PrepareWttSourceAsync(intent, quote.Result!, "Transfer", 0);
        Assert.False(prepared.IsError, prepared.Message);
        rpc.FinalizedIntent = intent; rpc.ReceiptEvidence = evidence;
        var observed = await rpc.Provider.ObserveWttSourceAsync(intent, quote.Result!, prepared.Result!);
        Assert.False(observed.IsError, observed.Message);
        Assert.Equal(verdict, observed.Result!.Verdict);
        Assert.Empty(rpc.Submissions);
    }

    [Fact]
    public async Task LostPreparationResponseRecoversSavedWireAfterMutableCodeChangesWithoutSigningOrSending()
    {
        using var rpc = new AssetRpc("pipeline");
        var intent = await PipelineIntent(rpc);
        var quote = await rpc.Provider.QuoteWttSourceAsync(intent);
        var first = await rpc.Provider.PrepareWttSourceAsync(intent, quote.Result!, "Transfer", 0);
        Assert.False(first.IsError, first.Message);
        rpc.ChangedCode = true;
        var recovered = await rpc.Provider.PrepareWttSourceAsync(intent, quote.Result!, "Transfer", 0);
        Assert.False(recovered.IsError, recovered.Message);
        Assert.Equal(first.Result, recovered.Result);
        Assert.Equal(1, rpc.SignCount);
        Assert.Empty(rpc.Submissions);
    }

    [Theory]
    [InlineData("exact", "Confirmed")]
    [InlineData("duplicate", "Unknown")]
    [InlineData("payload", "Unknown")]
    [InlineData("missing-fee", "Unknown")]
    [InlineData("upgrade", "Unknown")]
    [InlineData("missing-debit", "Unknown")]
    [InlineData("revert", "FailedOnChain")]
    public async Task FinalizedReceiptRequiresExactUniquePublicationAndExecutionEvidence(string evidence, string verdict)
    {
        using var rpc = new AssetRpc("pipeline");
        var intent = await PipelineIntent(rpc);
        var quote = await rpc.Provider.QuoteWttSourceAsync(intent);
        var prepared = await rpc.Provider.PrepareWttSourceAsync(intent, quote.Result!, "Transfer", 0);
        Assert.False(prepared.IsError, prepared.Message);
        rpc.FinalizedIntent = intent;
        rpc.ReceiptEvidence = evidence;
        var observed = await rpc.Provider.ObserveWttSourceAsync(intent, quote.Result!, prepared.Result!);
        Assert.False(observed.IsError, observed.Message);
        Assert.Equal(verdict, observed.Result!.Verdict);
        if (verdict == "Confirmed")
        {
            Assert.NotNull(observed.Result.Publication);
            Assert.Equal("100000000", WttPayloadCodec.ParseTransfer(Convert.FromHexString(observed.Result.Publication!.PayloadHex)).Amount);
            Assert.Equal("63000", observed.Result.NetworkFeePaid);
        }
        else Assert.Null(observed.Result.Publication);
        Assert.Empty(rpc.Submissions);
    }

    [Fact]
    public async Task LostSubmissionResponseReplaysSameDurableNonceAndSignedWire()
    {
        using var rpc = new AssetRpc("pipeline");
        var intent = await PipelineIntent(rpc);
        var quote = await rpc.Provider.QuoteWttSourceAsync(intent);
        Assert.False(quote.IsError, quote.Message);
        var prepared = await rpc.Provider.PrepareWttSourceAsync(intent, quote.Result!, "Transfer", 0);
        Assert.False(prepared.IsError, prepared.Message);
        Assert.Equal(1, rpc.SignCount);
        Assert.Single(rpc.Preparations.Snapshot);
        Assert.Empty(rpc.Submissions);
        rpc.LoseNextSubmission = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => rpc.Provider.SubmitWttSourceAsync(intent, quote.Result!, prepared.Result!));
        var resumed = await rpc.Provider.PrepareWttSourceAsync(intent, quote.Result!, "Transfer", 0);
        Assert.False(resumed.IsError, resumed.Message);
        Assert.Equal(prepared.Result, resumed.Result);
        Assert.Equal(1, rpc.SignCount);
        await rpc.Provider.SubmitWttSourceAsync(intent, quote.Result!, resumed.Result!);
        Assert.Equal(2, rpc.Submissions.Count);
        Assert.Equal(rpc.Submissions[0], rpc.Submissions[1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => rpc.Provider.SubmitWttSourceAsync(intent, quote.Result!,
            resumed.Result! with { Generation = 1 }));
        Assert.Equal(2, rpc.Submissions.Count);
    }

    [Fact]
    public async Task CurrentCostAboveFrozenStageLimitFailsBeforeNonceOrCustody()
    {
        using var rpc = new AssetRpc("pipeline");
        var intent = await PipelineIntent(rpc);
        var quote = await rpc.Provider.QuoteWttSourceAsync(intent);
        Assert.False(quote.IsError, quote.Message);
        rpc.ExcessiveGas = true;
        var prepared = await rpc.Provider.PrepareWttSourceAsync(intent, quote.Result!, "Transfer", 0);
        Assert.True(prepared.IsError);
        Assert.Empty(rpc.Preparations.Snapshot);
        Assert.Equal(0, rpc.SignCount);
        Assert.Empty(rpc.Submissions);
    }

    private static async Task<WttOperationIntent> PipelineIntent(AssetRpc rpc, bool native = false)
    {
        var inspected = await rpc.Provider.InspectWttSourceAsync(AssetRpc.Token, native);
        Assert.False(inspected.IsError, inspected.Message);
        var authority = new WttSigningAuthority(rpc.SignerAddress, new SigningContext(rpc.Actor, rpc.Wallet, false,
            Scope: AzoaScopes.WttSourceSign, ChainType: "Ethereum"));
        var target = new WttNetworkDescriptor("Algorand", ChainNetwork.Devnet, "algorand:fixture", "Devnet", 8, "core", "token", "code",
            new(8, new string('a', 64), new string('b', 64), new string('c', 64)), new string('d', 64));
        return new(1, Guid.NewGuid().ToString("N"), rpc.Actor, null, inspected.Result.Network, target, inspected.Result.Asset,
            authority, authority, "recipient", new string('a', 64), null, 8,
            WttPayloadCodec.Normalize("1000000000000000000", "0", 18), 42, 1, false, false,
            new("2000000", "0", native ? "1000000000000000007" : "7"), new("0", "0", "0"));
    }

    [Theory]
    [InlineData("quote", true)]
    [InlineData("fee-cap", false)]
    [InlineData("value-cap", false)]
    [InlineData("missing-stage-policy", false)]
    public async Task SourceQuoteReservesAllPrerequisitesWithoutAllocatingNonceOrSigning(string scenario, bool accepted)
    {
        using var rpc = new AssetRpc(scenario);
        var inspected = await rpc.Provider.InspectWttSourceAsync(AssetRpc.Token, false);
        Assert.False(inspected.IsError, inspected.Message);
        var actor = Guid.NewGuid();
        var authority = new WttSigningAuthority("0x4444444444444444444444444444444444444444",
            new SigningContext(actor, Guid.NewGuid(), false, Scope: AzoaScopes.WttSourceSign, ChainType: "Ethereum"));
        var target = new WttNetworkDescriptor("Algorand", ChainNetwork.Devnet, "algorand:fixture", "Devnet", 8, "core", "token", "code",
            new(8, new string('a', 64), new string('b', 64), new string('c', 64)), new string('d', 64));
        var intent = new WttOperationIntent(1, Guid.NewGuid().ToString("N"), actor, null,
            inspected.Result.Network, target, inspected.Result.Asset, authority, authority,
            "recipient", new string('a', 64), null, 8,
            WttPayloadCodec.Normalize("1000000000000000000", "0", 18), 42, 1, false, true,
            new(scenario == "fee-cap" ? "1" : "2000000", "0", scenario == "value-cap" ? "7" : "14"), new("0", "0", "0"));
        var quote = await rpc.Provider.QuoteWttSourceAsync(intent);
        Assert.Equal(accepted, !quote.IsError);
        if (accepted)
        {
            Assert.Equal(new[] { "ResetAllowance", "Approve", "Attest", "Transfer" }, quote.Result!.Stages.Select(stage => stage.Stage));
            Assert.Equal(new[] { "0", "0", "7", "7" }, quote.Result.Stages.Select(stage => stage.Value));
            Assert.All(quote.Result.Stages, stage => Assert.Equal("300000", stage.NetworkFee));
        }
    }

    [Theory]
    [InlineData("original", true)]
    [InlineData("wrapped", true)]
    [InlineData("wrong-genesis", false)]
    [InlineData("wrong-core", false)]
    [InlineData("changed-code", false)]
    [InlineData("wrong-registration", false)]
    [InlineData("wrong-beacon", false)]
    [InlineData("unreviewed-token", false)]
    public async Task InspectionPinsActualDeploymentAndRepresentationBeforeAnySigning(string scenario, bool accepted)
    {
        using var rpc = new AssetRpc(scenario);
        var result = await rpc.Provider.InspectWttSourceAsync(AssetRpc.Token, false);
        Assert.Equal(accepted, !result.IsError);
        Assert.True(rpc.PinnedReads > 0 || scenario == "wrong-genesis");
        if (!accepted) return;
        var asset = result.Result.Asset;
        Assert.Equal(AssetRpc.Token, asset.TokenAddress);
        if (scenario == "wrapped")
        {
            Assert.Equal((ushort)8, asset.OriginChain);
            Assert.Null(asset.OriginDecimals);
            Assert.Equal((byte)8, asset.Decimals);
        }
        else
        {
            Assert.Equal((ushort)2, asset.OriginChain);
            Assert.Equal((byte)18, asset.OriginDecimals);
        }
    }

    private sealed class AssetRpc : HttpMessageHandler
    {
        internal const string Token = "0x3333333333333333333333333333333333333333";
        private const string Core = "0x1111111111111111111111111111111111111111";
        private const string Bridge = "0x2222222222222222222222222222222222222222";
        private const string Implementation = "0x9999999999999999999999999999999999999999";
        private static readonly string Genesis = "0x" + new string('a', 64);
        private static readonly string Finalized = "0x" + new string('b', 64);
        private readonly string _scenario;
        internal EthereumProvider Provider { get; }
        internal int PinnedReads { get; private set; }
        internal Guid Actor { get; } = Guid.NewGuid();
        internal Guid Wallet { get; } = Guid.NewGuid();
        private readonly EthECKey _key = EthECKey.GenerateKey();
        internal string SignerAddress => _key.GetPublicAddress();
        internal FakeEthereumPreparationStore Preparations { get; } = new();
        internal int SignCount { get; private set; }
        internal bool ExcessiveGas { get; set; }
        internal bool LoseNextSubmission { get; set; }
        internal bool ChangedCode { get; set; }
        internal bool TargetSigning { get; set; }
        internal bool GuardianSignatureValid { get; set; } = true;
        internal List<string> Submissions { get; } = [];
        internal WttOperationIntent? FinalizedIntent { get; set; }
        internal string ReceiptEvidence { get; set; } = "exact";
        private bool Wrapped => _scenario is "wrapped" or "wrong-registration" or "wrong-beacon";

        internal AssetRpc(string scenario)
        {
            _scenario = scenario;
            var hash = EthereumHex.Data(new Sha3Keccack().CalculateHash(new byte[] { 1, 2, 3 }));
            var prefix = "AZOA:Ethereum:Wtt:Networks:Devnet:";
            var values = new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true", ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://wtt.test/",
                ["Blockchain:Chains:0:Devnet:ChainId"] = "31337", ["AZOA:Ethereum:RpcRetryMaxAttempts"] = "0",
                ["AZOA:Ethereum:AllocatorActivationEpoch"] = "wtt-fixture",
                [prefix + "Enabled"] = "true", [prefix + "CoreBridge"] = Core, [prefix + "TokenBridge"] = Bridge,
                [prefix + "GenesisHash"] = Genesis,
                [prefix + "StandardTokens:" + Token + ":RuntimeCodeHash"] = hash,
                [prefix + "StandardTokens:" + Token + ":ContractKind"] = scenario == "unreviewed-token" ? "" : "NonProxy",
            };
            foreach (var name in new[] { "CoreProxyCodeHash", "CoreImplementationCodeHash", "TokenBridgeProxyCodeHash",
                "TokenBridgeImplementationCodeHash", "WrappedProxyCodeHash", "WrappedImplementationCodeHash" }) values[prefix + name] = hash;
            foreach (var stage in new[] { "ResetAllowance", "Approve", "Attest", "Transfer", "Register", "Redeem" })
                values[prefix + "StageGasLimits:" + stage] = scenario == "missing-stage-policy" ? null : "100000";
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(values).Build();
            if (scenario == "pipeline")
            {
                Preparations.SeedCheckpoint("eip155:31337", SignerAddress, "wtt-fixture");
                var custody = new Mock<IKeyCustodyService>();
                custody.Setup(value => value.WithSigningKeyAsync(It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                    .Returns(async (SigningContext context, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
                    {
                        Assert.Equal(Actor, context.UserId); Assert.Equal(Wallet, context.WalletId);
                        Assert.Equal(TargetSigning ? AzoaScopes.WttTargetSign : AzoaScopes.WttSourceSign, context.Scope); SignCount++;
                        return AZOAResult<AZOAResult<byte[]>>.Success(await sign(_key.GetPrivateKeyAsBytes()));
                    });
                var signatures = new Mock<IWttGuardianSignatureVerifier>();
                signatures.Setup(value => value.VerifyGuardianSignature(It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(), It.IsAny<byte[]>()))
                    .Returns(() => GuardianSignatureValid);
                var verifier = new WttProofVerifier(signatures.Object, TimeProvider.System,
                    Options.Create(new WttFinalityOptions { Networks = [new("eip155:31337:" + Genesis, 1200)] }));
                Provider = new(config, NullLogger<EthereumProvider>.Instance, new TransactionSignerFactory([new EthereumTransactionSigner()]),
                    custody.Object, null, this, Preparations, verifier);
            }
            else Provider = new(config, NullLogger<EthereumProvider>.Instance, null, null, null, this);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = doc.RootElement; var args = root.GetProperty("params"); var method = root.GetProperty("method").GetString();
            object? result;
            if (method == "eth_chainId") result = "0x7a69";
            else if (method == "eth_maxPriorityFeePerGas") result = "0x1";
            else if (method == "eth_estimateGas" && _scenario == "pipeline") result = ExcessiveGas ? "0x30d40" : "0x5208";
            else if (method == "eth_getTransactionCount" && _scenario == "pipeline") result = "0x0";
            else if (method == "eth_getTransactionByHash" && FinalizedIntent is not null)
            {
                var saved = Preparations.Snapshot.Single();
                using var call = JsonDocument.Parse(saved.IntentJson);
                result = new { hash = saved.TransactionId, from = saved.Sender, nonce = "0x0", chainId = "0x7a69",
                    value = EthereumHex.Quantity(BigInteger.Parse(call.RootElement.GetProperty("Value").GetString()!, System.Globalization.CultureInfo.InvariantCulture)),
                    input = call.RootElement.GetProperty("Data").GetString(), to = Bridge };
            }
            else if (method == "eth_getTransactionReceipt" && FinalizedIntent is not null) result = Receipt();
            else if (method == "eth_getLogs" && FinalizedIntent is not null)
                result = ReceiptEvidence == "upgrade" ? new object[] { new { address = Bridge } } : Array.Empty<object>();
            else if (method == "eth_sendRawTransaction" && _scenario == "pipeline")
            {
                var wire = args[0].GetString()!; Submissions.Add(wire);
                if (LoseNextSubmission) { LoseNextSubmission = false; throw new HttpRequestException("Simulated lost response after acceptance."); }
                result = EthereumHex.Data(new Sha3Keccack().CalculateHash(Convert.FromHexString(wire[2..])));
            }
            else if (method == "eth_getBlockByNumber") result = new
            {
                hash = args[0].GetString() == "0x0" ? (_scenario == "wrong-genesis" ? Finalized : Genesis) : Finalized,
                timestamp = "0x" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x"),
                baseFeePerGas = "0x1",
                number = "0x64", parentHash = Genesis,
            };
            else
            {
                var block = args[args.GetArrayLength() - 1];
                Assert.Contains(block.GetProperty("blockHash").GetString(), FinalizedIntent is null ? new[] { Finalized } : new[] { Finalized, Genesis });
                Assert.True(block.GetProperty("requireCanonical").GetBoolean()); PinnedReads++;
                result = method switch
                {
                    "eth_getCode" => _scenario == "changed-code" || ChangedCode ? "0x010204" : "0x010203",
                    "eth_getStorageAt" => Address(args[0].GetString() == Token
                        ? (_scenario == "wrong-beacon" ? Core : Bridge) : Implementation),
                    "eth_call" => Call(args[0]),
                    _ => throw new InvalidOperationException("Unexpected read or egress: " + method),
                };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result }), System.Text.Encoding.UTF8, "application/json") };
        }

        private string Call(JsonElement call)
        {
            var data = call.GetProperty("data").GetString()!; var to = call.GetProperty("to").GetString();
            bool Is(string signature) => data.StartsWith("0x" + EthereumAbi.Selector(signature), StringComparison.Ordinal);
            if (Is("chainId()")) return UInt(to == Token ? 8 : 2);
            if (Is("evmChainId()")) return UInt(31337);
            if (Is("finality()")) return UInt(1);
            if (Is("messageFee()")) return UInt(7);
            if (Is("isTransferCompleted(bytes32)")) return UInt(FinalizedIntent is null ? 0 : 1);
            if (Is("getCurrentGuardianSetIndex()")) return UInt(0);
            if (Is("bridgeContracts(uint16)")) return "0x" + new string('d', 64);
            if (Is("getGuardianSet(uint32)")) return UInt(32) + UInt(64)[2..] + UInt(0)[2..] + UInt(1)[2..]
                + Address("0x5555555555555555555555555555555555555555")[2..];
            if (Is("allowance(address,address)")) return UInt(1);
            if (Is("wormhole()")) return Address(_scenario == "wrong-core" ? Bridge : Core);
            if (Is("WETH()")) return Address(Token);
            if (Is("decimals()")) return UInt(Wrapped ? 8 : 18);
            if (Is("isWrappedAsset(address)")) return UInt(Wrapped ? 1 : 0);
            if (Is("wrappedAsset(uint16,bytes32)")) return Address(_scenario == "wrong-registration" ? Core : Token);
            if (Is("implementation()") || Is("tokenImplementation()")) return Address(Implementation);
            if (Is("nativeContract()")) return "0x" + new string('c', 64);
            if (Is("symbol()") || Is("name()")) return UInt(32) + UInt(1)[2..] + "41" + new string('0', 62);
            throw new InvalidOperationException("Unexpected contract getter: " + data);
        }

        private object Receipt()
        {
            if (TargetSigning) return TargetReceipt();
            var intent = FinalizedIntent!;
            var tx = Preparations.Snapshot.Single().TransactionId!;
            var payload = WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, intent.Asset.OriginAddress,
                intent.Asset.OriginChain, intent.RecipientProtocolAddress, intent.Target.ProtocolChain, intent.Amount.Fee));
            if (ReceiptEvidence == "payload") payload[^1] ^= 1;
            var data = new byte[160 + (payload.Length + 31) / 32 * 32];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(24), 17);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(60), intent.Nonce);
            data[95] = 128; data[127] = intent.ConsistencyLevel;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(156), (uint)payload.Length);
            payload.CopyTo(data, 160);
            var log = new { address = Core, transactionHash = tx, blockHash = Finalized, logIndex = "0x0", removed = false,
                topics = new[] { "0x" + new Sha3Keccack().CalculateHash("LogMessagePublished(address,uint64,uint32,bytes,uint8)"), Address(Bridge) },
                data = EthereumHex.Data(data) };
            var debit = new { address = Token, transactionHash = tx, blockHash = Finalized, logIndex = "0x1", removed = false,
                topics = intent.Asset.NativeAsset
                    ? new[] { "0x" + new Sha3Keccack().CalculateHash("Deposit(address,uint256)"), Address(Bridge) }
                    : new[] { "0x" + new Sha3Keccack().CalculateHash("Transfer(address,address,uint256)"), Address(SignerAddress), Address(Bridge) },
                data = "0x" + Convert.ToHexStringLower(WttPayloadCodec.ParseAmount(intent.Amount.Debited).ToByteArray(true, true)).PadLeft(64, '0') };
            return new { transactionHash = tx, blockHash = Finalized, blockNumber = "0x64",
                status = ReceiptEvidence == "revert" ? "0x0" : "0x1", gasUsed = ReceiptEvidence == "missing-fee" ? null : "0x5208",
                effectiveGasPrice = "0x3", logs = ReceiptEvidence == "duplicate" ? new[] { log, log, debit }
                    : ReceiptEvidence == "missing-debit" ? new[] { log } : new[] { log, debit } };
        }

        private object TargetReceipt()
        {
            var intent = FinalizedIntent!;
            var tx = Preparations.Snapshot.Single().TransactionId!;
            var redemption = new { address = Bridge, transactionHash = tx, blockHash = Finalized, removed = false,
                topics = new[] { "0x" + new Sha3Keccack().CalculateHash("TransferRedeemed(uint16,bytes32,uint64)"), UInt(1),
                    "0x" + intent.Source.EmitterAddressHex, UInt(17) }, data = "0x" };
            var payout = new { address = Token, transactionHash = tx, blockHash = Finalized, removed = false,
                topics = new[] { "0x" + new Sha3Keccack().CalculateHash("Transfer(address,address,uint256)"), Address(Bridge), Address(intent.RecipientAddress) },
                data = "0x" + Convert.ToHexStringLower(BigInteger.Parse("1000000000000").ToByteArray(true, true)).PadLeft(64, '0') };
            return new { transactionHash = tx, blockHash = Finalized, blockNumber = "0x64",
                status = ReceiptEvidence == "revert" ? "0x0" : "0x1", gasUsed = ReceiptEvidence == "missing-fee" ? null : "0x5208",
                effectiveGasPrice = "0x3", logs = ReceiptEvidence == "duplicate" ? new[] { redemption, redemption, payout }
                    : ReceiptEvidence == "missing-debit" ? new[] { redemption } : new[] { redemption, payout } };
        }

        private static string UInt(int value) => "0x" + Convert.ToHexStringLower(new BigInteger(value).ToByteArray(true, true)).PadLeft(64, '0');
        private static string Address(string address) => "0x" + new string('0', 24) + address[2..];
    }
}
