using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Services.Users;

public sealed partial class NftLifecycleService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecycleView>> GetRequestAsync(Guid actor, string operation, string requestKey, Guid actingTenantId = default, CancellationToken ct = default)
    {
        if (!ValidOperation(operation) || !ValidRequestKey(requestKey)) return Refused("Invalid NFT request identity.");
        var row = await store.GetAsync(NftLifecycleIdentity.ForRequest(actor, operation, requestKey, actingTenantId), ct);
        if (row is null) return Refused("The request is not currently admitted; a delayed request may still arrive.");
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson) ?? throw new InvalidOperationException("NFT intent is unreadable.");
        return command.Actor != actor || command.ActingTenantId != actingTenantId ? Refused("NFT request not found.")
            : AZOAResult<NftLifecycleView>.Success(View(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecycleView>> CancelRequestAsync(Guid actor, string operation, string requestKey, Guid actingTenantId = default, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || !ValidOperation(operation) || !ValidRequestKey(requestKey)) return Refused("Invalid NFT request identity.");
        var command = new NftLifecycleCommand(actor, actor, Guid.Empty, Guid.Empty, string.Empty, string.Empty,
            new UserNFTMintModel(), -1, actingTenantId);
        var candidate = Candidate(actor, actor, Guid.Empty, operation, string.Empty, default, requestKey, command);
        candidate.State = "Cancelled";
        candidate.Stage = "Cancellation";
        candidate.Detail = "This request key was cancelled before admission and cannot be reused.";
        var row = await store.ClaimAsync(candidate, ct);
        if (row is null) return Refused("NFT admission changed; resolve the original request before retrying cancellation.");
        var original = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson) ?? throw new InvalidOperationException("NFT intent is unreadable.");
        if (original.Actor != actor || original.ActingTenantId != actingTenantId) return Refused("NFT request not found.");
        return row.State == "Cancelled" ? AZOAResult<NftLifecycleView>.Success(View(row)) : await CancelAsync(actor, row.Id, ct);
    }

    private static bool ValidOperation(string operation) => operation is "Mint" or "Transfer" or "Burn" or "Revoke" or "Refund";

    private static AZOAResult<NftLifecycleView> CancelledClaim(AZOA.WebAPI.Persistence.SurrealDb.Models.NftLifecycle row, Guid tenant)
        => JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)?.ActingTenantId == tenant
            ? AZOAResult<NftLifecycleView>.Success(View(row)) : Refused("NFT request not found.");

    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecyclePage>> ListPendingAsync(Guid actor, string? cursor, int limit = 25, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || limit is < 1 or > 50 || cursor?.Length > 1000)
            return AZOAResult<NftLifecyclePage>.Failure("Invalid NFT lifecycle page request.");
        var position = new NftLifecycleCursor(actor, clock.GetUtcNow(), 0);
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                position = JsonSerializer.Deserialize<NftLifecycleCursor>(Convert.FromBase64String(cursor))!;
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return AZOAResult<NftLifecyclePage>.Failure("Invalid NFT lifecycle cursor.");
            }
            if (position is null || position.Participant != actor || position.Offset < 0 || position.Offset > 1000000
                || position.Snapshot > clock.GetUtcNow())
                return AZOAResult<NftLifecyclePage>.Failure("Invalid NFT lifecycle cursor.");
        }
        var rows = await store.ListParticipantSnapshotAsync(actor, position.Snapshot, position.Offset, limit + 1, ct);
        var items = rows.Take(limit).Where(row => row.State is not ("Completed" or "Failed" or "Cancelled")).Select(View).ToArray();
        var next = rows.Count > limit
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(position with { Offset = position.Offset + limit })) : null;
        return AZOAResult<NftLifecyclePage>.Success(new(items, next));
    }
}
