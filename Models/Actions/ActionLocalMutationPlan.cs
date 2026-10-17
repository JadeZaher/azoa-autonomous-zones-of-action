using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Models.Actions;

/// <summary>Bounded read witnesses and staged writes for one atomic local action effect.</summary>
public sealed class ActionLocalMutationPlan
{
    public const int MaximumRows = 2048;
    public Dictionary<Guid, Holon?> HolonReads { get; } = [];
    public Dictionary<Guid, Wallet?> WalletReads { get; } = [];
    public Dictionary<Guid, Holon?> HolonWrites { get; } = [];
    public Dictionary<Guid, Wallet?> WalletWrites { get; } = [];
    public List<HolonQueryWitness> HolonQueries { get; } = [];
    public List<WalletQueryWitness> WalletQueries { get; } = [];
    public string? Refusal { get; private set; }
    public bool RefusalIsDeterministic { get; private set; }

    public void Reject(string reason, bool deterministic = false)
    {
        if (Refusal is not null) return;
        Refusal = reason;
        RefusalIsDeterministic = deterministic;
    }

    public bool IsWithinBounds()
    {
        if (HolonReads.Count + WalletReads.Count + HolonWrites.Count + WalletWrites.Count
            + HolonQueries.Count + WalletQueries.Count > MaximumRows)
            Reject($"A local action transaction may inspect or mutate at most {MaximumRows} rows.", deterministic: true);
        return Refusal is null;
    }
}

/// <summary>Exact original query membership that must remain unchanged at commit.</summary>
public sealed record HolonQueryWitness(HolonQueryRequest? Query, IReadOnlyList<Guid> Ids);

/// <summary>Exact wallet query membership; null owner denotes the uniqueness lookup.</summary>
public sealed record WalletQueryWitness(Guid? Owner, IReadOnlyList<Guid> Ids);
