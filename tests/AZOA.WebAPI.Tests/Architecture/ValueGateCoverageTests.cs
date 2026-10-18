using System.Reflection;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Services.Kyc;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>
/// Value paths must reach the KYC decision only through the risk-hold-wrapped <see cref="IRealValueKycGate"/>
/// (RV-L2 L2-8 / RV-X X-2); see Architecture/AGENTS.md §value-gate-coverage.
/// </summary>
public sealed class ValueGateCoverageTests
{
    [Fact]
    public void No_production_service_depends_on_the_unwrapped_kyc_gate()
    {
        var offenders = typeof(Program).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type != typeof(KycGateService))
            .Where(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IKycGateService))))
            .Select(type => type.FullName)
            .ToList();

        offenders.Should().BeEmpty("IKycGateService is the bare KYC decision; a value path built on it skips the reversal risk hold");
    }
}
