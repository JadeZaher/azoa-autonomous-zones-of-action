using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>
/// Partial-class accessors for the source-gen'd <see cref="DappSeriesAction"/>
/// POCO. Adds caller-friendly Guid views over the storage-side Guid('N')
/// hex string fields plus a factory.
/// </summary>
/// <remarks>
/// Pattern documented in <c>Persistence/SurrealDb/CONVENTION.md §3.1</c>.
/// </remarks>
public partial class DappSeriesAction
{
    /// <summary>Caller-friendly Guid view of the storage-side Id (Guid('N') hex).</summary>
    [NotMapped]
    public Guid IdGuid
    {
        get => Guid.ParseExact(Id, "N");
        set => Id = value.ToString("N");
    }

    /// <summary>Caller-friendly Guid view of <c>dapp_series_id</c>.</summary>
    [NotMapped]
    public Guid DappSeriesIdGuid
    {
        get => Guid.ParseExact(DappSeriesId, "N");
        set => DappSeriesId = value.ToString("N");
    }

    /// <summary>Caller-friendly Guid view of <c>action_id</c>.</summary>
    [NotMapped]
    public Guid ActionIdGuid
    {
        get => Guid.ParseExact(ActionId, "N");
        set => ActionId = value.ToString("N");
    }

    /// <summary>
    /// Factory for a new ordered entry. Encapsulates Guid-to-hex-string
    /// conversion at the API boundary.
    /// </summary>
    public static DappSeriesAction NewEntry(
        Guid dappSeriesId, Guid actionId, int order, string? inputMappings = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        DappSeriesId = dappSeriesId.ToString("N"),
        ActionId = actionId.ToString("N"),
        Order = order,
        InputMappings = inputMappings,
    };
}
