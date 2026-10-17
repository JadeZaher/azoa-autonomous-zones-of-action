using AZOA.WebAPI.Services.Admin;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Locks the exact pending genesis definition under a current recent node-operator session.</summary>
public static class PlatformGenesisFence
{
    public const string FailureMarker = "PLATFORM_GENESIS_AUTHORITY_CHANGED";

    public static IReadOnlyList<SurrealQuery>? Create(FungibleCreationCommand command, PlatformGenesisAuthority purpose)
    {
        if (!PlatformGenesisIdentity.Matches(command, purpose)) return null;
        var credential = Operator(purpose.Credential);
        if (credential is null) return null;
        var statements = credential.ToList();
        statements.AddRange(Definition(command, purpose));
        return statements;
    }

    /// <summary>Checks the actual recent node-operator session before freezing or continuing genesis.</summary>
    public static IReadOnlyList<SurrealQuery>? Operator(SigningCredentialWitness credential)
    {
        if (credential.ActorUserId != NodeOperatorIdentity.UserId || credential.TenantId is not null
            || credential.TokenUse != AzoaClaims.TokenUseNodeOperator || credential.OwnerAuthority || credential.IssuedAt is null) return null;
        var current = SigningAuthorityFence.ForRunAdmission(credential, "fungible_genesis");
        if (current is null) return null;
        var statements = current.ToList();
        // raw: actual recent operator authorization joins the immutable definition transaction.
        statements.Add(SurrealQuery.Of("""
            IF $_genesis_auth_time < time::now() - 10m OR $_genesis_auth_time > time::now() + 1m
                { THROW 'PLATFORM_GENESIS_AUTHORITY_CHANGED' }
            """).WithParam("_genesis_auth_time", credential.IssuedAt.Value));
        return statements;
    }

    /// <summary>Locks the immutable platform definition for accounting an already-finalized creation.</summary>
    public static IReadOnlyList<SurrealQuery> Definition(FungibleCreationCommand command, PlatformGenesisAuthority purpose)
    {
        if (!PlatformGenesisIdentity.Matches(command, purpose)) return [SurrealQuery.Of("THROW 'PLATFORM_GENESIS_AUTHORITY_CHANGED'")];
        // raw: binds the original pending platform definition to the same finalized creation transaction.
        return [SurrealQuery.Of("""
            LET $_genesis_definition = (UPDATE ONLY type::record('platform_token', $_genesis_id) SET creation_operation_id = $_genesis_operation
                WHERE status = 'Pending' AND asset_id = NONE AND chain = $_genesis_chain AND network = $_genesis_network
                    AND name = $_genesis_name AND unit_name = $_genesis_unit AND decimals = $_genesis_decimals
                    AND total_supply = $_genesis_total AND reserve_address = $_genesis_creator
                    AND genesis_operation_key = $_genesis_key AND genesis_user_id = type::record('user', $_genesis_actor)
                    AND (creation_operation_id = NONE OR creation_operation_id = $_genesis_operation) RETURN AFTER)
            """).WithParam("_genesis_id", purpose.PlatformTokenId).WithParam("_genesis_chain", command.Intent.ChainType)
                .WithParam("_genesis_network", command.Intent.Network.ToString()).WithParam("_genesis_name", command.Intent.Name)
                .WithParam("_genesis_unit", command.Intent.UnitName).WithParam("_genesis_decimals", command.Intent.Decimals)
                .WithParam("_genesis_total", command.Intent.TotalBaseUnits).WithParam("_genesis_creator", command.Intent.CreatorAddress)
                .WithParam("_genesis_key", command.RequestKey).WithParam("_genesis_actor", command.ActorUserId.ToString("N"))
                .WithParam("_genesis_operation", FungibleCreationIdentity.OperationId(command.ActorUserId, null, command.RequestKey)),
            SurrealQuery.Of("IF $_genesis_definition = NONE { THROW 'PLATFORM_GENESIS_AUTHORITY_CHANGED' }")];
    }
}
