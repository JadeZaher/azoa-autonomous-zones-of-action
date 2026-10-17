using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Interfaces.Actions;

/// <summary>Recovers exact admitted domain operations without fabricating an actor principal.</summary>
public interface IActionExternalOperationResolver
{
    /// <summary>Unique server-controlled envelope domain.</summary>
    string Domain { get; }

    /// <summary>Observes saved intent; any permitted writes must enforce the domain's live authority rules.</summary>
    Task<ActionExternalOperationObservation> ObserveAsync(
        ActionExternalOperationReference reference, string canonicalInput, CancellationToken ct = default);
}
