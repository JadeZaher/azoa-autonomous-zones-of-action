namespace AZOA.WebAPI.Helpers;

/// <summary>Derives stable entity IDs from one frozen action command and deterministic planning order.</summary>
public sealed class ActionLocalEntityIds(string commandId)
{
    private int _index;

    public Guid Next() => Guid.ParseExact(CanonicalJson.Hash(new
        { Domain = "action-local-entity-v1", CommandId = commandId, Index = _index++ })[..32], "N");
}
