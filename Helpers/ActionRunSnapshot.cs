using System.Text.Json;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Helpers;

/// <summary>Captures and validates the immutable graph bound to an action run.</summary>
public static class ActionRunSnapshot
{
    public static void Capture(ActionRun run, ActionDefinition action)
    {
        run.DefinitionSnapshotJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(action));
        run.DefinitionSnapshotHash = CanonicalJson.HashCanonical(run.DefinitionSnapshotJson);
    }

    public static ActionDefinition Read(ActionRun run)
    {
        if (run.DefinitionSnapshotJson is not { } json || CanonicalJson.HashCanonical(json) != run.DefinitionSnapshotHash)
            throw new InvalidOperationException("Saved action graph is missing or does not match its admitted hash.");
        return JsonSerializer.Deserialize<ActionDefinition>(json) ?? throw new InvalidOperationException("Saved action graph is unreadable.");
    }
}
