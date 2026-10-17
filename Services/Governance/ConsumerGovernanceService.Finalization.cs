using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;

namespace AZOA.WebAPI.Services.Governance;

public sealed partial class ConsumerGovernanceService
{
    public async Task<ActionNodeHandlerResult> FinalizeAsync(ActionNodeExecutionContext context, GovernanceTallyNodeConfig config, CancellationToken ct)
    {
        var command = Receipt(context, config.ProposalHolonId.ToString("N"));
        var previous = await ReplayAsync(command, ct);
        if (previous is not null) return previous;
        var proposal = await store.GetAsync(command.ProposalId, ct);
        if (proposal is null || proposal.TenantId != command.ActorId
            || (context.ActingTenantId.HasValue && context.ActingTenantId.Value.ToString("N") != proposal.TenantId))
            return ActionNodeResults.Fail("Only the tenant principal may finalize this election.");
        var authorized = await TenantAuthority.CheckAsync(context.ActingUserId, users, apiKeys, DateTimeOffset.UtcNow, ct);
        if (authorized.IsError || !authorized.Result) return ActionNodeResults.Fail("Current tenant provisioning authority is required.");
        if (proposal.FinalResultJson is not null) return ActionNodeResults.Ok(proposal.FinalResultJson);
        if (DateTimeOffset.UtcNow < proposal.ClosesAt) return ActionNodeResults.Fail("Voting must close before finalization.");
        var definition = JsonSerializer.Deserialize<Definition>(proposal.DefinitionJson)!;
        var rows = await store.DecisionsAsync(proposal, ct);
        var counted = GovernanceClaimLedgerTally.CountFrozenDecisions(proposal, definition.Options, rows);
        if (counted.IsError || counted.Result is null) return ActionNodeResults.Fail(counted.Message);
        var counts = counted.Result.CountsByOption;
        var total = counted.Result.TotalVotes;
        command.OutputJson = JsonSerializer.Serialize(new { proposalId = config.ProposalHolonId, tallySchemaVersion = 1,
            totalVotes = total, counts, minimumVotes = proposal.MinimumVotes, quorumReached = total >= proposal.MinimumVotes,
            closesAt = Iso(proposal.ClosesAt), tallyAt = Iso(command.CreatedAt), proposal.DefinitionHash,
            tallyHash = GovernanceTallyHash.Compute(config.ProposalHolonId, Iso(proposal.ClosesAt), total, counts) }, ActionNodeJson.Options);
        var result = await store.FinalizeAsync(proposal, counts, command, ct);
        if (result.IsError)
        {
            var concurrent = await store.GetAsync(proposal.Id, ct);
            if (concurrent?.FinalResultJson is not null) return ActionNodeResults.Ok(concurrent.FinalResultJson);
        }
        return Result(result);
    }
}
