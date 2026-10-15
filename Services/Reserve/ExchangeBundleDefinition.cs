// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions.Handlers;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>What one leg of an exchange group does.</summary>
public enum ExchangeLegRole
{
    /// <summary>
    /// The funds leg: the platform unit moving from the sponsor to the PROJECT'S own
    /// account. It never goes to the platform reserve — a project that receives nothing on
    /// chain has a funding balance that exists only as rows in someone's database, which is
    /// exactly the ledger-of-record D47 rejected.
    /// </summary>
    Funds = 0,

    /// <summary>The units leg: the project's asset moving out of the reserve to the recipient.</summary>
    Units = 1,
}

/// <summary>
/// WHOSE key signs a leg. It is a named discriminator and not an inference from the signing
/// context because two node-held accounts resolve their keys DIFFERENTLY: the reserve signs with
/// the chain's platform key, while a project treasury signs with a key DERIVED from it inside
/// <c>IProjectTreasury.SignAsTreasuryAsync</c>. Both look like a platform context, and a
/// submitter that could not tell them apart would sign a treasury's money with the reserve's key.
/// </summary>
public enum ExchangeLegSigner
{
    /// <summary>The person's own custodial key.</summary>
    Recipient = 0,

    /// <summary>The platform reserve, signing with the chain's platform key.</summary>
    PlatformReserve = 1,

    /// <summary>The project's treasury, signing with a key derived from the platform key for it.</summary>
    ProjectTreasury = 2,
}

/// <summary>
/// One leg of an exchange group. <see cref="SigningContext"/> carries identity only, never
/// key material, and <see cref="Signer"/> names which account's key resolves it.
/// </summary>
public sealed record ExchangeGroupLeg(
    ExchangeLegRole Role,
    ExchangeLegSigner Signer,
    string AssetId,
    string FromAddress,
    string ToAddress,
    BigInteger Amount,
    SigningContext SigningContext)
{
    /// <summary>True when this leg is signed by a person's custodial key rather than the node's.</summary>
    public bool IsUserSigned => Signer == ExchangeLegSigner.Recipient;
}

/// <summary>
/// The atomic group one exchange executes as: every leg, plus the account that pools the
/// chain fee for the legs a person signs.
/// </summary>
/// <remarks>
/// <para><b>Why fee pooling is part of the plan and not an implementation detail.</b> A user's
/// custodial account never holds spendable native units — nothing on this node funds one
/// beyond the account minimum a recipient preparation advances. A group whose user-signed leg
/// pays its own fee therefore fails on chain, on the sponsorship moment itself, with an error
/// about a balance the sponsor was never told they needed. The reserve carries the fee-payer
/// role (W3.1) and pools the fee for every user-signed leg in the group.</para>
/// <para>All legs settle in ONE group, so either everything moves or nothing does. A partial
/// exchange — funds taken, units not delivered — is the failure this shape exists to make
/// impossible.</para>
/// </remarks>
public sealed record ExchangeGroupPlan
{
    private ExchangeGroupPlan(
        string chainType,
        ChainNetwork network,
        string idempotencyKey,
        string groupIdentity,
        IReadOnlyList<ExchangeGroupLeg> legs,
        string feePayerAddress,
        SigningContext feePayerSigningContext,
        ProjectTreasuryReference? treasuryReference)
    {
        ChainType = chainType;
        Network = network;
        IdempotencyKey = idempotencyKey;
        GroupIdentity = groupIdentity;
        Legs = legs;
        FeePayerAddress = feePayerAddress;
        FeePayerSigningContext = feePayerSigningContext;
        TreasuryReference = treasuryReference;
    }

    /// <summary>Canonical rail binding a submitter must match exactly.</summary>
    public string ChainType { get; }

    /// <summary>Canonical network binding a submitter must match exactly.</summary>
    public ChainNetwork Network { get; }

    /// <summary>The exchange's replay anchor. A submitter must not resend a group under a key it already sent.</summary>
    public string IdempotencyKey { get; }

    /// <summary>Stable digest of the whole immutable group decision.</summary>
    public string GroupIdentity { get; }

    /// <summary>Every leg, in the order the group declares them.</summary>
    public IReadOnlyList<ExchangeGroupLeg> Legs { get; }

    /// <summary>The account that pools the chain fee for the whole group — always the reserve.</summary>
    public string FeePayerAddress { get; }

    /// <summary>The fee payer's signing identity.</summary>
    public SigningContext FeePayerSigningContext { get; }

    /// <summary>
    /// Names the project whose treasury signs a <see cref="ExchangeLegSigner.ProjectTreasury"/>
    /// leg. Present only when the group carries one, because a treasury key is DERIVED from the
    /// platform key per project — an address alone cannot tell a submitter which key to derive.
    /// </summary>
    public ProjectTreasuryReference? TreasuryReference { get; }

    /// <summary>How many legs a person signs, and therefore how many fees the payer must pool.</summary>
    public int UserSignedLegCount => Legs.Count(leg => leg.IsUserSigned);

    /// <summary>
    /// Validates and freezes one group, or refuses it. The refusals here are controls, not
    /// formalities: a group with no fee payer behind its user-signed legs cannot settle, and a
    /// group whose funds leg points anywhere but the project's account funds the wrong party.
    /// </summary>
    public static AZOAResult<ExchangeGroupPlan> TryCreate(
        string chainType,
        ChainNetwork network,
        string idempotencyKey,
        IReadOnlyList<ExchangeGroupLeg> legs,
        string feePayerAddress,
        SigningContext feePayerSigningContext,
        ProjectTreasuryReference? treasuryReference = null)
    {
        if (string.IsNullOrWhiteSpace(chainType))
            return Error("An exchange group must name the rail it settles on.");
        if (!Enum.IsDefined(network))
            return Error("An exchange group must name a supported network.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Error("An exchange group must carry its replay anchor.");
        if (legs is null || legs.Count == 0)
            return Error("An exchange group must carry at least one leg.");
        if (string.IsNullOrWhiteSpace(feePayerAddress))
            return Error("An exchange group must name the account that pools its fees.");
        if (!feePayerSigningContext.IsPlatform)
            return Error("The exchange fee payer must be the node's own reserve account.");

        foreach (var leg in legs)
        {
            if (string.IsNullOrWhiteSpace(leg.AssetId)
                || string.IsNullOrWhiteSpace(leg.FromAddress)
                || string.IsNullOrWhiteSpace(leg.ToAddress))
            {
                return Error("Every exchange leg needs an asset, a source and a destination.");
            }

            if (leg.Amount <= BigInteger.Zero)
                return Error("Every exchange leg must move a positive whole amount.");
            if (string.Equals(leg.FromAddress, leg.ToAddress, StringComparison.Ordinal))
                return Error("An exchange leg cannot send an amount to the account it came from.");
            if (leg.IsUserSigned && !leg.SigningContext.IsResolvableUserContext)
                return Error("A person-signed exchange leg needs a resolvable custodial identity.");

            // The discriminator and the context must agree. A node-held leg carrying a person's
            // context (or the reverse) is a leg somebody would sign with the wrong key.
            if (leg.IsUserSigned == leg.SigningContext.IsPlatform)
                return Error("An exchange leg's declared signer disagrees with its signing identity.");
        }

        // The whole reason the reserve is in the group: without a fee payer, the first real
        // exchange fails on chain because a sponsor's custodial account holds no spendable
        // native units. This is the check that makes the pooling requirement a control.
        if (legs.Any(leg => leg.IsUserSigned)
            && string.Equals(
                feePayerAddress.Trim(),
                legs.First(leg => leg.IsUserSigned).FromAddress.Trim(),
                StringComparison.Ordinal))
        {
            return Error(
                "The exchange fee payer cannot be the person signing a leg: a custodial account "
                + "holds no spendable native units, so it cannot pay its own fee.");
        }

        // A treasury leg names an account whose key does not exist until it is derived FOR one
        // project. Without the reference a submitter would have to guess, and the only key it
        // could reach by guessing is the reserve's — which would move the project's money under
        // the node's own signature.
        if (legs.Any(leg => leg.Signer == ExchangeLegSigner.ProjectTreasury))
        {
            if (treasuryReference is null || !treasuryReference.IsComplete)
            {
                return Error(
                    "An exchange leg paid out of a project treasury must name the project whose "
                    + "treasury key signs it.");
            }

            if (!string.Equals(
                    treasuryReference.NormalizedChainType,
                    chainType.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return Error("The named project treasury is on a different rail than this exchange.");
            }
        }

        var normalized = legs
            .Select(leg => leg with
            {
                AssetId = leg.AssetId.Trim(),
                FromAddress = leg.FromAddress.Trim(),
                ToAddress = leg.ToAddress.Trim(),
            })
            .ToArray();

        var identity = Digest(Canonicalize(
            chainType.Trim(), network, idempotencyKey.Trim(), normalized, feePayerAddress.Trim(),
            treasuryReference));

        return AZOAResult<ExchangeGroupPlan>.Success(new ExchangeGroupPlan(
            chainType.Trim(),
            network,
            idempotencyKey.Trim(),
            identity,
            normalized,
            feePayerAddress.Trim(),
            feePayerSigningContext,
            treasuryReference));
    }

    /// <summary>The canonical JSON a bundle carries as its deployment config, and a submitter replays from.</summary>
    public string ToDeploymentConfig() => JsonSerializer.Serialize(new
    {
        version = CanonicalVersion,
        chainType = ChainType,
        network = Network.ToString(),
        groupIdentity = GroupIdentity,
        feePayerAddress = FeePayerAddress,
        userSignedLegCount = UserSignedLegCount,
        legs = Legs.Select(leg => new
        {
            role = leg.Role.ToString(),
            assetId = leg.AssetId,
            from = leg.FromAddress,
            to = leg.ToAddress,
            amount = leg.Amount.ToString(CultureInfo.InvariantCulture),
            signer = leg.Signer.ToString(),
        }),
    });

    private const string CanonicalVersion = "azoa.exchange-group.v1";

    private static string Canonicalize(
        string chainType,
        ChainNetwork network,
        string idempotencyKey,
        IReadOnlyList<ExchangeGroupLeg> legs,
        string feePayerAddress,
        ProjectTreasuryReference? treasuryReference)
    {
        var builder = new StringBuilder(CanonicalVersion);
        Append(builder, chainType);
        Append(builder, network.ToString());
        Append(builder, idempotencyKey);
        Append(builder, feePayerAddress);
        Append(builder, treasuryReference?.TenantIdText ?? string.Empty);
        Append(builder, treasuryReference?.NormalizedProjectReference ?? string.Empty);
        foreach (var leg in legs)
        {
            Append(builder, leg.Role.ToString());
            Append(builder, leg.Signer.ToString());
            Append(builder, leg.AssetId);
            Append(builder, leg.FromAddress);
            Append(builder, leg.ToAddress);
            Append(builder, leg.Amount.ToString(CultureInfo.InvariantCulture));
            Append(builder, leg.SigningContext.UserId.ToString("N"));
            Append(builder, leg.SigningContext.WalletId.ToString("N"));
            Append(builder, leg.SigningContext.IsPlatform ? "1" : "0");
            Append(builder, leg.SigningContext.ActingTenantId.ToString("N"));
            Append(builder, leg.SigningContext.GrantorUserId.ToString("N"));
            Append(builder, leg.SigningContext.Scope ?? string.Empty);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append('|').Append(value.Length).Append(':').Append(value);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AZOAResult<ExchangeGroupPlan> Error(string message) =>
        AZOAResult<ExchangeGroupPlan>.FailureWithCode(message, AzoaErrorCodes.InvalidRequest);
}

/// <summary>Where a rail left one exchange group.</summary>
public enum ExchangeGroupOutcome
{
    /// <summary>
    /// The rail took the group and whether it commits is still unknown. Nothing may be recorded
    /// as settled, and nothing may be handed back either: an unwind here could undo a group that
    /// commits a moment later.
    /// </summary>
    Accepted = 0,

    /// <summary>The rail confirmed the group on chain. This is the only outcome that moved value.</summary>
    Confirmed = 1,

    /// <summary>
    /// The rail took the group and it can NEVER commit — its validity window has closed. This is
    /// the accepted-then-failed outcome, and it is the one that must unwind every node-side
    /// commitment the exchange took before submitting.
    /// </summary>
    DroppedAfterAcceptance = 2,
}

/// <summary>What a rail did with one exchange group. Never an individual-leg result.</summary>
/// <param name="GroupReference">The rail's reference for the group as a whole.</param>
/// <param name="LegReferences">The rail's per-leg references, in the plan's own leg order.</param>
/// <param name="Confirmed">True only when the rail has confirmed the group, not merely accepted it.</param>
/// <param name="Outcome">
/// Where the rail left the group. Defaulted so a submitter that only knows confirmed-or-not
/// still reports a coherent outcome; <see cref="Settlement"/> is the reading to branch on.
/// </param>
public sealed record ExchangeGroupSubmission(
    string GroupReference,
    IReadOnlyList<string> LegReferences,
    bool Confirmed,
    ExchangeGroupOutcome Outcome = ExchangeGroupOutcome.Accepted)
{
    /// <summary>The outcome to act on: <see cref="Confirmed"/> always wins over a defaulted <see cref="Outcome"/>.</summary>
    public ExchangeGroupOutcome Settlement => Confirmed ? ExchangeGroupOutcome.Confirmed : Outcome;

    /// <summary>
    /// True when the rail accepted the group and it can never commit. The manager unwinds on
    /// this and only this: an <see cref="ExchangeGroupOutcome.Accepted"/> group whose fate is
    /// unknown must be left alone for reconciliation.
    /// </summary>
    public bool DroppedAfterAcceptance => Settlement == ExchangeGroupOutcome.DroppedAfterAcceptance;
}

/// <summary>
/// Submits one exchange group atomically. The implementation belongs to a rail adapter: it
/// needs suggested params, multi-signer assembly and fee pooling, all of which are chain
/// plumbing that lives behind the provider boundary.
/// </summary>
public interface IExchangeGroupSubmitter
{
    /// <summary>True when this submitter can settle a group on <paramref name="chainType"/>.</summary>
    bool CanSubmit(string chainType);

    /// <summary>
    /// Settles the whole plan or nothing. A submitter MUST NOT settle a subset of the legs,
    /// and MUST NOT resend a group it already submitted under the plan's idempotency key.
    /// </summary>
    Task<AZOAResult<ExchangeGroupSubmission>> SubmitAsync(
        ExchangeGroupPlan plan, CancellationToken ct = default);
}

/// <summary>
/// The fallback a node runs on a rail it has no exchange adapter for. It settles NOTHING and
/// says so.
/// </summary>
/// <remarks>
/// <para><b>This is the fail-closed kind of stub, and it is no longer the only submitter.</b>
/// <c>AlgorandExchangeGroupSubmitter</c> settles a real two-asset, two-signer, fee-pooled group
/// on Algorand and is registered ahead of this one, so this type takes effect only where no
/// adapter matched — <c>IAtomicTransferGroupModule</c> cannot stand in, because it expresses
/// only a group whose legs share one asset, one source and one signing context.</para>
/// <para>Where it does take effect, every exchange refuses with
/// <see cref="ConsumerSafeErrorCodes.RailUnavailable"/>. It never falls back to sending the legs
/// one at a time: two sequential transfers are exactly the partial exchange — funds taken, units
/// undelivered — that atomicity exists to prevent.</para>
/// </remarks>
public sealed class UnavailableExchangeGroupSubmitter : IExchangeGroupSubmitter
{
    /// <summary>The single refusal, kept as a constant so its wording cannot drift.</summary>
    public const string UnavailableMessage =
        "This node cannot settle an exchange right now. Nothing was moved.";

    /// <inheritdoc />
    public bool CanSubmit(string chainType) => false;

    /// <inheritdoc />
    public Task<AZOAResult<ExchangeGroupSubmission>> SubmitAsync(
        ExchangeGroupPlan plan, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<ExchangeGroupSubmission>.FailureWithCode(
            UnavailableMessage, ConsumerSafeErrorCodes.RailUnavailable));
}

/// <summary>Everything <see cref="ExchangeBundleDefinition.Compose"/> needs to instantiate one exchange.</summary>
public sealed record ExchangeBundleInputs(
    string ReceiptReference,
    string IdempotencyKey,
    string ChainType,
    ChainNetwork Network,
    ExchangeDirection Direction,
    Guid RecipientUserId,
    string ProjectReference,
    string ProjectAssetId,
    string PlatformAssetId,
    BigInteger PlatformAmount,
    BigInteger ProjectUnitAmount,
    string ReservePartition,
    string ReserveAddress,
    string TreasuryAddress,
    string RecipientAddress,
    SigningContext RecipientSigningContext,
    SigningContext ReserveSigningContext,
    SigningContext TreasurySigningContext,
    string? HoldId,
    DateTimeOffset? ReleaseNotBefore,
    string? Purpose,
    string PolicyVersion,
    TenantDeclaredRate? DeclaredRate);

/// <summary>
/// One instantiated exchange: the DAG that records what the node decided, the deployable
/// bundle that carries it, and the group that actually moves the value.
/// </summary>
/// <param name="Definition">The plan of record. Draft, never persisted, never dispatched.</param>
/// <param name="Bundle">The deployable unit, carrying the canonical group as its deployment config.</param>
/// <param name="Group">The atomic group the manager submits. Authoritative.</param>
public sealed record ExchangeBundle(
    ActionDefinition Definition,
    ActionGraphBundle Bundle,
    ExchangeGroupPlan Group);

/// <summary>
/// Composes the treasury-rate exchange as an Action DAG plus an <c>ActionGraphBundle</c>
/// (N-X1 / D50), instantiated per request from amounts the consumer already decided.
/// </summary>
/// <remarks>
/// <para><b>The DAG and the group are two projections of one decision.</b> Both are built here,
/// from the same <see cref="ExchangeBundleInputs"/>, so the record of what the node intended
/// cannot drift from what it submitted.</para>
/// <para><b>The DAG is a plan of record, not a dispatch path.</b> It is returned as
/// <see cref="ActionStatus.Draft"/> and is never written to the action store, because the legs
/// have to settle in ONE group and running them as separate nodes would be separate
/// transactions. Its nodes are the ones whose existing config this exchange can populate
/// faithfully — a <see cref="ActionNodeType.GateCheck"/> for the preconditions, a
/// <see cref="ActionNodeType.Hold"/> carrying the real hold window when there is one, and an
/// <see cref="ActionNodeType.Emit"/> for the settlement fact handed back to the consumer.</para>
/// <para><b>The funds leg has no node type yet.</b> <see cref="ActionNodeType.Transfer"/> and
/// <see cref="ActionNodeType.Grant"/> are NFT-shaped (<c>NftTransferRequest</c>,
/// <c>NftMintRequest</c>), so a fungible move between two node-held accounts cannot be
/// expressed as either without lying about what the node would do if anything ever dispatched
/// it. It is therefore carried in the group and in the definition's metadata under
/// <c>azoa.exchange.funds-leg</c> rather than mis-typed into a node. Adding a faithful
/// fungible-value node type belongs to the owner of <c>Models/Actions/ActionEnums.cs</c>.</para>
/// </remarks>
public static class ExchangeBundleDefinition
{
    /// <summary>Metadata key naming how this definition's value actually moves.</summary>
    public const string DispatchMetadataKey = "azoa.exchange.dispatch";

    /// <summary>The one legal value of <see cref="DispatchMetadataKey"/>.</summary>
    public const string AtomicGroupDispatch = "atomic-group";

    /// <summary>Metadata key carrying the funds leg that has no node type of its own.</summary>
    public const string FundsLegMetadataKey = "azoa.exchange.funds-leg";

    /// <summary>Node name of the preconditions gate.</summary>
    public const string PreconditionsNodeName = "exchange-preconditions";

    /// <summary>Node name of the units hold.</summary>
    public const string UnitsHoldNodeName = "exchange-units-hold";

    /// <summary>Node name of the settlement fact handed to the consumer.</summary>
    public const string SettlementNodeName = "exchange-settlement";

    /// <summary>Instantiates one exchange, or refuses to.</summary>
    public static AZOAResult<ExchangeBundle> Compose(ExchangeBundleInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var legs = BuildLegs(inputs);
        // The treasury reference is derived from the acting tenant already stamped on the
        // treasury signing context, so the project a treasury leg is signed FOR can never be a
        // different project from the one the exchange was driven by.
        var group = ExchangeGroupPlan.TryCreate(
            inputs.ChainType,
            inputs.Network,
            inputs.IdempotencyKey,
            legs,
            inputs.ReserveAddress,
            inputs.ReserveSigningContext,
            new ProjectTreasuryReference(
                inputs.ChainType,
                inputs.TreasurySigningContext.ActingTenantId,
                inputs.ProjectReference));
        if (group.IsError || group.Result is null)
        {
            return AZOAResult<ExchangeBundle>.FailureWithCode(
                group.Message, group.Code ?? AzoaErrorCodes.InvalidRequest);
        }

        var definition = BuildDefinition(inputs, group.Result);
        var bundle = new ActionGraphBundle
        {
            Id = DeterministicId(inputs.ReceiptReference, "bundle"),
            Name = $"exchange:{inputs.ProjectReference}:{inputs.Direction}",
            Description =
                "Treasury-rate exchange composed from the node's value primitives and settled as one "
                + "atomic group.",
            UserId = inputs.RecipientUserId,
            TargetChain = inputs.ChainType,
            DeploymentConfig = group.Result.ToDeploymentConfig(),
            IsActive = true,
            IsPublic = false,
        };

        return AZOAResult<ExchangeBundle>.Success(
            new ExchangeBundle(definition, bundle, group.Result),
            "Exchange bundle instantiated.");
    }

    /// <summary>
    /// The legs, in settlement order. The funds leg always exists; the units leg is on chain
    /// only when the recipient takes delivery now — a hold keeps the units in the reserve's own
    /// holding account, so there is nothing to move until it is released.
    /// </summary>
    private static IReadOnlyList<ExchangeGroupLeg> BuildLegs(ExchangeBundleInputs inputs)
    {
        var legs = new List<ExchangeGroupLeg>(2);

        var buying = inputs.Direction == ExchangeDirection.Buy;

        // Funds leg. On a buy the sponsor pays the PROJECT, never the platform reserve.
        legs.Add(buying
            ? new ExchangeGroupLeg(
                ExchangeLegRole.Funds,
                ExchangeLegSigner.Recipient,
                inputs.PlatformAssetId,
                inputs.RecipientAddress,
                inputs.TreasuryAddress,
                inputs.PlatformAmount,
                inputs.RecipientSigningContext)
            // A pay-out leaves the PROJECT's treasury, so it is signed with that treasury's own
            // derived key through IProjectTreasury.SignAsTreasuryAsync, never the reserve's.
            : new ExchangeGroupLeg(
                ExchangeLegRole.Funds,
                ExchangeLegSigner.ProjectTreasury,
                inputs.PlatformAssetId,
                inputs.TreasuryAddress,
                inputs.RecipientAddress,
                inputs.PlatformAmount,
                inputs.TreasurySigningContext));

        // Units leg. The reserve stays the counterparty for the project's asset in both
        // directions, which is what lets one partition ledger police every draw.
        if (buying)
        {
            if (inputs.ReleaseNotBefore is null)
            {
                legs.Add(new ExchangeGroupLeg(
                    ExchangeLegRole.Units,
                    ExchangeLegSigner.PlatformReserve,
                    inputs.ProjectAssetId,
                    inputs.ReserveAddress,
                    inputs.RecipientAddress,
                    inputs.ProjectUnitAmount,
                    inputs.ReserveSigningContext));
            }
        }
        else
        {
            legs.Add(new ExchangeGroupLeg(
                ExchangeLegRole.Units,
                ExchangeLegSigner.Recipient,
                inputs.ProjectAssetId,
                inputs.RecipientAddress,
                inputs.ReserveAddress,
                inputs.ProjectUnitAmount,
                inputs.RecipientSigningContext));
        }

        return legs;
    }

    private static ActionDefinition BuildDefinition(ExchangeBundleInputs inputs, ExchangeGroupPlan group)
    {
        var actionId = DeterministicId(inputs.ReceiptReference, "action");
        var nodes = new List<ActionNode>();
        var edges = new List<ActionEdge>();

        var preconditions = new ActionNode
        {
            Id = DeterministicId(inputs.ReceiptReference, PreconditionsNodeName),
            ActionId = actionId,
            NodeType = ActionNodeType.GateCheck,
            Name = PreconditionsNodeName,
            Config = SerializeGateCheck(inputs),
            IsEntry = true,
            ExecutionOrder = 0,
        };
        nodes.Add(preconditions);

        ActionNode? unitsHold = null;
        if (inputs.ReleaseNotBefore is { } releaseNotBefore)
        {
            unitsHold = new ActionNode
            {
                Id = DeterministicId(inputs.ReceiptReference, UnitsHoldNodeName),
                ActionId = actionId,
                NodeType = ActionNodeType.Hold,
                Name = UnitsHoldNodeName,
                Config = SerializeHold(inputs, releaseNotBefore),
                ExecutionOrder = 1,
            };
            nodes.Add(unitsHold);
            edges.Add(ControlEdge(actionId, preconditions, unitsHold, inputs.ReceiptReference, "gate-to-hold"));
        }

        var settlement = new ActionNode
        {
            Id = DeterministicId(inputs.ReceiptReference, SettlementNodeName),
            ActionId = actionId,
            NodeType = ActionNodeType.Emit,
            Name = SettlementNodeName,
            Config = SerializeEmit(inputs, group),
            IsTerminal = true,
            ExecutionOrder = nodes.Count,
        };
        nodes.Add(settlement);
        edges.Add(ControlEdge(
            actionId, unitsHold ?? preconditions, settlement, inputs.ReceiptReference, "to-settlement"));

        return new ActionDefinition
        {
            Id = actionId,
            Name = $"exchange:{inputs.ProjectReference}",
            Description =
                "Instantiated treasury-rate exchange. The value legs settle as one atomic group; "
                + "this definition is the record of that decision, not a dispatch path.",
            UserId = inputs.RecipientUserId,
            Nodes = nodes,
            Edges = edges,
            // Draft, and never written to the action store: nothing may start a run of it.
            Status = ActionStatus.Draft,
            IsPublic = false,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DispatchMetadataKey] = AtomicGroupDispatch,
                ["azoa.exchange.direction"] = inputs.Direction.ToString(),
                ["azoa.exchange.receipt"] = inputs.ReceiptReference,
                ["azoa.exchange.group-identity"] = group.GroupIdentity,
                ["azoa.exchange.policy-version"] = inputs.PolicyVersion,
                ["azoa.exchange.project-reference"] = inputs.ProjectReference,
                [FundsLegMetadataKey] = SerializeFundsLeg(group),
            },
        };
    }

    /// <summary>
    /// The preconditions the exchange refuses on, written as the gate's own predicate over
    /// injected reads. Every one of them is separately enforced by the manager; this node is
    /// what makes them legible on the record.
    /// </summary>
    private static string SerializeGateCheck(ExchangeBundleInputs inputs) => JsonSerializer.Serialize(
        new GateCheckNodeConfig
        {
            Predicate =
                "reads.valueAccessApproved == true && reads.rateAdmitted == true "
                + "&& reads.partitionAvailable == true",
            Reads = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["chainType"] = JsonText(inputs.ChainType),
                ["projectAssetId"] = JsonText(inputs.ProjectAssetId),
                ["reservePartition"] = JsonText(inputs.ReservePartition),
                ["projectUnitAmount"] = JsonText(inputs.ProjectUnitAmount.ToString(CultureInfo.InvariantCulture)),
                ["policyVersion"] = JsonText(inputs.PolicyVersion),
            },
        });

    private static string SerializeHold(ExchangeBundleInputs inputs, DateTimeOffset releaseNotBefore) =>
        JsonSerializer.Serialize(new HoldNodeConfig
        {
            ChainType = inputs.ChainType,
            HoldId = inputs.HoldId,
            AssetId = inputs.ProjectAssetId,
            Amount = inputs.ProjectUnitAmount.ToString(CultureInfo.InvariantCulture),
            ReservePartition = inputs.ReservePartition,
            BeneficiaryUserId = inputs.RecipientUserId,
            ReleaseNotBefore = releaseNotBefore,
            AutoRelease = true,
            Purpose = inputs.Purpose,
        });

    private static string SerializeEmit(ExchangeBundleInputs inputs, ExchangeGroupPlan group) =>
        JsonSerializer.Serialize(new EmitNodeConfig
        {
            EventType = "exchange.settled",
            Payload = JsonElementOf(new
            {
                receipt = inputs.ReceiptReference,
                direction = inputs.Direction.ToString(),
                projectReference = inputs.ProjectReference,
                spentAssetId = inputs.Direction == ExchangeDirection.Buy
                    ? inputs.PlatformAssetId
                    : inputs.ProjectAssetId,
                spentAmount = (inputs.Direction == ExchangeDirection.Buy
                    ? inputs.PlatformAmount
                    : inputs.ProjectUnitAmount).ToString(CultureInfo.InvariantCulture),
                receivedAssetId = inputs.Direction == ExchangeDirection.Buy
                    ? inputs.ProjectAssetId
                    : inputs.PlatformAssetId,
                receivedAmount = (inputs.Direction == ExchangeDirection.Buy
                    ? inputs.ProjectUnitAmount
                    : inputs.PlatformAmount).ToString(CultureInfo.InvariantCulture),
                groupIdentity = group.GroupIdentity,
                rate = inputs.DeclaredRate?.MinorUnitsPerBaseUnit.ToString(CultureInfo.InvariantCulture),
                rateSource = inputs.DeclaredRate?.Source,
                rateVersion = inputs.DeclaredRate?.Version,
                releaseNotBefore = inputs.ReleaseNotBefore,
            }),
        });

    private static string SerializeFundsLeg(ExchangeGroupPlan group)
    {
        var funds = group.Legs.First(leg => leg.Role == ExchangeLegRole.Funds);
        return JsonSerializer.Serialize(new
        {
            note =
                "No ActionNodeType expresses a fungible move between node-held accounts, so this leg "
                + "is recorded here rather than mis-typed as an NFT node. It settles in the group.",
            assetId = funds.AssetId,
            from = funds.FromAddress,
            to = funds.ToAddress,
            amount = funds.Amount.ToString(CultureInfo.InvariantCulture),
            signer = funds.Signer.ToString(),
        });
    }

    private static ActionEdge ControlEdge(
        Guid actionId, ActionNode source, ActionNode target, string receiptReference, string discriminator) =>
        new()
        {
            Id = DeterministicId(receiptReference, $"edge:{discriminator}"),
            ActionId = actionId,
            SourceNodeId = source.Id,
            TargetNodeId = target.Id,
            EdgeType = ActionEdgeType.Control,
        };

    private static JsonElement JsonText(string value) =>
        JsonSerializer.SerializeToElement(value);

    private static JsonElement JsonElementOf<T>(T value) =>
        JsonSerializer.SerializeToElement(value);

    /// <summary>
    /// A stable id per (exchange, part). Deriving rather than generating is what makes a
    /// retried exchange compose the SAME definition instead of a second one that only looks
    /// like it.
    /// </summary>
    private static Guid DeterministicId(string receiptReference, string part)
    {
        var digest = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"azoa:exchange:v1:{receiptReference}:{part}"));
        return new Guid(digest.AsSpan(0, 16));
    }
}
