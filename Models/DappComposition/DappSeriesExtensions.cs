using System.Text.Json;
using AZOA.WebAPI.Core.Json;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>
/// Partial-class accessors for the source-gen'd <see cref="DappSeries"/>
/// POCO. Adds caller-friendly Guid and Dictionary views over the
/// storage-side string-and-JsonElement fields, plus domain predicates that
/// belong to the entity itself.
/// </summary>
/// <remarks>
/// Pattern documented in <c>Persistence/SurrealDb/CONVENTION.md §3.1</c>.
/// Lives under <c>Models/DappComposition/</c> for organizational locality
/// with the rest of the dapp-composition surface; the namespace
/// (<c>AZOA.WebAPI.Persistence.SurrealDb.Models</c>) is what matters for the
/// partial-class compiler match.
/// </remarks>
public partial class DappSeries
{
    /// <summary>Caller-friendly Guid view of the storage-side Id (Guid('N') hex).</summary>
    [NotMapped]
    public Guid IdGuid
    {
        get => Guid.ParseExact(Id, "N");
        set => Id = value.ToString("N");
    }

    /// <summary>Caller-friendly Guid view of <c>avatar_id</c>.</summary>
    [NotMapped]
    public Guid AvatarIdGuid
    {
        get => Guid.ParseExact(AvatarId, "N");
        set => AvatarId = value.ToString("N");
    }

    /// <summary>
    /// Caller-friendly Guid view of <c>star_odk_id</c>. Null when the series
    /// has not yet been generated.
    /// </summary>
    [NotMapped]
    public Guid? StarOdkIdGuid
    {
        get => string.IsNullOrEmpty(StarOdkId) ? null : Guid.ParseExact(StarOdkId, "N");
        set => StarOdkId = value?.ToString("N");
    }

    /// <summary>
    /// Caller-friendly <see cref="IDictionary{TKey, TValue}"/> view over the
    /// storage-side <see cref="JsonElement"/> <c>SharedConfig</c> field.
    /// Round-trips through <see cref="JsonElementExtensions"/>; safe for
    /// typical config-blob sizes (small N keys).
    /// </summary>
    [NotMapped]
    public IDictionary<string, string> SharedConfigDict
    {
        get => SharedConfig.ToStringDictionary();
        set => SharedConfig = ((IReadOnlyDictionary<string, string>)value).ToJsonElement();
    }

    /// <summary>Domain predicate: does this series belong to the given avatar?</summary>
    public bool OwnedBy(Guid avatarId) => AvatarId == avatarId.ToString("N");

    /// <summary>
    /// Factory for a new draft series. Encapsulates the Guid-to-hex-string
    /// conversion and the empty-SharedConfig initialization that managers
    /// would otherwise reimplement.
    /// </summary>
    public static DappSeries NewDraft(Guid avatarId, string name, string? description = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Description = description,
        AvatarId = avatarId.ToString("N"),
        Status = StatusKind.Draft,
        SharedConfig = JsonDocument.Parse("{}").RootElement,
        CreatedDate = DateTimeOffset.UtcNow,
    };
}
