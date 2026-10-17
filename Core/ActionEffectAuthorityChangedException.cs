namespace AZOA.WebAPI.Core;

/// <summary>Signals that a workflow lost authority before durable domain admission.</summary>
public sealed class ActionEffectAuthorityChangedException : Exception
{
    public ActionEffectAuthorityChangedException() : base("The workflow no longer authorizes this effect.") { }
}
