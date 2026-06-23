using System.Text.Json;
using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Services.Quest.Workflow;

/// <summary>
/// How a node hands off to its successor(s) once its work is done
/// (durable-workflow-engine D5). Read from the node's opaque <c>Config</c> JSON
/// under a reserved <c>_workflow</c> key, so no schema migration is needed (the
/// <c>Config</c> bag already exists). A node with no <c>_workflow</c> marker
/// defaults to <see cref="Auto"/> — fully back-compatible with existing quests.
/// </summary>
public enum WorkflowAdvance
{
    /// <summary>Complete and immediately enqueue the downstream node(s). The run
    /// stays <c>Running</c>. The default when no marker is present.</summary>
    Auto,

    /// <summary>Complete but do NOT enqueue a successor: the run SUSPENDS and the
    /// consumer must call <c>advance(runId, fromNodeId)</c> to push the actor on
    /// (the literal <c>step(...)</c> primitive). Run projects to
    /// <c>Suspended</c>.</summary>
    Manual,

    /// <summary>PARK on an external signal before doing the work: the run waits
    /// at this gate until <c>signal(runId, gateId, payload)</c> arrives. Run
    /// projects to <c>AwaitingSignal</c>.</summary>
    Gated,

    /// <summary>PARK on a timer: the run auto-resumes once the timer is due, with
    /// no external call. Run projects to <c>AwaitingTimer</c>.</summary>
    Timer
}

/// <summary>
/// The advancement marker parsed from a node's <c>Config._workflow</c>. All
/// fields optional; absence ⇒ <see cref="WorkflowAdvance.Auto"/>.
/// </summary>
public sealed class WorkflowNodeConfig
{
    [JsonPropertyName("advance")]
    public string? Advance { get; set; }

    /// <summary>The gate id a <see cref="WorkflowAdvance.Gated"/> node parks on
    /// (matched by an incoming <c>signal</c>); also the timer key for
    /// <see cref="WorkflowAdvance.Timer"/>.</summary>
    [JsonPropertyName("gateId")]
    public string? GateId { get; set; }

    /// <summary>For <see cref="WorkflowAdvance.Timer"/>: seconds from park until
    /// the step auto-resumes through the existing due scan.</summary>
    [JsonPropertyName("resumeInSeconds")]
    public int? ResumeInSeconds { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parse the <c>_workflow</c> marker out of a node's opaque <c>Config</c>
    /// JSON. Returns the parsed <see cref="WorkflowAdvance"/> + the marker (the
    /// marker is <c>null</c> when absent). Malformed config is treated as
    /// <see cref="WorkflowAdvance.Auto"/> with no marker — a node's domain config
    /// is the node handler's concern, not the engine's.
    /// </summary>
    public static (WorkflowAdvance Advance, WorkflowNodeConfig? Marker) Parse(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            return (WorkflowAdvance.Auto, null);

        WorkflowNodeConfig? marker;
        try
        {
            using var doc = JsonDocument.Parse(config);
            if (!doc.RootElement.TryGetProperty("_workflow", out var wf))
                return (WorkflowAdvance.Auto, null);
            marker = wf.Deserialize<WorkflowNodeConfig>(Options);
        }
        catch (JsonException)
        {
            return (WorkflowAdvance.Auto, null);
        }

        if (marker is null)
            return (WorkflowAdvance.Auto, null);

        var advance = marker.Advance?.Trim().ToLowerInvariant() switch
        {
            AdvanceManual => WorkflowAdvance.Manual,
            AdvanceGated  => WorkflowAdvance.Gated,
            AdvanceTimer  => WorkflowAdvance.Timer,
            _             => WorkflowAdvance.Auto
        };
        return (advance, marker);
    }

    // The node Config._workflow.advance wire vocabulary — named so a new mode is
    // added here (and greppable from tests/docs) rather than as a bare inline
    // literal. "auto" is the implicit default (absent marker / unknown value).
    public const string AdvanceAuto   = "auto";
    public const string AdvanceManual = "manual";
    public const string AdvanceGated  = "gated";
    public const string AdvanceTimer  = "timer";
}
