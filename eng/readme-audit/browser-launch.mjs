import { readFile } from "node:fs/promises";

// On Windows, the Edge launcher may exit successfully after handing startup
// to a child process. Only a nonzero exit is an immediate launch failure; a
// zero exit still gets the full interval to publish DevToolsActivePort.
export async function waitForDevToolsPort(
  file,
  processHandle,
  recentError,
  timeoutMs = 20_000,
  pollMs = 50,
) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const content = await readFile(file, "utf8");
      const portLine = /^([1-9][0-9]{0,4})\r?\n/u.exec(content);
      const port = Number(portLine?.[1]);
      if (portLine && port <= 65_535) return content;
      // Chromium can create the file before its port line is complete.
    } catch (error) {
      // Windows can briefly deny reads while Chromium creates the file.
      if (!["ENOENT", "EBUSY", "EPERM", "EACCES"].includes(error?.code)) throw error;
    }
    if (processHandle.exitCode !== null && processHandle.exitCode !== 0) {
      throw new Error(
        `Edge exited before DevTools became ready (${processHandle.exitCode}). ${recentError()}`);
    }
    await new Promise(resolve => setTimeout(resolve, pollMs));
  }
  if (processHandle.exitCode !== null) {
    throw new Error(
      `Edge exited before DevTools became ready (${processHandle.exitCode}). ${recentError()}`);
  }
  throw new Error(`Timed out waiting for Edge DevTools at ${file}. ${recentError()}`);
}
