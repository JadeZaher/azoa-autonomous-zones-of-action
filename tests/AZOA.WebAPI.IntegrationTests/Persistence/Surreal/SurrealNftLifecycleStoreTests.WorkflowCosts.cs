using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Workflow = AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealNftLifecycleStoreTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task EmptyWorkflowRejectionRequiresOriginalExecutionAndCannotContainPreparedWire(bool canceled, bool prepared, bool accepted)
    {
        var (source, receipt) = await WorkflowCostCandidate();
        var actor = receipt.Reference.ActorUserId;
        var command = new NftLifecycleCommand(actor, actor, Guid.Empty, Guid.Empty, "", "", new UserNFTMintModel(), -1,
            Guid.Empty, ActionAuthority: receipt.OriginalAuthority.Action);
        var json = JsonSerializer.Serialize(command);
        var rejected = new NftLifecycle { Id = source.Id, ActorUserId = actor.ToString("N"), RecipientUserId = actor.ToString("N"),
            NftId = Guid.Empty.ToString("N"), Operation = "Mint", State = "Failed", Stage = "AdmissionFailure",
            RequestJson = json, RequestHash = NftLifecycleIdentity.Fingerprint(json), PreparedTransactionJson = prepared ? "{}" : null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        if (canceled)
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<ActionRun>(receipt.OriginalAuthority.Action.RunId.ToString("N")).Where(seed => true)
                .Set(run => run.Status, ActionRun.ActionRunStatusKind.Cancelled).Build())).EnsureAllOk();
        NftLifecycle? claimed = null;
        try { claimed = await _store.ClaimAsync(rejected); }
        catch (ActionEffectAuthorityChangedException) { }
        (claimed is not null).Should().Be(accepted);
        ((await _store.GetAsync(source.Id)) is not null).Should().Be(accepted);
    }

    [Fact]
    public async Task EmptyCancellationMarkerWinsAgainstDelayedWorkflowAdmissionWithoutSigningAuthority()
    {
        var (candidate, receipt) = await WorkflowCostCandidate();
        var actor = receipt.Reference.ActorUserId;
        var command = new NftLifecycleCommand(actor, actor, Guid.Empty, Guid.Empty, "", "", new UserNFTMintModel(), -1, Guid.Empty);
        var json = JsonSerializer.Serialize(command);
        var marker = new NftLifecycle { Id = candidate.Id, ActorUserId = actor.ToString("N"), RecipientUserId = actor.ToString("N"),
            NftId = Guid.Empty.ToString("N"), Operation = "Mint", State = "Cancelled", Stage = "Cancellation", ChainType = "",
            Network = default(ChainNetwork).ToString(), RequestJson = json, RequestHash = NftLifecycleIdentity.Fingerprint(json),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        (await _store.ClaimAsync(marker))!.State.Should().Be("Cancelled");
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        (await acceptances.ClaimAsync(candidate, receipt)).Should().BeNull();
        (await _store.GetAsync(candidate.Id))!.RequestJson.Should().Be(json);
        marker.Id = Guid.NewGuid().ToString("N");
        marker.PreparedTransactionJson = "{}";
        (await _store.ClaimAsync(marker)).Should().BeNull();
    }

    [Fact]
    public async Task RetirementRejectsAnotherActorTenantAndAlteredReplay()
    {
        var (row, receipt) = await WorkflowCostCandidate();
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        var consent = receipt.AcceptingAuthority;
        (await acceptances.RetireAsync(receipt.Request, consent with {
            Credential = consent.Credential with { ActorUserId = Guid.NewGuid() } })).Should().BeFalse();
        (await acceptances.RetireAsync(receipt.Request, consent with {
            Credential = consent.Credential with { TenantId = Guid.NewGuid() } })).Should().BeFalse();
        var original = await _executor.QuerySingleAsync<ActionNodeExecution>(SurrealQuery<ActionNodeExecution>.Key(receipt.Request.ExecutionId.ToString("N")));
        original!.Version.Should().Be(receipt.Request.ExpectedExecutionVersion);
        (await acceptances.RetireAsync(receipt.Request, consent)).Should().BeTrue();
        var changed = receipt.Request with { Authorization = receipt.Request.Authorization with { QuoteHash = "different" } };
        (await acceptances.RetireAsync(changed, consent)).Should().BeFalse();
        (await _store.GetAsync(row.Id)).Should().BeNull();
    }

    [Fact]
    public async Task RetiringExpiredUncommittedOfferFencesDelayedAcceptanceAndReplaysAfterRevocation()
    {
        var (row, receipt) = await WorkflowCostCandidate();
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        (await _executor.ExecuteAsync(SurrealWriter.DeleteOnly<NftProtocolCostQuoteRecord>(receipt.Quote.QuoteId).Where(seed => true).Build())).EnsureAllOk();
        (await acceptances.RetireAsync(receipt.Request, receipt.AcceptingAuthority)).Should().BeTrue();
        (await _store.GetAsync(row.Id)).Should().BeNull();
        (await acceptances.ClaimAsync(row, receipt)).Should().BeNull();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(receipt.Reference.ActorUserId.ToString("N")).Where(seed => true)
            .Set(user => user.IsActive, false).Build())).EnsureAllOk();
        (await acceptances.RetireAsync(receipt.Request, receipt.AcceptingAuthority)).Should().BeTrue();
        (await acceptances.RetireAsync(receipt.Request with { ExpectedExecutionVersion = 999 }, receipt.AcceptingAuthority)).Should().BeFalse();
    }

    [Fact]
    public async Task AcceptanceAndRetirementRaceHaveExactlyOneWinner()
    {
        var (row, receipt) = await WorkflowCostCandidate();
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var accepting = acceptances.ClaimAsync(row, receipt, deadline.Token);
        var retiring = acceptances.RetireAsync(receipt.Request, receipt.AcceptingAuthority, deadline.Token);
        await Task.WhenAll(accepting, retiring);
        ((accepting.Result is not null) ^ retiring.Result).Should().BeTrue();
        ((await _store.GetAsync(row.Id)) is not null).Should().Be(accepting.Result is not null);
        var execution = await _executor.QuerySingleAsync<ActionNodeExecution>(SurrealQuery<ActionNodeExecution>.Key(receipt.Request.ExecutionId.ToString("N")));
        execution!.Version.Should().Be(receipt.Request.ExpectedExecutionVersion + 1);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Submitted")]
    public async Task OriginalWorkflowRevocationStopsNewEffectsButNotObservationUpdates(string targetState)
    {
        var (row, receipt) = await WorkflowCostCandidate();
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        (await acceptances.ClaimAsync(row, receipt)).Should().NotBeNull();
        var stored = (await _store.GetAsync(row.Id))!;
        stored.Detail = "Authorized preparation checkpoint";
        stored = (await _store.UpdateAsync(stored, stored.Version, requireActionAuthority: true))!;
        stored.Should().NotBeNull();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(receipt.Reference.ActorUserId.ToString("N")).Where(seed => true)
            .Set(user => user.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        stored.State = targetState;
        (await _store.UpdateAsync(stored, stored.Version, requireActionAuthority: true)).Should().BeNull();
        var original = (await _store.GetAsync(row.Id))!;
        original.Detail = "Observation remains available after revocation";
        (await _store.UpdateAsync(original, original.Version, requireActionAuthority: false)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("fee")]
    public async Task ChangedInitialIntentCannotConsumeWorkflowOffer(string change)
    {
        var (row, receipt) = await WorkflowCostCandidate();
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        command = change == "recipient" ? command with { RecipientAddress = "different", RecipientWalletId = Guid.NewGuid() }
            : command with { FeeQuote = command.FeeQuote! with { NativeBaseUnits = "999" } };
        row.RequestJson = JsonSerializer.Serialize(command);
        row.RequestHash = NftLifecycleIdentity.Fingerprint(row.RequestJson);
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        (await acceptances.ClaimAsync(row, receipt)).Should().BeNull();
        (await _store.GetAsync(row.Id)).Should().BeNull();
        (await acceptances.GetAsync(receipt.Reference.ActorUserId, null, receipt.Request.ExecutionId, receipt.Request.RequestId)).Should().BeNull();
    }

    [Fact]
    public async Task WorkflowCostAcceptanceCommitsOnceAndReplaysAfterAuthorityAndQuoteRemoval()
    {
        var (row, receipt) = await WorkflowCostCandidate();
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        var accepted = await acceptances.ClaimAsync(row, receipt);
        accepted.Should().NotBeNull();
        (await _store.GetAsync(row.Id))!.RequestJson.Should().Be(row.RequestJson);
        var execution = await _executor.QuerySingleAsync<ActionNodeExecution>(SurrealQuery<ActionNodeExecution>.Key(receipt.Request.ExecutionId.ToString("N")));
        execution!.Version.Should().Be(receipt.Request.ExpectedExecutionVersion + 1);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(receipt.Reference.ActorUserId.ToString("N")).Where(seed => true)
            .Set(user => user.IsActive, false).Build())).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.DeleteOnly<NftProtocolCostQuoteRecord>(receipt.Quote.QuoteId).Where(seed => true).Build())).EnsureAllOk();
        (await acceptances.ClaimAsync(row, receipt))!.ReceiptJson.Should().Be(accepted!.ReceiptJson);
        (await acceptances.ClaimAsync(row, receipt with { Request = receipt.Request with { ExpectedExecutionVersion = 9 } })).Should().BeNull();
    }

    [Fact]
    public async Task CompetingWorkflowAcceptancesCannotBothWinTheExecutionVersion()
    {
        var (row, receipt) = await WorkflowCostCandidate();
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        var other = receipt with { Request = receipt.Request with { RequestId = Guid.NewGuid() } };
        var results = await Task.WhenAll(acceptances.ClaimAsync(row, receipt), acceptances.ClaimAsync(WithAcceptance(row, other), other)).WaitAsync(TimeSpan.FromSeconds(15));
        results.Count(result => result is not null).Should().Be(1);
        (await _store.GetAsync(row.Id)).Should().NotBeNull();
        var audits = await _executor.QueryAsync<NftWorkflowCostAcceptanceRecord>(SurrealQuery<NftWorkflowCostAcceptanceRecord>.From());
        audits.Should().ContainSingle();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("version")]
    [InlineData("revoked")]
    [InlineData("reference")]
    public async Task ChangedWorkflowFenceLeavesNeitherAcceptanceNorLifecycle(string change)
    {
        var (row, receipt) = await WorkflowCostCandidate();
        if (change == "cancel")
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<ActionRun>(receipt.OriginalAuthority.Action.RunId.ToString("N")).Where(seed => true)
                .Set(run => run.Status, ActionRun.ActionRunStatusKind.Cancelled).Build())).EnsureAllOk();
        else if (change == "revoked")
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-10);
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(receipt.Reference.ActorUserId.ToString("N")).Where(seed => true)
                .Set(user => user.AuthNotBefore, cutoff).Build())).EnsureAllOk();
            receipt = receipt with { AcceptingAuthority = receipt.AcceptingAuthority with {
                Credential = receipt.AcceptingAuthority.Credential with { AuthNotBefore = cutoff, IssuedAt = cutoff.AddSeconds(1) } } };
            row = WithAcceptance(row, receipt);
        }
        else if (change == "reference")
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<ActionNodeExecution>(receipt.Request.ExecutionId.ToString("N")).Where(seed => true)
                .Set(execution => execution.ExternalOperationJson, "{}").Build())).EnsureAllOk();
        else
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<ActionNodeExecution>(receipt.Request.ExecutionId.ToString("N")).Where(seed => true)
                .Set(execution => execution.Version, receipt.Request.ExpectedExecutionVersion + 1).Build())).EnsureAllOk();
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        (await acceptances.ClaimAsync(row, receipt)).Should().BeNull();
        (await _store.GetAsync(row.Id)).Should().BeNull();
        (await acceptances.GetAsync(receipt.Reference.ActorUserId, null, receipt.Request.ExecutionId, receipt.Request.RequestId)).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentCancellationAndAcceptanceLeaveAnAtomicOutcomeAndPreventLateConsent()
    {
        var (row, receipt) = await WorkflowCostCandidate();
        var acceptances = new SurrealNftWorkflowCostAcceptanceStore(_executor);
        var accepting = acceptances.ClaimAsync(row, receipt);
        var cancelling = SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<ActionRun>(receipt.OriginalAuthority.Action.RunId.ToString("N")).Where(seed => true)
                .Set(run => run.Status, ActionRun.ActionRunStatusKind.Cancelled).Build());
            result.EnsureAllOk();
            return result;
        }, CancellationToken.None);
        await Task.WhenAll(accepting, cancelling).WaitAsync(TimeSpan.FromSeconds(15));
        var audit = await acceptances.GetAsync(receipt.Reference.ActorUserId, null, receipt.Request.ExecutionId, receipt.Request.RequestId);
        var lifecycle = await _store.GetAsync(row.Id);
        (audit is null).Should().Be(lifecycle is null, "cancellation cannot leave only half of the acceptance transaction");
        (await accepting is null).Should().Be(audit is null);
        var late = receipt with { Request = receipt.Request with { RequestId = Guid.NewGuid() } };
        (await acceptances.ClaimAsync(WithAcceptance(row, late), late)).Should().BeNull();
    }

    private static NftLifecycle WithAcceptance(NftLifecycle source, NftWorkflowCostAcceptanceReceipt receipt)
    {
        var copy = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(source))!;
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(copy.RequestJson)! with { WorkflowCostAcceptance = receipt };
        copy.RequestJson = JsonSerializer.Serialize(command);
        copy.RequestHash = NftLifecycleIdentity.Fingerprint(copy.RequestJson);
        return copy;
    }

    private async Task<(NftLifecycle Row, NftWorkflowCostAcceptanceReceipt Receipt)> WorkflowCostCandidate()
    {
        var actor = Guid.NewGuid(); var run = Guid.NewGuid(); var node = Guid.NewGuid(); var execution = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var mint = new NftMintRequest { WalletId = Guid.NewGuid(), Name = "NFT", ChainId = "Solana", MetadataUri = "ipfs://immutable" };
        var input = new Workflow.ActionFrozenNodeInput(1, run, node, Workflow.ActionNodeType.NftMint, actor, null,
            JsonSerializer.SerializeToElement(mint), new Dictionary<Guid, Workflow.ActionNodeExecution>(), new Dictionary<Guid, Workflow.ActionNodeExecution>());
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
        var hash = CanonicalJson.HashCanonical(frozen);
        var key = $"action:{run:N}:{node:N}:nft";
        var id = NftLifecycleIdentity.ForRequest(actor, "Mint", key);
        var reference = new Workflow.ActionExternalOperationReference(1, "nft", key, id, hash, actor, null);
        var credential = new SigningCredentialWitness(1, "Jwt", actor, null, null, now.AddMinutes(-1), now.AddHours(1),
            null, null, [AzoaScopes.ActionExecute, AzoaScopes.NftMint], true, TokenUse: AzoaClaims.TokenUseLogin);
        var original = new WorkflowSigningWitness(1, credential, new(run, node, actor, null, hash),
            [AzoaScopes.ActionExecute, AzoaScopes.NftMint], []);
        var metadata = NftLifecycleOperationProjection.MintModel(mint);
        var quote = new NftProtocolCostQuote(Guid.NewGuid().ToString("N"), actor, Guid.Empty, mint.WalletId, "payer", "Solana",
            ChainNetwork.Devnet, "solana:fixture", id, NftProtocolCostContract.DraftHash(id, actor, actor, metadata), "Create", 0,
            "NFT", "{\"policy\":1}", new("1", "2", "3"), now, now.AddMinutes(5), new string('a', 64));
        metadata.ProtocolCostAuthorization = new(quote.QuoteId, NftProtocolCostContract.Hash(quote), quote.Limits);
        var command = new NftLifecycleCommand(actor, actor, mint.WalletId, mint.WalletId, "payer", "payer", metadata, -1,
            Guid.Empty, FeeQuote: new("Solana", ChainNetwork.Devnet, "Mint", 1, "SOL", 9, "0", "", actor),
            ActionAuthority: original.Action, ProtocolCostQuote: quote,
            ActorAuthority: new(1, "Workflow", credential, new(mint.WalletId, actor, "payer", "Solana"),
                original.RequiredScopes, original.Grants, original.Action, original.AuthorizationId));
        var json = JsonSerializer.Serialize(command);
        var candidate = new NftLifecycle { Id = id, ActorUserId = actor.ToString("N"), RecipientUserId = actor.ToString("N"),
            NftId = Guid.NewGuid().ToString("N"), Operation = "Mint", ChainType = "Solana", Network = "Devnet",
            RequestJson = json, RequestHash = NftLifecycleIdentity.Fingerprint(json), CreatedAt = now, UpdatedAt = now };
        var initialIntent = NftLifecycleIntentFactory.Create(candidate, command, ChainNetwork.Devnet, null, Guid.Empty)!;
        quote = quote with { QuotedIntentHash = NftProtocolCostContract.IntentHash(initialIntent, command.RecipientWalletId) };
        metadata.ProtocolCostAuthorization = new(quote.QuoteId, NftProtocolCostContract.Hash(quote), quote.Limits);
        command = command with { ProtocolCostQuote = quote };
        candidate.RequestJson = JsonSerializer.Serialize(command);
        candidate.RequestHash = NftLifecycleIdentity.Fingerprint(candidate.RequestJson);
        (await _executor.ExecuteAsync(SurrealWriter.Create(new User { Id = actor.ToString("N"), Username = actor.ToString("N"),
            Email = actor + "@test.invalid", PasswordHash = "fixture", IsActive = true }))).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new Wallet { Id = mint.WalletId.ToString("N"),
            UserId = "user:" + actor.ToString("N"), ChainType = "Solana", Address = "payer" }))).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new ActionRun { Id = run.ToString("N"), ActionId = "action:" + Guid.NewGuid().ToString("N"),
            UserId = "user:" + actor.ToString("N"), Status = ActionRun.ActionRunStatusKind.Running, StartedAt = now }))).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new ActionNodeExecution { Id = execution.ToString("N"), RunId = "action_run:" + run.ToString("N"),
            NodeId = "action_node:" + node.ToString("N"), State = ActionNodeExecution.ActionNodeState.Running, Version = 3,
            FrozenInput = frozen, ExternalOperationJson = JsonSerializer.Serialize(reference), SigningAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(original)) }))).EnsureAllOk();
        await new SurrealNftProtocolCostQuoteStore(_executor).ClaimAsync(quote);
        var receipt = new NftWorkflowCostAcceptanceReceipt(1, new(Guid.NewGuid(), execution, 3, metadata.ProtocolCostAuthorization), reference,
            original, new(1, credential, [AzoaScopes.NftMint], []), quote, now);
        command = command with { WorkflowCostAcceptance = receipt };
        candidate.RequestJson = JsonSerializer.Serialize(command);
        candidate.RequestHash = NftLifecycleIdentity.Fingerprint(candidate.RequestJson);
        return (candidate, receipt);
    }
}
