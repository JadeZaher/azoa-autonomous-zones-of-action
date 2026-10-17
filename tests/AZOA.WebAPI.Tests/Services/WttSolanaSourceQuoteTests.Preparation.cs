using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttSolanaSourceQuoteTests
{
    [Fact]
    public async Task LostSignedCommitResponseReplaysExactThreeSignaturesWithoutNewCustodyOrBlockhash()
    {
        using var rpc = new SourceRpc(); var intent = await rpc.IntentAsync();
        var quote = (await rpc.Provider.QuoteWttSourceAsync(intent)).Result!;
        using var signing = new SigningFixture(rpc, intent.SourceAuthority.Context) { LoseCommittedResponse = true };
        await Assert.ThrowsAsync<IOException>(() => signing.Provider.PrepareWttSourceAsync(intent, quote, "Transfer", 0));
        var stored = Assert.Single(signing.Preparations.Values); Assert.NotNull(stored.SignedPreparationJson);
        var before = signing.CustodyCalls; var simulatedBefore = rpc.Simulated.Count;
        signing.AllowCustody = false; rpc.Mutation = "guard-mutable";
        var retried = await signing.Provider.PrepareWttSourceAsync(intent, quote, "Transfer", 0);
        Assert.False(retried.IsError, retried.Message);
        Assert.Equal(before, signing.CustodyCalls); Assert.Equal(simulatedBefore, rpc.Simulated.Count);
        Assert.Equal(JsonSerializer.Deserialize<WttStagePreparation>(stored.SignedPreparationJson!), retried.Result);
        using var metadata = JsonDocument.Parse(stored.PlanJson);
        var message = Convert.FromBase64String(metadata.RootElement.GetProperty("MessageBase64").GetString()!);
        var parsed = SolanaMessageEnvelope.Read(message); Assert.Equal(3, parsed.RequiredSignatures);
        Assert.Equal(retried.Result!.TransactionId, SolanaMessageEnvelope.VerifyWire(Convert.FromBase64String(retried.Result.SignedTransactionBase64), message));
        Assert.Equal(2, signing.Keys.Count);
    }

    [Fact]
    public async Task ChangedGuardBeforePreparationNeverClaimsKeysOrUsesCustody()
    {
        using var rpc = new SourceRpc(); var intent = await rpc.IntentAsync();
        var quote = (await rpc.Provider.QuoteWttSourceAsync(intent)).Result!;
        using var signing = new SigningFixture(rpc, intent.SourceAuthority.Context);
        rpc.Mutation = "guard-mutable";
        var prepared = await signing.Provider.PrepareWttSourceAsync(intent, quote, "Transfer", 0);
        Assert.True(prepared.IsError); Assert.Equal(0, signing.CustodyCalls);
        Assert.Empty(signing.Keys); Assert.Empty(signing.Preparations);
    }

    [Fact]
    public async Task ExpiredUnsignedWinnerCannotAllocateReplacementBlockhashOrSignAgain()
    {
        using var rpc = new SourceRpc(); var intent = await rpc.IntentAsync();
        var quote = (await rpc.Provider.QuoteWttSourceAsync(intent)).Result!;
        using var signing = new SigningFixture(rpc, intent.SourceAuthority.Context) { FailBeforeSignedCommit = true };
        await Assert.ThrowsAsync<IOException>(() => signing.Provider.PrepareWttSourceAsync(intent, quote, "Transfer", 0));
        var saved = Assert.Single(signing.Preparations.Values); var originalPlan = saved.PlanJson;
        Assert.Null(saved.SignedPreparationJson); var before = signing.CustodyCalls; rpc.Height = 501;
        var retry = await signing.Provider.PrepareWttSourceAsync(intent, quote, "Transfer", 0);
        Assert.True(retry.IsError); Assert.Equal(before, signing.CustodyCalls);
        Assert.Equal(originalPlan, Assert.Single(signing.Preparations.Values).PlanJson); Assert.Equal(2, signing.Keys.Count);
    }

    [Fact]
    public async Task LostSubmissionResponseReplaysOnlySavedWireAndExpiredMissingHistoryRemainsAmbiguous()
    {
        using var rpc = new SourceRpc(); var intent = await rpc.IntentAsync();
        var quote = (await rpc.Provider.QuoteWttSourceAsync(intent)).Result!;
        using var signing = new SigningFixture(rpc, intent.SourceAuthority.Context);
        var prepared = await signing.Provider.PrepareWttSourceAsync(intent, quote, "Transfer", 0);
        Assert.False(prepared.IsError, prepared.Message); var before = signing.CustodyCalls;
        rpc.LoseSendResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => signing.Provider.SubmitWttSourceAsync(intent, quote, prepared.Result!));
        await signing.Provider.SubmitWttSourceAsync(intent, quote, prepared.Result!);
        Assert.Equal(new[] { prepared.Result!.SignedTransactionBase64, prepared.Result.SignedTransactionBase64 }, rpc.Sent);
        Assert.Equal(before, signing.CustodyCalls);
        var pending = await signing.Provider.ObserveWttSourceAsync(intent, quote, prepared.Result);
        Assert.False(pending.IsError); Assert.Equal("Pending", pending.Result!.Verdict);
        rpc.Height = 501;
        var unknown = await signing.Provider.ObserveWttSourceAsync(intent, quote, prepared.Result);
        Assert.False(unknown.IsError); Assert.Equal("NeedsOperator", unknown.Result!.Verdict);
        Assert.Null(unknown.Result.Publication); Assert.Equal(before, signing.CustodyCalls);
        await signing.Provider.SubmitWttSourceAsync(intent, quote, prepared.Result);
        Assert.Equal(2, rpc.Sent.Count);
    }

    private sealed class SigningFixture : IDisposable
    {
        private readonly SourceRpc rpc;
        private readonly SigningContext owner;
        private readonly WttStageKeyProtector protector;
        public Dictionary<string, WttStageKeyEnvelope> Keys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, WttPreparation> Preparations { get; } = new(StringComparer.Ordinal);
        public SolanaProvider Provider { get; }
        public int CustodyCalls { get; private set; }
        public bool AllowCustody { get; set; } = true;
        public bool LoseCommittedResponse { get; set; }
        public bool FailBeforeSignedCommit { get; set; }
        public SigningContext? AdditionalContext { get; set; }

        public SigningFixture(SourceRpc rpc, SigningContext owner)
        {
            this.rpc = rpc; this.owner = owner;
            var custody = new Mock<IKeyCustodyService>(MockBehavior.Strict);
            SetupCustody<WttStageKeyEnvelope>(custody); SetupCustody<byte[]>(custody); SetupCustody<AZOAResult<byte[]>>(custody);
            var keyStore = new Mock<IWttStageKeyStore>(MockBehavior.Strict);
            keyStore.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Keys.GetValueOrDefault(id));
            keyStore.Setup(value => value.ClaimAsync(It.IsAny<WttStageKeyEnvelope>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WttStageKeyEnvelope candidate, CancellationToken _) =>
                {
                    if (!Keys.TryGetValue(candidate.Id, out var saved)) Keys.Add(candidate.Id, saved = candidate);
                    return saved;
                });
            protector = new(Options.Create(new WttStageKeyOptions
            {
                ActiveKeyId = "fixture-v1", Keys = new() { ["fixture-v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) },
            }));
            var vault = new WttStageKeyVault(keyStore.Object, protector, custody.Object);
            var preparations = new Mock<IWttPreparationStore>(MockBehavior.Strict);
            preparations.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Preparations.GetValueOrDefault(id));
            preparations.Setup(value => value.ClaimAsync(It.IsAny<WttPreparation>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WttPreparation candidate, CancellationToken _) =>
                {
                    if (!Preparations.TryGetValue(candidate.Id, out var saved)) Preparations.Add(candidate.Id, saved = candidate);
                    if (saved.BindingJson != candidate.BindingJson) throw new InvalidDataException("Fixture immutable admission conflict.");
                    return saved;
                });
            preparations.Setup(value => value.TrySaveSignedAsync(It.IsAny<WttPreparation>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((WttPreparation expected, string signed, CancellationToken _) =>
                {
                    if (FailBeforeSignedCommit) throw new IOException("Simulated unavailable signed commit.");
                    var saved = Preparations[expected.Id];
                    if (saved.Sealed || saved.Version != expected.Version || saved.PlanJson != expected.PlanJson || saved.SignedPreparationJson is not null)
                        return Task.FromResult<WttPreparation?>(null);
                    var committed = JsonSerializer.Deserialize<WttPreparation>(JsonSerializer.Serialize(saved))!;
                    committed.SignedPreparationJson = signed; committed.Version++; Preparations[expected.Id] = committed;
                    if (LoseCommittedResponse) { LoseCommittedResponse = false; throw new IOException("Simulated lost successful signed-commit response."); }
                    return Task.FromResult<WttPreparation?>(committed);
                });
            Provider = rpc.SigningProvider(custody.Object, preparations.Object, vault);
        }

        private void SetupCustody<T>(Mock<IKeyCustodyService> custody)
            => custody.Setup(value => value.WithSigningKeyAsync(It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<T>>>()))
                .Returns((SigningContext context, Func<byte[], Task<T>> use) => Use(context, use));

        private async Task<AZOAResult<T>> Use<T>(SigningContext context, Func<byte[], Task<T>> callback)
        {
            CustodyCalls++;
            if (!AllowCustody || context != owner && context != AdditionalContext) return AZOAResult<T>.Failure("Fixture custody is revoked or belongs to another exact participant.");
            var bytes = (byte[])rpc.OwnerAccount.PrivateKey.KeyBytes.Clone();
            try { return AZOAResult<T>.Success(await callback(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        public void Dispose() => protector.Dispose();
    }

    private sealed partial class SourceRpc
    {
        public SolanaProvider SigningProvider(IKeyCustodyService custody, IWttPreparationStore preparations, WttStageKeyVault vault)
            => new(_configuration, NullLogger<SolanaProvider>.Instance,
                new TransactionSignerFactory(new ITransactionSigner[] { new SolanaTransactionSigner() }), custody,
                null, this, preparations, vault, _targetVerifier);
    }
}
