export class DocumentReadinessTimeout extends Error {}

export function metricMap(response) {
  return Object.fromEntries(response.metrics.map(metric => [metric.name, metric.value]));
}

// A timed-out GitHub document may be an upstream delivery stall. Make exactly
// one fresh attempt, preserving the failed attempt in wall time while keeping
// its CPU/layout work out of the successful navigation's comparison metrics.
export async function navigateReadme(
  cdp,
  repositoryUrl,
  waitForDocumentReady,
  readTimeOrigin,
  now = () => performance.now(),
) {
  let navigationStarted = 0;
  let navigationRetries = 0;
  let retryMetricBaseline = {};
  for (let attempt = 0; attempt < 2; attempt++) {
    if (attempt > 0) {
      await cdp.send("Page.stopLoading");
      retryMetricBaseline = metricMap(await cdp.send("Performance.getMetrics"));
    }
    const previousTimeOrigin = await readTimeOrigin();
    navigationStarted = now();
    const navigation = await cdp.send("Page.navigate", { url: repositoryUrl });
    if (navigation.errorText) {
      throw new Error(`Edge navigation failed: ${navigation.errorText}`);
    }
    try {
      await waitForDocumentReady(cdp, previousTimeOrigin, 60_000);
      return { navigationStarted, navigationRetries, retryMetricBaseline };
    } catch (error) {
      if (!(error instanceof DocumentReadinessTimeout) || attempt === 1) throw error;
      navigationRetries++;
    }
  }
  throw new Error("The bounded Edge navigation loop completed unexpectedly.");
}
