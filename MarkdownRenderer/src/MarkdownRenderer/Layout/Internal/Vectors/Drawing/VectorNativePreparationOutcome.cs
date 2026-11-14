namespace MarkdownRenderer.Layout.Internal.Vectors.Drawing;

internal readonly struct VectorNativePreparationOutcome(
    VectorNativePreparationStatus Status,
    long WeightUnits,
    long CapacityGeneration)
{
    public VectorNativePreparationStatus Status { get; init; } = Status;
    public long WeightUnits { get; init; } = WeightUnits;
    public long CapacityGeneration { get; init; } = CapacityGeneration;
}
