import { execFile } from "node:child_process";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

// Edge may hand off startup to another process and let the spawned PID exit
// normally. Browser.close and child.kill cannot then account for the browser
// processes that still own this oracle's unique temporary profile. Select only
// msedge.exe processes whose command line contains that exact profile path.
const stopProfileScript = String.raw`
$auditEdgeProfilePath = $env:JITHUB_README_EDGE_PROFILE_TO_STOP
if ([string]::IsNullOrWhiteSpace($auditEdgeProfilePath)) { exit 2 }
for ($pass = 0; $pass -lt 3; $pass++) {
    $targets = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" |
        Where-Object {
            $_.CommandLine -and
            $_.CommandLine.IndexOf($auditEdgeProfilePath, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
    if ($targets.Count -eq 0) { break }
    foreach ($target in $targets) {
        Stop-Process -Id $target.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 100
}
`;

export async function stopBrowserProfileProcesses(profileDirectory) {
  const resolved = path.resolve(profileDirectory);
  const expectedParent = path.resolve(os.tmpdir());
  if (path.dirname(resolved).toLowerCase() !== expectedParent.toLowerCase() ||
      !/^jithub-readme-edge-[a-zA-Z0-9]{6}$/u.test(path.basename(resolved))) {
    throw new Error("Refusing to stop Edge processes outside one generated audit profile.");
  }
  if (process.platform !== "win32") return;
  await execFileAsync("powershell.exe", [
    "-NoProfile",
    "-NonInteractive",
    "-Command",
    stopProfileScript,
  ], {
    windowsHide: true,
    timeout: 15_000,
    maxBuffer: 64 * 1024,
    env: {
      ...process.env,
      JITHUB_README_EDGE_PROFILE_TO_STOP: resolved,
    },
  });
}
