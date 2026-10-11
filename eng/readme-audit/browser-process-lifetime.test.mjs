import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { stopBrowserProfileProcesses } from "./browser-process-lifetime.mjs";

test("profile cleanup rejects broad and non-audit process targets", async () => {
  let executions = 0;
  const execute = async () => { executions++; };
  await assert.rejects(
    stopBrowserProfileProcesses(os.tmpdir(), { platform: "win32", execute }),
    /Refusing to stop Edge processes/u);
  await assert.rejects(
    stopBrowserProfileProcesses(path.join(os.tmpdir(), "jithub-readme-edge-abcd"), { platform: "win32", execute }),
    /Refusing to stop Edge processes/u);
  await assert.rejects(
    stopBrowserProfileProcesses(path.join(os.tmpdir(), "nested", "jithub-readme-edge-abcdef"), { platform: "win32", execute }),
    /Refusing to stop Edge processes/u);
  const temporaryDirectory = path.resolve(os.tmpdir());
  const pathSegments = temporaryDirectory.split(path.sep);
  const caseVariantSegment = pathSegments.findIndex((segment, index) => index > 0 && /[a-z]/iu.test(segment));
  assert.notEqual(caseVariantSegment, -1, "temporary directory path should contain a caseable segment");
  const originalSegment = pathSegments[caseVariantSegment];
  const letterIndex = originalSegment.search(/[a-z]/iu);
  pathSegments[caseVariantSegment] = originalSegment.slice(0, letterIndex) +
    (originalSegment[letterIndex] === originalSegment[letterIndex].toLowerCase()
      ? originalSegment[letterIndex].toUpperCase()
      : originalSegment[letterIndex].toLowerCase()) +
    originalSegment.slice(letterIndex + 1);
  await assert.rejects(
    stopBrowserProfileProcesses(path.join(pathSegments.join(path.sep), "jithub-readme-edge-abcdef"), { platform: "win32", execute }),
    /Refusing to stop Edge processes/u);
  assert.equal(executions, 0, "invalid profile targets must be rejected before process execution");
});

test("profile cleanup constructs the exact scoped command for one generated audit profile", async () => {
  const profile = await mkdtemp(path.join(os.tmpdir(), "jithub-readme-edge-"));
  const calls = [];
  try {
    await stopBrowserProfileProcesses(profile, {
      platform: "win32",
      environment: { EXISTING_ENVIRONMENT_VALUE: "preserved" },
      execute: async (...args) => { calls.push(args); },
    });
    assert.equal(calls.length, 1);
    assert.equal(calls[0][0], "powershell.exe");
    assert.deepEqual(calls[0][1].slice(0, 3), ["-NoProfile", "-NonInteractive", "-Command"]);
    assert.match(calls[0][1][3], /CommandLineToArgvW/u);
    assert.match(calls[0][1][3], /Get-AuditProfileArgumentOwnership \$commandLineProperty\.Value \$ExpectedProfile/u);
    assert.match(calls[0][1][3], /\[string\]::Equals\(\$CandidatePath, \$ExpectedProfile, \[StringComparison\]::Ordinal\)/u);
    assert.match(calls[0][1][3], /\[string\]::Equals\(\$candidateFullPath, \$expectedFullPath, \[StringComparison\]::OrdinalIgnoreCase\)/u);
    assert.match(calls[0][1][3], /function Test-AuditFullyQualifiedWindowsPath/u);
    assert.doesNotMatch(calls[0][1][3], /IsPathFullyQualified/u);
    assert.doesNotMatch(calls[0][1][3], /CommandLine\.IndexOf\(\$auditEdgeProfilePath/u);
    assert.match(calls[0][1][3], /\$candidateProcess\.Kill\(\)/u);
    assert.doesNotMatch(calls[0][1][3], /Stop-Process\s+-Id/u);
    assert.match(calls[0][1][3], /\$candidateProcess\.Handle/u);
    assert.match(calls[0][1][3], /\$candidateProcess\.Dispose\(\)/u);
    assert.match(calls[0][1][3], /\$candidateProcess\.Id \$candidateProcess\.ProcessName \$target\.CreationDate/u);
    assert.match(calls[0][1][3], /\.Ticks % 10/u);
    assert.match(calls[0][1][3], /function Get-AuditProfileProcessRows/u);
    assert.match(calls[0][1][3], /function Get-AuditProfileArgumentOwnership/u);
    assert.match(calls[0][1][3], /Get-AuditProfileArgumentOwnership \$commandLineProperty\.Value \$ExpectedProfile/u);
    assert.match(calls[0][1][3], /if \(\$ownership -eq 'Unknown'\)/u);
    assert.match(calls[0][1][3], /return 'Owner'/u);
    assert.match(calls[0][1][3], /return 'NotOwner'/u);
    const selectorStart = calls[0][1][3].indexOf("$ownedRows = [System.Collections.Generic.List[object]]::new()");
    const selectorEnd = calls[0][1][3].indexOf("function ConvertTo-AuditUtcStartTime", selectorStart);
    const selectorLoop = calls[0][1][3].slice(selectorStart, selectorEnd);
    const staleRowFence = selectorLoop.indexOf("if (-not $recheckedIds.Contains($cimProcessId)) { continue }");
    const commandLineRead = selectorLoop.indexOf("$commandLineProperty = $row.PSObject.Properties['CommandLine']");
    assert.ok(staleRowFence >= 0 && commandLineRead > staleRowFence,
      "rows proven absent from the rechecked live inventory must be skipped before inspecting their command line");
    assert.match(calls[0][1][3], /\$null -eq \$commandLineProperty/u);
    assert.match(calls[0][1][3], /Get-AuditProfileProcessRows -ObservedProcessIds \$observedProcessIds -ProcessRows \$processRows -RecheckedProcessIds \$recheckedProcessIds/u);
    assert.match(calls[0][1][3], /Get-AuditProfileProcessRows -ObservedProcessIds \$remainingObservedProcessIds -ProcessRows \$remainingRows -RecheckedProcessIds \$remainingRecheckedProcessIds/u);
    assert.match(calls[0][1][3], /Get-AuditEdgeProcessIds\)/u);
    assert.doesNotMatch(calls[0][1][3], /\$_.CommandLine -and/u);
    assert.match(calls[0][1][3], /if \(\$observedProcessIds\.Count -eq 0\) \{ \$sawEdgeProcess = \$false; break \}/u);
    assert.match(calls[0][1][3], /NoProcessFoundForGivenName,Microsoft\.PowerShell\.Commands\.GetProcessCommand/u);
    assert.match(calls[0][1][3], /could not confirm whether Edge processes remain/u);
    assert.deepEqual(calls[0][2], {
      windowsHide: true,
      timeout: 30_000,
      maxBuffer: 64 * 1024,
      env: {
        EXISTING_ENVIRONMENT_VALUE: "preserved",
        JITHUB_README_EDGE_PROFILE_TO_STOP: path.resolve(profile),
      },
    });
  } finally {
    await rm(profile, { recursive: true, force: true });
  }
});

test("non-Windows profile cleanup validates the target and does not execute a command", async () => {
  const profile = await mkdtemp(path.join(os.tmpdir(), "jithub-readme-edge-"));
  let executions = 0;
  try {
    await stopBrowserProfileProcesses(profile, {
      platform: "linux",
      execute: async () => { executions++; },
    });
    assert.equal(executions, 0);
  } finally {
    await rm(profile, { recursive: true, force: true });
  }
});
