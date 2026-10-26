using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Applies actual participant consent to original custody commands and exposes safe recovery progress.</summary>
public sealed partial class HoldCommandService(IHoldCommandStore store, SigningAdmissionAuthorityFactory authorities,
    HoldRailExecutor execution, IValueAccessService valueAccess, IWalletStore wallets, IBlockchainProviderFactory providers)
{
    /// <summary>Discovers original participant commands after browser loss without advancing custody.</summary>
    public async Task<AZOAResult<HoldCommandPage>> ListAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || tenant == Guid.Empty || offset < 0 || offset > int.MaxValue - 100 || limit is < 1 or > 100)
            return AZOAResult<HoldCommandPage>.Failure("A valid participant and bounded page are required.");
        var rows = await store.ListParticipantAsync(actor, tenant, offset, limit, ct);
        var items = new List<HoldCommandView>();
        foreach (var row in rows)
        {
            if (!Visible(row, actor, tenant)) continue;
            var result = await ReadResultAsync(row, ct);
            if (result.IsError || result.Result is null) return AZOAResult<HoldCommandPage>.Failure(result.Message);
            items.Add(result.Result);
        }
        return AZOAResult<HoldCommandPage>.Success(new(items, rows.Count == limit ? checked(offset + rows.Count) : null));
    }

    /// <summary>Cancels a no-effect command using the original actor's current owner credential.</summary>
    public async Task<AZOAResult<HoldCommandView>> CancelAsync(string id, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var cancelled = await store.CancelRailAsync(id, request, credential, ct);
        return cancelled is null ? AZOAResult<HoldCommandView>.Failure("Cancellation requires the original actor and an unchanged command with no ambiguous value movement.")
            : Result(cancelled);
    }

    /// <summary>Records current actor consent without replacing the original quote, generation or signed intent.</summary>
    public async Task<AZOAResult<HoldCommandView>> ContinueAsync(string id, HoldContinuationRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var row = await store.GetCommandAsync(id, ct);
        if (row is null || row.ActorUserId != credential.ActorUserId.ToString("N")
            || row.TenantId != credential.TenantId?.ToString("N") || request.RequestId == Guid.Empty
            || request.Version < 0 || row.RailPlanJson is null) return Refuse();
        if (row.State == "Completed") return Result(row);
        var access = await valueAccess.RequireValueAccessAsync(credential.ActorUserId, credential.TenantId, ct);
        if (access.IsError) return AZOAResult<HoldCommandView>.Failure(access.Message);
        var plan = JsonSerializer.Deserialize<HoldRailPlan>(row.RailPlanJson)!;
        var consent = await authorities.BindConsentAsync(credential, [AzoaScopes.TransferSign], ct);
        if (consent.IsError || consent.Result is null) return AZOAResult<HoldCommandView>.Failure(consent.Message);
        SigningAdmissionAuthority? sender = null;
        if (plan.SenderWallet is { } wallet)
        {
            var bound = await authorities.BindDirectAsync(credential, wallet.Id, wallet.ChainType, wallet.Address,
                [AzoaScopes.TransferSign], ct);
            if (bound.IsError || bound.Result is null) return AZOAResult<HoldCommandView>.Failure(bound.Message);
            sender = bound.Result;
        }
        var authority = new HoldCommandAuthority(1, Direct: consent.Result, Sender: sender);
        var audit = new HoldCommandAuthorization
        {
            Id = HoldConsentIdentity.Id("Transfer", id, credential.ActorUserId, credential.TenantId, request.RequestId),
            OperationId = id, ActorUserId = row.ActorUserId, TenantId = row.TenantId, ExpectedVersion = request.Version,
            RequestHash = HoldConsentIdentity.RequestHash("Transfer", id, credential.ActorUserId, credential.TenantId, request),
            QuoteId = request.QuoteId, MaximumNetworkFeeBaseUnits = request.MaximumNetworkFeeBaseUnits,
            AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)),
            PreparedHash = row.PreparedJson is null ? null : CanonicalJson.HashCanonical(row.PreparedJson), CreatedAt = DateTimeOffset.UtcNow,
        };
        var authorized = await store.AuthorizeRailAsync(row, audit, ct);
        if (authorized is null) return AZOAResult<HoldCommandView>.Failure("The exact quote, operation version or current authority changed.");
        return Convert(await execution.AdvanceAsync(authorized, true, ct));
    }

    /// <summary>Admits only the actual saved recipient's acceptance, with separate displayed native costs.</summary>
    public async Task<AZOAResult<HoldCommandView>> AcceptAsync(string id, HoldContinuationRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var row = await store.GetCommandAsync(id, ct);
        if (row?.RailPlanJson is null || request.RequestId == Guid.Empty) return Refuse();
        var plan = JsonSerializer.Deserialize<HoldRailPlan>(row.RailPlanJson)!;
        if (plan.RecipientWallet is not { } recipient || recipient.OwnerUserId != credential.ActorUserId
            || request.QuoteId != plan.Quote.QuoteId) return Refuse();
        var access = await valueAccess.RequireValueAccessAsync(credential.ActorUserId, credential.TenantId, ct);
        if (access.IsError) return AZOAResult<HoldCommandView>.Failure(access.Message);
        var bound = await authorities.BindDirectAsync(credential, recipient.Id, recipient.ChainType, recipient.Address,
            [AzoaScopes.TransferSign], ct);
        if (bound.IsError || bound.Result is null) return AZOAResult<HoldCommandView>.Failure(bound.Message);
        var signing = new SigningContext(recipient.OwnerUserId, recipient.Id, false, credential.TenantId ?? Guid.Empty,
            credential.TenantId is null ? Guid.Empty : recipient.OwnerUserId, AzoaScopes.TransferSign, recipient.ChainType);
        var intent = new ValueAcceptanceIntent(plan.Quote.Request.ChainType, plan.Quote.Request.Network,
            plan.Quote.Request.AssetId, recipient.Address, $"hold:{id}:Acceptance:{row.Generation}", signing,
            new(1, id, "Acceptance", row.Generation, plan.Quote, recipient.Address, request.MaximumNetworkFeeBaseUnits));
        var admitted = await store.AdmitAcceptanceAsync(row, intent, bound.Result, request, ct);
        return admitted is null ? AZOAResult<HoldCommandView>.Failure("The preserved acceptance or actual recipient authority changed.")
            : Convert(await execution.AdvanceAcceptanceAsync(admitted, true, ct));
    }

    /// <summary>Observes existing egress without acquiring fresh participant signatures.</summary>
    public async Task<AZOAResult<HoldCommandView>> RefreshAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var row = await store.GetCommandAsync(id, ct);
        if (!Visible(row, actor, tenant)) return Refuse();
        return row!.State == "Completed" ? await ReadResultAsync(row, ct)
            : Convert(await execution.AdvanceAsync(row, false, ct));
    }

    /// <summary>Returns participant-safe original progress without disclosing authority witnesses or signed bytes.</summary>
    public async Task<AZOAResult<HoldCommandView>> GetAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var row = await store.GetCommandAsync(id, ct);
        return Visible(row, actor, tenant) ? await ReadResultAsync(row!, ct) : Refuse();
    }

    private static bool Visible(HoldCommand? row, Guid actor, Guid? tenant)
    {
        if (row is null) return false;
        if (row.ActorUserId == actor.ToString("N") && row.TenantId == tenant?.ToString("N")) return true;
        if (tenant is not null || row.RailPlanJson is null) return false;
        return JsonSerializer.Deserialize<HoldRailPlan>(row.RailPlanJson)!.RecipientWallet?.OwnerUserId == actor;
    }

    private static AZOAResult<HoldCommandView> Convert(AZOAResult<HoldCommand> value)
        => value.IsError || value.Result is null ? AZOAResult<HoldCommandView>.Failure(value.Message) : Result(value.Result);

    private async Task<AZOAResult<HoldCommandView>> ReadResultAsync(HoldCommand row, CancellationToken ct)
    {
        if (row.State != "Completed" || row.RailPlanJson is not null) return Result(row);
        var receipt = await store.GetReceiptAsync(row.Id, ct);
        return receipt is null ? AZOAResult<HoldCommandView>.Failure("The original completed command receipt is unavailable.")
            : AZOAResult<HoldCommandView>.Success(JsonSerializer.Deserialize<HoldCommandView>(receipt.OutcomeJson)!);
    }

    private static AZOAResult<HoldCommandView> Result(HoldCommand row)
    {
        var intent = JsonSerializer.Deserialize<HoldCommandIntent>(row.IntentJson)!;
        var plan = row.RailPlanJson is null ? null : JsonSerializer.Deserialize<HoldRailPlan>(row.RailPlanJson);
        var draft = row.WorkflowMandateDraftJson is null ? null : JsonSerializer.Deserialize<HoldWorkflowMandateDraft>(row.WorkflowMandateDraftJson);
        var wire = row.PreparedJson is null ? null : JsonSerializer.Deserialize<PreparedValueTransfer>(row.PreparedJson);
        var quantity = plan?.Quote.Request.QuantityBaseUnits ?? intent.Definition?.QuantityBaseUnits ?? "";
        var beneficiary = intent.Definition?.BeneficiaryUserId ?? plan?.RecipientWallet?.OwnerUserId ?? Guid.Empty;
        return AZOAResult<HoldCommandView>.Success(new(row.Id, row.Version, row.Kind, row.State, row.HoldRecordId,
            intent.HoldId, intent.Asset, quantity, intent.ActorUserId, intent.OwnerUserId, intent.TenantId, beneficiary,
            wire?.TransactionId, row.Detail, CustodyTenantId: intent.CustodyTenantId, CostQuote: plan?.Quote ?? draft?.Quote,
            AcceptanceState: row.AcceptanceState, Generation: row.Generation, MandateTerms: draft?.Terms,
            MandateDraftHash: row.WorkflowMandateDraftJson is null ? null : CanonicalJson.HashCanonical(row.WorkflowMandateDraftJson)));
    }

    /// <summary>A request key already admitted a different command for this same actor and tenant.</summary>
    internal const string ReplayConflict = "This request key already carries a different hold command; replay the original intent exactly.";

    private static AZOAResult<HoldCommandView> Refuse() =>AZOAResult<HoldCommandView>.Failure("The original scoped hold operation was not found.");
}
