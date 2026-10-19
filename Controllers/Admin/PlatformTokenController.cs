// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.NodeAdmin;

namespace AZOA.WebAPI.Controllers.Admin;

/// <summary>Reads verified platform issuance and admits an operator-authorized genesis.</summary>
/// <remarks>Promotion checklist + the decide-once warning: <c>docs/PLATFORM-TOKEN-GENESIS.md</c>.</remarks>
[ApiController]
[Route("api/admin/platform-token")]
[Authorize(Policy = "Operator")]
public sealed class PlatformTokenController : ControllerBase
{
    private readonly IPlatformTokenGenesis _genesis;
    private readonly AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? _signingAuthority;

    public PlatformTokenController(IPlatformTokenGenesis genesis, AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? signingAuthority = null)
    {
        _genesis = genesis ?? throw new ArgumentNullException(nameof(genesis));
        _signingAuthority = signingAuthority;
    }

    /// <summary>Returns the verified platform definition and available reserve supply.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AZOAResult<PlatformTokenStateResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AZOAResult<PlatformTokenStateResponse>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AZOAResult<PlatformTokenStateResponse>>> Get(CancellationToken ct)
    {
        var state = await _genesis.GetStateAsync(ct);
        if (state.IsError || state.Result is null)
        {
            var failure = new AZOAResult<PlatformTokenStateResponse>
            {
                IsError = true,
                Message = state.Message,
                Code = state.Code,
            };

            return state.Code switch
            {
                PlatformTokenGenesisCodes.NotGenesised => NotFound(failure),
                PlatformTokenGenesisCodes.SupplyUnavailable =>
                    StatusCode(StatusCodes.Status503ServiceUnavailable, failure),
                _ => BadRequest(failure),
            };
        }

        return Ok(AZOAResult<PlatformTokenStateResponse>.Success(
            PlatformTokenStateResponse.From(state.Result), state.Message));
    }

    /// <summary>Claims or replays genesis without accepting costs or submitting a new transaction.</summary>
    [HttpPost("genesis")]
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<PlatformTokenGenesisResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AZOAResult<PlatformTokenGenesisResponse>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AZOAResult<PlatformTokenGenesisResponse>>> Genesis(
        [FromBody] PlatformTokenGenesisRequest request, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor))
            return Unauthorized(new AZOAResult<PlatformTokenGenesisResponse>
            {
                IsError = true,
                Message = "Invalid token.",
            });

        var factory = _signingAuthority ?? throw new InvalidOperationException("Platform genesis credential capture is not registered.");
        var captured = await factory.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        request.AuthenticationWitness = captured.Result;
        var outcome = await _genesis.RunAsync(request, operatorActor.SubjectUserId, ct);
        if (outcome.IsError || outcome.Result is null)
        {
            var failure = new AZOAResult<PlatformTokenGenesisResponse>
            {
                IsError = true,
                Message = outcome.Message,
                Code = outcome.Code,
            };

            return outcome.Code switch
            {
                PlatformTokenGenesisCodes.AlreadyComplete => Conflict(failure),
                PlatformTokenGenesisCodes.DefinitionFrozen => Conflict(failure),
                PlatformTokenGenesisCodes.Unrecorded => Conflict(failure),
                _ => BadRequest(failure),
            };
        }

        var response = AZOAResult<PlatformTokenGenesisResponse>.Success(PlatformTokenGenesisResponse.From(outcome.Result), outcome.Message);
        return outcome.Result.Operation is { State: not "Completed" } ? Accepted(response) : Ok(response);
    }
}

/// <summary>The platform unit's on-chain definition. Amounts are base-unit decimal strings.</summary>
public sealed class PlatformTokenDefinitionResponse
{
    public string ChainType { get; init; } = string.Empty;
    public string Network { get; init; } = string.Empty;
    public string AssetId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string UnitName { get; init; } = string.Empty;
    public int Decimals { get; init; }
    public string TotalSupply { get; init; } = string.Empty;
    public string ReserveAddress { get; init; } = string.Empty;
    public DateTimeOffset? ActivatedAt { get; init; }

    /// <summary>Projects the domain definition onto the allowlisted wire shape.</summary>
    public static PlatformTokenDefinitionResponse From(PlatformTokenDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new PlatformTokenDefinitionResponse
        {
            ChainType = definition.ChainType,
            Network = definition.Network,
            AssetId = definition.AssetId,
            Name = definition.Name,
            UnitName = definition.UnitName,
            Decimals = definition.Decimals,
            TotalSupply = definition.TotalSupply.ToString(CultureInfo.InvariantCulture),
            ReserveAddress = definition.ReserveAddress,
            ActivatedAt = definition.ActivatedAt,
        };
    }
}

/// <summary>
/// Supply in base units as decimal strings — BigInteger has no wire form, and every
/// amount in this codebase crosses the wire as a string so nothing is truncated.
/// </summary>
public sealed class PlatformTokenSupplyResponse
{
    /// <summary>The genesis ceiling: everything this node could ever issue.</summary>
    public string Total { get; init; } = string.Empty;

    /// <summary>Reserve outflow: units that have left the reserve and not come back.</summary>
    public string Circulating { get; init; } = string.Empty;

    /// <summary>Units committed to holds; promised, but not yet out of the reserve.</summary>
    public string Held { get; init; } = string.Empty;

    /// <summary>Total minus circulating: what the reserve still accounts for.</summary>
    public string InReserve { get; init; } = string.Empty;

    /// <summary>Projects the domain supply onto the allowlisted wire shape.</summary>
    public static PlatformTokenSupplyResponse From(PlatformTokenSupply supply)
    {
        ArgumentNullException.ThrowIfNull(supply);

        return new PlatformTokenSupplyResponse
        {
            Total = supply.Total.ToString(CultureInfo.InvariantCulture),
            Circulating = supply.Circulating.ToString(CultureInfo.InvariantCulture),
            Held = supply.Held.ToString(CultureInfo.InvariantCulture),
            InReserve = supply.InReserve.ToString(CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>The platform unit plus its live supply.</summary>
public sealed class PlatformTokenStateResponse
{
    public PlatformTokenDefinitionResponse Definition { get; init; } = new();
    public PlatformTokenSupplyResponse Supply { get; init; } = new();

    /// <summary>Projects the domain state onto the allowlisted wire shape.</summary>
    public static PlatformTokenStateResponse From(PlatformTokenState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new PlatformTokenStateResponse
        {
            Definition = PlatformTokenDefinitionResponse.From(state.Definition),
            Supply = PlatformTokenSupplyResponse.From(state.Supply),
        };
    }
}

/// <summary>What a genesis run did.</summary>
public sealed class PlatformTokenGenesisResponse
{
    public AZOA.WebAPI.Models.Blockchain.FungibleCreationView? Operation { get; init; }
    public string State => Operation?.State ?? "Completed";
    public PlatformTokenDefinitionResponse Definition { get; init; } = new();

    /// <summary><c>false</c> when the call replayed an already-completed genesis.</summary>
    public bool Created { get; init; }

    /// <summary>Funding and recovery guidance for the original genesis operation.</summary>
    public string? OperationsNote { get; init; }

    /// <summary>Projects the domain outcome onto the allowlisted wire shape.</summary>
    public static PlatformTokenGenesisResponse From(PlatformTokenGenesisOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new PlatformTokenGenesisResponse
        {
            Definition = PlatformTokenDefinitionResponse.From(outcome.Definition),
            Created = outcome.Created,
            OperationsNote = outcome.OperationsNote,
            Operation = outcome.Operation,
        };
    }
}
