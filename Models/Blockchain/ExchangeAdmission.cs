using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>One exchange admission attempt: the admitted or replayed source, or why nothing was written.</summary>
/// <remarks><see cref="DeadlinePassed"/> is set only when no source exists under the key; see Services/Reserve/AGENTS.md §admission-deadline.</remarks>
public sealed record ExchangeAdmission(ExchangeHoldSource? Source, bool DeadlinePassed)
{
    /// <summary>Nothing admitted: the key names a different exchange, or an authority fence refused.</summary>
    public static ExchangeAdmission Refused { get; } = new(null, false);

    /// <summary>Nothing admitted: the deadline had passed at the admission write.</summary>
    public static ExchangeAdmission Late { get; } = new(null, true);

    /// <summary>The admitted or replayed source, or <see cref="Refused"/> when there is none.</summary>
    public static ExchangeAdmission Of(ExchangeHoldSource? source) => source is null ? Refused : new(source, false);
}
