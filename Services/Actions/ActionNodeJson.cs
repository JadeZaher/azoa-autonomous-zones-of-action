using System.Text.Json;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for action node config
/// deserialization and result serialization. Lifted verbatim from the former
/// <c>ActionManager.JsonOptions</c> so handler dispatch is byte-identical.
/// </summary>
public static class ActionNodeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
