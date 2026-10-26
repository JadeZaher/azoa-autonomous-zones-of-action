using System.Text.Json;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Services.Actions.Designer;

public static class WorkflowDesignerCatalog
{
    public const string ExecutionDisclosure = "Importing does not execute these notes. A later explicit run can enqueue webhook notifications to a configured tenant registration and requires an active registration. Checkpoints neither wait for nor prove review or approval.";

    public static string Question(WorkflowDesignerQuestion id) => id switch
    {
        WorkflowDesignerQuestion.Purpose => "What are you creating, and what event earns or releases value?",
        WorkflowDesignerQuestion.ParticipantRoles => "Who pays, receives, operates and approves? Describe entity types and whether minors participate.",
        WorkflowDesignerQuestion.OperatingLocations => "Which countries/states govern the operator and participants, and where are they tax resident?",
        WorkflowDesignerQuestion.ValueAndCustody => "What assets, exact units, amounts, fees and custody arrangements apply? Who controls the funds?",
        WorkflowDesignerQuestion.AllocationsAndRecords => "Who receives each allocation, who bears fees/taxes, and who records valuation, basis and reporting?",
        WorkflowDesignerQuestion.FailureAndRefunds => "Who approves release, and what happens on cancellation, disputes, expiry, partial completion or failed payment?",
        WorkflowDesignerQuestion.ApprovalEvidence => "What evidence must the independent approvers review, and who resolves a rejected or disputed decision?",
        WorkflowDesignerQuestion.ExternalSettlementEvidence => "Who performs settlement outside this planning graph, and what confirmed-paid, confirmed-unpaid or unresolved evidence will they reconcile before retrying?",
        WorkflowDesignerQuestion.TaxYear => "Which tax year and payment dates should your qualified adviser assess? Rules and reporting requirements can change over time.",
        WorkflowDesignerQuestion.PayerPayeeClassification => "Ask your adviser to determine the payer/payee entity and worker/member relationship before choosing any reporting or withholding treatment.",
        WorkflowDesignerQuestion.PaymentSourceAndResidence => "Ask your adviser which payment type, work/payment source, entity status, residences and any treaty/documentation facts are relevant; residence alone does not determine withholding.",
        WorkflowDesignerQuestion.LocalProfessionalReview => "Who will obtain qualified legal and tax review in each relevant jurisdiction before any real operation? Record unresolved determinations outside this interview.",
        WorkflowDesignerQuestion.PlanningSequence => "Which review checkpoints and external handoffs should the planning outline cover? You can continue the interview or use the manual builder.",
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    public static WorkflowDesignerDimension? Dimension(WorkflowDesignerQuestion id) => id switch
    {
        WorkflowDesignerQuestion.Purpose or WorkflowDesignerQuestion.PlanningSequence => WorkflowDesignerDimension.Purpose,
        WorkflowDesignerQuestion.ParticipantRoles => WorkflowDesignerDimension.Participants,
        WorkflowDesignerQuestion.OperatingLocations => WorkflowDesignerDimension.Jurisdictions,
        WorkflowDesignerQuestion.ValueAndCustody => WorkflowDesignerDimension.ValueMovement,
        WorkflowDesignerQuestion.AllocationsAndRecords => WorkflowDesignerDimension.AllocationsAndTax,
        WorkflowDesignerQuestion.FailureAndRefunds or WorkflowDesignerQuestion.ApprovalEvidence or WorkflowDesignerQuestion.ExternalSettlementEvidence => WorkflowDesignerDimension.FailureAndRefunds,
        _ => null
    };

    public static WorkflowDesignerQuestion DefaultQuestion(WorkflowDesignerDimension dimension) => dimension switch
    {
        WorkflowDesignerDimension.Purpose => WorkflowDesignerQuestion.Purpose,
        WorkflowDesignerDimension.Participants => WorkflowDesignerQuestion.ParticipantRoles,
        WorkflowDesignerDimension.Jurisdictions => WorkflowDesignerQuestion.OperatingLocations,
        WorkflowDesignerDimension.ValueMovement => WorkflowDesignerQuestion.ValueAndCustody,
        WorkflowDesignerDimension.AllocationsAndTax => WorkflowDesignerQuestion.AllocationsAndRecords,
        WorkflowDesignerDimension.FailureAndRefunds => WorkflowDesignerQuestion.FailureAndRefunds,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension))
    };

    public static string Risk(WorkflowDesignerRisk id) => id switch
    {
        WorkflowDesignerRisk.WorkerClassification => "Legal/tax review: determine the worker, member, contractor and entity relationships with qualified local advisers; this checklist does not classify anyone.",
        WorkflowDesignerRisk.IncomeAndValuation => "Tax review: ask about income, disposal, valuation and basis records for the actual assets and transactions; no tax treatment is determined here.",
        WorkflowDesignerRisk.ReportingAndWithholding => "Tax review: determine applicable reporting, withholding, forms and documentation with a qualified adviser for the relevant tax year and facts. This assistant supplies no statutory threshold or rate.",
        WorkflowDesignerRisk.CrossBorderFacts => "Cross-border review: identify payer/payee entities, payment nature and source, work locations, residences and any treaty/documentation facts before professional determination; do not infer obligations from residence alone.",
        WorkflowDesignerRisk.CustodyAndLicensing => "Legal review: ask qualified local advisers about custody, control of funds and any licensing or money-transmission questions for the actual business model.",
        WorkflowDesignerRisk.SecuritiesAndLending => "Legal review: any investment, lending or participation arrangement needs qualified classification and local review; a planning node does not authorize the activity.",
        WorkflowDesignerRisk.EligibilityAndPrivacy => "Eligibility/privacy review: determine participant eligibility, data minimization and any identity-verification duties with qualified advisers; do not upload personal documents here.",
        WorkflowDesignerRisk.ConsumerAndDisputeTerms => "Legal/operational review: confirm approval, cancellation, refund, dispute and participant terms before operation. This outline does not enforce those terms.",
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    public static string Limitation(WorkflowDesignerBlocker id) => id switch
    {
        WorkflowDesignerBlocker.FundsMovementUnsupported => "This assistant proposes planning/data-only graphs. It cannot implement automatic fiat payouts or other funds movement; use separately supported, reviewed settlement systems.",
        WorkflowDesignerBlocker.AutomaticEnforcementUnsupported => "Planning checkpoints do not enforce approvals, spending caps, deadlines, disputes, idempotency or payment finality. Those controls require separate implementation and evidence.",
        WorkflowDesignerBlocker.ExternalSettlementRequired => "Any actual settlement remains a manual or external handoff. A recorded checkpoint is not evidence that a payment occurred.",
        WorkflowDesignerBlocker.PlanningSequenceUnclear => "A safe planning sequence is not yet specified. Explain the review checkpoints and external handoffs, or continue in the manual builder.",
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    public static string Summary(WorkflowDesignerState state) => state switch
    {
        WorkflowDesignerState.NeedsDetails => "Some operational details still need an answer. No draft is available yet. Adviser-review questions are separate from these planning blockers.",
        WorkflowDesignerState.NeedsConfirmation => "Confirm the operating locations and economic terms in your brief. These are your stated facts, not confirmation of legal or tax compliance. No draft is available yet.",
        WorkflowDesignerState.NeedsPlanningScope => "Choose planning/data-only scope to receive an editable graph with no funds movement, or continue interviewing and use the manual builder. No draft has been created yet.",
        WorkflowDesignerState.NeedsPlanningStructure => "A safe planning sequence could not be proposed yet. No draft is available. Explain the review checkpoints or use the manual builder.",
        WorkflowDesignerState.DraftRejected => "The planning proposal did not pass graph validation. No draft is available; review the validation issues or use the manual builder.",
        WorkflowDesignerState.DraftReady => "An editable private planning-notes graph is ready. Each node records a manual/external handoff note only; it does not implement two-person approval, eligibility, caps, refunds, payouts, settlement or legal/tax determination.",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public static ActionCreateModel PlanningDraft(IReadOnlyList<WorkflowDesignerStage> selected)
    {
        var stages = new[] { WorkflowDesignerStage.RequestReview }.Concat(selected.Where(stage => stage != WorkflowDesignerStage.RecordsReview))
            .Distinct().Append(WorkflowDesignerStage.RecordsReview).ToArray();
        return new()
        {
            Name = "Planning notes — no funds movement", IsPublic = false,
            Description = "Each Emit node records planning handoff data, not proof that a review occurred. No two-person approval, eligibility, cap, deadline, refund or payout enforcement is implemented. Settlement is manual/external. This is not legal or tax approval. " + ExecutionDisclosure,
            Nodes = stages.Select((stage, index) => new ActionNodeCreateModel
            {
                Name = StageName(stage), NodeType = ActionNodeType.Emit, IsEntry = index == 0, IsTerminal = index == stages.Length - 1,
                Config = JsonSerializer.Serialize(new { eventType = "planning.checkpoint", payload = new
                { scope = "planning_data_only", checkpoint = stage.ToString(), fundsMoved = false, requiresExternalReview = true } })
            }).ToList(),
            Edges = Enumerable.Range(0, stages.Length - 1).Select(index => new ActionEdgeCreateModel
            { SourceNodeId = index, TargetNodeId = index + 1, EdgeType = ActionEdgeType.Control }).ToList()
        };
    }

    private static string StageName(WorkflowDesignerStage stage) => stage switch
    {
        WorkflowDesignerStage.RequestReview => "Record planning request",
        WorkflowDesignerStage.EligibilityReview => "Record external eligibility-review note",
        WorkflowDesignerStage.EvidenceReview => "Record external evidence-review note",
        WorkflowDesignerStage.ApprovalReview => "Record external approval handoff note",
        WorkflowDesignerStage.DisputeReview => "Record external dispute handoff note",
        WorkflowDesignerStage.ExternalSettlementReview => "Record external settlement handoff note",
        WorkflowDesignerStage.ReconciliationReview => "Record external reconciliation note",
        WorkflowDesignerStage.CancellationReview => "Record external cancellation/refund note",
        WorkflowDesignerStage.RecordsReview => "Record external review checklist note",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
}
