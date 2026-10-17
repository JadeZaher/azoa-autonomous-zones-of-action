using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Rejects startup or key retirement when a retained protocol key would become unreadable.</summary>
public sealed class WttStageKeyReadiness(IWttStageKeyStore store, IWttStageKeyProtector protector)
{
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (await store.FindUnavailableKeyAsync(protector.AvailableKeyVersions, ct) is not null)
            throw new InvalidOperationException("A retained WTT stage encryption key version is missing; restore it or rewrap before retirement.");
    }
}
