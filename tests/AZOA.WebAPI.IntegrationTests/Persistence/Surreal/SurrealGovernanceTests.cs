using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using RunRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;
using ExecutionRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;
using HolonRow = AZOA.WebAPI.Persistence.SurrealDb.Models.Holon;
using ActionNode = AZOA.WebAPI.Models.Actions.ActionNode;
using ActionDefinition = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealGovernanceTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private readonly string _tenant = Guid.NewGuid().ToString("N");
    private readonly string _voter = Guid.NewGuid().ToString("N");
    private readonly string _key = Guid.NewGuid().ToString("N");
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealGovernanceStore _store = null!;
    private GovernanceProposal _proposal = null!;
    private KycSubmission _approval = null!;
    private string _identity = string.Empty;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "api_key", "holon", "consent_grant", "kyc_submission",
            "kyc_identity", "kyc_screening_result", "kyc_provider_profile", "tenant_kyc_provider_selection",
            "person_operation_claim", "governance_proposal", "governance_command_receipt", "action_run", "action_node_execution",
            "node_governance_parameters", "node_governance_policy_lock", "kyc_authority_guard", "local_entity_mutation_lock");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
        await Save(new User { Id = _tenant, Username = _tenant, Email = _tenant + "@test.invalid", PasswordHash = "test", IsActive = true });
        await Save(new User { Id = _voter, Username = _voter, Email = _voter + "@test.invalid", PasswordHash = "test", IsActive = true,
            OwnerTenantId = "user:" + _tenant });
        await Save(new ApiKey { Id = _key, UserId = "user:" + _tenant, KeyHash = "hash", KeyPrefix = "prefix",
            IsActive = true, Scopes = [AzoaScopes.TenantProvision] });
        await Save(new KycProviderProfile { Id = "identity", DisplayName = "Test provider", AdapterKey = "IDENTOMAT", Enabled = true,
            PolicyVersion = "v1", AssuranceLevel = "verified", TrustRevision = 1, Version = 1, UpdatedByUserId = "user:" + _tenant });
        await Save(new TenantKycProviderSelection { Id = _tenant, TenantId = "user:" + _tenant, ProviderKey = "identity",
            SelectionVersion = 1, UpdatedByUserId = "user:" + _tenant });
        _approval = new KycSubmission { Id = Guid.NewGuid().ToString("N"), UserId = "user:" + _voter, TenantId = "user:" + _tenant,
            Provider = KycProvider.IDENTOMAT, ProviderKey = "identity", Status = KycStatus.APPROVED, ProviderResult = "fixture-proof",
            ProviderSelectionVersion = 1, ProviderTrustRevision = 1, AmlLineageKey = "lineage", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), SubmittedAt = DateTimeOffset.UtcNow };
        await Save(_approval);
        _identity = Guid.NewGuid().ToString("N");
        await Save(new KycIdentity { Id = _identity, TenantScope = _tenant, TenantId = "user:" + _tenant, PersonKey = "person",
            GlobalPersonKey = "global-person", UserIds = [_voter], Provider = KycProvider.IDENTOMAT, ProviderKey = "identity" });
        await Save(new KycScreeningResult { Id = "clearance", SubjectUserId = "user:" + _voter, TenantId = "user:" + _tenant,
            LineageKey = "lineage", Decision = KycScreeningDecision.NO_HIT, ResultsTruncated = false, PolicyVersion = "v1", ScreenedAt = DateTimeOffset.UtcNow });
        var id = Guid.NewGuid().ToString("N");
        _proposal = new() { Id = id, TenantId = _tenant, CreatorId = _tenant, DefinitionHash = "definition", IntentHash = "create",
            DefinitionJson = "{}", ElectorateJson = JsonSerializer.Serialize(new[] { _tenant, _voter }.Order(StringComparer.Ordinal)),
            ElectorateHash = "members", UniquenessScope = _tenant, OperationKey = ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(Guid.ParseExact(id, "N")),
            OpensAt = DateTimeOffset.UtcNow.AddMinutes(-1), ClosesAt = DateTimeOffset.UtcNow.AddMinutes(10), CreatedAt = DateTimeOffset.UtcNow };
    }

    public async Task DisposeAsync() { _connection.Dispose(); await SurrealTestSchema.DropAsync(_namespace); }

    [Fact]
    public async Task Provisioned_principal_handler_creates_exact_election_and_resolver_replays_after_cancel()
    {
        var config = new GovernanceProposalCreateNodeConfig { Name = "Choose", Options = ["yes", "no"],
            MinimumVotes = 2, ClosesAt = DateTimeOffset.UtcNow.AddHours(1) };
        var context = await HandlerContext(_tenant, config);
        var service = CreationService();
        var handler = new GovernanceProposalCreateNodeHandler(service);
        var created = await handler.HandleAsync(context);
        created.IsError.Should().BeFalse(created.Message);
        var id = GovernanceCommandIdentity.Id(context);
        var proposal = await _store.GetAsync(id, default);
        proposal!.TenantId.Should().Be(_tenant);
        proposal.MinimumVotes.Should().Be(2);
        JsonSerializer.Deserialize<string[]>(proposal.ElectorateJson).Should().BeEquivalentTo(new[] { _tenant, _voter });
        await Execute(SurrealWriter.UpdateOnly<RunRow>(context.RunId.ToString("N")).Set(row => row.Status, RunRow.ActionRunStatusKind.Cancelled).Build());
        var canonical = Frozen(context);
        var recovered = await new GovernanceActionResolver(service).ObserveAsync(handler.CreateReference(context, CanonicalJson.HashCanonical(canonical)), canonical);
        recovered.Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        recovered.Output.Should().Be(created.Output);
    }

    [Fact]
    public async Task Unprovisioned_principal_and_owned_child_cannot_create_election()
    {
        var config = new GovernanceProposalCreateNodeConfig { Name = "Choose", Options = ["yes", "no"], ClosesAt = DateTimeOffset.UtcNow.AddHours(1) };
        var child = await HandlerContext(_voter, config);
        (await CreationService().CreateAsync(child, config, default)).IsError.Should().BeTrue();
        await Execute(SurrealWriter.UpdateOnly<ApiKey>(_key).Set(row => row.IsActive, false).Build());
        var principal = await HandlerContext(_tenant, config);
        (await CreationService().CreateAsync(principal, config, default)).IsError.Should().BeTrue();
        (await _store.GetAsync(GovernanceCommandIdentity.Id(principal), default)).Should().BeNull();
    }

    [Fact]
    public async Task Invalid_frozen_command_recovers_terminal_failure_receipt_without_election()
    {
        var context = await HandlerContext(_tenant, new() { Name = "Invalid", Options = ["one"] });
        var service = CreationService();
        var canonical = Frozen(context);
        var reference = GovernanceCommandIdentity.Reference(context, CanonicalJson.HashCanonical(canonical));
        var resolver = new GovernanceActionResolver(service);
        var first = await resolver.ObserveAsync(reference, canonical);
        first.Status.Should().Be(ActionExternalOperationStatus.Failed);
        var receipt = await _store.ReceiptAsync(reference.OperationId!, default);
        receipt!.IsError.Should().BeTrue();
        (await _store.GetAsync(reference.OperationId!, default)).Should().BeNull();
        await Execute(SurrealWriter.UpdateOnly<RunRow>(context.RunId.ToString("N")).Set(row => row.Status, RunRow.ActionRunStatusKind.Cancelled).Build());
        (await resolver.ObserveAsync(reference, canonical)).Status.Should().Be(ActionExternalOperationStatus.Failed);
    }

    [Fact]
    public async Task Proposal_projection_and_receipt_commit_once_and_cancelled_origin_cannot_create()
    {
        var receipt = await Command(_tenant);
        var projection = Projection();
        var results = await Task.WhenAll(_store.CreateAsync(_proposal, projection, receipt, default), _store.CreateAsync(_proposal, projection, receipt, default));
        results.Should().OnlyContain(result => !result.IsError);
        (await _store.GetAsync(_proposal.Id, default)).Should().NotBeNull();
        (await _store.ReceiptAsync(receipt.Id, default))!.OutputJson.Should().Be(receipt.OutputJson);
        var other = await Command(_tenant);
        await Execute(SurrealWriter.UpdateOnly<RunRow>(other.RunId).Set(row => row.Status, RunRow.ActionRunStatusKind.Cancelled).Build());
        var rejected = await _store.CreateAsync(_proposal, projection, other, default);
        rejected.IsError.Should().BeTrue();
        (await _store.ReceiptAsync(other.Id, default)).Should().BeNull();
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("approval")]
    [InlineData("selection")]
    [InlineData("aml")]
    [InlineData("cancel")]
    [InlineData("cutoff")]
    public async Task Vote_rechecks_current_authority_inside_commit(string change)
    {
        await SeedProposal();
        var receipt = await Command(_voter);
        switch (change)
        {
            case "membership": await Execute(SurrealWriter.UpdateOnly<User>(_voter).Unset(row => row.OwnerTenantId).Build()); break;
            case "approval": await Execute(SurrealWriter.UpdateOnly<KycSubmission>(_approval.Id).Set(row => row.Status, KycStatus.REJECTED).Build()); break;
            case "selection": await Execute(SurrealWriter.UpdateOnly<TenantKycProviderSelection>(_tenant).Set(row => row.SelectionVersion, 2L).Build()); break;
            case "aml": await Execute(SurrealWriter.UpdateOnly<KycScreeningResult>("clearance").Set(row => row.Decision, KycScreeningDecision.HIT).Build()); break;
            case "cancel": await Execute(SurrealWriter.UpdateOnly<RunRow>(receipt.RunId).Set(row => row.Status, RunRow.ActionRunStatusKind.Cancelled).Build()); break;
            case "cutoff": await Execute(SurrealWriter.UpdateOnly<GovernanceProposal>(_proposal.Id).Set(row => row.ClosesAt, DateTimeOffset.UtcNow.AddSeconds(-1)).Build()); break;
        }
        var result = await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, receipt, default);
        result.IsError.Should().BeTrue();
        (await _store.DecisionsAsync(_proposal, default)).Should().BeEmpty();
        (await _store.ReceiptAsync(receipt.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Vote_replay_cannot_overwrite_a_later_revote_and_second_account_for_same_person_cannot_vote()
    {
        await SeedProposal();
        var first = await Command(_voter);
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, first, default)).IsError.Should().BeFalse();
        var second = await Command(_voter);
        (await _store.VoteAsync(_proposal, Claim("no"), _approval, _identity, second, default)).IsError.Should().BeFalse();
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, first, default)).IsError.Should().BeFalse();
        var rows = await _store.DecisionsAsync(_proposal, default);
        rows.Should().ContainSingle().Which.DecisionOption.Should().Be("no");
        var forged = Claim("yes");
        forged.UserId = _tenant;
        var wrongActor = await Command(_tenant);
        (await _store.VoteAsync(_proposal, forged, _approval, _identity, wrongActor, default)).IsError.Should().BeTrue();
        var generic = new SurrealPersonOperationClaimStore(_executor);
        (await generic.TryClaimAsync(Claim("yes"))).IsError.Should().BeTrue();
        (await generic.ReleaseAsync(_tenant, _proposal.OperationKey, "person", _voter, DateTimeOffset.UtcNow)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Policy_first_insertion_and_revision_change_refuse_stale_creation()
    {
        (await _store.PolicyVersionAsync(default)).Should().BeNull();
        await Save(new NodeGovernanceParameters { Version = 1, AllowedAssetTypes = [], UpdatedAt = DateTimeOffset.UtcNow });
        var command = await Command(_tenant);
        (await _store.CreateAsync(_proposal, Projection(), command, default)).IsError.Should().BeTrue();
        (await _store.GetAsync(_proposal.Id, default)).Should().BeNull();
        (await _store.ReceiptAsync(command.Id, default)).Should().BeNull();
        var version = await _store.PolicyVersionAsync(default);
        await Execute(SurrealWriter.UpdateOnly<NodeGovernanceParameters>("local").Set(row => row.Version, 2L).Build());
        (await _store.CreateAsync(_proposal, Projection(), command, default, version)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Committed_receipt_survives_cancellation_but_changed_intent_cannot_reuse_it()
    {
        await SeedProposal();
        var receipt = await Command(_voter);
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, receipt, default)).IsError.Should().BeFalse();
        await Execute(SurrealWriter.UpdateOnly<RunRow>(receipt.RunId).Set(row => row.Status, RunRow.ActionRunStatusKind.Cancelled).Build());
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, receipt, default)).IsError.Should().BeFalse();
        receipt.IntentHash = "different-intent";
        (await _store.VoteAsync(_proposal, Claim("no"), _approval, _identity, receipt, default)).IsError.Should().BeTrue();
        (await _store.DecisionsAsync(_proposal, default)).Should().ContainSingle().Which.DecisionOption.Should().Be("yes");
    }

    [Fact]
    public async Task Expired_participation_is_not_membership_and_live_participation_can_vote()
    {
        await SeedProposal();
        await Execute(SurrealWriter.UpdateOnly<User>(_voter).Unset(row => row.OwnerTenantId).Build());
        await Save(new ConsentGrant { Id = "participation", GrantorUserId = "user:" + _voter, TenantId = "user:" + _tenant,
            Origin = "Participation", ParticipationRef = "project", Scopes = "wallet:read", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, await Command(_voter), default)).IsError.Should().BeTrue();
        await Execute(SurrealWriter.UpdateOnly<ConsentGrant>("participation").Set(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(10)).Build());
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, await Command(_voter), default)).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_AML_hit_append_and_vote_have_one_serial_order_and_later_votes_are_refused()
    {
        await SeedProposal();
        var receipt = await Command(_voter);
        var screening = new SurrealKycScreeningStore(_executor);
        var hit = new KycScreeningResult { Id = Guid.NewGuid().ToString("N"), SubjectUserId = _voter, TenantId = _tenant,
            LineageKey = "lineage", Decision = KycScreeningDecision.HIT, PolicyVersion = "v1", ScreenedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        var append = screening.SaveResultAsync(hit);
        var vote = _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, receipt, default);
        await Task.WhenAll(append, vote);
        (await append).IsError.Should().BeFalse();
        var decisions = await _store.DecisionsAsync(_proposal, default);
        if ((await vote).IsError)
        {
            decisions.Should().BeEmpty();
            (await _store.ReceiptAsync(receipt.Id, default)).Should().BeNull();
        }
        else
        {
            decisions.Should().ContainSingle();
            (await _store.ReceiptAsync(receipt.Id, default)).Should().NotBeNull();
        }
        (await _store.VoteAsync(_proposal, Claim("no"), _approval, _identity, await Command(_voter), default)).IsError.Should().BeTrue();
        (await _store.DecisionsAsync(_proposal, default)).Count.Should().Be(decisions.Count);
    }

    [Fact]
    public async Task Concurrent_finalizers_publish_one_immutable_snapshot()
    {
        await SeedProposal();
        _proposal.ClosesAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await Execute(SurrealWriter.UpdateOnly<GovernanceProposal>(_proposal.Id).Set(row => row.ClosesAt, _proposal.ClosesAt).Build());
        var first = await Command(_tenant); var second = await Command(_tenant);
        first.OutputJson = "{\"winner\":\"first\",\"totalVotes\":0}";
        second.OutputJson = "{\"winner\":\"second\",\"totalVotes\":0}";
        var counts = new Dictionary<string, int> { ["yes"] = 0, ["no"] = 0 };
        var results = await Task.WhenAll(_store.FinalizeAsync(_proposal, counts, first, default), _store.FinalizeAsync(_proposal, counts, second, default));
        results.Count(result => !result.IsError).Should().Be(1);
        var final = (await _store.GetAsync(_proposal.Id, default))!.FinalResultJson;
        final.Should().BeOneOf(first.OutputJson, second.OutputJson);
        var winner = final == first.OutputJson ? first : second;
        (await _store.FinalizeAsync(_proposal, counts, winner, default)).IsError.Should().BeFalse();
        (await _store.GetAsync(_proposal.Id, default))!.FinalResultJson.Should().Be(final);
    }

    [Fact]
    public async Task Finalization_is_immutable_counts_claims_only_and_refuses_before_cutoff()
    {
        await SeedProposal();
        var early = await Command(_tenant);
        (await _store.FinalizeAsync(_proposal, new Dictionary<string, int> { ["yes"] = 0, ["no"] = 0 }, early, default)).IsError.Should().BeTrue();
        // Test-only operator seed models an already closed election without wall-clock sleeps.
        _proposal.ClosesAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await Execute(SurrealWriter.UpdateOnly<GovernanceProposal>(_proposal.Id).Set(row => row.ClosesAt, _proposal.ClosesAt).Build());
        var claim = Claim("yes"); claim.DecidedAt = _proposal.ClosesAt.AddSeconds(-1);
        await Save(claim);
        await Execute(SurrealWriter.UpdateOnly<User>(_voter).Unset(row => row.OwnerTenantId).Set(row => row.IsActive, false).Build());
        await Execute(SurrealWriter.UpdateOnly<KycSubmission>(_approval.Id).Set(row => row.Status, KycStatus.REJECTED).Build());
        for (var index = 0; index < 500; index++)
            await Save(new HolonRow { Id = Guid.NewGuid().ToString("N"), Name = "Forged ballot", ProviderName = "test",
                UserId = "user:" + _voter, ParentHolonId = "holon:" + _proposal.Id, AssetType = ConsumerGovernanceHolonTypes.VoteAssetType,
                Metadata = JsonSerializer.SerializeToElement(new { option = "no", castAt = "1900-01-01" }) });
        var receipt = await Command(_tenant);
        var counts = new Dictionary<string, int> { ["yes"] = 1, ["no"] = 0 };
        receipt.OutputJson = JsonSerializer.Serialize(new { totalVotes = 1, counts, tallySchemaVersion = 1 });
        var completed = await _store.FinalizeAsync(_proposal, counts, receipt, default);
        completed.IsError.Should().BeFalse(completed.Message);
        var saved = await _store.GetAsync(_proposal.Id, default);
        saved!.FinalResultJson.Should().Be(receipt.OutputJson);
        (await _store.FinalizeAsync(_proposal, counts, receipt, default)).IsError.Should().BeFalse();
        var altered = await Command(_tenant);
        (await _store.FinalizeAsync(_proposal, new Dictionary<string, int> { ["yes"] = 0, ["no"] = 500 }, altered, default)).IsError.Should().BeTrue();
        (await _store.GetAsync(_proposal.Id, default))!.FinalResultJson.Should().Be(saved.FinalResultJson);
    }

    [Fact]
    public async Task Snapshot_excludes_new_members_and_user_explicit_grants_do_not_create_membership()
    {
        await SeedProposal();
        await Execute(SurrealWriter.UpdateOnly<User>(_voter).Unset(row => row.OwnerTenantId).Build());
        await Save(new ConsentGrant { Id = "consent", GrantorUserId = "user:" + _voter, TenantId = "user:" + _tenant,
            Origin = "UserExplicit", Scopes = "wallet:read", ParticipationRef = "external" });
        (await _store.MembersAsync(_tenant, default)).Should().NotContain(_voter);
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, await Command(_voter), default)).IsError.Should().BeTrue();
        await Execute(SurrealWriter.UpdateOnly<ConsentGrant>("consent").Set(row => row.Origin, "Participation").Build());
        (await _store.MembersAsync(_tenant, default)).Should().Contain(_voter);
        _proposal.ElectorateJson = JsonSerializer.Serialize(new[] { _tenant });
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, await Command(_voter), default)).IsError.Should().BeTrue();
    }

    private async Task SeedProposal() { await Save(_proposal); await Save(Projection()); }
    private ConsumerGovernanceService CreationService() => new(_store, new SurrealUserStore(_executor), new SurrealApiKeyStore(_executor),
        null!, new SurrealKycStore(_executor), new SurrealKycIdentityStore(_executor), new HolonManager(new SurrealHolonStore(_executor)), NodeGovernanceGuard.Unrestricted);
    private static string Frozen(ActionNodeExecutionContext context)
    {
        using var config = JsonDocument.Parse(context.Node.Config);
        return CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new ActionFrozenNodeInput(1, context.RunId, context.NodeId,
            context.Node.NodeType, context.ActingUserId, context.ActingTenantId, config.RootElement, context.UpstreamExecutions, context.AllRunExecutions)));
    }
    private async Task<ActionNodeExecutionContext> HandlerContext(string actor, GovernanceProposalCreateNodeConfig config)
    {
        var node = new ActionNode { Id = Guid.NewGuid(), NodeType = ActionNodeType.GovernanceProposalCreate, Config = JsonSerializer.Serialize(config) };
        var context = new ActionNodeExecutionContext(Guid.NewGuid(), node.Id, new ActionDefinition { Nodes = [node] },
            Guid.ParseExact(actor, "N"), actingTenantId: Guid.ParseExact(_tenant, "N"));
        await Save(new RunRow { Id = context.RunId.ToString("N"), UserId = "user:" + actor, ActingTenantId = "user:" + _tenant,
            ActionId = "action:fixture", Status = RunRow.ActionRunStatusKind.Running, StartedAt = DateTimeOffset.UtcNow });
        await Save(new ExecutionRow { Id = Guid.NewGuid().ToString("N"), RunId = "action_run:" + context.RunId.ToString("N"),
            NodeId = "action_node:" + node.Id.ToString("N"), State = ExecutionRow.ActionNodeState.Running,
            FrozenInput = Frozen(context), StartedAt = DateTimeOffset.UtcNow });
        return context;
    }
    private HolonRow Projection() => new() { Id = _proposal.Id, Name = "Election", ProviderName = "AZOA", UserId = "user:" + _tenant,
        AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType, IsPublic = true, IsActive = true,
        Metadata = JsonSerializer.SerializeToElement(new { status = "Open" }) };
    private PersonOperationClaim Claim(string option) => new()
    {
        Id = PersonOperationClaim.RecordIdFor(_tenant, _proposal.OperationKey, "person"), UniquenessScope = _tenant,
        OperationKey = _proposal.OperationKey, PersonKey = "person", UserId = _voter, TenantId = "user:" + _tenant,
        DecisionOption = option, DecidedAt = DateTimeOffset.UtcNow, DecisionClosesAt = _proposal.ClosesAt,
        GovernanceProposalId = _proposal.Id, GovernanceDefinitionHash = _proposal.DefinitionHash,
        GovernanceDecisionHash = CanonicalJson.Hash(new { _proposal.DefinitionHash, option }), GovernanceEligibilityJson = "{\"eligible\":true}"
    };
    private async Task<GovernanceCommandReceipt> Command(string actor)
    {
        var run = Guid.NewGuid().ToString("N"); var node = Guid.NewGuid().ToString("N");
        var frozen = "{\"command\":\"" + Guid.NewGuid().ToString("N") + "\"}";
        await Save(new RunRow { Id = run, UserId = "user:" + actor, ActingTenantId = "user:" + _tenant,
            ActionId = "action:fixture", Status = RunRow.ActionRunStatusKind.Running, StartedAt = DateTimeOffset.UtcNow });
        await Save(new ExecutionRow { Id = Guid.NewGuid().ToString("N"), RunId = "action_run:" + run, NodeId = "action_node:" + node,
            State = ExecutionRow.ActionNodeState.Running, FrozenInput = frozen, StartedAt = DateTimeOffset.UtcNow });
        return new GovernanceCommandReceipt { Id = Guid.NewGuid().ToString("N"), RunId = run, NodeId = node, ActorId = actor,
            TenantId = _tenant, IntentHash = CanonicalJson.HashCanonical(frozen), ProposalId = _proposal.Id,
            OutputJson = "{\"recorded\":true}", CreatedAt = DateTimeOffset.UtcNow };
    }
    private Task Save<T>(T value) where T : class, ISurrealRecord, new() => Execute(SurrealWriter.Create(value));
    private async Task Execute(SurrealQuery query) { var result = await _executor.ExecuteAsync(query); result.EnsureAllOk(); }
}
