// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Models.Actions
{
    /// <summary>
    /// CredentialRevoke config (D19): revoke the soulbound credential named by
    /// <see cref="AssetId"/> on <see cref="ChainType"/>. The config names NO issuer and
    /// NO holder — the issuer is the run's acting user (checked against the credential's
    /// recorded issuer) and the holder is read from the ownership row, so no config field
    /// can redirect who is authorised or whose asset moves.
    /// </summary>
    /// <remarks>
    /// Declared here rather than in <c>NodeConfigs.cs</c> only because that file sits
    /// outside this change's file partition; it belongs beside its siblings and should be
    /// folded in on the next pass.
    /// </remarks>
    public class CredentialRevokeNodeConfig
    {
        /// <summary>Chain the credential lives on. Also the settlement rail the capability gate asks about.</summary>
        public string ChainType { get; set; } = "Algorand";

        /// <summary>Chain-native asset id (ASA id) of the credential to revoke.</summary>
        public string AssetId { get; set; } = string.Empty;

        /// <summary>Optional reason recorded on the operation row. Never interpreted.</summary>
        public string? Reason { get; set; }
        public string? Network { get; set; }
        public string? ContractAddress { get; set; }
        public AZOA.WebAPI.Models.Blockchain.NftFeeAuthorization? FeeAuthorization { get; set; }
    }
}

namespace AZOA.WebAPI.Services.Actions.Handlers
{
    /// <summary>
    /// Handles <see cref="ActionNodeType.CredentialRevoke"/> — Tier-2 chain action (D19).
    /// Claws a soulbound credential back from its holder to the issuer with an on-chain
    /// ASA clawback. See <c>Services/Actions/AGENTS.md</c> §credential-revoke.
    /// </summary>
    /// <remarks>
    /// <para>Mechanism only: the handler deserializes, forwards to
    /// <see cref="ICredentialManager"/>, and projects the outcome. Authorization (caller
    /// == recorded issuer) and every chain precondition live below it, so a second
    /// dispatch seam cannot reach a different answer.</para>
    /// <para><b>A pending transaction is reported as a FAILURE, not a success.</b> A
    /// broadcast that has not confirmed comes back with its tx hash attached so the
    /// reconcile-before-retry engine verifies chain truth instead of blind-retrying —
    /// reporting it Succeeded is exactly the fabricated-success bug this codebase has
    /// shipped before.</para>
    /// </remarks>
    public sealed class CredentialRevokeNodeHandler : IActionNodeHandler
    {
        private readonly ICredentialManager _credentials;

        public CredentialRevokeNodeHandler(ICredentialManager credentials) => _credentials = credentials;

        public ActionNodeType NodeType => ActionNodeType.CredentialRevoke;

        /// <summary>
        /// True: this node reaches an on-chain sign with the platform's clawback key.
        /// Omitting it would let the pre-execution gate pass a runner who has no
        /// destination on the rail straight through to a signing path.
        /// </summary>
        public bool RequiresChainCapability => true;

        /// <summary>
        /// A revocation settles on the chain the credential lives on — where the clawback
        /// is signed and where the issuer must be able to receive the returned asset.
        /// </summary>
        public string? ResolveSettlementRailId(ActionNode node) =>
            ActionNodeConfig.TryDeserialize<CredentialRevokeNodeConfig>(
                node.Config, nameof(ActionNodeType.CredentialRevoke), out var cfg, out _)
                ? cfg.ChainType
                : null;

        public async Task<ActionNodeHandlerResult> HandleAsync(
            ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            if (!ActionNodeConfig.TryDeserialize<CredentialRevokeNodeConfig>(
                    context.Node.Config, nameof(ActionNodeType.CredentialRevoke), out var cfg, out var cfgError))
                return ActionNodeResults.Fail(cfgError);

            if (string.IsNullOrWhiteSpace(cfg.AssetId))
                return ActionNodeResults.Invalid("[CredentialRevoke] AssetId is required and can never succeed on retry.");

            // Idempotency seed: the run/node identity (Bridge/Back precedent). A
            // re-evaluation of the same node therefore dedupes to ONE irreversible
            // clawback rather than taking the credential twice.
            var clientIdempotencyKey = $"{context.RunId}:{context.NodeId}";

            // Actor is ALWAYS the run-context user; the manager then checks that actor
            // IS the credential's recorded issuer. The config carries no user at all.
            var r = await _credentials.RevokeAsync(
                cfg.AssetId,
                new CredentialRevokeRequest { ChainType = cfg.ChainType, Reason = cfg.Reason },
                context.ActingUserId,
                clientIdempotencyKey,
                context.ActingTenantId,
                ct);

            // Forward the tx hash on EVERY outcome: a broadcast-then-timeout surfaces as
            // a non-success while the clawback may already have landed, so the engine
            // must reconcile against chain truth rather than blind-retry.
            var txHash = r.Result?.TxHash;
            var chainType = string.IsNullOrWhiteSpace(r.Result?.ChainType) ? cfg.ChainType : r.Result!.ChainType;

            // A broadcast-but-unconfirmed revoke now arrives here with IsError = true (the
            // manager refuses to hand any client a success it cannot prove), so it lands on
            // this branch carrying the manager's own "submitted but NOT confirmed" message
            // and its tx hash — which is exactly what reconcile-before-retry needs.
            if (r.IsError || r.Result is null)
                return ActionNodeResults.Fail(r.Message, txHash: txHash, chainType: chainType);

            // Anything short of a confirmed revocation fails the node. The run must not
            // proceed as though the credential moved when the chain has not said so. This
            // stays as the second guard: a result that claims success while carrying a
            // non-Completed status is malformed and must never advance a run.
            if (!r.Result.IsConfirmed)
                return ActionNodeResults.Fail(
                    $"[CredentialRevoke] credential {cfg.AssetId} is {r.Result.Status}, not revoked: {r.Message}",
                    txHash: txHash,
                    chainType: chainType);

            var outputJson = System.Text.Json.JsonSerializer.Serialize(r.Result, ActionNodeJson.Options);
            return ActionNodeResults.Ok(outputJson, txHash: txHash, chainType: chainType);
        }
    }
}
