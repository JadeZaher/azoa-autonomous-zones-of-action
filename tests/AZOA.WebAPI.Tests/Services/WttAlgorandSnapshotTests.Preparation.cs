using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttAlgorandSnapshotTests
{
    [Fact]
    public async Task Lost_submission_rebroadcasts_exact_saved_group_and_never_signs_again()
    {
        using var fixture = new AlgoPreparationFixture();
        var (intent, quote) = await fixture.IntentAsync();
        var prepared = await fixture.Provider.PrepareWttSourceAsync(intent, quote, "EmitterStorage", 0);
        Assert.False(prepared.IsError, prepared.Message);
        var payloads = new List<byte[]>();
        fixture.Rpc.Submit = async (request, ct) =>
        {
            payloads.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            if (payloads.Count == 1) throw new HttpRequestException("Lost response after possible send.");
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { txId = prepared.Result!.TransactionId })) };
        };
        fixture.AllowCustody = false;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Provider.SubmitWttSourceAsync(intent, quote, prepared.Result!));
        await fixture.Provider.SubmitWttSourceAsync(intent, quote, prepared.Result!);
        Assert.Equal(payloads[0], payloads[1]);
        var envelopes = new[] { prepared.Result!.SignedTransactionBase64 }.Concat(
            JsonSerializer.Deserialize<string[]>(prepared.Result.AdditionalSignedTransactionsJson!)!).SelectMany(Convert.FromBase64String).ToArray();
        Assert.Equal(envelopes, payloads[0]);
        Assert.Equal(1, fixture.CustodyCalls);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Provider.SubmitWttSourceAsync(intent, quote,
            prepared.Result with { TransactionId = "different" }));
        Assert.Equal(2, payloads.Count);
    }

    [Fact]
    public async Task Lost_signed_commit_recovers_original_wire_without_new_custody_or_current_quote()
    {
        using var fixture = new AlgoPreparationFixture();
        var (intent, quote) = await fixture.IntentAsync();
        fixture.LoseCommit = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Provider.PrepareWttSourceAsync(intent, quote, "EmitterStorage", 0));
        Assert.Equal(1, fixture.CustodyCalls);
        fixture.AllowCustody = false;
        var retry = await fixture.Provider.PrepareWttSourceAsync(intent, quote, "EmitterStorage", 0);
        Assert.False(retry.IsError, retry.Message);
        Assert.Equal(1, fixture.CustodyCalls);
        Assert.Equal(fixture.Rows.Values.Single().PlanJson, retry.Result!.RecoveryMetadata);
        var repeated = await fixture.Provider.PrepareWttSourceAsync(intent, quote, "EmitterStorage", 0);
        Assert.Equal(retry.Result, repeated.Result);
    }

    [Fact]
    public async Task Overlapping_preparers_return_the_single_persisted_candidate()
    {
        using var fixture = new AlgoPreparationFixture();
        var (intent, quote) = await fixture.IntentAsync();
        fixture.HoldFirstSignature = true;
        var first = fixture.Provider.PrepareWttSourceAsync(intent, quote, "EmitterStorage", 0);
        await fixture.FirstSignature.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await fixture.Provider.PrepareWttSourceAsync(intent, quote, "EmitterStorage", 0);
        fixture.ReleaseSignature.TrySetResult();
        var resumed = await first;
        Assert.False(second.IsError, second.Message);
        Assert.False(resumed.IsError, resumed.Message);
        Assert.Equal(second.Result, resumed.Result);
        Assert.Single(fixture.Rows);
        Assert.Equal(1, fixture.Rows.Values.Single().Version);
    }

    [Fact]
    public async Task Cancellation_during_custody_cannot_publish_a_signed_candidate()
    {
        using var fixture = new AlgoPreparationFixture();
        var (intent, quote) = await fixture.IntentAsync();
        fixture.SealDuringCustody = true;
        var result = await fixture.Provider.PrepareWttSourceAsync(intent, quote, "EmitterStorage", 0);
        Assert.True(result.IsError);
        Assert.Null(fixture.Rows.Values.Single().SignedPreparationJson);
        Assert.True(fixture.Rows.Values.Single().Sealed);
    }

    [Fact]
    public async Task Unfinished_prerequisites_refuse_publication_before_any_custody()
    {
        using var fixture = new AlgoPreparationFixture();
        var (intent, quote) = await fixture.IntentAsync();
        var result = await fixture.Provider.PrepareWttSourceAsync(intent, quote, "Transfer", 0);
        Assert.True(result.IsError);
        Assert.Empty(fixture.Rows);
        Assert.Equal(0, fixture.CustodyCalls);
    }

    private sealed partial class AlgoPreparationFixture : IDisposable
    {
        private readonly global::Algorand.Algod.Model.Account account = new();
        private readonly Rpc rpc;
        internal Rpc Rpc => rpc;
        private readonly ServiceProvider services;
        private readonly SigningContext context = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid())
            .ForChain("Algorand") with { Scope = AzoaScopes.WttSourceSign };
        internal readonly Dictionary<string, WttEvidenceProgress> Evidence = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, WttPreparation> Rows = new(StringComparer.Ordinal);
        internal readonly AZOA.WebAPI.Providers.Blockchain.Algorand.AlgorandProvider Provider;
        internal bool LoseCommit, SealDuringCustody, HoldFirstSignature;
        internal readonly TaskCompletionSource FirstSignature = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseSignature = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool AllowCustody = true;
        internal int CustodyCalls;

        internal AlgoPreparationFixture(bool target = false)
        {
            if (target) context = context with { Scope = AzoaScopes.WttTargetSign };
            rpc = new Rpc(target ? "wrapped" : "quote-exact", account.Address.ToString());
            var store = new Mock<IWttPreparationStore>(MockBehavior.Strict);
            store.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Rows.GetValueOrDefault(id));
            store.Setup(value => value.ClaimAsync(It.IsAny<WttPreparation>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WttPreparation candidate, CancellationToken _) =>
                {
                    if (!Rows.TryGetValue(candidate.Id, out var row)) Rows[candidate.Id] = row = candidate;
                    Assert.Equal(candidate.BindingJson, row.BindingJson);
                    return row;
                });
            store.Setup(value => value.TrySaveSignedAsync(It.IsAny<WttPreparation>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((WttPreparation expected, string signed, CancellationToken _) =>
                {
                    var current = Rows[expected.Id];
                    if (current.Sealed || current.Version != expected.Version || current.SignedPreparationJson is not null)
                        return Task.FromResult<WttPreparation?>(null);
                    var next = JsonSerializer.Deserialize<WttPreparation>(JsonSerializer.Serialize(current))!;
                    next.Version++; next.SignedPreparationJson = signed; Rows[expected.Id] = next;
                    if (LoseCommit) { LoseCommit = false; throw new IOException("Lost successful persistence response."); }
                    return Task.FromResult<WttPreparation?>(next);
                });
            var custody = new Mock<IKeyCustodyService>(MockBehavior.Strict);
            custody.Setup(value => value.WithSigningKeyAsync(It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                .Returns(async (SigningContext actual, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
                {
                    CustodyCalls++;
                    Assert.Single(Rows); Assert.Equal(context, actual);
                    if (!AllowCustody) throw new InvalidOperationException("Unexpected fresh custody on recovery.");
                    if (CustodyCalls == 1 && HoldFirstSignature)
                    {
                        FirstSignature.TrySetResult();
                        await ReleaseSignature.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    if (SealDuringCustody) { Rows.Values.Single().Sealed = true; Rows.Values.Single().Version++; }
                    var key = (byte[])account.KeyPair.ClearTextPrivateKey.Clone();
                    try { return AZOAResult<AZOAResult<byte[]>>.Success(await sign(key)); }
                    finally { CryptographicOperations.ZeroMemory(key); }
                });
            var progress = new Mock<IWttEvidenceProgressStore>(MockBehavior.Strict);
            progress.Setup(value => value.ClaimAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string binding, string initial, CancellationToken _) =>
                {
                    var candidate = AZOA.WebAPI.Helpers.WttEvidenceProgressContract.Begin(binding, initial);
                    if (!Evidence.TryGetValue(candidate.Id, out var row)) Evidence[candidate.Id] = row = candidate;
                    Assert.Equal(initial, row.InitialStateJson);
                    return row;
                });
            progress.Setup(value => value.AdvanceAsync(It.IsAny<WttEvidenceProgress>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WttEvidenceProgress expected, string state, string digest, string? cursor, CancellationToken _) =>
                {
                    if (Evidence[expected.Id].Version != expected.Version) return null;
                    return Evidence[expected.Id] = AZOA.WebAPI.Helpers.WttEvidenceProgressContract.Advance(expected, state, digest, cursor);
                });
            var registrations = new ServiceCollection().AddSingleton(store.Object).AddSingleton(progress.Object);
            if (target) AddTargetVerifier(registrations);
            services = registrations.BuildServiceProvider();
            Provider = Create(rpc, custody.Object, services.GetRequiredService<IServiceScopeFactory>());
        }

        internal async Task<(WttOperationIntent, WttStageQuote)> IntentAsync()
        {
            var inspected = await Provider.InspectWttSourceAsync("0", true);
            Assert.False(inspected.IsError, inspected.Message);
            var source = inspected.Result.Network;
            var intent = new WttOperationIntent(1, "actual-algo-preparation", context.UserId, null, source,
                source with { ChainType = "Ethereum", ProtocolChain = 2, ProtocolIdentity = "eip155:31337" },
                inspected.Result.Asset, new(account.Address.ToString(), context), new(account.Address.ToString(), context),
                "recipient", new string('a', 64), null, 6, WttPayloadCodec.Normalize("10000", "0", 6), 0, 0, false, false,
                new("1000000", "3000000", "100000"), new("1000000", "3000000", "100000"));
            var quote = await Provider.QuoteWttSourceAsync(intent);
            Assert.False(quote.IsError, quote.Message);
            return (intent, quote.Result!);
        }

        public void Dispose() { services.Dispose(); rpc.Dispose(); }
    }
}
