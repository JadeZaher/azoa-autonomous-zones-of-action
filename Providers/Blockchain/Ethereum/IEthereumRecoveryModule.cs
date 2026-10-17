using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Authenticated recovery of immutable Ethereum nonce admissions.</summary>
public interface IEthereumRecoveryModule
{
    /// <summary>Reconciles or replays only explicitly authorized immutable submissions.</summary>
    Task ReplayAuthorizedAsync(CancellationToken ct);
    /// <summary>Activates a signer only against a clean finalized nonce checkpoint and configured deployment epoch.</summary>
    Task<AZOAResult<bool>> ActivateSignerAsync(string sender, Guid actor, CancellationToken ct);
    /// <summary>Returns public recovery metadata, never signed wire or signing contexts.</summary>
    Task<IReadOnlyList<EthereumPreparationView>> ListPreparationsAsync(Guid owner, CancellationToken ct);
    /// <summary>Resumes legacy admitted work or authorizes same-nonce cancellation under the original signer's authority.</summary>
    Task<AZOAResult<EthereumPreparationView>> RecoverPreparationAsync(string id, Guid actor, Guid tenant,
        bool platformOperator, bool cancel, string maximumFeePerGas, string reason, IReadOnlySet<string> signingScopes, CancellationToken ct);
}

public sealed record EthereumPreparationView(string Id, string Network, string Sender, string State,
    string Nonce, string? TransactionId, string PreparationKey, DateTimeOffset UpdatedAt);
