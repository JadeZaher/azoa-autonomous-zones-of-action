namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Immutable associated-data identity for one protocol stage key.</summary>
public sealed record WttStageKeyBinding(string ProtocolIdentity, Guid ActorUserId, Guid? TenantId,
    Guid WalletId, string OperationId, string StageRole, int Generation, string SigningScope);

public sealed record WttProtectedStageKey(string KeyVersion, string NonceBase64, string TagBase64, string CiphertextBase64);

/// <summary>Versioned encryption keys supplied only through the node's secret configuration.</summary>
public sealed class WttStageKeyOptions
{
    public string ActiveKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}
