// SPDX-License-Identifier: UNLICENSED

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand.Algod.Model.Transactions;
using AlgoEncoder = Algorand.Utils.Encoder;
using AlgoSignedTransaction = Algorand.Algod.Model.Transactions.SignedTransaction;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Providers.Blockchain.ClientSigning;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Services.Blockchain.ClientSigning;
using AZOA.WebAPI.Services.Reconciliation;
using AZOA.WebAPI.Tests.TestSupport;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// D-3 flows: an External-wallet transfer is planned as exact unsigned bytes, only the owner's exact signature over them is
/// broadcast, and the row reaches a chain-proven terminal state (or an operator-visible park) with its idempotency claim settled.
/// Real providers over in-process RPC; only the chain endpoints are faked.
/// </summary>
public sealed class ClientSignedTransferFlowTests
{
    private const string Asset = "123";
    private readonly InMemoryOperationLog _log = new();
    private readonly FakeIdempotencyStore _idempotency = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly SwitchableValueGate _valueGate = new();
    private readonly FakeEthereumPreparationStore _nonces = new();

    // ── Algorand ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Algorand_owner_signature_over_the_saved_bytes_is_broadcast_once_and_confirms_from_chain_truth()
    {
        var sender = new AlgoAccount();
        using var chain = new AlgorandChain();
        var (manager, service) = Algorand(chain, sender.Address.EncodeAsString());

        var executed = await manager.ExecuteAsync(Transfer(sender.Address.EncodeAsString(), new AlgoAccount().Address.EncodeAsString(), Asset));

        executed.Result!.Status.Should().Be(OperationStatus.AwaitingSignature);
        var operationId = executed.Result.Id;
        var claim = _idempotency.Keys.Single();
        (await _idempotency.GetAsync(claim, default))!.State.Should().Be(IdempotencyState.InProgress, "nothing was broadcast yet");
        var plan = (await service.GetPlanAsync(operationId, _owner, null, default)).Result!;
        plan.Format.Should().Be(ClientSigningFormats.Algorand);
        var unsigned = Convert.FromBase64String(plan.UnsignedPayloadBase64);

        var stranger = await service.SubmitAsync(operationId, _owner, null, AlgorandExactWire.Sign(unsigned, new AlgoAccount()), default);
        stranger.Code.Should().Be(ClientSigningCodes.SignatureMismatch, "only the saved sender's key authorizes the saved bytes");
        var signed = AlgorandExactWire.Sign(unsigned, sender);
        (await service.SubmitAsync(operationId, Guid.NewGuid(), null, signed, default)).Code.Should().Be(AzoaErrorCodes.NotFound);
        chain.Broadcasts.Should().BeEmpty();

        var submitted = await service.SubmitAsync(operationId, _owner, null, signed, default);
        var replay = await service.SubmitAsync(operationId, _owner, null, signed, default);

        submitted.Result!.Status.Should().Be(OperationStatus.PendingConfirmation);
        submitted.Result.Parameters[ClientSigningParameters.TransactionHash].Should().Be(plan.PredictedTransactionId);
        replay.IsError.Should().BeFalse("re-submitting the identical signed bytes is idempotent");
        chain.Broadcasts.Should().OnlyContain(wire => wire.SequenceEqual(Convert.FromBase64String(signed)),
            "only the exact persisted bytes ever reach the node");
        (await service.SubmitAsync(operationId, _owner, null, AlgorandExactWire.Sign(unsigned, new AlgoAccount()), default))
            .Code.Should().Be(AzoaErrorCodes.Conflict, "a second, different signature never replaces the persisted one");

        chain.Indexed = true;
        (await service.ReconcileAsync(_log.Row(operationId), DateTime.UtcNow, default)).Should().Be(ClientSigningReconcileOutcome.Completed);

        _log.Row(operationId).Status.Should().Be(OperationStatus.Completed);
        var settled = (await _idempotency.GetAsync(claim, default))!;
        settled.State.Should().Be(IdempotencyState.Completed);
        settled.ResultPayload.Should().Be(plan.PredictedTransactionId);
    }

    [Fact]
    public async Task Algorand_plan_never_signed_closes_as_abandoned_once_its_validity_window_provably_passed()
    {
        var sender = new AlgoAccount();
        using var chain = new AlgorandChain();
        var (manager, service) = Algorand(chain, sender.Address.EncodeAsString());
        var operationId = (await manager.ExecuteAsync(Transfer(sender.Address.EncodeAsString(), new AlgoAccount().Address.EncodeAsString(), Asset))).Result!.Id;

        (await service.ReconcileAsync(_log.Row(operationId), DateTime.UtcNow, default)).Should().Be(ClientSigningReconcileOutcome.Unchanged,
            "inside the validity window the signer may still broadcast the bytes themselves");
        chain.Round = 2000;

        (await service.ReconcileAsync(_log.Row(operationId), DateTime.UtcNow, default)).Should().Be(ClientSigningReconcileOutcome.Failed);

        var row = _log.Row(operationId);
        row.Status.Should().Be(OperationStatus.Failed);
        row.Parameters[ClientSigningParameters.Outcome].Should().Be("Abandoned");
        (await _idempotency.GetAsync(_idempotency.Keys.Single(), default))!.State.Should().Be(IdempotencyState.Failed,
            "the claim leaves InProgress so a retry is not answered 'still in progress' forever");
        var plan = ClientSignedOperationService.ReadPlan(row)!;
        (await service.SubmitAsync(operationId, _owner, null, AlgorandExactWire.Sign(Convert.FromBase64String(plan.UnsignedPayloadBase64), sender), default))
            .Code.Should().Be(AzoaErrorCodes.Conflict);
        chain.Broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_driven_transfer_from_an_external_wallet_is_never_planned_for_client_signing()
    {
        var sender = new AlgoAccount();
        using var chain = new AlgorandChain();
        var (manager, _) = Algorand(chain, sender.Address.EncodeAsString());
        var operation = Transfer(sender.Address.EncodeAsString(), new AlgoAccount().Address.EncodeAsString(), Asset);
        operation.ActingTenantId = Guid.NewGuid();
        operation.SigningScope = AzoaScopes.TransferSign;

        var executed = await manager.ExecuteAsync(operation);

        executed.Result!.Status.Should().Be(OperationStatus.Failed, "a tenant-driven transfer goes to custody, which never signs for an External wallet");
        executed.Result.Parameters.Should().NotContainKey(ClientSigningParameters.Plan);
    }

    // ── Ethereum ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("superseded", OperationStatus.Failed)]
    [InlineData("signer-broadcast-own-bytes", OperationStatus.Completed)]
    public async Task Ethereum_unsigned_plan_parks_for_an_operator_who_settles_only_what_the_chain_proves(string chainFact, string expected)
    {
        var key = EthECKey.GenerateKey();
        var sender = key.GetPublicAddress();
        using var chain = new EthereumChain(sender);
        var (manager, service) = Ethereum(chain, sender);
        var operationId = (await manager.ExecuteAsync(Transfer(sender, "0x" + new string('b', 40), "ETH"))).Result!.Id;
        var plan = ClientSignedOperationService.ReadPlan(_log.Row(operationId))!;
        plan.ValidThroughHeight.Should().BeNull("an Ethereum signature never expires");

        (await service.ReconcileAsync(_log.Row(operationId), DateTime.UtcNow, default)).Should().Be(ClientSigningReconcileOutcome.Parked);
        _log.Row(operationId).Status.Should().Be(ClientSigningStatus.NeedsOperator);
        (await service.ListStuckAsync(10, default)).Should().ContainSingle(view => view.OperationId == operationId && !view.Submitted);
        (await service.ResolveAsync(operationId, Guid.NewGuid(), new("customer called", null), default))
            .Code.Should().Be(AzoaErrorCodes.Conflict, "an unconsumed nonce proves nothing either way");

        string evidence;
        if (chainFact == "superseded")
            evidence = chain.MineForeign(nonce: 7);
        else
            evidence = chain.MineOwn(Sign(key, Convert.FromBase64String(plan.UnsignedPayloadBase64)));
        chain.FinalizedNonce = 8;

        var resolved = await service.ResolveAsync(operationId, Guid.NewGuid(), new("verified against the explorer", evidence), default);

        resolved.IsError.Should().BeFalse(resolved.Message);
        var row = _log.Row(operationId);
        row.Status.Should().Be(expected);
        row.Parameters.Should().ContainKey(ClientSigningParameters.OperatorResolution);
        (await _idempotency.GetAsync(_idempotency.Keys.Single(), default))!.State
            .Should().Be(expected == OperationStatus.Completed ? IdempotencyState.Completed : IdempotencyState.Failed);
        chain.Broadcasts.Should().BeEmpty("resolution observes; it never signs or sends");
    }

    [Fact]
    public async Task Ethereum_submitted_bytes_whose_nonce_another_transaction_finalized_close_as_superseded_only_once_attributed()
    {
        var key = EthECKey.GenerateKey();
        var sender = key.GetPublicAddress();
        using var chain = new EthereumChain(sender) { AcceptBroadcast = false };
        var (manager, service) = Ethereum(chain, sender);
        var operationId = (await manager.ExecuteAsync(Transfer(sender, "0x" + new string('b', 40), "ETH"))).Result!.Id;
        var plan = ClientSignedOperationService.ReadPlan(_log.Row(operationId))!;

        (await service.SubmitAsync(operationId, _owner, null, Convert.ToBase64String(Sign(EthECKey.GenerateKey(),
            Convert.FromBase64String(plan.UnsignedPayloadBase64))), default)).Code.Should().Be(ClientSigningCodes.SignatureMismatch);
        var signed = Convert.ToBase64String(Sign(key, Convert.FromBase64String(plan.UnsignedPayloadBase64)));
        var submitted = await service.SubmitAsync(operationId, _owner, null, signed, default);
        submitted.Result!.Status.Should().Be(OperationStatus.PendingConfirmation, "the signed bytes are persisted even when the node refuses them");
        (await service.ReconcileAsync(_log.Row(operationId), DateTime.UtcNow, default)).Should().Be(ClientSigningReconcileOutcome.Unchanged);
        (await service.ListStuckAsync(10, default)).Should().ContainSingle(view => view.OperationId == operationId && view.Submitted,
            "a dropped or underpriced submission ages into the operator console instead of waiting invisibly");
        var foreign = chain.MineForeign(nonce: 7, mined: false);
        chain.FinalizedNonce = 8;

        (await service.ReconcileAsync(_log.Row(operationId), DateTime.UtcNow, default)).Should().Be(ClientSigningReconcileOutcome.Parked,
            "an unattributed consumer might be the plan itself; failing it could invite a double payment");
        (await service.ResolveAsync(operationId, Guid.NewGuid(), new("a node shows the replacement", foreign), default))
            .Code.Should().Be(AzoaErrorCodes.Conflict, "a mempool-only consumer from a lagging node proves nothing");
        chain.Mine(foreign);
        var resolved = await service.ResolveAsync(operationId, Guid.NewGuid(), new("explorer shows the replacement", foreign), default);

        resolved.IsError.Should().BeFalse(resolved.Message);
        _log.Row(operationId).Parameters[ClientSigningParameters.Outcome].Should().Be("Superseded");
    }

    [Fact]
    public async Task Ethereum_identical_concurrent_transfers_from_one_wallet_get_their_own_nonces_and_one_transaction_never_settles_both()
    {
        var key = EthECKey.GenerateKey();
        var sender = key.GetPublicAddress();
        using var chain = new EthereumChain(sender);
        var (manager, service) = Ethereum(chain, sender);
        var recipient = "0x" + new string('b', 40);
        var first = (await manager.ExecuteAsync(Transfer(sender, recipient, "ETH", parentKey: "payout-1"))).Result!.Id;
        var second = (await manager.ExecuteAsync(Transfer(sender, recipient, "ETH", parentKey: "payout-2"))).Result!.Id;
        var firstPlan = ClientSignedOperationService.ReadPlan(_log.Row(first))!;
        var secondPlan = ClientSignedOperationService.ReadPlan(_log.Row(second))!;

        (firstPlan.Nonce, secondPlan.Nonce).Should().Be(("7", "8"), "each open plan reserves the signer's next nonce");
        firstPlan.UnsignedPayloadBase64.Should().NotBe(secondPlan.UnsignedPayloadBase64);
        var firstSigned = Convert.ToBase64String(Sign(key, Convert.FromBase64String(firstPlan.UnsignedPayloadBase64)));
        var secondSigned = Convert.ToBase64String(Sign(key, Convert.FromBase64String(secondPlan.UnsignedPayloadBase64)));
        secondSigned.Should().NotBe(firstSigned, "distinct nonces make distinct signed bytes even for an identical transfer");
        var firstHash = (await service.SubmitAsync(first, _owner, null, firstSigned, default)).Result!.Parameters[ClientSigningParameters.TransactionHash];

        chain.MineOwn(Convert.FromBase64String(firstSigned));
        (await service.ReconcileAsync(_log.Row(first), DateTime.UtcNow, default)).Should().Be(ClientSigningReconcileOutcome.Completed);
        // The second signer never submitted; an operator attributing the first transfer's hash to it must be refused.
        (await service.ResolveAsync(second, Guid.NewGuid(), new("operator pastes the first transfer's hash", firstHash), default))
            .IsError.Should().BeTrue("a hash attributes only to the row whose reserved nonce it consumed");

        _log.Row(second).Status.Should().NotBe(OperationStatus.Completed);
        var claims = await Task.WhenAll(_idempotency.Keys.Select(claim => _idempotency.GetAsync(claim, default)));
        claims.Select(claim => claim!.State).Should().BeEquivalentTo([IdempotencyState.Completed, IdempotencyState.InProgress],
            "one transfer happened, so exactly one claim completes");
    }

    [Fact]
    public async Task A_terminal_row_whose_claim_settlement_was_lost_repairs_on_the_next_reconcile()
    {
        var key = EthECKey.GenerateKey();
        var sender = key.GetPublicAddress();
        using var chain = new EthereumChain(sender);
        var (manager, service) = Ethereum(chain, sender);
        var operationId = (await manager.ExecuteAsync(Transfer(sender, "0x" + new string('b', 40), "ETH"))).Result!.Id;
        var row = _log.Row(operationId);
        // The crash window: the terminal status committed and the process died before the claim was settled.
        var parameters = new Dictionary<string, string>(row.Parameters) { [ClientSigningParameters.TransactionHash] = "0x" + new string('a', 64) };
        (await _log.TryTransitionAsync(operationId, row.Status, OperationStatus.Completed, parameters, DateTimeOffset.UtcNow)).Should().BeTrue();

        await service.ReconcileAsync(_log.Row(operationId), DateTime.UtcNow, default);

        (await _idempotency.GetAsync(_idempotency.Keys.Single(), default))!.State.Should().Be(IdempotencyState.Completed,
            "a duplicate request must not be answered as still in progress forever");
    }

    [Fact]
    public async Task A_paused_account_cannot_submit_a_signed_transfer_and_nothing_is_broadcast()
    {
        var key = EthECKey.GenerateKey();
        var sender = key.GetPublicAddress();
        using var chain = new EthereumChain(sender);
        var (manager, service) = Ethereum(chain, sender);
        var operationId = (await manager.ExecuteAsync(Transfer(sender, "0x" + new string('b', 40), "ETH"))).Result!.Id;
        var plan = ClientSignedOperationService.ReadPlan(_log.Row(operationId))!;
        _valueGate.Paused = true;

        var refused = await service.SubmitAsync(operationId, _owner, null,
            Convert.ToBase64String(Sign(key, Convert.FromBase64String(plan.UnsignedPayloadBase64))), default);

        refused.Code.Should().Be(AzoaErrorCodes.Forbidden);
        _log.Row(operationId).Status.Should().Be(OperationStatus.AwaitingSignature);
        chain.Broadcasts.Should().BeEmpty();
    }

    // ── Wiring ────────────────────────────────────────────────────────────────

    private BlockchainOperation Transfer(string sender, string recipient, string asset, string? parentKey = null)
    {
        var operation = new BlockchainOperation
        {
            OperationType = "Transfer", UserId = _owner, WalletId = WalletId, RecipientAddress = recipient,
            Parameters = new() { ["WalletAddress"] = sender, ["SourceTokenId"] = asset, ["Amount"] = "500", ["ChainNetwork"] = "Devnet" },
        };
        if (parentKey is not null) operation.Parameters["IdempotencyKey"] = parentKey;
        return operation;
    }

    private Guid WalletId { get; } = Guid.NewGuid();

    private (BlockchainOperationManager, ClientSignedOperationService) Algorand(AlgorandChain chain, string sender)
    {
        var config = Config("Algorand", new() { ["Blockchain:Chains:0:Devnet:IndexerUrl"] = "http://indexer.test/" });
        return Wire("Algorand", sender, config, () => new AlgorandProvider(config, NullLogger<AlgorandProvider>.Instance,
            signerFactory: null, keyService: null, custodyService: null, custodyScopeFactory: null, faucet: null, httpMessageHandler: chain));
    }

    private (BlockchainOperationManager, ClientSignedOperationService) Ethereum(EthereumChain chain, string sender)
    {
        var config = Config("Ethereum", new() { ["Blockchain:Chains:0:Devnet:ChainId"] = EthereumChain.ChainId.ToString(),
            ["AZOA:Ethereum:RpcRetryMaxAttempts"] = "0" });
        return Wire("Ethereum", sender, config, () => new EthereumProvider(config, NullLogger<EthereumProvider>.Instance,
            signerFactory: null, custodyService: null, custodyScopeFactory: null, httpMessageHandler: chain, preparationStore: _nonces));
    }

    private (BlockchainOperationManager, ClientSignedOperationService) Wire(string chainType, string sender, IConfiguration config,
        Func<IBlockchainProvider> provider)
    {
        var factory = new BlockchainProviderFactory([new BlockchainProviderRegistration(chainType, provider)], config);
        var wallets = new Mock<IWalletStore>();
        wallets.Setup(store => store.GetByIdAsync(WalletId, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IWallet>.Success(
            new Wallet { Id = WalletId, UserId = _owner, ChainType = chainType, Address = sender, WalletType = WalletType.External }));
        var manager = new BlockchainOperationManager(_log, factory, _idempotency, wallets: wallets.Object);
        var service = new ClientSignedOperationService(_log, _log, factory, _idempotency,
            Options.Create(new ReconciliationOptions { ClientSignatureTtlSeconds = 0 }), NullLogger<ClientSignedOperationService>.Instance, _valueGate);
        return (manager, service);
    }

    private static IConfiguration Config(string chainType, Dictionary<string, string?> extra) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Blockchain:DefaultChain"] = chainType, ["Blockchain:DefaultNetwork"] = "Devnet",
            ["Blockchain:Chains:0:ChainType"] = chainType, ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://node.test/", ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "2000",
        }.Concat(extra).ToDictionary())
        .Build();

    private static byte[] Sign(EthECKey key, byte[] unsigned)
    {
        using var material = new SigningKeyMaterial(key.GetPrivateKeyAsBytes());
        return new EthereumTransactionSigner().Sign(unsigned, material).Result!;
    }

    private static HttpResponseMessage Json(object? body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    /// <summary>Algod + indexer: one fixed genesis, a movable round, and an index that holds a broadcast only once told to.</summary>
    private sealed class AlgorandChain : HttpMessageHandler
    {
        internal long Round { get; set; } = 100;
        internal bool Indexed { get; set; }
        internal List<byte[]> Broadcasts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "indexer.test")
            {
                var id = Uri.UnescapeDataString(request.RequestUri.Query.Split('=')[1]);
                var sent = Broadcasts.Select(wire => AlgoEncoder.DecodeFromMsgPack<AlgoSignedTransaction>(wire).Tx)
                    .Where(tx => Indexed && tx.TxID() == id).Cast<AssetTransferTransaction>().Take(1).ToArray(); // one ledger entry per id
                return Json(new { current_round = Round + 1, transactions = sent.Select(tx => new Dictionary<string, object?>
                {
                    ["id"] = tx.TxID(), ["sender"] = tx.Sender.EncodeAsString(), ["tx-type"] = "axfer", ["fee"] = tx.Fee ?? 0UL,
                    ["confirmed-round"] = tx.FirstValid!.Value + 1,
                    ["asset-transfer-transaction"] = new Dictionary<string, object?>
                    {
                        ["asset-id"] = tx.XferAsset, ["amount"] = tx.AssetAmount, ["receiver"] = tx.AssetReceiver.EncodeAsString(),
                    },
                }) }.ToIndexerShape());
            }
            if (path == "/v2/transactions/params")
                return Json(new Dictionary<string, object?> { ["fee"] = 0, ["min-fee"] = 1000, ["last-round"] = Round,
                    ["genesis-id"] = "devnet-v1.0", ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=" });
            if (path == "/v2/status") return Json(new Dictionary<string, object?> { ["last-round"] = Round });
            if (path == "/v2/transactions" && request.Method == HttpMethod.Post)
            {
                var wire = await request.Content!.ReadAsByteArrayAsync(ct);
                Broadcasts.Add(wire);
                return Json(new { txId = AlgoEncoder.DecodeFromMsgPack<AlgoSignedTransaction>(wire).Tx.TxID() });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>JSON-RPC node with a pending nonce of 7, a movable finalized nonce, and mined transactions keyed by hash.</summary>
    private sealed class EthereumChain(string sender) : HttpMessageHandler
    {
        internal const long ChainId = 11155111;
        private readonly Dictionary<string, JsonObject> _transactions = new(StringComparer.OrdinalIgnoreCase);
        internal long FinalizedNonce { get; set; } = 7;
        internal bool AcceptBroadcast { get; init; } = true;
        internal List<string> Broadcasts { get; } = new();

        /// <summary>A different transaction from the signer at <paramref name="nonce"/>; <paramref name="mined"/> false leaves it mempool-only.</summary>
        internal string MineForeign(long nonce, bool mined = true)
        {
            var hash = "0x" + new string('f', 64);
            _transactions[hash] = new JsonObject { ["hash"] = hash, ["from"] = sender, ["nonce"] = Hex(nonce),
                ["to"] = "0x" + new string('c', 40), ["value"] = "0x1", ["input"] = "0x", ["chainId"] = Hex(ChainId) };
            if (mined) _transactions[hash]!["mined"] = true;
            return hash;
        }

        internal void Mine(string hash) => _transactions[hash]["mined"] = true;

        internal string MineOwn(byte[] wire)
        {
            var tx = Transaction1559Encoder.Current.Decode(wire);
            var hash = "0x" + Convert.ToHexStringLower(new Sha3Keccack().CalculateHash(wire));
            _transactions[hash] = new JsonObject { ["hash"] = hash, ["from"] = TransactionVerificationAndRecovery.GetSenderAddress(tx),
                ["nonce"] = Hex((long)tx.Nonce!.Value), ["to"] = tx.ReceiverAddress, ["value"] = "0x" + tx.Amount!.Value.ToString("x").TrimStart('0'),
                ["input"] = string.IsNullOrEmpty(tx.Data) ? "0x" : tx.Data, ["chainId"] = Hex(ChainId), ["mined"] = true };
            return hash;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var id = body.RootElement.GetProperty("id").GetInt32();
            var method = body.RootElement.GetProperty("method").GetString();
            var parameters = body.RootElement.GetProperty("params");
            object? result = method switch
            {
                "eth_chainId" => Hex(ChainId),
                "eth_getBlockByNumber" => parameters[0].GetString() == "finalized" ? new { number = "0x20", hash = "0x" + new string('2', 64) }
                    : new { number = "0x10", hash = "0x" + new string('1', 64), baseFeePerGas = "0x3b9aca00" },
                "eth_maxPriorityFeePerGas" => "0x3b9aca00",
                "eth_estimateGas" => "0x5208",
                "eth_getTransactionCount" => parameters[1].GetString() == "finalized" ? Hex(FinalizedNonce) : "0x7",
                "eth_getTransactionByHash" => _transactions.GetValueOrDefault(parameters[0].GetString()!),
                "eth_getTransactionReceipt" => _transactions.TryGetValue(parameters[0].GetString()!, out var mined) && mined["mined"] is not null
                    ? new { transactionHash = parameters[0].GetString(), blockNumber = "0x10", blockHash = "0x" + new string('1', 64), status = "0x1" }
                    : null,
                "eth_sendRawTransaction" => Broadcast(parameters[0].GetString()!),
                _ => throw new InvalidOperationException("Unexpected RPC method " + method),
            };
            if (method == "eth_sendRawTransaction" && !AcceptBroadcast)
                return Json(new { jsonrpc = "2.0", id, error = new { code = -32000, message = "nonce too low" } });
            return Json(new { jsonrpc = "2.0", id, result });
        }

        private string Broadcast(string raw)
        {
            Broadcasts.Add(raw);
            return "0x" + Convert.ToHexStringLower(new Sha3Keccack().CalculateHash(Convert.FromHexString(raw[2..])));
        }

        private static string Hex(long value) => "0x" + value.ToString("x");
    }
}

internal static class IndexerShapeExtensions
{
    /// <summary>Renames the anonymous <c>current_round</c> member to the indexer's hyphenated field.</summary>
    internal static JsonNode ToIndexerShape(this object body)
    {
        var node = JsonSerializer.SerializeToNode(body)!.AsObject();
        node["current-round"] = node["current_round"]!.DeepClone();
        node.Remove("current_round");
        return node;
    }
}
