using System.Text.RegularExpressions;
using AZOA.WebAPI.Core;
using Microsoft.AspNetCore.Authorization;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>What a node admin needs on one operator route: a permission (null = legacy operator only) and whether step-up applies.</summary>
public sealed record NodeAdminRouteGrant(string? Permission, bool RequireRecent);

/// <summary>The single route → permission table for every operator-policy route (plan §4, M7); see <c>Services/NodeAdmin/AGENTS.md</c> §route-permission-map.</summary>
public static partial class NodeAdminRoutePermissions
{
    /// <summary>Policies that admit the node operator; a route carrying any of them must be mapped.</summary>
    public static readonly IReadOnlySet<string> OperatorPolicies = new HashSet<string>(
        ["Operator", "NodeGovern", "NodeOperatorSession", "RecentNodeOperatorSession", "DappRoleAssignment"], StringComparer.Ordinal);

    /// <summary>Policies whose legacy branch requires a recent login; a mapped route carrying one requires node-admin step-up.</summary>
    public static readonly IReadOnlySet<string> RecentOperatorPolicies = new HashSet<string>(
        ["RecentNodeOperatorSession", "DappRoleAssignment"], StringComparer.Ordinal);

    private static NodeAdminRouteGrant Read(string permission) => new(permission, false);
    private static NodeAdminRouteGrant Write(string permission) => new(permission, false);
    private static NodeAdminRouteGrant Recent(string permission) => new(permission, true);

    /// <summary>Key = <c>VERB normalized-template</c> (lowercase, no route constraints).</summary>
    public static readonly IReadOnlyDictionary<string, NodeAdminRouteGrant> Map = new Dictionary<string, NodeAdminRouteGrant>(StringComparer.Ordinal)
    {
        // Governance (NodeGovernanceController)
        ["GET api/node-governance/parameters"] = Read(NodeAdminPermissions.GovernanceRead),
        ["PUT api/node-governance/parameters"] = Write(NodeAdminPermissions.GovernanceWrite),
        ["GET api/node-governance/fee-schedule"] = Read(NodeAdminPermissions.GovernanceRead),
        ["PUT api/node-governance/fee-schedule"] = Write(NodeAdminPermissions.GovernanceWrite),
        ["GET api/node-governance/treasury/{chain}/{network}"] = Read(NodeAdminPermissions.GovernanceRead),
        ["PUT api/node-governance/treasury"] = Write(NodeAdminPermissions.GovernanceWrite),
        ["GET api/node-governance/audit"] = Read(NodeAdminPermissions.AuditRead),
        ["GET api/node-governance/fee-audit"] = Read(NodeAdminPermissions.AuditRead),
        ["GET api/node-governance/treasury-audit"] = Read(NodeAdminPermissions.AuditRead),

        // KYC / AML (NodeOperatorController, AmlReviewController)
        ["GET api/operator/overview"] = Read(NodeAdminPermissions.KycRead),
        ["GET api/operator/kyc/providers"] = Read(NodeAdminPermissions.KycRead),
        ["GET api/operator/kyc/providers/{providerkey}"] = Read(NodeAdminPermissions.KycRead),
        ["PUT api/operator/kyc/providers/{providerkey}"] = Recent(NodeAdminPermissions.KycConfigure),
        ["GET api/operator/tenants"] = Read(NodeAdminPermissions.KycRead),
        ["PUT api/operator/tenants/{tenantid}/kyc-provider"] = Recent(NodeAdminPermissions.KycConfigure),
        ["GET api/operator/kyc/submissions"] = Read(NodeAdminPermissions.KycRead),
        ["POST api/operator/kyc/submissions/{id}/decision"] = Recent(NodeAdminPermissions.KycDecide),
        ["GET api/operator/kyc/audit"] = Read(NodeAdminPermissions.AuditRead),
        ["GET api/operator/kyc/aml-reviews"] = Read(NodeAdminPermissions.KycRead),
        ["POST api/operator/kyc/aml-reviews/{userid}/decision"] = Recent(NodeAdminPermissions.KycDecide),

        // Users (UserController, SearchController)
        ["GET api/user"] = Read(NodeAdminPermissions.UsersRead),
        ["POST api/search/operator"] = Read(NodeAdminPermissions.UsersRead),
        ["PUT api/user/{id}/dapp-role"] = Recent(NodeAdminPermissions.UsersRolesAssign),

        // Tenant keys, key rotation, catalog
        ["POST api/apikey/tenant"] = Recent(NodeAdminPermissions.TenantsKeysMint),
        ["POST api/admin/key-rotation/rotate"] = Recent(NodeAdminPermissions.KeysRotate),
        ["POST api/holon-types"] = Write(NodeAdminPermissions.CatalogManage),
        ["POST api/holon-types/{assettype}/deactivate"] = Write(NodeAdminPermissions.CatalogManage),
        ["DELETE api/holon-types/{assettype}"] = Write(NodeAdminPermissions.CatalogManage),
        ["GET api/operator/tenants/{tenantid}/webhook-registration"] = Recent(NodeAdminPermissions.CatalogManage),
        ["PUT api/operator/tenants/{tenantid}/webhook-registration"] = Recent(NodeAdminPermissions.CatalogManage),
        ["POST api/operator/tenants/{tenantid}/webhook-registration/withdraw"] = Recent(NodeAdminPermissions.CatalogManage),

        // Money (fiat events/payouts/reversals/on-ramp lots/floats, project treasuries, platform-token reads)
        ["GET api/admin/project-treasuries"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/admin/project-treasuries/tenant/{tenantid}"] = Read(NodeAdminPermissions.MoneyRead),
        ["POST api/admin/project-treasuries/resolve"] = Write(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/project-treasuries/close"] = Write(NodeAdminPermissions.MoneyOperate),
        ["GET api/admin/fiat-events"] = Read(NodeAdminPermissions.MoneyRead),
        ["POST api/admin/fiat-events/{id}/replay"] = Write(NodeAdminPermissions.MoneyOperate),
        ["GET api/operator/fiat-events"] = Read(NodeAdminPermissions.MoneyRead),
        ["POST api/operator/fiat-events/{id}/replay"] = Write(NodeAdminPermissions.MoneyOperate),
        ["GET api/admin/fiat-payouts"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/admin/fiat-payouts/{id}"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/admin/fiat-payouts/accounts/{userid}"] = Read(NodeAdminPermissions.MoneyRead),
        ["POST api/admin/fiat-payouts/{id}/advance"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-payouts/{id}/return"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-payouts/{id}/recover"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-payouts/{id}/resolve"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-payouts/{id}/park"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-payouts/{id}/restart"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-payouts/accounts/{userid}/setup/resolve"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["GET api/admin/fiat-reversals"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/admin/fiat-reversals/{id}"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/admin/fiat-reversals/accounts/{userid}"] = Read(NodeAdminPermissions.MoneyRead),
        ["POST api/admin/fiat-reversals/{id}/recover"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-reversals/{id}/settle"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-reversals/{id}/resolve"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-reversals/{id}/restart"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/fiat-reversals/{id}/clear-flag"] = Recent(NodeAdminPermissions.MoneyOperate),
        ["GET api/admin/on-ramp-lots"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/admin/on-ramp-lots/intents/{intentid}"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/admin/on-ramp-lots/accounts/{userid}/issued"] = Read(NodeAdminPermissions.MoneyRead),
        ["POST api/admin/on-ramp-lots/intents/{intentid}/resume"] = Write(NodeAdminPermissions.MoneyOperate),
        ["POST api/admin/on-ramp-lots/intents/{intentid}/refund"] = Write(NodeAdminPermissions.MoneyOperate),
        ["GET api/operator/on-ramp-lots"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/operator/on-ramp-lots/intents/{intentid}"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/operator/on-ramp-lots/accounts/{userid}/issued"] = Read(NodeAdminPermissions.MoneyRead),
        ["POST api/operator/on-ramp-lots/intents/{intentid}/resume"] = Write(NodeAdminPermissions.MoneyOperate),
        ["POST api/operator/on-ramp-lots/intents/{intentid}/refund"] = Write(NodeAdminPermissions.MoneyOperate),
        ["GET api/admin/tenant-payout-floats/{tenantid}"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/admin/platform-token"] = Read(NodeAdminPermissions.MoneyRead),
        ["GET api/admin/platform-token/creation/{id}"] = Read(NodeAdminPermissions.MoneyRead),

        // Platform genesis
        ["POST api/admin/platform-token/genesis"] = Recent(NodeAdminPermissions.PlatformGenesis),
        ["POST api/admin/platform-token/creation/{id}/refresh"] = Write(NodeAdminPermissions.PlatformGenesis),
        ["POST api/admin/platform-token/creation/{id}/quote"] = Recent(NodeAdminPermissions.PlatformGenesis),
        ["POST api/admin/platform-token/creation/{id}/accept-quote"] = Recent(NodeAdminPermissions.PlatformGenesis),
        ["POST api/admin/platform-token/creation/{id}/continue"] = Recent(NodeAdminPermissions.PlatformGenesis),
        ["POST api/admin/platform-token/creation/{id}/replace"] = Recent(NodeAdminPermissions.PlatformGenesis),

        // Recovery (sagas, action/deployment/blockchain-op/dex/wtt/ethereum/hosted-session, sweeps, backfill,
        // retired rows, wallet-identity conflicts, webhook event retries)
        ["POST api/action/runs/reconcile-sweep"] = Write(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/action-recovery"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/action-recovery/{id}"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/action-recovery/{id}/history"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/action-recovery/{id}/retry"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/admin/backfill/list"] = Read(NodeAdminPermissions.RecoveryOperate),
        ["POST api/admin/backfill/apply"] = Write(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/blockchain-operations/stuck"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/blockchain-operations/{id}/resolve"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/deployments"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/deployments/{id}/resolve"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/dex/operations/{id}/settle-review"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/ethereum/recovery/{network}"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/ethereum/recovery/{network}/activate"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/ethereum/recovery/{network}/{id}"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/exchange/operator/retired-rows"] = Read(NodeAdminPermissions.RecoveryOperate),
        ["POST api/exchange/operator/retired-rows/{operationid}/resolve"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/hosted-sessions/reconciliation"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/hosted-sessions/{sessionid}/reconcile"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/hosted-sessions/{sessionid}/refresh"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/admin/sagas/dead-letters"] = Read(NodeAdminPermissions.RecoveryOperate),
        ["POST api/admin/sagas/{id}/requeue"] = Write(NodeAdminPermissions.RecoveryOperate),
        ["POST api/admin/sagas/{id}/cancel"] = Write(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/wallet-identities"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/wallet-identities/clear-conflicting-binding"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/tenants/{tenantid}/webhook-events"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/tenants/{tenantid}/webhook-events/{eventid}"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/tenants/{tenantid}/webhook-events/{eventid}/retry"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/tenants/{tenantid}/consent-webhook-events"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/tenants/{tenantid}/consent-webhook-events/{eventid}"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/tenants/{tenantid}/consent-webhook-events/{eventid}/retry"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/wtt/stage-keys/readiness"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/wtt/stage-keys/rewrap"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["GET api/operator/wtt/operations/{id}"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/wtt/operations/{id}/reconcile"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/wtt/operations/{id}/refetch-proof"] = Recent(NodeAdminPermissions.RecoveryOperate),
        ["POST api/operator/wtt/operations/{id}/resume-proof"] = Recent(NodeAdminPermissions.RecoveryOperate),

        // Legacy operator only: its own password login and session revocation.
        ["POST api/operator/session"] = new(null, false),
        ["POST api/operator/session/revoke"] = new(null, true),
    };

    /// <summary>Row key from an HTTP verb and a route template.</summary>
    public static string Key(string verb, string? template) => $"{verb.Trim().ToUpperInvariant()} {Normalize(template)}";

    /// <summary>Lowercase template without leading <c>~/</c> or slashes and without route constraints.</summary>
    public static string Normalize(string? template)
        => Constraint().Replace((template ?? string.Empty).Trim().TrimStart('~').Trim('/').ToLowerInvariant(), "{$1}");

    /// <summary>True when the routed endpoint carries any operator policy.</summary>
    public static bool IsOperatorEndpoint(Endpoint? endpoint)
        => endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy is { } policy && OperatorPolicies.Contains(policy)) == true;

    /// <summary>The grant of an operator-policy endpoint for <paramref name="method"/>; false when unmapped (node admins are then denied, M7).</summary>
    public static bool TryResolve(Endpoint? endpoint, string method, out NodeAdminRouteGrant grant)
    {
        grant = default!;
        if (endpoint is not RouteEndpoint route || !IsOperatorEndpoint(endpoint)
            || !Map.TryGetValue(Key(method, route.RoutePattern.RawText), out var found)) return false;
        grant = found;
        return true;
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)(:[^}]*)?\??\}")]
    private static partial Regex Constraint();
}
