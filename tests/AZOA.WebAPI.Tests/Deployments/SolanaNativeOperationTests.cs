using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed partial class SolanaNativeOperationTests
{
    [Fact]
    public async Task LoaderQuotePricesFallbackWithoutAddingItToSuccessfulSequence()
    {
        using var fixture = new SolanaDurableFixture(new MemoryPreparations());
        var elf = new byte[64];
        new byte[] { 0x7f, 0x45, 0x4c, 0x46, 2, 1, 1 }.CopyTo(elf, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18, 2), 247);
        var artifact = new NativeArtifact("solana-elf/1", JsonSerializer.SerializeToElement(new SolanaElfArtifact(Convert.ToBase64String(elf))), "uploaded");
        var intent = new NativeOperationIntent(1, "quoted-loader", "Deploy", "Solana", "Devnet", "", "artifact",
            JsonSerializer.SerializeToElement(new SolanaNativeInput(artifact, fixture.Actor.PublicKey.Key, fixture.Actor.PublicKey.Key,
                RentRecipient: fixture.Recipient.PublicKey.Key)),
            [new(fixture.Actor.PublicKey.Key, new SigningContext(fixture.ActorId, fixture.ActorWalletId, false, Scope: AzoaScopes.ContractDeploySign))],
            new("20000", "99999999", "0", ""));
        var quoted = await fixture.Provider.QuoteNativeOperationAsync(intent);
        quoted.IsError.Should().BeFalse(quoted.Message);
        using var plan = JsonDocument.Parse(quoted.Result!.PlanJson);
        plan.RootElement.GetProperty("Stages").EnumerateArray().Select(item => item.GetProperty("Name").GetString())
            .Should().Equal("CreateBuffer", "Write:0", "Deploy");
        plan.RootElement.GetProperty("FallbackCleanup").GetProperty("Name").GetString().Should().Be("CloseBuffer");
        quoted.Result.NetworkFee.Should().Be("20000");
        quoted.Result.Disclosure!.RefundDestination.Should().Be(fixture.Actor.PublicKey.Key);
        quoted.Result.Disclosure.CleanupRefundDestination.Should().Be(fixture.Recipient.PublicKey.Key);
        intent = intent with { ProtocolIdentity = quoted.Result.ProtocolIdentity };
        var quote = quoted.Result with { QuoteId = "quote", IntentHash = NativeOperationAuthorization.IntentHash(intent) };
        intent = intent with { Quote = quote, Budget = intent.Budget with { QuoteId = "quote" } };
        var next = await fixture.Provider.GetNextStageAsync(intent, []);
        next.Result!.Stage.Should().Be("CreateBuffer");
        (await fixture.Provider.GetRecoveryNextStageAsync(intent, [], "AbortCleanup")).IsError.Should().BeTrue();
        fixture.Consents.Should().BeEmpty();
        fixture.Sends.Should().Be(0);
    }

    [Fact]
    public async Task AtaQuoteDoesNotSignAndLostPrepareResponseKeepsOneCanonicalWire()
    {
        var store = new MemoryPreparations();
        using var fixture = new SolanaDurableFixture(store);
        var intent = await QuotedAtaAsync(fixture);
        fixture.Consents.Should().BeEmpty();
        fixture.Sends.Should().Be(0);
        var first = await fixture.Provider.PrepareNativeStageAsync(intent, "CreateAta", 0);
        first.IsError.Should().BeFalse(first.Message);
        var second = await fixture.Provider.PrepareNativeStageAsync(intent, "CreateAta", 0);
        second.Result.Should().Be(first.Result);
        fixture.Consents.Should().ContainSingle();
        fixture.Consents[0].Scope.Should().Be(AzoaScopes.SolanaAccountSign);
        fixture.Sends.Should().Be(0);
        store.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task HigherCurrentFeeCannotClaimPlanOrEnterCustody()
    {
        var store = new MemoryPreparations();
        using var fixture = new SolanaDurableFixture(store);
        var intent = await QuotedAtaAsync(fixture);
        fixture.MessageFee = 5001;
        var result = await fixture.Provider.PrepareNativeStageAsync(intent, "CreateAta", 0);
        result.IsError.Should().BeTrue();
        store.Rows.Should().BeEmpty();
        fixture.Consents.Should().BeEmpty();
        fixture.Sends.Should().Be(0);
    }

    [Fact]
    public async Task LostSendReplaysExactWireAndExpiredMissingHistoryStaysUncertain()
    {
        var store = new MemoryPreparations();
        using var fixture = new SolanaDurableFixture(store);
        var intent = await QuotedAtaAsync(fixture);
        var prepared = (await fixture.Provider.PrepareNativeStageAsync(intent, "CreateAta", 0)).Result!;
        fixture.LoseResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Provider.SubmitNativeStageAsync(intent, prepared));
        var wire = fixture.SubmittedWire;
        fixture.LoseResponse = false;
        (await fixture.Provider.SubmitNativeStageAsync(intent, prepared)).IsError.Should().BeFalse();
        fixture.SubmittedWire.Should().Be(wire);
        fixture.Height = 151;
        fixture.OmitHistory = true;
        var observed = await fixture.Provider.ObserveNativeStageAsync(intent, prepared);
        observed.Result!.Verdict.Should().Be("Unknown");
        fixture.Consents.Should().ContainSingle();
    }

    [Fact]
    public async Task ChangedEconomicIntentOrAppendedWireCannotReachSubmission()
    {
        using var fixture = new SolanaDurableFixture(new MemoryPreparations());
        var intent = await QuotedAtaAsync(fixture);
        var prepared = (await fixture.Provider.PrepareNativeStageAsync(intent, "CreateAta", 0)).Result!;
        var changed = intent with { Budget = intent.Budget with { MaximumLockedRent = "999999999" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.SubmitNativeStageAsync(changed, prepared));
        var wire = Convert.FromBase64String(prepared.SignedTransactionBase64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.SubmitNativeStageAsync(intent,
            prepared with { SignedTransactionBase64 = Convert.ToBase64String([.. wire, .. wire]) }));
        fixture.Sends.Should().Be(0);
    }

    private static async Task<NativeOperationIntent> QuotedAtaAsync(SolanaDurableFixture fixture)
    {
        var mint = new Account().PublicKey.Key;
        fixture.Accounts[mint] = new { owner = TokenProgram.ProgramIdKey.Key, executable = false,
            data = new { parsed = new { type = "mint", info = new { decimals = 0, supply = "1", mintAuthority = fixture.Actor.PublicKey.Key } } } };
        var artifact = new NativeArtifact("solana-account/1", JsonSerializer.SerializeToElement(
            new SolanaAccountArtifact("CreateAta", mint, fixture.Actor.PublicKey.Key)), "spl-token");
        var intent = new NativeOperationIntent(1, Guid.NewGuid().ToString("N"), "Account", "Solana", "Devnet", "", "artifact",
            JsonSerializer.SerializeToElement(new SolanaNativeInput(artifact, fixture.Actor.PublicKey.Key, fixture.Actor.PublicKey.Key, "Account")),
            [new(fixture.Actor.PublicKey.Key, new SigningContext(fixture.ActorId, fixture.ActorWalletId, false, Scope: AzoaScopes.SolanaAccountSign))],
            new("5000", "1461600", "0", ""));
        var result = await fixture.Provider.QuoteNativeOperationAsync(intent);
        result.IsError.Should().BeFalse(result.Message);
        intent = intent with { ProtocolIdentity = result.Result!.ProtocolIdentity };
        var quote = result.Result with { QuoteId = "quote", IntentHash = NativeOperationAuthorization.IntentHash(intent) };
        return intent with { Quote = quote, Budget = intent.Budget with { QuoteId = "quote" } };
    }

    private sealed class MemoryPreparations : INativePreparationStore
    {
        internal readonly Dictionary<string, NativePreparation> Rows = new(StringComparer.Ordinal);
        public Task<NativePreparation?> GetAsync(string id, CancellationToken ct = default)
        { lock (Rows) return Task.FromResult(Rows.GetValueOrDefault(id)); }
        public Task<NativePreparation> ClaimAsync(NativePreparation candidate, CancellationToken ct = default)
        { lock (Rows) { if (!Rows.ContainsKey(candidate.Id)) Rows[candidate.Id] = candidate; return Task.FromResult(Rows[candidate.Id]); } }
        public Task<NativePreparation> SavePreparedAsync(string id, string intentHash, string preparedJson, CancellationToken ct = default)
        {
            lock (Rows)
            {
                var row = Rows[id];
                if (row.IntentHash != intentHash || row.PreparedJson is not null && row.PreparedJson != preparedJson)
                    throw new InvalidOperationException("Preparation changed.");
                row.PreparedJson = preparedJson;
                return Task.FromResult(row);
            }
        }
    }
}
