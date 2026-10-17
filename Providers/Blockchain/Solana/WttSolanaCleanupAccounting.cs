namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Separates original finalized funding from unrelated stage-account credits.</summary>
internal static class WttSolanaCleanupAccounting
{
    internal static ulong OriginalDeposit(ulong before, ulong after, ulong funding, ulong protocolCost,
        ulong reservedDeposit, bool failed)
    {
        if (protocolCost > funding) throw new InvalidDataException("Original protocol cost exceeds its exact stage funding.");
        if (failed)
        {
            if (before != after) throw new InvalidDataException("Failed atomic stage changed its protected payer balance.");
            return 0;
        }
        if (after <= before) return 0;
        var increase = after - before;
        if (increase > funding - protocolCost || increase > reservedDeposit)
            throw new InvalidDataException("Finalized payer increase exceeds the original non-refundable-cost-adjusted allocation.");
        return increase;
    }

    internal static ulong CurrentReturn(ulong originalAfter, ulong current)
    {
        if (current < originalAfter)
            throw new InvalidDataException("Stage balance fell after finalization without a journaled return.");
        return current;
    }

    internal static ulong FinalizedReturn(ulong payerBefore, ulong payerAfter, ulong destinationBefore,
        ulong destinationAfter, ulong fee, ulong quotedReturn, ulong attributedDeposit)
    {
        if (attributedDeposit > quotedReturn || payerBefore < quotedReturn || payerAfter != payerBefore - quotedReturn
            || (System.Numerics.BigInteger)destinationAfter - destinationBefore + fee != quotedReturn)
            throw new InvalidDataException("Cleanup balance changes differ from the exact fixed return and separate network fee.");
        return payerAfter;
    }
}
