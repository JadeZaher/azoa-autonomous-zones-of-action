using System.Text.Json;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Validates saved operation identity before invoking a registered domain observer.</summary>
public sealed class ActionOperationRecovery
{
    private readonly IReadOnlyDictionary<string, IActionExternalOperationResolver> _resolvers;

    public ActionOperationRecovery(IEnumerable<IActionExternalOperationResolver> resolvers)
        => _resolvers = resolvers.ToDictionary(item => item.Domain, StringComparer.Ordinal);

    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionNodeExecution execution, ActionRun run, CancellationToken ct)
    {
        var reference = execution.ExternalOperation;
        if (reference is null)
            throw new InvalidOperationException("A legacy execution requires evidence-based operator recovery.");
        if (reference.Version != 1 || reference.ActorUserId != run.UserId || reference.ActingTenantId != run.ActingTenantId
            || execution.RunId != run.Id || string.IsNullOrWhiteSpace(execution.FrozenInput)
            || !_resolvers.TryGetValue(reference.Domain, out var resolver))
            return Ambiguous(reference, "Saved action operation identity or resolver is unavailable.");
        try
        {
            using var document = JsonDocument.Parse(execution.FrozenInput);
            var input = document.RootElement.Deserialize<ActionFrozenNodeInput>();
            if (CanonicalJson.Canonicalize(document.RootElement) != execution.FrozenInput
                || CanonicalJson.HashCanonical(execution.FrozenInput) != reference.IntentHash
                || input is null || input.Version != 1 || input.RunId != run.Id
                || input.NodeId != execution.NodeId || input.ActorUserId != run.UserId
                || input.ActingTenantId != run.ActingTenantId)
                return Ambiguous(reference, "Saved action operation input does not match its admitted identity.");
            if (run.DefinitionSnapshotJson is null
                || ActionRunSnapshot.Read(run).Nodes.SingleOrDefault(node => node.Id == input.NodeId)?.NodeType != input.NodeType)
                return Ambiguous(reference, "Saved action operation does not match its frozen graph node.");
        }
        catch (JsonException)
        {
            return Ambiguous(reference, "Saved action operation input is not valid canonical JSON.");
        }
        var observed = await resolver.ObserveAsync(reference, execution.FrozenInput, ct);
        return ActionOperationIdentity.Matches(reference, observed.Reference)
            ? observed : Ambiguous(reference, "Domain observation returned a different operation identity.");
    }

    private static ActionExternalOperationObservation Ambiguous(ActionExternalOperationReference reference, string detail)
        => new(ActionExternalOperationStatus.Ambiguous, reference, Detail: detail);
}
