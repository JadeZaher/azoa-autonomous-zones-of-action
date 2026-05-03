using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class STARODKController : ControllerBase
{
    private readonly ISTARManager _manager;

    public STARODKController(ISTARManager manager)
    {
        _manager = manager;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OASISResult<ISTARODK>>> Get(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _manager.GetAsync(id, request);
        if (result.IsError || result.Result == null) return NotFound(result);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<OASISResult<IEnumerable<ISTARODK>>>> GetAll([FromQuery] OASISRequest? request)
    {
        var result = await _manager.GetAllAsync(request);
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<OASISResult<ISTARODK>>> CreateOrUpdate([FromBody] STARODKCreateModel model, [FromQuery] OASISRequest? request)
    {
        var result = await _manager.CreateOrUpdateAsync(model, request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<OASISResponse>> Delete(Guid id, [FromQuery] OASISRequest? request)
    {
        var result = await _manager.DeleteAsync(id, request);
        if (result.IsError || !result.Result) return NotFound(result);
        return Ok(new OASISResponse { Message = "STAR ODK deleted." });
    }

    [HttpPost("{id:guid}/generate")]
    public async Task<ActionResult<OASISResult<ISTARODK>>> Generate(Guid id, [FromBody] STARDappGenerationRequest request, [FromQuery] OASISRequest? providerRequest)
    {
        var result = await _manager.GenerateAsync(id, request, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/deploy")]
    public async Task<ActionResult<OASISResult<ISTARODK>>> Deploy(Guid id, [FromQuery] OASISRequest? providerRequest)
    {
        var result = await _manager.DeployAsync(id, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }
}
