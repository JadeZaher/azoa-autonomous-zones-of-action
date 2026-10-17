using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Provider-owned exact group preparation, replay and authoritative exchange observation.</summary>
public interface IExchangeTransactionModule : IBlockchainProviderModule
{
    /// <summary>Claims immutable unsigned bytes before custody and returns the same canonical signed winner on retry; never sends.</summary>
    Task<AZOAResult<PreparedExchangeWire>> PrepareExchangeAsync(ExchangeWireIntent intent, CancellationToken ct = default);

    /// <summary>Rebroadcasts only the exact persisted signed group after the caller has durably authorized first egress.</summary>
    Task SubmitExchangeAsync(PreparedExchangeWire prepared, CancellationToken ct = default);

    /// <summary>Observes the complete original group; partial or unavailable evidence cannot confirm or prove no effect.</summary>
    Task<AZOAResult<ExchangeWireObservation>> ObserveExchangeAsync(PreparedExchangeWire prepared, CancellationToken ct = default);
}
