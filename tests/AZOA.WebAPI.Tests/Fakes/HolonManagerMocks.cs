using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Fakes;

/// <summary>
/// Shared <see cref="IHolonManager"/> mocks for the GateCheck holon-state resolver
/// (smart-gates-holon-state §8.1). The handler reads a holon's live state to expose
/// <c>holon.&lt;id&gt;.&lt;field&gt;</c> to the predicate; a gate test that does not
/// use holons uses <see cref="Empty"/> (any id ⇒ "not found", fail-closed), and a
/// holon-state test uses <see cref="WithHolons"/> to seed specific holons.
/// </summary>
public static class HolonManagerMocks
{
    /// <summary>
    /// An <see cref="IHolonManager"/> whose <c>GetAsync</c> returns a not-found error
    /// for ANY id — the fail-closed default. Existing gate tests (no holons in config)
    /// never call it, so this keeps them unchanged.
    /// </summary>
    public static IHolonManager Empty() => WithHolons();

    /// <summary>
    /// An <see cref="IHolonManager"/> that resolves the supplied holons by id and
    /// returns a not-found error for any other id (fail-closed).
    /// </summary>
    public static IHolonManager WithHolons(params IHolon[] holons)
    {
        var byId = holons.ToDictionary(h => h.Id);
        var mock = new Mock<IHolonManager>();
        mock.Setup(m => m.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((Guid id, Guid? _, AZOARequest? __) => byId.TryGetValue(id, out var h)
                ? new AZOAResult<IHolon> { Result = h }
                : new AZOAResult<IHolon> { IsError = true, Message = $"Holon {id} not found." });
        return mock.Object;
    }

    /// <summary>
    /// Build a holon with a status (and optional extra metadata) for a gate test.
    /// <paramref name="ownerAvatarId"/> sets <see cref="IHolon.AvatarId"/> — the
    /// GateCheck resolver is owner-scoped, so a legitimate-path test must seed the
    /// holon's owner to match the run owner (the quest's AvatarId); a cross-tenant
    /// test seeds a DIFFERENT owner to prove the read fails closed.
    /// </summary>
    public static Holon HolonWithStatus(
        Guid id, string status, Guid? ownerAvatarId = null, params (string Key, string Value)[] extra)
    {
        var holon = new Holon { Id = id, Name = $"holon-{id}", IsActive = true, AvatarId = ownerAvatarId };
        holon.Metadata["status"] = status;
        foreach (var (k, v) in extra)
            holon.Metadata[k] = v;
        return holon;
    }
}
