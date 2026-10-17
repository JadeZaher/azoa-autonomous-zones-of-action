using Algorand.Algod.Model.Transactions;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Reads the called application id, which Algorand2 declares per on-completion subclass; see AGENTS.md §Application call identity.</summary>
internal static class AlgorandApplicationCall
{
    /// <summary>The target application id, or null for a create or any unrecognized call shape (callers fail closed).</summary>
    public static ulong? Id(ApplicationCallTransaction transaction) => transaction switch
    {
        ApplicationNoopTransaction call => call.ApplicationId,
        ApplicationOptInTransaction call => call.ApplicationId,
        ApplicationCloseOutTransaction call => call.ApplicationId,
        ApplicationClearStateTransaction call => call.ApplicationId,
        ApplicationUpdateTransaction call => call.ApplicationId,
        ApplicationDeleteTransaction call => call.ApplicationId,
        _ => null,
    };
}
