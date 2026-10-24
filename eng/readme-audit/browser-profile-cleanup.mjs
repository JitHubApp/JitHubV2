import { lstat, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { stopBrowserProfileProcesses } from "./browser-process-lifetime.mjs";

export class BrowserProfileCleanupError extends Error {
  constructor(failureStage, profileState, serverCloseFailed) {
    const stageMessage = failureStage === "stop-profile-processes"
      ? "could not confirm scoped Edge processes stopped"
      : failureStage === "remove-profile"
        ? "could not confirm the temporary browser profile was removed"
        : failureStage === "close-servers"
          ? "could not confirm loopback servers closed"
          : "rejected an invalid temporary profile target";
    const profileMessage = profileState === "preserved"
      ? "; the profile was preserved"
      : profileState === "unknown"
        ? "; profile removal state is unknown"
        : profileState === "removed" ? "; the profile was removed" : "";
    const serverMessage = serverCloseFailed ? "; loopback server closure also failed" : "";
    super(`Edge audit cleanup ${stageMessage}${profileMessage}${serverMessage}.`);
    this.name = "BrowserProfileCleanupError";
    this.failureStage = failureStage;
    this.profileState = profileState;
    this.profilePreserved = profileState === "preserved" ? true : profileState === "unknown" ? null : false;
    this.serverCloseFailed = serverCloseFailed;
  }
}

async function removeAndVerifyProfile(profileDirectory) {
  await rm(profileDirectory, { recursive: true, force: true });
  try {
    await lstat(profileDirectory);
  } catch (error) {
    if (error?.code === "ENOENT") return;
    throw error;
  }
  throw new Error("Profile still exists after removal.");
}

function isGeneratedAuditProfile(profileDirectory, temporaryDirectory) {
  if (typeof profileDirectory !== "string" || profileDirectory.length === 0) return false;
  const resolved = path.resolve(profileDirectory);
  const expectedParent = path.resolve(temporaryDirectory);
  return path.dirname(resolved) === expectedParent &&
    /^jithub-readme-edge-[a-zA-Z0-9]{6}$/u.test(path.basename(resolved));
}

/**
 * Stop only the generated profile's processes before removing its directory.
 * The injected operations make ordering/failure behavior testable without
 * starting a browser or touching host processes. `closeServers` always runs,
 * including when profile validation or process shutdown fails.
 */
export async function cleanupBrowserProfile(profileDirectory, {
  stopProcesses = stopBrowserProfileProcesses,
  removeProfile = removeAndVerifyProfile,
  closeServers = async () => {},
  temporaryDirectory = os.tmpdir(),
} = {}) {
  if (typeof stopProcesses !== "function" || typeof removeProfile !== "function" ||
      typeof closeServers !== "function") {
    throw new TypeError("Browser profile cleanup callbacks must be functions.");
  }

  let failureStage = null;
  let profileState = "preserved";
  let serverCloseFailed = false;

  if (!isGeneratedAuditProfile(profileDirectory, temporaryDirectory)) {
    failureStage = "validate-profile";
  } else {
    try {
      await stopProcesses(profileDirectory);
    } catch {
      failureStage = "stop-profile-processes";
    }

    if (failureStage === null) {
      try {
        await removeProfile(profileDirectory);
        profileState = "removed";
      } catch {
        failureStage = "remove-profile";
        profileState = "unknown";
      }
    }
  }

  try {
    await closeServers();
  } catch {
    serverCloseFailed = true;
    if (failureStage === null) failureStage = "close-servers";
  }

  if (failureStage !== null) {
    throw new BrowserProfileCleanupError(failureStage, profileState, serverCloseFailed);
  }

  return { profileState, serversClosed: true };
}
