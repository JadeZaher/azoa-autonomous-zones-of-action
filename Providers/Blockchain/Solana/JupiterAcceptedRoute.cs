using System.Buffers.Binary;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Retains accepted exact-input economics while refreshing reviewed route accounts.</summary>
internal static class JupiterAcceptedRoute
{
    internal static JupiterBuildPlan Bind(JupiterBuildPlan current, string pool, bool sharedAccounts,
        bool whirlpoolV2, bool aToB, ulong input, ulong output, ulong minimum, ushort slippage)
    {
        if (current.Pool != pool || current.InputAmount != input || current.Route.SharedAccounts != sharedAccounts ||
            current.Route.WhirlpoolV2 != whirlpoolV2 || current.Route.AToB != aToB ||
            !JupiterRouteCodec.TryDecode(current.Swap.Data, out var decoded) || decoded != current.Route ||
            decoded!.InputAmount != input || decoded.QuotedOutput != current.OutputAmount ||
            decoded.MinimumOutput != current.MinimumOutput || decoded.SlippageBps != slippage ||
            output == 0 || minimum == 0 || slippage >= 10000)
            throw new InvalidDataException("The refreshed route differs from the accepted protocol or exact input.");
        var accepted = decoded with { QuotedOutput = output, SlippageBps = slippage };
        if (accepted.MinimumOutput != minimum)
            throw new InvalidDataException("The accepted output floor differs from its exact route arithmetic.");
        var bytes = current.Swap.Data.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(bytes.Length - 11, 8), output);
        if (!JupiterRouteCodec.TryDecode(bytes, out var rebound) || rebound != accepted)
            throw new InvalidDataException("The accepted route economics could not be represented exactly.");
        return current with
        {
            OutputAmount = output, MinimumOutput = minimum, Route = accepted,
            Swap = current.Swap with { Data = bytes }
        };
    }
}
