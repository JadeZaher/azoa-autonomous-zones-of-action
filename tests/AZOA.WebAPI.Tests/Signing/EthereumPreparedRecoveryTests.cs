using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;
using Xunit;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class EthereumPreparedRecoveryTests
{
    [Fact]
    public async Task Nft_adapter_persists_both_permits_and_exact_struct_before_any_egress()
    {
        using var fixture = new Fixture();
        var actor = Guid.NewGuid();
        var context = SigningContext.ForUser(actor, Guid.NewGuid());
        var lifecycle = Guid.NewGuid().ToString("N");
        var quote = new NftFeeQuote("Ethereum", ChainNetwork.Devnet, "Mint", 7, "ETH", 18, "0", "", actor);
        var grant = new NftOperationAuthorization(lifecycle, "Mint", "Create", 0, actor, fixture.Sender, context,
            null, null, null, true, quote);
        var intent = new NftTransactionIntent("Create", "Ethereum", ChainNetwork.Devnet, fixture.Sender, fixture.Sender,
            null, "test", "ipfs://immutable", false, context, PreparationKey: lifecycle + ":Create:0", Authorization: grant);
        var prepared = await fixture.Provider.PrepareNftTransactionAsync(intent);
        prepared.IsError.Should().BeFalse(prepared.Message);
        var replay = await fixture.NewProvider().PrepareNftTransactionAsync(intent);
        replay.Result!.SignedTransactionBase64.Should().Be(prepared.Result!.SignedTransactionBase64);
        fixture.Rpc.Wires.Should().BeEmpty();
        var tx = Transaction1559Encoder.Current.Decode(Convert.FromBase64String(prepared.Result.SignedTransactionBase64));
        var data = Convert.FromHexString(tx.Data.StartsWith("0x", StringComparison.Ordinal) ? tx.Data[2..] : tx.Data);
        data[..4].Should().Equal(new Sha3Keccack().CalculateHash(Encoding.UTF8.GetBytes("execute(bytes,bytes,bytes)"))[..4]);
        var arguments = data[4..];
        static int Word(byte[] bytes, int at) => (int)new BigInteger(bytes.AsSpan(at, 32), true, true);
        var payloadOffset = Word(arguments, 0);
        var payload = arguments.AsSpan(payloadOffset + 32, Word(arguments, payloadOffset)).ToArray();
        Word(payload, 0).Should().Be(32, "Solidity decodes a single dynamic Execution tuple");
        payload.AsSpan(32, 32).ToArray().Should().Equal(new Sha3Keccack().CalculateHash(Encoding.UTF8.GetBytes(lifecycle)));
        Word(payload, 32 + 16 * 32).Should().Be(7, "the immutable policy revision is in the contract tuple");
        Word(arguments, Word(arguments, 32)).Should().Be(65, "the actor permit is a full recoverable signature");
        Word(arguments, Word(arguments, 64)).Should().Be(65, "a distinct policy permit accompanies every effect");
        await fixture.Provider.ReplayAuthorizedAsync(default);
        fixture.Rpc.Wires.Should().BeEmpty();
        await fixture.Provider.SubmitNftTransactionAsync(prepared.Result);
        fixture.Rpc.Wires.Should().ContainSingle();
    }

    [Fact]
    public async Task Preparation_survives_another_provider_and_cannot_authorize_egress_alone()
    {
        using var fixture = new Fixture();
        var intent = fixture.Intent("value:claim:0");
        var first = await fixture.Provider.PrepareValueTransferAsync(intent, default);
        first.IsError.Should().BeFalse(first.Message);
        var second = await fixture.NewProvider().PrepareValueTransferAsync(intent, default);
        second.Result.Should().Be(first.Result);
        fixture.Store.Snapshot.Should().ContainSingle();
        await fixture.Provider.ReplayAuthorizedAsync(default);
        fixture.Rpc.Wires.Should().BeEmpty();
        var other = await fixture.Provider.PrepareValueTransferAsync(fixture.Intent("value:other:0"), default);
        other.IsError.Should().BeFalse(other.Message);
        fixture.Store.Snapshot.Select(row => row.Nonce).Should().OnlyHaveUniqueItems();
        var tampered = first.Result! with { Intent = intent with { QuantityBaseUnits = "2" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.SubmitValueTransferAsync(tampered, default));
        fixture.Rpc.Wires.Should().BeEmpty();
    }

    [Fact]
    public async Task Lost_submission_response_replays_same_wire_only_after_durable_authorization()
    {
        using var fixture = new Fixture();
        var prepared = (await fixture.Provider.PrepareValueTransferAsync(fixture.Intent("value:crash:0"), default)).Result!;
        fixture.Rpc.FailNextSend = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Provider.SubmitValueTransferAsync(prepared, default));
        fixture.Store.Snapshot.Single().State.Should().Be("SubmissionAuthorized");
        await fixture.NewProvider().ReplayAuthorizedAsync(default);
        fixture.Rpc.Wires.Should().HaveCount(2);
        fixture.Rpc.Wires.Distinct().Should().ContainSingle();
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).Result.Should().Be(ChainConfirmation.Unknown);
        fixture.Rpc.Finalized = true;
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).Result.Should().Be(ChainConfirmation.Confirmed);
    }

    [Fact]
    public async Task Resubmitting_a_mined_transaction_the_node_refuses_is_idempotent_and_still_confirms_only_from_chain()
    {
        using var fixture = new Fixture();
        var prepared = (await fixture.Provider.PrepareValueTransferAsync(fixture.Intent("value:resubmit:0"), default)).Result!;
        await fixture.Provider.SubmitValueTransferAsync(prepared, default);
        fixture.Rpc.RejectSendWith = "Nonce too low. Expected nonce to be 1 but got 0.";

        await fixture.NewProvider().SubmitValueTransferAsync(prepared, default);

        fixture.Rpc.Rejected.Should().ContainSingle().Which.Should().Be(fixture.Rpc.Wires.Single(), "the re-submit re-sent the stored wire, not a new one");
        fixture.Store.Snapshot.Single().State.Should().Be("SubmissionAuthorized", "a refused re-broadcast records nothing as landed");
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).Result.Should().Be(ChainConfirmation.Unknown);
        fixture.Rpc.Finalized = true;
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).Result.Should().Be(ChainConfirmation.Confirmed);
    }

    public static TheoryData<string> UnvouchedRefusals => new() { "never-accepted", "lookup-error", "hash", "from", "nonce" };

    [Theory]
    [MemberData(nameof(UnvouchedRefusals))]
    public async Task A_refused_broadcast_the_node_cannot_vouch_for_by_exact_hash_still_requires_reconciliation(string variant)
    {
        using var fixture = new Fixture();
        var prepared = (await fixture.Provider.PrepareValueTransferAsync(fixture.Intent("value:unvouched:0"), default)).Result!;
        if (variant != "never-accepted") await fixture.Provider.SubmitValueTransferAsync(prepared, default);
        fixture.Rpc.RejectSendWith = "Nonce too low. Expected nonce to be 1 but got 0.";
        fixture.Rpc.LookupFailsWith = variant == "lookup-error" ? "header not found" : null;
        fixture.Rpc.LookupForgery = variant is "hash" or "from" or "nonce" ? variant : null;

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Provider.SubmitValueTransferAsync(prepared, default));

        refused.Message.Should().StartWith("Ethereum submission requires reconciliation: [-32000] Nonce too low");
        fixture.Rpc.Rejected.Should().ContainSingle();
    }

    [Fact]
    public async Task Wrong_semantics_or_noncanonical_receipt_never_confirms_returned_units()
    {
        using var fixture = new Fixture();
        var prepared = (await fixture.Provider.PrepareValueTransferAsync(fixture.Intent("value:evidence:0"), default)).Result!;
        await fixture.Provider.SubmitValueTransferAsync(prepared, default);
        fixture.Rpc.Finalized = true;
        fixture.Rpc.CorruptInput = true;
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).IsError.Should().BeTrue();
        fixture.Rpc.CorruptInput = false;
        fixture.Rpc.CorruptCanonicalHash = true;
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).Result.Should().Be(ChainConfirmation.Unknown);
        fixture.Rpc.CorruptCanonicalHash = false;
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).Result.Should().Be(ChainConfirmation.Confirmed);
    }

    [Fact]
    public async Task Missing_activation_checkpoint_refuses_before_signing_or_nonce_claim()
    {
        using var fixture = new Fixture(seedCheckpoint: false);
        var result = await fixture.Provider.PrepareValueTransferAsync(fixture.Intent("value:unactivated:0"), default);
        result.IsError.Should().BeTrue();
        fixture.Store.Snapshot.Should().BeEmpty();
        fixture.Rpc.Wires.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_original_authority_can_cancel_and_finalized_cancel_proves_no_original_effect()
    {
        using var fixture = new Fixture();
        var prepared = (await fixture.Provider.PrepareValueTransferAsync(fixture.Intent("value:cancel:0"), default)).Result!;
        var row = fixture.Store.Snapshot.Single();
        var forbidden = await fixture.Provider.RecoverPreparationAsync(row.Id, Guid.NewGuid(), Guid.Empty,
            false, true, "100000000000", "clear abandoned nonce", AzoaScopes.ValueSigningScopes, default);
        forbidden.IsError.Should().BeTrue();
        fixture.Rpc.Wires.Should().BeEmpty();
        var canceled = await fixture.Provider.RecoverPreparationAsync(row.Id, Guid.NewGuid(), Guid.Empty,
            true, true, "100000000000", "clear abandoned nonce", AzoaScopes.ValueSigningScopes, default);
        canceled.IsError.Should().BeFalse(canceled.Message);
        var replacement = Transaction1559Encoder.Current.Decode(Convert.FromHexString(fixture.Rpc.Wires.Single()[2..]));
        replacement.Nonce.Should().Be(BigInteger.Zero);
        replacement.Amount.Should().Be(BigInteger.Zero);
        EthereumAddress.AreSame(replacement.ReceiverAddress, fixture.Sender).Should().BeTrue();
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).Result.Should().Be(ChainConfirmation.Pending);
        await fixture.Provider.SubmitValueTransferAsync(prepared, default);
        fixture.Rpc.Wires.Should().ContainSingle();
        fixture.Rpc.Finalized = true;
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).Result.Should().Be(ChainConfirmation.FailedOnChain);
        await fixture.Provider.SubmitValueTransferAsync(prepared, default);
        fixture.Rpc.Wires.Should().ContainSingle();
    }

    [Fact]
    public async Task Owner_recovery_cannot_bypass_value_owner_submission_transition()
    {
        using var fixture = new Fixture();
        await fixture.Provider.PrepareValueTransferAsync(fixture.Intent("value:owner:0"), default);
        var result = await fixture.Provider.RecoverPreparationAsync(fixture.Store.Snapshot.Single().Id, Guid.NewGuid(), Guid.Empty,
            true, false, "0", "resume", AzoaScopes.ValueSigningScopes, default);
        result.IsError.Should().BeTrue();
        fixture.Rpc.Wires.Should().BeEmpty();
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly EthECKey _key = EthECKey.GenerateKey();
        private readonly IConfiguration _configuration;
        private readonly IKeyCustodyService _custody;
        public string Sender => _key.GetPublicAddress();
        public FakeEthereumPreparationStore Store { get; } = new();
        public Rpc Rpc { get; } = new();
        public EthereumProvider Provider { get; }
        public int SigningCalls { get; private set; }
        internal Func<Task>? BeforeUserSigning { get; set; }
        public Fixture(bool seedCheckpoint = true)
        {
            _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:Ethereum:PlatformPrivateKey"] = "0x" + Convert.ToHexString(_key.GetPrivateKeyAsBytes()),
                ["AZOA:Ethereum:AllocatorActivationEpoch"] = "prepared-fixture",
                ["AZOA:Ethereum:RpcRetryMaxAttempts"] = "0",
                ["AZOA:Ethereum:Nft:Networks:Devnet:ContractAddress"] = "0x2222222222222222222222222222222222222222",
                ["AZOA:Ethereum:Nft:Networks:Devnet:CredentialAuthorityAddress"] = Sender,
                ["AZOA:Ethereum:Nft:Networks:Devnet:RuntimeCodeHash"] = EthereumHex.Data(new Sha3Keccack().CalculateHash(new byte[] { 0x60, 0x00 })),
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://ethereum.test/", ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:ChainId"] = "31337",
            }).Build();
            var custody = new Mock<IKeyCustodyService>();
            custody.Setup(c => c.WithSigningKeyAsync(It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                .Returns(async (SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
                {
                    if (BeforeUserSigning is { } before) await before();
                    SigningCalls++;
                    return new AZOAResult<AZOAResult<byte[]>> { Result = await sign(_key.GetPrivateKeyAsBytes()) };
                });
            custody.Setup(c => c.WithPlatformSigningKeyAsync(It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                .Returns(async (bool _, SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
                    new AZOAResult<AZOAResult<byte[]>> { Result = await sign(_key.GetPrivateKeyAsBytes()) });
            _custody = custody.Object;
            Rpc.Authority = Sender;
            if (seedCheckpoint) Store.SeedCheckpoint("eip155:31337", Sender, "prepared-fixture");
            Provider = NewProvider();
        }
        public EthereumProvider NewProvider() => new(_configuration, NullLogger<EthereumProvider>.Instance,
            new TransactionSignerFactory(new ITransactionSigner[] { new EthereumTransactionSigner() }), _custody, null, Rpc, Store);
        public ValueTransferIntent Intent(string key) => new("Ethereum", ChainNetwork.Devnet, "ETH", Sender,
            "0x1111111111111111111111111111111111111111", "1", SigningContext.PlatformFor("Ethereum"), key);
        public void Dispose() => Rpc.Dispose();
    }

    internal sealed class Rpc : HttpMessageHandler
    {
        public List<string> Wires { get; } = [];
        public string Authority { get; set; } = "";
        public bool FailNextSend { get; set; }
        /// <summary>Wires the node refused with <see cref="RejectSendWith"/>; never accepted, so never findable by hash.</summary>
        public List<string> Rejected { get; } = [];
        public string? RejectSendWith { get; set; }
        public string? LookupFailsWith { get; set; }
        /// <summary>Which field of the by-hash answer to forge: "hash", "from" or "nonce".</summary>
        public string? LookupForgery { get; set; }
        public bool Finalized { get; set; }
        public string ReceiptStatus { get; set; } = "0x1";
        public string? ReceiptGasUsed { get; set; } = "0x5208";
        public bool CorruptInput { get; set; }
        public bool CorruptCanonicalHash { get; set; }
        public string GasEstimate { get; set; } = "0x5208";
        public string RuntimeCode { get; set; } = "0x6000";
        private static string BlockHash => "0x" + new string('b', 64);
        private static string Hash(string wire) => EthereumHex.Data(new Sha3Keccack().CalculateHash(Convert.FromHexString(wire[2..])));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var method = body.RootElement.GetProperty("method").GetString();
            var parameters = body.RootElement.GetProperty("params");
            object? result;
            switch (method)
            {
                case "eth_chainId": result = "0x7a69"; break;
                case "eth_getCode": result = RuntimeCode; break;
                case "eth_call": result = "0x" + Authority[2..].ToLowerInvariant().PadLeft(64, '0'); break;
                case "eth_getTransactionCount": result = "0x0"; break;
                case "eth_maxPriorityFeePerGas": result = "0x3b9aca00"; break;
                case "eth_estimateGas": result = GasEstimate; break;
                case "eth_getBlockByNumber":
                    result = new { number = parameters[0].GetString() == "finalized" && !Finalized ? "0x9" : "0xa",
                        hash = CorruptCanonicalHash ? "0x" + new string('c', 64) : BlockHash, baseFeePerGas = "0x3b9aca00" }; break;
                case "eth_sendRawTransaction":
                    var wire = parameters[0].GetString()!;
                    if (RejectSendWith is { } refusal)
                    {
                        Rejected.Add(wire);
                        return RpcError(body, refusal);
                    }
                    Wires.Add(wire);
                    if (FailNextSend) { FailNextSend = false; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
                    result = Hash(wire); break;
                case "eth_getTransactionReceipt":
                    var receiptHash = parameters[0].GetString()!;
                    result = Wires.Any(w => Hash(w) == receiptHash) ? new { transactionHash = receiptHash, blockHash = BlockHash, blockNumber = "0xa", status = ReceiptStatus, gasUsed = ReceiptGasUsed, effectiveGasPrice = "0x3b9aca00" } : null;
                    break;
                case "eth_getTransactionByHash":
                    if (LookupFailsWith is { } lookupFailure) return RpcError(body, lookupFailure);
                    var raw = Wires.FirstOrDefault(w => Hash(w) == parameters[0].GetString());
                    if (raw is null) { result = null; break; }
                    var tx = Transaction1559Encoder.Current.Decode(Convert.FromHexString(raw[2..]));
                    result = new { hash = LookupForgery == "hash" ? "0x" + new string('d', 64) : Hash(raw),
                        from = LookupForgery == "from" ? "0x3333333333333333333333333333333333333333" : TransactionVerificationAndRecovery.GetSenderAddress(tx),
                        to = tx.ReceiverAddress, nonce = EthereumHex.Quantity((tx.Nonce ?? 0) + (LookupForgery == "nonce" ? 1 : 0)), value = EthereumHex.Quantity(tx.Amount ?? 0),
                        input = CorruptInput ? "0x1234" : string.IsNullOrEmpty(tx.Data) ? "0x" : tx.Data.StartsWith("0x", StringComparison.Ordinal) ? tx.Data : "0x" + tx.Data,
                        chainId = EthereumHex.Quantity(tx.ChainId) };
                    break;
                default: throw new InvalidOperationException("Unexpected RPC method: " + method);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id = body.RootElement.GetProperty("id").GetInt32(), result }), Encoding.UTF8, "application/json") };
        }

        /// <summary>A JSON-RPC error object on HTTP 200, the way Hardhat and geth refuse a call.</summary>
        private static HttpResponseMessage RpcError(JsonDocument body, string message)
            => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id = body.RootElement.GetProperty("id").GetInt32(), error = new { code = -32000, message } }),
                Encoding.UTF8, "application/json") };
    }
}
