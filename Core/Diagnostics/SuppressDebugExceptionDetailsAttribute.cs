namespace AZOA.WebAPI.Core.Diagnostics;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class SuppressDebugExceptionDetailsAttribute : Attribute
{
}
