export class DocumentReadinessTimeout extends Error {}

export function metricMap(response) {
  return Object.fromEntries(response.metrics.map(metric => [metric.name, metric.value]));
}

// Performance.getMetrics counters can reset if navigation swaps the renderer
// process. In that case the new counter already represents the successful
// attempt and subtracting the old process's baseline would undercount it.
export function metricDelta(currentMetrics, baselineMetrics, name) {
  const current = currentMetrics[name] || 0;
  const baseline = baselineMetrics[name] || 0;
  return Math.max(0, current >= baseline ? current - baseline : current);
}

// A timed-out GitHub document or exhausted Edge network buffers may be an
// upstream/runner stall. Make exactly one fresh attempt, preserving the failed
// attempt in wall time while keeping its CPU/layout work out of the successful
// navigation's comparison metrics. All other navigation errors remain fatal.
export async function navigateReadme(
  cdp,
  repositoryUrl,
  waitForDocumentReady,
  readTimeOrigin,
  now = () => performance.now(),
  pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)),
) {
  let navigationStarted = 0;
  let navigationRetries = 0;
  let retryMetricBaseline = {};
  let retryAfterBufferExhaustion = false;
  for (let attempt = 0; attempt < 2; attempt++) {
    if (attempt > 0) {
      await cdp.send("Page.stopLoading");
      if (retryAfterBufferExhaustion) await pause(1_000);
      retryMetricBaseline = metricMap(await cdp.send("Performance.getMetrics"));
    }
    const previousTimeOrigin = await readTimeOrigin();
    navigationStarted = now();
    const navigation = await cdp.send("Page.navigate", { url: repositoryUrl });
    if (navigation.errorText) {
      if (navigation.errorText === "net::ERR_NO_BUFFER_SPACE" && attempt === 0) {
        navigationRetries++;
        retryAfterBufferExhaustion = true;
        continue;
      }
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
