namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Conservative deterministic accounting for formula parsing/layout and scene
/// retention. The source reservation covers the object-heavy upstream semantic
/// model; scene reservations cover both transient builders and immutable copies.
/// </summary>
internal sealed class MathWorkingMemoryBudget
{
    private const long FixedFormulaBytes = 16 * 1024;
    private const long BytesPerSourceCodeUnit = 192;

    private long _reservedBytes;

    internal MathWorkingMemoryBudget(long maximumBytes, int sourceLength)
    {
        MaximumBytes = maximumBytes;
        Reserve(EstimateSourceBytes(sourceLength));
    }

    internal long MaximumBytes { get; }

    internal long ReservedBytes => _reservedBytes;

    internal static long EstimateSourceBytes(int sourceLength) =>
        checked(FixedFormulaBytes + (sourceLength * BytesPerSourceCodeUnit));

    internal void Reserve(long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        long next;
        try
        {
            next = checked(_reservedBytes + byteCount);
        }
        catch (OverflowException)
        {
            throw new MathSceneBudgetException(MathSceneBudget.WorkingMemory);
        }

        if (next > MaximumBytes)
            throw new MathSceneBudgetException(MathSceneBudget.WorkingMemory);
        _reservedBytes = next;
    }
}
