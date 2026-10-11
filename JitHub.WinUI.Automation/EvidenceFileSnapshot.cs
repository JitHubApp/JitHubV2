using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class EvidenceFileSnapshot
{
    internal static bool PreserveIfPresent(string source, string destination, TimeSpan timeout)
        => PreserveIfPresent(source, destination, timeout,
            static (temporaryPath, destinationPath) => File.Move(temporaryPath, destinationPath, overwrite: true));

    internal static bool PreserveIfPresent(
        string source, string destination, TimeSpan timeout, Action<string, string> publishSnapshot)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ArgumentNullException.ThrowIfNull(publishSnapshot);

        Stopwatch stopwatch = Stopwatch.StartNew();
        IOException? lastReadFailure = null;
        bool observedSource = false;
        while (stopwatch.Elapsed < timeout)
        {
            if (!File.Exists(source))
            {
                if (!observedSource)
                    return false;
                Thread.Sleep(20);
                continue;
            }
            observedSource = true;

            string temporaryPath = destination + ".snapshot-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // The app atomically replaces lifecycle JSON and may still append
                // NDJSON. Read one open file identity, permitting replacement while
                // we copy it, then publish the snapshot atomically as well.
                using (var input = new FileStream(
                    source, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }

                publishSnapshot(temporaryPath, destination);
                return true;
            }
            catch (IOException exception)
            {
                lastReadFailure = exception;
            }
            finally
            {
                // Only this attempt's generated temporary path is removed.
                // Cleanup must not replace a publication failure or a success;
                // non-IO failures still propagate to strict evidence callers.
                try { File.Delete(temporaryPath); }
                catch (Exception) { }
            }
            Thread.Sleep(20);
        }

        throw new IOException(
            $"Could not snapshot audit evidence '{source}' within {timeout.TotalSeconds:0.###} seconds.",
            lastReadFailure);
    }
}
