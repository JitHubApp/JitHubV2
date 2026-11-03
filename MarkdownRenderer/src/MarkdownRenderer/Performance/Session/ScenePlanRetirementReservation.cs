using System.Threading.Tasks;

namespace MarkdownRenderer.Performance;

/// <summary>A plan whose retirement is already tracked for shutdown, with the completion to settle.</summary>
internal sealed class ScenePlanRetirementReservation(
    MarkdownProgressiveScenePlan plan,
    TaskCompletionSource<bool> completion)
{
    internal MarkdownProgressiveScenePlan Plan { get; } = plan;
    internal TaskCompletionSource<bool> Completion { get; } = completion;
}
