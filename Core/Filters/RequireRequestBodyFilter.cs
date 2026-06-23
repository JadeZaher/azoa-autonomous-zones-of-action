using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AZOA.WebAPI.Core.Filters;

/// <summary>
/// Global action filter that fails closed on a missing/malformed request body.
///
/// When a <c>[FromBody]</c> parameter deserializes to <c>null</c> (empty body,
/// <c>null</c> JSON literal, or a content-type the binder could not bind), the
/// action would otherwise run and dereference the argument — an unhandled
/// <see cref="NullReferenceException"/> surfacing as an opaque 500. This filter
/// short-circuits BEFORE the action with a uniform 400 carrying the same
/// <c>isError</c>/<c>message</c> shape every <c>AZOAResult&lt;T&gt;</c> error uses,
/// so every controller is covered without per-endpoint guards.
///
/// Registered globally (see <c>Program.cs</c> → <c>AddControllers</c>), it targets
/// only parameters actually bound from the body, so query/route/form actions are
/// untouched.
/// </summary>
public sealed class RequireRequestBodyFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        foreach (var parameter in context.ActionDescriptor.Parameters)
        {
            if (parameter is not Microsoft.AspNetCore.Mvc.Controllers.ControllerParameterDescriptor descriptor)
                continue;

            // Only guard parameters the framework binds from the request body.
            if (descriptor.ParameterInfo.GetCustomAttributes(typeof(FromBodyAttribute), inherit: false).Length == 0)
                continue;

            if (!context.ActionArguments.TryGetValue(parameter.Name, out var value) || value is null)
            {
                context.Result = new BadRequestObjectResult(new
                {
                    isError = true,
                    error = "Request body is required.",
                    message = "Request body is required.",
                });
                return;
            }
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        // No post-action work.
    }
}
