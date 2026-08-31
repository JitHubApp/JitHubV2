namespace JitHub.Services;

public readonly record struct MeWorkItemProjectionDecision(
    bool Apply,
    bool RemoveMissing,
    bool CommitsQuery);

public static class MeWorkItemProjectionPolicy
{
    public static MeWorkItemProjectionDecision Evaluate(
        bool queryChanged,
        bool queryAlreadyCommitted,
        bool hasExistingRows,
        int incomingRowCount,
        bool isAuthoritative,
        bool isFinal,
        PagedDataCompleteness completeness)
    {
        bool hasIncomingRows = incomingRowCount > 0;
        bool isCompleteReconciliation =
            isAuthoritative &&
            isFinal &&
            completeness == PagedDataCompleteness.Complete;
        bool mayReplaceVisibleRows = hasIncomingRows || isCompleteReconciliation;
        if (!queryAlreadyCommitted && queryChanged && !mayReplaceVisibleRows)
        {
            return new(false, false, false);
        }

        if (hasExistingRows && !hasIncomingRows && !isCompleteReconciliation)
        {
            return new(false, false, false);
        }

        bool firstQueryCommit = queryChanged && !queryAlreadyCommitted;
        return new(
            Apply: true,
            RemoveMissing: isCompleteReconciliation,
            CommitsQuery: firstQueryCommit);
    }
}
