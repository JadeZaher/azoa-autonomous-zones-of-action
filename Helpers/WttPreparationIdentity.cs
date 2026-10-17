using System.Text.Json;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Helpers;

/// <summary>Stable stage identity excludes replaceable candidate plans while its binding fixes the accepted intent and quote.</summary>
public static class WttPreparationIdentity
{
    public static string Id(WttPreparationBinding binding)
    {
        if (binding.ActorUserId == Guid.Empty || binding.Generation < 0 || binding.Phase is not ("Source" or "Target" or "Participant" or "Cleanup")
            || string.IsNullOrWhiteSpace(binding.ProtocolIdentity) || string.IsNullOrWhiteSpace(binding.OperationId)
            || string.IsNullOrWhiteSpace(binding.Stage) || string.IsNullOrWhiteSpace(binding.IntentHash) || string.IsNullOrWhiteSpace(binding.QuoteId))
            throw new ArgumentException("Complete immutable WTT preparation identity is required.", nameof(binding));
        return CanonicalJson.Hash(new { Version = 1, binding.ProtocolIdentity, binding.OperationId, binding.Phase, binding.Stage, binding.Generation });
    }

    public static WttPreparationBinding Parse(string json)
    {
        var binding = JsonSerializer.Deserialize<WttPreparationBinding>(json)
            ?? throw new ArgumentException("WTT preparation binding is missing.", nameof(json));
        _ = Id(binding);
        if (CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)) != json)
            throw new ArgumentException("WTT preparation binding must be canonical.", nameof(json));
        return binding;
    }
}
