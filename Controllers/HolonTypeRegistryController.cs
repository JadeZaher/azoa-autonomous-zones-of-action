// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Opt-in Holon AssetType registry surface (final-hardening-cutover F5). Reads are open
/// to any authenticated caller (the platform vocabulary is public); registration and
/// mutation are <c>Operator</c>-scoped (the same hardened gate as the backfill / reconcile
/// admin surfaces — an API key can never reach it), because the registry governs a
/// platform-wide constraint on every tenant's holons.
/// </summary>
/// <remarks>Contract + rationale: <c>Managers/AGENTS.md</c> §holon-type-registry.</remarks>
[ApiController]
[Route("api/holon-types")]
[Authorize]
public sealed class HolonTypeRegistryController : ControllerBase
{
    private readonly IHolonTypeRegistryManager _registry;

    public HolonTypeRegistryController(IHolonTypeRegistryManager registry)
    {
        _registry = registry;
    }

    /// <summary>Lists every registered holon type, most recent first. Open to any authenticated caller.</summary>
    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<HolonType>>>> List([FromQuery] AZOARequest? request)
        => Ok(await _registry.ListAsync(request));

    /// <summary>Gets one registered holon type by its AssetType name. Open to any authenticated caller.</summary>
    [HttpGet("{assetType}")]
    public async Task<ActionResult<AZOAResult<HolonType>>> Get(string assetType, [FromQuery] AZOARequest? request)
    {
        var result = await _registry.GetAsync(assetType, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    /// <summary>Registers or re-registers a holon type. Operator-scoped.</summary>
    [HttpPost]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<AZOAResult<HolonType>>> Register([FromBody] HolonTypeRegisterModel model, [FromQuery] AZOARequest? request)
    {
        var result = await _registry.RegisterAsync(model, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Marks a registered type inactive (validation then ignores it). Operator-scoped.</summary>
    [HttpPost("{assetType}/deactivate")]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<AZOAResult<HolonType>>> Deactivate(string assetType, [FromQuery] AZOARequest? request)
    {
        var result = await _registry.DeactivateAsync(assetType, request);
        if (result.IsError) return NotFound(result);
        return Ok(result);
    }

    /// <summary>Hard-deletes a registration. Operator-scoped.</summary>
    [HttpDelete("{assetType}")]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<AZOAResponse>> Delete(string assetType, [FromQuery] AZOARequest? request)
    {
        var result = await _registry.DeleteAsync(assetType, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new AZOAResponse { Message = "Holon type deleted." });
    }
}
