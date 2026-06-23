using System.Text.Json;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for quest node config
/// deserialization and result serialization. Lifted verbatim from the former
/// <c>QuestManager.JsonOptions</c> so handler dispatch is byte-identical.
/// </summary>
public static class QuestNodeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
