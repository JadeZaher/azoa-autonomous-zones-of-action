using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Actions;
using System.Security.Claims;
using System.Text.Json;
using AZOA.WebAPI.Services.Actions.Predicates;

namespace AZOA.WebAPI.Services.Actions.Designer;

public static class WorkflowDesignerPolicy
{
    public static string BudgetPartition(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value, out var owner)
            ? $"designer-tenant:{owner:N}" : "designer-unidentified";

    public static readonly IReadOnlySet<ActionNodeType> AllowedNodes = new HashSet<ActionNodeType>
    {
        ActionNodeType.GateCheck, ActionNodeType.Emit, ActionNodeType.ComposeOutputs,
        ActionNodeType.Hold, ActionNodeType.Release, ActionNodeType.HoldRefund, ActionNodeType.ReleaseIntoHold,
        ActionNodeType.Transfer, ActionNodeType.Refund, ActionNodeType.Swap,
        ActionNodeType.LoanOpen, ActionNodeType.LoanRepaymentRecord, ActionNodeType.LoanBalance
    };

    public static readonly IReadOnlyList<WorkflowDesignerSource> Sources = [
        new("irs-digital-assets", "IRS: Digital assets and tax records", "https://www.irs.gov/filing/digital-assets", "United States federal tax; other jurisdictions require local advice"),
        new("fincen-cvc", "FinCEN: Convertible virtual currency business models", "https://www.fincen.gov/resources/statutes-regulations/guidance/application-fincens-regulations-certain-business-models", "United States federal AML; state and non-US rules require separate review"),
        new("sec-crypto", "SEC: Transactions involving crypto assets", "https://www.sec.gov/resources-small-businesses/capital-raising-building-blocks/transactions-involving-crypto-assets", "United States securities; classification depends on facts")
    ];

    public static List<WorkflowDesignerDimension> MissingDimensions(WorkflowDesignerIntake intake)
    {
        var missing = new List<WorkflowDesignerDimension>();
        foreach (var (answer, dimension) in new[] {
            (intake.Purpose, WorkflowDesignerDimension.Purpose), (intake.Participants, WorkflowDesignerDimension.Participants),
            (intake.Jurisdictions, WorkflowDesignerDimension.Jurisdictions), (intake.ValueMovement, WorkflowDesignerDimension.ValueMovement),
            (intake.AllocationsAndTax, WorkflowDesignerDimension.AllocationsAndTax), (intake.FailureAndRefunds, WorkflowDesignerDimension.FailureAndRefunds) })
        {
            var wholeAnswer = string.Join(" ", (answer ?? "").Trim().TrimEnd('.', '!', '?').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
            if (wholeAnswer.Length < 12 || UncertaintyAnswers.Contains(wholeAnswer)) missing.Add(dimension);
        }
        return missing;
    }

    private static readonly IReadOnlySet<string> UncertaintyAnswers = new HashSet<string>(StringComparer.Ordinal)
    {
        "unknown", "unsure", "tbd", "undecided", "unconfirmed", "not known", "not yet known", "not sure",
        "not decided", "not yet decided", "not determined", "not yet determined", "we have not decided",
        "don't know", "do not know", "i don't know", "i do not know", "jurisdiction is unknown"
    };

    public static List<string> ValidateDraft(WorkflowDesignerReply reply, WorkflowDesignerRequest request, IActionDagValidator dagValidator,
        IActionDagExecutabilityValidator executabilityValidator)
    {
        var draft = reply.Draft;
        if (draft is null) return ["The assistant has not proposed a graph yet."];
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Length > 150 || draft.Description?.Length > 4000)
            errors.Add("Draft name or description is missing or too long.");
        if (draft.Nodes is null || draft.Edges is null || draft.Nodes.Count is < 1 or > 20 || draft.Edges.Count > 40)
            return ["A draft must contain 1–20 nodes and at most 40 edges."];
        foreach (var node in draft.Nodes)
        {
            if (node is null || !AllowedNodes.Contains(node.NodeType))
            {
                errors.Add("The draft contains an unsupported node type.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(node.Name) || node.Name.Length > 100 || string.IsNullOrWhiteSpace(node.Config) || node.Config.Length > 8000 || node.NodeTemplateId is not null)
            {
                errors.Add("Node names/configs must be bounded; template handles cannot be generated.");
                continue;
            }
            JsonDocument config;
            try { config = JsonDocument.Parse(node.Config); }
            catch (JsonException)
            {
                errors.Add("Node config must be a valid JSON object.");
                continue;
            }
            using (config)
            {
                if (config.RootElement.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Node config must be a valid JSON object.");
                    continue;
                }
                var configError = ActionNodeConfigRegistry.Validate(node.NodeType, node.Config);
                if (configError is not null) errors.Add(configError);
                else ValidateResourceHandles(config.RootElement, request.ConfirmedResourceHandles, draft, errors);
            }
        }
        if (draft.Edges.Any(e => e is null || !Enum.IsDefined(e.EdgeType) || e.SourceNodeId < 0 || e.SourceNodeId >= draft.Nodes.Count ||
            e.TargetNodeId < 0 || e.TargetNodeId >= draft.Nodes.Count || e.Condition?.Length > 1000))
            errors.Add("The draft contains an invalid edge reference or type.");
        if (errors.Count > 0) return errors;
        var nodes = draft.Nodes.Select(n => new ActionNode { Id = Guid.NewGuid(), Name = n.Name, NodeType = n.NodeType,
            Config = n.Config, IsEntry = n.IsEntry, IsTerminal = n.IsTerminal }).ToList();
        var action = new ActionDefinition { Name = draft.Name, Nodes = nodes, Edges = draft.Edges.Select(e => new ActionEdge {
            Id = Guid.NewGuid(), SourceNodeId = nodes[e.SourceNodeId].Id, TargetNodeId = nodes[e.TargetNodeId].Id,
            EdgeType = e.EdgeType, Condition = e.Condition }).ToList() };
        var structural = dagValidator.Validate(action, fanOutAsError: true);
        errors.AddRange(structural.Errors);
        if (structural.IsValid) errors.AddRange(executabilityValidator.Validate(action).Errors);
        draft.IsPublic = false;
        return errors;
    }

    private static void ValidateResourceHandles(JsonElement element, IReadOnlyList<string> supplied,
        AZOA.WebAPI.Models.Requests.ActionCreateModel draft, List<string> errors, string? field = null)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) ValidateResourceHandles(property.Value, supplied, draft, errors, property.Name);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateResourceHandles(item, supplied, draft, errors, field);
        }
        else if (element.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            var value = element.ToString();
            if (value.Length == 0) return;
            if (string.Equals(field, "HoldNodeName", StringComparison.OrdinalIgnoreCase))
            {
                if (!draft.Nodes.Any(node => node is not null && node.Name == value && node.NodeType is ActionNodeType.Hold or ActionNodeType.ReleaseIntoHold))
                    errors.Add("HoldNodeName must name a hold-producing node in this draft.");
                return;
            }
            if (GatePath.TryParse(value, out var path, out _) && path[0] == "holon")
            {
                value = path[1];
                field = "HolonId";
            }
            var isGuid = Guid.TryParse(value, out var guid);
            var handleField = field is not null && (field.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ||
                field.EndsWith("Address", StringComparison.OrdinalIgnoreCase) ||
                new[] { "to", "ref", "reference", "recipient", "ids", "ReservePartition", "CurrencyCode", "ProtocolIdentity" }.Contains(field, StringComparer.OrdinalIgnoreCase));
            if ((isGuid || handleField) && !supplied.Any(handle => isGuid ? Guid.TryParse(handle, out var parsed) && parsed == guid : string.Equals(handle, value, StringComparison.Ordinal)))
                errors.Add($"Resource field '{field}' uses an unconfirmed identifier. Add the exact handle to the confirmed resource list before drafting.");
        }
    }
}
