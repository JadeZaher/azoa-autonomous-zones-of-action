// SPDX-License-Identifier: UNLICENSED

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Blockchain.ClientSigning;
using AZOA.WebAPI.Services.Reconciliation;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// RV-L5-02: a Solana client-signed plan settles "never landed" only from a complete finalized-ancestry scan past its blockhash
/// expiry. A null signature status or a node without ledger history never fails the row. Real provider; only the RPC node is faked.
/// </summary>
public sealed class ClientSignedSolanaFlowTests
{
    private const long ContextSlot = 100;
    private const long LastValidHeight = 52;
    private readonly InMemoryOperationLog _log = new();
    private readonly FakeIdempotencyStore _idempotency = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _walletId = Guid.NewGuid();

    [Theory]
    [InlineData("history-unavailable", ClientSigningStatus.NeedsOperator, IdempotencyState.InProgress)]
    [InlineData("never-landed", OperationStatus.Failed, IdempotencyState.Failed)]
    [InlineData("landed-but-status-lost", OperationStatus.Completed, IdempotencyState.Completed)]
    public async Task An_expired_plan_settles_only_from_finalized_history(string chainFact, string expectedStatus, IdempotencyState expectedClaim)
    {
        var signer = new Account();
        using var chain = new SolanaChain { History = chainFact != "history-unavailable" };
        var (manager, service) = Wire(chain, signer.PublicKey.Key);
        var operationId = (await manager.ExecuteAsync(Transfer(signer.PublicKey.Key, new Account().PublicKey.Key))).Result!.Id;
        var plan = ClientSignedOperationService.ReadPlan(_log.Row(operationId))!;
        if (chainFact == "landed-but-status-lost")
        {
            var message = Convert.FromBase64String(plan.UnsignedPayloadBase64);
            var wire = SolanaMessageEnvelope.Populate(message, [signer.Sign(message)]);
            var submitted = await service.SubmitAsync(operationId, _owner, null, Convert.ToBase64String(wire), default);
            submitted.Result!.Status.Should().Be(OperationStatus.PendingConfirmation, submitted.Message);
            chain.Land(wire);
        }

        chain.Height = LastValidHeight + 10;
        await service.ReconcileAsync(_log.Row(operationId), DateTime.UtcNow, default);

        _log.Row(operationId).Status.Should().Be(expectedStatus);
        (await _idempotency.GetAsync(_idempotency.Keys.Single(), default))!.State.Should().Be(expectedClaim,
            "only chain-proven absence may release the claim for a retry, and a landed transfer completes it");
    }

    private BlockchainOperation Transfer(string sender, string recipient) => new()
    {
        OperationType = "Transfer", UserId = _owner, WalletId = _walletId, RecipientAddress = recipient,
        Parameters = new() { ["WalletAddress"] = sender, ["SourceTokenId"] = "SOL", ["Amount"] = "500", ["ChainNetwork"] = "Devnet" },
    };

    private (BlockchainOperationManager, ClientSignedOperationService) Wire(SolanaChain chain, string sender)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Blockchain:DefaultChain"] = "Solana", ["Blockchain:DefaultNetwork"] = "Devnet",
            ["Blockchain:Chains:0:ChainType"] = "Solana", ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://node.test/", ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "2000",
            ["AZOA:Solana:RpcRetryMaxAttempts"] = "0",
        }).Build();
        var factory = new BlockchainProviderFactory([new BlockchainProviderRegistration("Solana", () => new SolanaProvider(config,
            NullLogger<SolanaProvider>.Instance, signerFactory: null, custodyService: null, custodyScopeFactory: null, httpMessageHandler: chain))], config);
        var wallets = new Mock<IWalletStore>();
        wallets.Setup(store => store.GetByIdAsync(_walletId, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IWallet>.Success(
            new AZOA.WebAPI.Models.Wallet { Id = _walletId, UserId = _owner, ChainType = "Solana", Address = sender, WalletType = WalletType.External }));
        var manager = new BlockchainOperationManager(_log, factory, _idempotency, wallets: wallets.Object);
        var service = new ClientSignedOperationService(_log, _log, factory, _idempotency,
            Options.Create(new ReconciliationOptions { ClientSignatureTtlSeconds = 0 }), NullLogger<ClientSignedOperationService>.Instance,
            new ApprovedRealValueKycGate());
        return (manager, service);
    }

    /// <summary>
    /// JSON-RPC cluster: finalized blocks 100(h50) → 101(h51) → 103(h52) → 104(h53), a movable finalized height, and either full
    /// ledger history or none (getBlock/getBlocksWithLimit answer "missing in long-term storage"). Signature statuses are always
    /// null, as from a node past its status cache.
    /// </summary>
    private sealed class SolanaChain : HttpMessageHandler
    {
        private static readonly (long Slot, long Height, long Parent)[] Blocks = [(100, 50, 99), (101, 51, 100), (103, 52, 101), (104, 53, 103)];
        private readonly List<string> _landed = [];
        internal bool History { get; init; }
        internal long Height { get; set; } = 50;

        internal void Land(byte[] wire) => _landed.Add(Convert.ToBase64String(wire));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var id = body.RootElement.GetProperty("id").GetInt32();
            var method = body.RootElement.GetProperty("method").GetString();
            var parameters = body.RootElement.GetProperty("params");
            if (!History && method is "getBlock" or "getBlocksWithLimit" or "getTransaction")
                return Json(new { jsonrpc = "2.0", id, error = new { code = -32009, message = "Slot was skipped, or missing in long-term storage" } });
            object? result = method switch
            {
                "getGenesisHash" => "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG",
                "getLatestBlockhash" => new { context = new { slot = ContextSlot }, value = new { blockhash = Hash(99), lastValidBlockHeight = LastValidHeight } },
                "getBlockHeight" => Height,
                "getSignatureStatuses" => new { value = new object?[] { null } },
                "sendTransaction" => Signature(Convert.FromBase64String(parameters[0].GetString()!)),
                "getBlocksWithLimit" => Blocks.Select(block => block.Slot).Where(slot => slot >= parameters[0].GetInt64())
                    .Take(parameters[1].GetInt32()).ToArray(),
                "getBlock" => Block(parameters[0].GetInt64()),
                "getTransaction" => _landed.FirstOrDefault(wire => Signature(Convert.FromBase64String(wire)) == parameters[0].GetString()) is { } found
                    ? new { transaction = new[] { found, "base64" }, meta = new { err = (object?)null } } : null,
                _ => throw new InvalidOperationException("Unexpected RPC method " + method),
            };
            return Json(new { jsonrpc = "2.0", id, result });
        }

        private object Block(long slot)
        {
            var (_, height, parent) = Blocks.Single(block => block.Slot == slot);
            var transactions = slot == 103 ? _landed.Select(wire => new { transaction = new[] { wire, "base64" } }).ToArray() : [];
            return new { blockhash = Hash(slot), previousBlockhash = Hash(parent), parentSlot = parent, blockHeight = height, transactions };
        }

        private static string Hash(long slot) => Encoders.Base58.EncodeData(Enumerable.Repeat((byte)slot, 32).ToArray());
        private static string Signature(byte[] wire) => Encoders.Base58.EncodeData(wire.AsSpan(1, 64).ToArray());

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }
}
