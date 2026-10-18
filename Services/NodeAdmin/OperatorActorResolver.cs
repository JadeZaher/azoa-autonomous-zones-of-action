using System.Diagnostics.CodeAnalysis;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Resolves the request's <see cref="OperatorActor"/> once from the authenticated principal; see <c>Services/NodeAdmin/AGENTS.md</c> §operator-actor.</summary>
public static class OperatorActorResolver
{
    private const string ItemKey = "AZOA.OperatorActor";

    /// <summary>The operator actor of an authorized operator route; false for any other principal.</summary>
    public static bool TryGetOperatorActor(this HttpContext context, [NotNullWhen(true)] out OperatorActor? actor)
    {
        if (context.Items.TryGetValue(ItemKey, out var cached))
        {
            actor = cached as OperatorActor;
            return actor is not null;
        }
        actor = Resolve(context);
        context.Items[ItemKey] = actor;
        return actor is not null;
    }

    /// <summary>Validated node-admin caller → admin/root; legacy reserved-operator token → legacy; otherwise null.</summary>
    public static OperatorActor? Resolve(HttpContext context)
    {
        var principal = context.User;
        if (NodeAdminClaims.IsNodeAdmin(principal))
        {
            var caller = NodeAdminCaller.From(context);
            if (caller is null) return null;
            if (caller.RootFingerprint is { } fingerprint) return OperatorActor.Root(fingerprint);
            return caller.Principal.StartsWith(NodeAdminClaims.AdminPrincipalPrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(caller.Principal[NodeAdminClaims.AdminPrincipalPrefix.Length..], "D", out var adminId)
                    ? OperatorActor.Admin(adminId)
                    : null;
        }
        return AzoaClaims.IsNodeOperator(principal)
            && AzoaClaims.TryGetSubjectId(principal, out var subject)
            && subject == OperatorActor.Legacy.SubjectUserId
            && principal.GetActingTenantId() is null
                ? OperatorActor.Legacy
                : null;
    }
}
