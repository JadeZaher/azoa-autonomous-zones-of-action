using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Creates a bounded, signed, privacy-safe checkpoint over governance audit history.</summary>
public interface INodeTransparencyHistoryService
{
    /// <summary>Returns the current checkpoint only when it is a valid extension of local protected history.</summary>
    Task<NodeTransparencyHistoryAvailability> TryGetAsync(CancellationToken ct = default);
}
