using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private const string WttMessageScanFormat = "wtt-solana-message-scan/1";
    private const string WttMessageAbsenceFormat = "wtt-solana-message-absence/1";

    /// <summary>Resumable finalized-ancestry scan for one exact message; adapted from the reviewed Jupiter absence observer.</summary>
    private sealed record WttMessageHistoryCursor(int Version, string MessageHash, string Genesis, ulong StartSlot,
        ulong UpperSlot, string UpperHash, ulong UpperHeight, ulong NextSlot, string ExpectedHash,
        ulong ExpectedHeight, ulong ScannedBlocks, string TranscriptHash, bool Complete);

    /// <inheritdoc cref="IWttNativeOutputModule.ObserveExposedWttNativeOutputAsync"/>
    public async Task<AZOAResult<WttExposedObservation>> ObserveExposedWttNativeOutputAsync(WttOperationIntent intent,
        WttNativeOutputQuote quote, WttNativeOutputConsent consent, WttParticipantPreparation participant, string? previousEvidenceJson,
        CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttNativeOutputQuote(intent, quote);
            var binding = WttNativeOutputBinding(intent, quote, consent, participant.Generation);
            using var scope = _custodyScopeFactory?.CreateScope();
            var store = _wttPreparationStore ?? scope?.ServiceProvider.GetRequiredService<IWttPreparationStore>()
                ?? throw new InvalidOperationException("Durable native-output preparation storage is required.");
            var saved = await store.GetAsync(WttPreparationIdentity.Id(binding), ct)
                ?? throw new InvalidDataException("Recipient message was never durably admitted.");
            var wire = ReadWttNativeOutputWire(intent, quote, plan, binding, saved);
            if (participant != new WttParticipantPreparation(intent.OperationId, binding.Stage, binding.Generation, quote.RecipientAddress,
                    quote.ProtocolIdentity, wire.MessageBase64, saved.PlanJson, WttNativeOutputReservation(binding, quote)))
                throw new InvalidDataException("Observed recipient message differs from its exact disclosed intent.");
            if (await VerifyGenesisAsync(ct) != wire.Recovery.Genesis) throw new InvalidDataException("Saved native output belongs to another genesis.");
            var message = Convert.FromBase64String(wire.MessageBase64);
            var (cursor, signed) = await ScanWttMessageHistoryAsync(message, wire.Recovery, ReadWttMessageCursor(previousEvidenceJson), ct);
            if (signed is not null)
            {
                var accepted = await AcceptWttNativeOutputSignatureAsync(intent, quote, consent, participant, signed, ct);
                return accepted.IsError || accepted.Result is null
                    ? Ok(new WttExposedObservation("Pending", previousEvidenceJson, Detail: accepted.Message))
                    : Ok(new WttExposedObservation("Landed", Landed: accepted.Result, Detail: "The recipient submitted the disclosed message."));
            }
            if (cursor is null)
                return Ok(new WttExposedObservation("Pending", Detail: "The disclosed message's blockhash has not finalized past its validity."));
            if (!cursor.Complete)
                return Ok(new WttExposedObservation("Pending", WttCanonical(new { format = WttMessageScanFormat, history = cursor }),
                    Detail: $"Scanned {cursor.ScannedBlocks} finalized blocks toward the message's context slot."));
            return Ok(new WttExposedObservation("ExpiredNoEffect", WttCanonical(new
            {
                format = WttMessageAbsenceFormat, intent.OperationId, binding.Stage, binding.Generation, cursor.MessageHash, cursor.Genesis,
                contextSlot = cursor.StartSlot, lastValidBlockHeight = wire.Recovery.LastValidBlockHeight, cursor.UpperSlot, cursor.UpperHash,
                cursor.UpperHeight, cursor.ScannedBlocks, cursor.TranscriptHash,
            }), Detail: "Every finalized block from the message's context slot past its blockhash expiry lacks the message."));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException
            or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Error<WttExposedObservation>(exception.Message); }
    }

    /// <summary>Walks finalized parent links from a block past the blockhash expiry down to the context slot, matching exact message bytes.
    /// <paramref name="upperSlot"/> names a known finalized start block (default: the current finalized slot).</summary>
    private async Task<(WttMessageHistoryCursor? Cursor, string? SignedWireBase64)> ScanWttMessageHistoryAsync(byte[] message,
        DurableSolanaPlan recovery, WttMessageHistoryCursor? cursor, CancellationToken ct, ulong? upperSlot = null, int blocksPerPass = 8)
    {
        var messageHash = CanonicalJson.Hash(new { Domain = "wtt-message/1", Message = Convert.ToBase64String(message) });
        if (cursor is not null) RequireWttMessageCursor(cursor, messageHash, recovery);
        if (cursor?.Complete == true) return (cursor, null);
        JsonElement? firstBlock = null;
        if (cursor is null)
        {
            var upper = upperSlot ?? 0;
            if (upperSlot is null)
            {
                var root = await DurableRpcAsync("getSlot", [new { commitment = "finalized" }], ct);
                if (root.ValueKind != JsonValueKind.Number || !root.TryGetUInt64(out upper))
                    throw new InvalidDataException("Finalized message history boundary is unreadable.");
            }
            if (upper < (ulong)recovery.ContextSlot)
                throw new InvalidDataException("Finalized message history boundary precedes preparation.");
            var block = await ReadJupiterHistoryBlockAsync(upper, ct);
            var header = JupiterBlockHeader(block, upper);
            if (header.Height <= (ulong)recovery.LastValidBlockHeight) return (null, null);
            cursor = new(1, messageHash, recovery.Genesis, (ulong)recovery.ContextSlot, upper, header.Hash, header.Height, upper,
                header.Hash, header.Height, 0, CanonicalJson.Hash(new { Domain = "wtt-message-history/1", Message = messageHash, Slot = upper, header.Hash }),
                false);
            firstBlock = block;
        }
        for (var count = 0; count < blocksPerPass && !cursor.Complete; count++)
        {
            var block = firstBlock ?? await ReadJupiterHistoryBlockAsync(cursor.NextSlot, ct);
            firstBlock = null;
            var header = JupiterBlockHeader(block, cursor.NextSlot);
            if (header.Hash != cursor.ExpectedHash || header.Height != cursor.ExpectedHeight)
                throw new InvalidDataException("Finalized block ancestry changed while scanning for the participant message.");
            var transactions = block.GetProperty("transactions");
            if (transactions.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Complete finalized transaction bodies are required for message absence.");
            foreach (var transaction in transactions.EnumerateArray())
            {
                var encoded = transaction.GetProperty("transaction");
                if (encoded.ValueKind != JsonValueKind.Array || encoded.GetArrayLength() != 2 || encoded[1].GetString() != "base64"
                    || encoded[0].GetString() is not { } text)
                    throw new InvalidDataException("Finalized block omitted canonical transaction wire.");
                var bytes = Convert.FromBase64String(text);
                if (bytes.Length is 0 or > 1232 || Convert.ToBase64String(bytes) != text || bytes[0] is 0 or > 127 || bytes.Length <= 1 + bytes[0] * 64)
                    throw new InvalidDataException("Finalized block transaction signature framing is invalid.");
                if (bytes.AsSpan(1 + bytes[0] * 64).SequenceEqual(message)) return (cursor, text);
            }
            cursor = cursor with
            {
                NextSlot = header.Parent, ExpectedHash = header.PreviousHash, ExpectedHeight = header.Height - 1,
                ScannedBlocks = checked(cursor.ScannedBlocks + 1),
                TranscriptHash = CanonicalJson.Hash(new { Previous = cursor.TranscriptHash, Slot = cursor.NextSlot, header.Hash, header.Parent,
                    header.PreviousHash, Transactions = transactions.GetArrayLength() }),
                Complete = header.Parent < cursor.StartSlot,
            };
        }
        return (cursor, null);
    }

    private static WttMessageHistoryCursor? ReadWttMessageCursor(string? evidenceJson)
    {
        if (evidenceJson is null) return null;
        using var document = JsonDocument.Parse(evidenceJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("format", out var format) && format.GetString() == WttMessageScanFormat
            && root.TryGetProperty("history", out var history)
            ? history.Deserialize<WttMessageHistoryCursor>() ?? throw new InvalidDataException("Saved message scan cursor is missing.")
            : null;
    }

    private static void RequireWttMessageCursor(WttMessageHistoryCursor cursor, string messageHash, DurableSolanaPlan recovery)
    {
        if (cursor.Version != 1 || cursor.MessageHash != messageHash || cursor.Genesis != recovery.Genesis
            || cursor.StartSlot != (ulong)recovery.ContextSlot || cursor.UpperSlot < cursor.StartSlot
            || cursor.UpperHeight <= (ulong)recovery.LastValidBlockHeight || cursor.NextSlot > cursor.UpperSlot
            || cursor.ScannedBlocks > cursor.UpperHeight || cursor.ExpectedHeight != cursor.UpperHeight - cursor.ScannedBlocks
            || cursor.Complete != (cursor.NextSlot < cursor.StartSlot) || cursor.TranscriptHash is not { Length: 64 }
            || string.IsNullOrEmpty(cursor.UpperHash) || string.IsNullOrEmpty(cursor.ExpectedHash))
            throw new InvalidDataException("Saved message scan cursor belongs to different or inconsistent evidence.");
    }

    private static string WttCanonical(object value) => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(value));
}
