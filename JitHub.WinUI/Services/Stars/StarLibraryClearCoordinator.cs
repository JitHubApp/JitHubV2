using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed class StarLibraryClearCoordinationException : Exception
{
    public StarLibraryClearCoordinationException(string component, string message, Exception innerException)
        : base(message, innerException)
    {
        Component = component;
    }

    public string Component { get; }
}

public static class StarLibraryClearCoordinator
{
    public const string RecoveryComponent = "stars-recovery";
    public const string DatabaseComponent = CacheOwnerIds.StarsLibrary;

    public static async Task RecoverAsync(
        IStarLibraryStore store,
        IStarLibraryRecoveryStore recoveryStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recoveryStore);

        StarLibraryClearRecoveryState? pending =
            await recoveryStore.GetPendingClearAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> committed =
            await store.GetCommittedClearTransactionsAsync(cancellationToken).ConfigureAwait(false);

        if (pending is not null)
        {
            if (committed.Contains(pending.TransactionId, StringComparer.Ordinal))
            {
                await recoveryStore.CommitPendingClearAsync(pending.TransactionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await recoveryStore.RollbackPendingClearAsync(pending.TransactionId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        foreach (string transactionId in committed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.CompleteClearTransactionAsync(transactionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task ClearAsync(
        IStarLibraryStore store,
        IStarLibraryRecoveryStore recoveryStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recoveryStore);

        await RecoverAsync(store, recoveryStore, cancellationToken).ConfigureAwait(false);

        IStarLibraryRecoveryClearTransaction transaction;
        try
        {
            transaction = await recoveryStore.BeginClearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new StarLibraryClearCoordinationException(
                RecoveryComponent,
                "The Stars recovery journal could not stage a clear transaction.",
                exception);
        }

        await using (transaction.ConfigureAwait(false))
        {
            Exception? clearException = null;
            try
            {
                await store.ClearAllAsync(transaction.TransactionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception databaseException)
            {
                clearException = databaseException;
                bool databaseCommitted;
                try
                {
                    // A provider can commit durably and still throw while returning from
                    // CommitAsync. The matching marker is the only safe source of truth.
                    databaseCommitted = await store.IsClearTransactionCommittedAsync(
                            transaction.TransactionId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception markerException)
                {
                    throw new StarLibraryClearCoordinationException(
                        DatabaseComponent,
                        "The Stars database clear returned an error and its commit marker could not be read. The staged journal was preserved for relaunch recovery.",
                        new AggregateException(databaseException, markerException));
                }

                if (!databaseCommitted)
                {
                    try
                    {
                        // Rollback must not inherit caller cancellation: once journal staging
                        // changed durable state, restoration is mandatory.
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new StarLibraryClearCoordinationException(
                            RecoveryComponent,
                            "The Stars database clear did not commit and the staged recovery journal could not be restored. Relaunch recovery is required.",
                            new AggregateException(databaseException, rollbackException));
                    }

                    if (databaseException is OperationCanceledException)
                    {
                        throw;
                    }

                    throw new StarLibraryClearCoordinationException(
                        DatabaseComponent,
                        "The Stars database clear did not commit; the recovery journal was restored.",
                        databaseException);
                }

                // The requested durable postcondition is already true. Complete the
                // cross-store transaction instead of resurrecting the journal.
            }

            try
            {
                // SQLite has committed the matching marker. Finalization is deliberately
                // non-cancelable so cancellation cannot split the two durable stores.
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new StarLibraryClearCoordinationException(
                    RecoveryComponent,
                    "The Stars database clear committed, but recovery-journal finalization is pending and will resume on relaunch.",
                    clearException is null ? exception : new AggregateException(clearException, exception));
            }

            try
            {
                await store.CompleteClearTransactionAsync(transaction.TransactionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new StarLibraryClearCoordinationException(
                    DatabaseComponent,
                    "The Stars clear completed, but its SQLite transaction marker could not be finalized and will be cleaned on relaunch.",
                    clearException is null ? exception : new AggregateException(clearException, exception));
            }
        }
    }
}
