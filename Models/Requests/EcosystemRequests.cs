using AZOA.WebAPI.Models.Ecosystem;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>Attach a DappSeries (or nested ActionGraphBundle) as a node in an ActionGraphBundle's
/// ecosystem tree. The owner is always the authenticated user — no owner id
/// is accepted on the body (IDOR-resistant, ActionGraphBundle precedent).</summary>
public class AddDappSeriesRequest
{
    /// <summary>Guid of the DappSeries (or ActionGraphBundle when <see cref="RefKind"/> is ActionGraphBundle) to attach.</summary>
    public Guid RefId { get; set; }

    /// <summary>What the node references. Defaults to a DappSeries.</summary>
    public EcosystemRefKind RefKind { get; set; } = EcosystemRefKind.DappSeries;

    /// <summary>Parent node in the tree; null attaches the node directly under the ecosystem root.</summary>
    public Guid? ParentNodeId { get; set; }

    /// <summary>Optional display label for the tree UI.</summary>
    public string? Label { get; set; }

    /// <summary>Optional ecosystem name/target chain used only when the ecosystem
    /// does not yet exist for this ActionGraphBundle (first attach lazily creates it).</summary>
    public string? EcosystemName { get; set; }
    public string? TargetChain { get; set; }
}
