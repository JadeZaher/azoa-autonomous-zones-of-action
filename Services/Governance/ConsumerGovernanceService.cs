using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>Executes immutable tenant elections through atomic command receipts.</summary>
public sealed partial class ConsumerGovernanceService(SurrealGovernanceStore store, IUserStore users, IApiKeyStore apiKeys,
    IValueAccessService valueAccess, IKycStore kyc, IKycIdentityStore identities, IHolonManager holons, INodeGovernanceGuard governance)
{
    public async Task<ActionNodeHandlerResult> CreateAsync(ActionNodeExecutionContext context,
        GovernanceProposalCreateNodeConfig config, CancellationToken ct)
    {
        var tenant = context.ActingTenantId ?? context.ActingUserId;
        if (tenant != context.ActingUserId || tenant == Guid.Empty) return ActionNodeResults.Fail("Only the tenant principal may administer an election.");
        var command = Receipt(context, GovernanceCommandIdentity.Id(context));
        var previous = await ReplayAsync(command, ct);
        if (previous is not null) return previous;
        var authority = await TenantAuthority.CheckAsync(tenant, users, apiKeys, DateTimeOffset.UtcNow, ct);
        if (authority.IsError || !authority.Result) return ActionNodeResults.Fail("Current tenant provisioning authority is required.");
        if (config.Options is null || config.Options.Count is < 2 or > 100 || string.IsNullOrWhiteSpace(config.Name)
            || config.Name.Length > 500 || config.Description is null || config.Description.Length > 20_000)
            return ActionNodeResults.Invalid("Provide a bounded proposal name, description and between two and one hundred options.");
        var options = config.Options.Select(option => PersonUniquenessRequirement.CanonicalizeDecision(option)).ToArray();
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(config.Name) || options.Length is < 2 or > 100 || options.Any(option => option is null)
            || options.Distinct(StringComparer.Ordinal).Count() != options.Length || config.MinimumVotes < 0
            || config.ClosesAt is not { } closes || closes <= now || closes <= (config.OpensAt ?? now)
            || !ConsumerGovernanceHolonTypes.TryParseScope(config.UniquenessScope ?? "Tenant", out var scope))
            return ActionNodeResults.Invalid("Declare a name, distinct bounded options, nonnegative quorum and a future closing time after opening.");
        var policyVersion = await store.PolicyVersionAsync(ct);
        var policy = await governance.EnsureAssetTypeAllowedAsync(ConsumerGovernanceHolonTypes.ProposalAssetType, "governance:create", ct);
        if (!policy.IsAllowed) return ActionNodeResults.Fail(policy.ConsumerMessage);
        if (config.ParentHolonId is { } parentId)
        {
            var parent = await holons.GetAsync(parentId, context.ActingUserId);
            if (parent.IsError || parent.Result?.UserId != context.ActingUserId)
                return ActionNodeResults.Fail("The parent must be owned by the tenant principal.");
        }
        var members = await store.MembersAsync(tenant.ToString("N"), ct);
        if (members.Length > 100_000) return ActionNodeResults.Invalid("The electorate exceeds the atomic election limit of 100000 members.");
        var proposalId = Guid.ParseExact(command.ProposalId, "N");
        var definition = new Definition(config.Name.Trim(), config.Description, options.Select(option => option!).ToArray(),
            config.OpensAt ?? now, closes, config.MinimumVotes, scope.ToString());
        var proposal = new GovernanceProposal
        {
            Id = command.ProposalId, TenantId = tenant.ToString("N"), CreatorId = context.ActingUserId.ToString("N"),
            IntentHash = command.IntentHash, DefinitionJson = JsonSerializer.Serialize(definition),
            ElectorateJson = JsonSerializer.Serialize(members), ElectorateHash = CanonicalJson.Hash(members),
            UniquenessScope = scope == PersonUniquenessScope.Global ? KycPersonKeyDeriver.GlobalScopeSentinel : tenant.ToString("N"),
            OperationKey = ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposalId),
            OpensAt = definition.OpensAt, ClosesAt = closes, MinimumVotes = config.MinimumVotes, CreatedAt = now
        };
        proposal.DefinitionHash = CanonicalJson.Hash(new { proposal.Id, proposal.TenantId, definition, proposal.ElectorateHash, proposal.MembershipRuleVersion });
        var metadata = new Dictionary<string, string>
        {
            [ConsumerGovernanceHolonTypes.ProposalMetadata.Status] = ConsumerGovernanceHolonTypes.StatusOpen,
            [ConsumerGovernanceHolonTypes.ProposalMetadata.Options] = ConsumerGovernanceHolonTypes.SerializeOptions(definition.Options),
            [ConsumerGovernanceHolonTypes.ProposalMetadata.ElectorateTenant] = tenant.ToString("N"),
            [ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope] = scope.ToString(),
            [ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey] = proposal.OperationKey,
            [ConsumerGovernanceHolonTypes.ProposalMetadata.OpensAt] = Iso(definition.OpensAt),
            [ConsumerGovernanceHolonTypes.ProposalMetadata.ClosesAt] = Iso(closes),
            ["definitionHash"] = proposal.DefinitionHash, ["electorateCount"] = members.Length.ToString(CultureInfo.InvariantCulture)
        };
        var projection = new Holon { Id = proposal.Id, UserId = SurrealLink.ToLink("user", proposal.CreatorId),
            ParentHolonId = config.ParentHolonId.HasValue ? SurrealLink.ToLink("holon", config.ParentHolonId.Value.ToString("N")) : null,
            Name = definition.Name, Description = definition.Description, AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType,
            ProviderName = "AZOA", IsPublic = true, IsActive = true, Metadata = JsonSerializer.SerializeToElement(metadata), CreatedDate = now };
        command.OutputJson = JsonSerializer.Serialize(new { proposalId, definition.Name, options = definition.Options,
            definition.OpensAt, definition.ClosesAt, definition.MinimumVotes, electorateCount = members.Length, proposal.DefinitionHash }, ActionNodeJson.Options);
        return Result(await store.CreateAsync(proposal, projection, command, ct, policyVersion));
    }

    public async Task<ActionNodeHandlerResult> VoteAsync(ActionNodeExecutionContext context, GovernanceVoteNodeConfig config, CancellationToken ct)
    {
        var command = Receipt(context, config.ProposalHolonId.ToString("N"));
        var previous = await ReplayAsync(command, ct);
        if (previous is not null) return previous;
        var proposal = await store.GetAsync(command.ProposalId, ct);
        if (proposal is null || (context.ActingTenantId.HasValue && context.ActingTenantId.Value.ToString("N") != proposal.TenantId))
            return ActionNodeResults.Fail("Proposal unavailable to this caller.");
        var definition = JsonSerializer.Deserialize<Definition>(proposal.DefinitionJson)!;
        var option = PersonUniquenessRequirement.CanonicalizeDecision(config.Option);
        if (option is null || !definition.Options.Contains(option, StringComparer.Ordinal)) return ActionNodeResults.Invalid("Select a declared option.");
        var tenant = Guid.ParseExact(proposal.TenantId, "N");
        var approval = await kyc.GetLatestSubmissionAsync(context.ActingUserId, tenant, ct);
        if (approval.IsError || approval.Result is null) return ActionNodeResults.Fail("Current verification is required.");
        var access = await valueAccess.RequireValueAccessAsync(context.ActingUserId, tenant, ct);
        if (access.IsError || !access.Result) return ActionNodeResults.Fail("Current verification is required.");
        var identity = await identities.GetByUserAsync(proposal.TenantId, command.ActorId, ct);
        if (identity.IsError || identity.Result is null || identity.Result.ErasedAt is not null)
            return ActionNodeResults.Fail("Current verification is required.");
        var person = proposal.UniquenessScope == proposal.TenantId ? identity.Result.PersonKey : identity.Result.GlobalPersonKey;
        if (string.IsNullOrWhiteSpace(person)) return ActionNodeResults.Fail("Current verification is required.");
        var now = DateTimeOffset.UtcNow;
        var claim = new PersonOperationClaim
        {
            Id = PersonOperationClaim.RecordIdFor(proposal.UniquenessScope, proposal.OperationKey, person),
            UniquenessScope = proposal.UniquenessScope, OperationKey = proposal.OperationKey, PersonKey = person,
            UserId = command.ActorId, TenantId = SurrealLink.ToLink("user", proposal.TenantId), ClaimedAt = now,
            DecisionOption = option, DecidedAt = now, DecisionClosesAt = proposal.ClosesAt, ModifiedDate = now,
            GovernanceProposalId = proposal.Id, GovernanceDefinitionHash = proposal.DefinitionHash,
            GovernanceDecisionHash = CanonicalJson.Hash(new { proposal.DefinitionHash, option }),
            GovernanceEligibilityJson = JsonSerializer.Serialize(new { identityId = SurrealLink.FromLink(identity.Result.Id),
                approvalId = approval.Result.Id, proposal.ElectorateHash, proposal.MembershipRuleVersion, castAt = now })
        };
        command.OutputJson = JsonSerializer.Serialize(new { proposalId = config.ProposalHolonId, option, castAt = now,
            decisionHash = claim.GovernanceDecisionHash, receiptId = command.Id }, ActionNodeJson.Options);
        return Result(await store.VoteAsync(proposal, claim, approval.Result, SurrealLink.FromLink(identity.Result.Id)!, command, ct));
    }

    private async Task<ActionNodeHandlerResult?> ReplayAsync(GovernanceCommandReceipt command, CancellationToken ct)
    {
        var saved = await store.ReceiptAsync(command.Id, ct);
        return saved is null ? null : SurrealGovernanceStore.SameCommand(saved, command)
            ? ReceiptResult(saved) : ActionNodeResults.Fail("Command identity conflicts with a previous intent.");
    }
    public async Task<ActionNodeHandlerResult> RecordInvalidAsync(ActionNodeExecutionContext context,
        ActionNodeHandlerResult invalid, CancellationToken ct)
    {
        if (!invalid.IsError || invalid.Retriable) return invalid;
        var proposalId = GovernanceCommandIdentity.Id(context);
        using var config = JsonDocument.Parse(context.Node.Config);
        if (context.Node.NodeType != ActionNodeType.GovernanceProposalCreate && config.RootElement.ValueKind == JsonValueKind.Object)
        {
            var configuredId = config.RootElement.EnumerateObject().FirstOrDefault(property =>
                property.Name.Equals("proposalHolonId", StringComparison.OrdinalIgnoreCase)).Value;
            if (configuredId.ValueKind == JsonValueKind.String && configuredId.TryGetGuid(out var id)) proposalId = id.ToString("N");
        }
        var command = Receipt(context, proposalId);
        command.IsError = true;
        command.Error = invalid.Message ?? "Invalid election command.";
        command.OutputJson = "null";
        return Result(await store.RejectAsync(command, ct));
    }
    private static GovernanceCommandReceipt Receipt(ActionNodeExecutionContext context, string proposalId) => new()
    {
        Id = GovernanceCommandIdentity.Id(context), ActorId = context.ActingUserId.ToString("N"),
        RunId = context.RunId.ToString("N"), NodeId = context.NodeId.ToString("N"),
        TenantId = context.ActingTenantId?.ToString("N") ?? "direct", ProposalId = proposalId,
        IntentHash = GovernanceCommandIdentity.IntentHash(context), CreatedAt = DateTimeOffset.UtcNow
    };
    private static ActionNodeHandlerResult Result(AZOAResult<GovernanceCommandReceipt> result)
        => result.IsError || result.Result is null ? ActionNodeResults.Fail(result.Message) : ReceiptResult(result.Result);
    private static ActionNodeHandlerResult ReceiptResult(GovernanceCommandReceipt receipt)
        => receipt.IsError ? ActionNodeResults.Invalid(receipt.Error ?? "Invalid election command.") : ActionNodeResults.Ok(receipt.OutputJson);
    private static string Iso(DateTimeOffset instant) => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public sealed record Definition(string Name, string Description, string[] Options, DateTimeOffset OpensAt, DateTimeOffset ClosesAt, int MinimumVotes, string Scope);
}
