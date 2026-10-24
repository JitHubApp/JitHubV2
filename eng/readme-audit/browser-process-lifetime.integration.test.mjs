import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
  edgeProfileArgumentMatcherScript,
  stopBrowserProfileProcesses,
} from "./browser-process-lifetime.mjs";

test("integration: Windows argv matcher accepts only one exact audit profile switch", {
  skip: process.platform !== "win32" ? "Windows CommandLineToArgvW matcher coverage." : false,
}, () => {
  const simpleProfile = String.raw`C:\Users\Nero\AppData\Local\Temp\jithub-readme-edge-AbC123`;
  const spacedUnicodeProfile = String.raw`C:\Users\Zoë\AppData\Local\Temp\JitHub (Audit)+[A]\jithub-readme-edge-AbC123`;
  const newlineProfile = `C:\\Temp\\line\nbreak\\jithub-readme-edge-AbC123`;
  const cases = [
    { name: "bare equals argument", commandLine: `msedge.exe --user-data-dir=${simpleProfile} --type=renderer`, expected: true, profile: simpleProfile },
    { name: "quoted value with spaces and metacharacters", commandLine: `msedge.exe --user-data-dir="${spacedUnicodeProfile}" --type=renderer`, expected: true, profile: spacedUnicodeProfile },
    { name: "entire switch argument quoted", commandLine: `msedge.exe "--user-data-dir=${spacedUnicodeProfile}" --type=renderer`, expected: true, profile: spacedUnicodeProfile },
    { name: "separate switch and value", commandLine: `msedge.exe --user-data-dir "${spacedUnicodeProfile}" --type=renderer`, expected: false, profile: spacedUnicodeProfile },
    { name: "case-variant profile value", commandLine: `msedge.exe --user-data-dir=${simpleProfile.toUpperCase()}`, expected: false, profile: simpleProfile },
    { name: "Unicode case-variant profile value", commandLine: `msedge.exe --user-data-dir="${spacedUnicodeProfile.replace("Zoë", "ZOË")}"`, expected: false, profile: spacedUnicodeProfile },
    { name: "newline remains part of a quoted Windows argument", commandLine: `msedge.exe --user-data-dir="${newlineProfile}"`, expected: true, profile: newlineProfile },
    { name: "profile prefix neighbor", commandLine: `msedge.exe --user-data-dir=${simpleProfile}-neighbor`, expected: false, profile: simpleProfile },
    { name: "profile suffix child", commandLine: `msedge.exe --user-data-dir=${simpleProfile}\\child`, expected: false, profile: simpleProfile },
    { name: "unrelated argument merely contains profile", commandLine: `msedge.exe --profile-label=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "switch-like text inside another quoted argument", commandLine: `msedge.exe --other="prefix --user-data-dir=${simpleProfile} suffix"`, expected: false, profile: simpleProfile },
    { name: "switch-like text after a backslash-escaped quote", commandLine: String.raw`msedge.exe --other="prefix \" --user-data-dir=${simpleProfile} suffix"`, expected: false, profile: simpleProfile },
    { name: "longer lookalike flag", commandLine: `msedge.exe --user-data-directory=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "flag embedded in an unrelated token", commandLine: `msedge.exe prefix--user-data-dir=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "duplicate profile switches", commandLine: `msedge.exe --user-data-dir=${simpleProfile} --user-data-dir=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "duplicate profile alias after the canonical switch", commandLine: `msedge.exe --user-data-dir=${simpleProfile} /user-data-dir=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "canonical switch before terminator and lookalike after it", commandLine: `msedge.exe --user-data-dir=${simpleProfile} -- --user-data-dir=${simpleProfile}-other`, expected: true, profile: simpleProfile },
    { name: "profile switch after terminator", commandLine: `msedge.exe -- --user-data-dir=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "single-dash alias", commandLine: `msedge.exe -user-data-dir=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "slash alias", commandLine: `msedge.exe /user-data-dir=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "uppercase switch alias", commandLine: `msedge.exe --USER-DATA-DIR=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "single-argument terminator before target", commandLine: `msedge.exe --single-argument --user-data-dir=${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "single-argument terminator after target", commandLine: `msedge.exe --user-data-dir=${simpleProfile} --single-argument`, expected: false, profile: simpleProfile },
    { name: "padded quoted switch argument", commandLine: `msedge.exe " --user-data-dir=${simpleProfile} "`, expected: false, profile: simpleProfile },
    { name: "unbalanced command-line quote", commandLine: `msedge.exe --user-data-dir="${simpleProfile}`, expected: false, profile: simpleProfile },
    { name: "unquoted whitespace path is not the full profile", commandLine: `msedge.exe --user-data-dir=${spacedUnicodeProfile}`, expected: false, profile: spacedUnicodeProfile },
    { name: "empty profile switch", commandLine: "msedge.exe --user-data-dir=", expected: false, profile: simpleProfile },
  ];
  const probeScript = `${edgeProfileArgumentMatcherScript}
$ErrorActionPreference = 'Stop'
$cases = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:JITHUB_README_PROFILE_MATCHER_CASES)) | ConvertFrom-Json
foreach ($case in $cases) {
    $actual = Test-ExactAuditProfileArgument $case.commandLine $case.profile
    if ($actual -ne [bool]$case.expected) { throw "Matcher case failed: $($case.name)" }
}
$simpleProfile = Join-Path ([IO.Path]::GetTempPath()) 'jithub-readme-edge-AbC123'
$ownershipCases = @(
    @{ name = 'canonical single exact profile'; commandLine = "msedge.exe --user-data-dir=$simpleProfile"; expected = 'Owner' },
    @{ name = 'slash alias exact profile'; commandLine = "msedge.exe /user-data-dir=$simpleProfile"; expected = 'Unknown' },
    @{ name = 'single-dash alias exact profile'; commandLine = "msedge.exe -user-data-dir=$simpleProfile"; expected = 'Unknown' },
    @{ name = 'uppercase key exact profile'; commandLine = "msedge.exe --USER-DATA-DIR=$simpleProfile"; expected = 'Unknown' },
    @{ name = 'case-only path variant'; commandLine = "msedge.exe --user-data-dir=$($simpleProfile.ToUpperInvariant())"; expected = 'Unknown' },
    @{ name = 'alternate separator path'; commandLine = "msedge.exe --user-data-dir=$($simpleProfile.Replace([string][char]92, '/'))"; expected = 'Unknown' },
    @{ name = 'duplicate canonical exact profile'; commandLine = "msedge.exe --user-data-dir=$simpleProfile --user-data-dir=$simpleProfile"; expected = 'Unknown' },
    @{ name = 'canonical plus slash alias duplicate exact profile'; commandLine = "msedge.exe --user-data-dir=$simpleProfile /user-data-dir=$simpleProfile"; expected = 'Unknown' },
    @{ name = 'malformed command line'; commandLine = 'msedge.exe --user-data-dir="' + $simpleProfile; expected = 'Unknown' },
    @{ name = 'relative profile basename'; commandLine = 'msedge.exe --user-data-dir=jithub-readme-edge-AbC123'; expected = 'Unknown' },
    @{ name = 'clearly different profile'; commandLine = "msedge.exe --user-data-dir=$simpleProfile-neighbor"; expected = 'NotOwner' },
    @{ name = 'no profile key'; commandLine = 'msedge.exe --type=renderer'; expected = 'NotOwner' },
    @{ name = 'profile key after terminator'; commandLine = "msedge.exe -- --user-data-dir=$simpleProfile"; expected = 'NotOwner' },
    @{ name = 'separate switch value'; commandLine = 'msedge.exe --user-data-dir ' + '"' + $simpleProfile + '"'; expected = 'NotOwner' }
)
foreach ($case in $ownershipCases) {
    $actual = Get-AuditProfileArgumentOwnership $case.commandLine $simpleProfile
    if ($actual -cne $case.expected) { throw "Ownership case failed: $($case.name): expected $($case.expected), got $actual" }
}
Write-Output ("Passed {0} pure argv matcher cases and {1} tri-state ownership cases." -f $cases.Count, $ownershipCases.Count)
`;
  const result = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", probeScript], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 30_000,
    env: {
      ...process.env,
      JITHUB_README_PROFILE_MATCHER_CASES: Buffer.from(JSON.stringify(cases), "utf8").toString("base64"),
    },
  });
  assert.ifError(result.error);
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
  assert.match(result.stdout, /Passed 27 pure argv matcher cases and 14 tri-state ownership cases\./u);
});

test("integration: Windows process identity predicate fails closed and binds UTC PID identity", {
  skip: process.platform !== "win32" ? "Windows synthetic process identity coverage." : false,
}, () => {
  const probeScript = `${edgeProfileArgumentMatcherScript}
$utc = [DateTime]::SpecifyKind([DateTime]::new(2026, 10, 2, 12, 0, 0), [DateTimeKind]::Utc)
$local = $utc.ToLocalTime()
$dmtf = [System.Management.ManagementDateTimeConverter]::ToDmtfDateTime($utc.ToLocalTime())
$dmtfOneMicrosecondNewer = [System.Management.ManagementDateTimeConverter]::ToDmtfDateTime($utc.AddTicks(10).ToLocalTime())
$cases = @(
    @{ name = 'UTC DateTime'; pid = 17; candidate = 17; processName = 'msedge'; creation = $utc; start = $utc; handle = $true; expected = $true },
    @{ name = 'Local DateTime'; pid = 17; candidate = 17; processName = 'msedge'; creation = $local; start = $utc; handle = $true; expected = $true },
    @{ name = 'DateTimeOffset'; pid = 17; candidate = 17; processName = 'msedge'; creation = [DateTimeOffset]::new($utc).ToOffset([TimeSpan]::FromHours(-8)); start = $utc; handle = $true; expected = $true },
    @{ name = 'same microsecond tick bucket'; pid = 17; candidate = 17; processName = 'msedge'; creation = $utc.AddTicks(9); start = $utc; handle = $true; expected = $true },
    @{ name = 'one microsecond newer'; pid = 17; candidate = 17; processName = 'msedge'; creation = $utc.AddTicks(10); start = $utc; handle = $true; expected = $false },
    @{ name = 'one microsecond older'; pid = 17; candidate = 17; processName = 'msedge'; creation = $utc.AddTicks(-10); start = $utc; handle = $true; expected = $false },
    @{ name = 'DMTF date'; pid = 17; candidate = 17; processName = 'msedge'; creation = $dmtf; start = $utc; handle = $true; expected = $true },
    @{ name = 'DMTF one microsecond newer'; pid = 17; candidate = 17; processName = 'msedge'; creation = $dmtfOneMicrosecondNewer; start = $utc; handle = $true; expected = $false },
    @{ name = 'DMTF explicit UTC'; pid = 17; candidate = 17; processName = 'msedge'; creation = '20261002120000.000000+000'; start = $utc; handle = $true; expected = $true },
    @{ name = 'DMTF positive minute offset'; pid = 17; candidate = 17; processName = 'msedge'; creation = '20261002174500.000000+345'; start = $utc; handle = $true; expected = $true },
    @{ name = 'DMTF negative minute offset'; pid = 17; candidate = 17; processName = 'msedge'; creation = '20261002083000.000000-210'; start = $utc; handle = $true; expected = $true },
    @{ name = 'DMTF wrong offset'; pid = 17; candidate = 17; processName = 'msedge'; creation = '20261002120000.000000+060'; start = $utc; handle = $true; expected = $false },
    @{ name = 'DMTF invalid date'; pid = 17; candidate = 17; processName = 'msedge'; creation = '20260230120000.000000+000'; start = $utc; handle = $true; expected = $false },
    @{ name = 'DMTF wildcard time'; pid = 17; candidate = 17; processName = 'msedge'; creation = '20261002120000.******+000'; start = $utc; handle = $true; expected = $false },
    @{ name = 'DMTF repeated DST first hour'; pid = 17; candidate = 17; processName = 'msedge'; creation = '20261101013000.000000-420'; start = [DateTime]::SpecifyKind([DateTime]::new(2026, 11, 1, 8, 30, 0), [DateTimeKind]::Utc); handle = $true; expected = $true },
    @{ name = 'DMTF repeated DST second hour does not alias first'; pid = 17; candidate = 17; processName = 'msedge'; creation = '20261101013000.000000-480'; start = [DateTime]::SpecifyKind([DateTime]::new(2026, 11, 1, 8, 30, 0), [DateTimeKind]::Utc); handle = $true; expected = $false },
    @{ name = 'unspecified CIM time'; pid = 17; candidate = 17; processName = 'msedge'; creation = [DateTime]::SpecifyKind($utc, [DateTimeKind]::Unspecified); start = $utc; handle = $true; expected = $false },
    @{ name = 'unspecified process time'; pid = 17; candidate = 17; processName = 'msedge'; creation = $utc; start = [DateTime]::SpecifyKind($utc, [DateTimeKind]::Unspecified); handle = $true; expected = $false },
    @{ name = 'missing creation date'; pid = 17; candidate = 17; processName = 'msedge'; creation = $null; start = $utc; handle = $true; expected = $false },
    @{ name = 'invalid creation date'; pid = 17; candidate = 17; processName = 'msedge'; creation = 'not a DMTF timestamp'; start = $utc; handle = $true; expected = $false },
    @{ name = 'wrong process name'; pid = 17; candidate = 17; processName = 'not-edge'; creation = $utc; start = $utc; handle = $true; expected = $false },
    @{ name = 'PID reused by a different candidate'; pid = 17; candidate = 18; processName = 'msedge'; creation = $utc; start = $utc; handle = $true; expected = $false },
    @{ name = 'snapshot PID unavailable'; pid = 0; candidate = 0; processName = 'msedge'; creation = $utc; start = $utc; handle = $true; expected = $false },
    @{ name = 'process handle unavailable'; pid = 17; candidate = 17; processName = 'msedge'; creation = $utc; start = $utc; handle = $false; expected = $false }
)
foreach ($case in $cases) {
    $actual = Test-ExactAuditProcessIdentity $case.pid $case.candidate $case.processName $case.creation $case.start $case.handle
    if ($actual -ne [bool]$case.expected) { throw "Identity case failed: $($case.name)" }
}
$snapshotProfile = Join-Path ([IO.Path]::GetTempPath()) 'jithub-readme-edge-Snp123'
$processRows = @(
    [pscustomobject]@{ CommandLine = "msedge.exe --user-data-dir=$snapshotProfile"; ProcessId = 17 },
    [pscustomobject]@{ CommandLine = 'msedge.exe --type=renderer'; ProcessId = 18 }
)
$selectedRows = @(Get-AuditProfileProcessRows -ObservedProcessIds @(17, 18) -ProcessRows $processRows -RecheckedProcessIds @(17, 18) -ExpectedProfile $snapshotProfile)
if ($selectedRows.Count -ne 1 -or $selectedRows[0].ProcessId -ne 17) { throw 'Snapshot selector did not return exactly the profile-owned row.' }
$liveOmittedRejected = $false
try {
    $ignoredRows = @(Get-AuditProfileProcessRows -ObservedProcessIds @(19) -ProcessRows @() -RecheckedProcessIds @(19) -ExpectedProfile $snapshotProfile)
}
catch {
    if ($_.Exception.Message -notmatch '^Audit cleanup could not confirm an omitted live Edge process command line\.') { throw }
    $liveOmittedRejected = $true
}
if (-not $liveOmittedRejected) { throw 'Snapshot selector accepted a still-live PID omitted from CIM.' }
$racedExitRows = @(Get-AuditProfileProcessRows -ObservedProcessIds @(20) -ProcessRows @() -RecheckedProcessIds @() -ExpectedProfile $snapshotProfile)
if ($racedExitRows.Count -ne 0) { throw 'Snapshot selector should permit a process that exited between snapshots.' }
$freshOnlyOmissionRejected = $false
try {
    $ignoredRows = @(Get-AuditProfileProcessRows -ObservedProcessIds @() -ProcessRows @() -RecheckedProcessIds @(21) -ExpectedProfile $snapshotProfile)
}
catch {
    if ($_.Exception.Message -notmatch '^Audit cleanup could not confirm an omitted live Edge process command line\.') { throw }
    $freshOnlyOmissionRejected = $true
}
if (-not $freshOnlyOmissionRejected) { throw 'Snapshot selector accepted a fresh live PID omitted from CIM.' }
$unknownRows = @(
    [pscustomobject]@{ CommandLine = $null },
    [pscustomobject]@{ CommandLine = '' },
    [pscustomobject]@{ CommandLine = '   ' },
    [pscustomobject]@{ OtherProperty = 'CommandLine is unavailable' }
)
foreach ($unknownRow in $unknownRows) {
    try {
        $rowProperties = [ordered]@{ ProcessId = 22 }
        $commandLineProperty = $unknownRow.PSObject.Properties['CommandLine']
        if ($null -ne $commandLineProperty) { $rowProperties['CommandLine'] = $commandLineProperty.Value }
        $row = [pscustomobject]$rowProperties
        $ignoredRows = @(Get-AuditProfileProcessRows -ObservedProcessIds @(22) -ProcessRows @($row) -RecheckedProcessIds @(22) -ExpectedProfile $snapshotProfile)
        throw 'Snapshot selector accepted an unknown command line.'
    }
    catch {
        if ($_.Exception.Message -notmatch '^Audit cleanup could not inspect an Edge process command line\.') { throw }
    }
}
$staleUnavailableRow = [pscustomobject]@{ CommandLine = $null; ProcessId = 27 }
$staleUnavailableRows = @(Get-AuditProfileProcessRows -ObservedProcessIds @(27) -ProcessRows @($staleUnavailableRow) -RecheckedProcessIds @() -ExpectedProfile $snapshotProfile)
if ($staleUnavailableRows.Count -ne 0) { throw 'Snapshot selector should skip a CIM row proven absent from the fresh process inventory.' }
$unknownOwnershipRows = @(
    [pscustomobject]@{ CommandLine = "msedge.exe /user-data-dir=$snapshotProfile"; ProcessId = 23 },
    [pscustomobject]@{ CommandLine = "msedge.exe --USER-DATA-DIR=$snapshotProfile"; ProcessId = 24 },
    [pscustomobject]@{ CommandLine = "msedge.exe --user-data-dir=$($snapshotProfile.ToUpperInvariant())"; ProcessId = 25 },
    [pscustomobject]@{ CommandLine = "msedge.exe --user-data-dir=$snapshotProfile --user-data-dir=$snapshotProfile"; ProcessId = 26 }
)
foreach ($unknownOwnershipRow in $unknownOwnershipRows) {
    try {
        $ignoredRows = @(Get-AuditProfileProcessRows -ObservedProcessIds @($unknownOwnershipRow.ProcessId) -ProcessRows @($unknownOwnershipRow) -RecheckedProcessIds @($unknownOwnershipRow.ProcessId) -ExpectedProfile $snapshotProfile)
        throw 'Snapshot selector accepted a process with uncertain profile ownership.'
    }
    catch {
        if ($_.Exception.Message -notmatch '^Audit cleanup could not establish whether an Edge process owns the audit profile\.') { throw }
    }
}
Write-Output ("Passed {0} synthetic process identity and {1} snapshot inventory cases." -f $cases.Count, ($unknownRows.Count + $unknownOwnershipRows.Count + 5))
`;
  const result = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", probeScript], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 30_000,
  });
  assert.ifError(result.error);
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
  assert.match(result.stdout, /Passed 24 synthetic process identity and 13 snapshot inventory cases\./u);
});

test("integration: generated-profile cleanup launches the scoped Windows process command", {
  skip: process.platform !== "win32" ? "Windows process-launch integration coverage." : false,
}, async () => {
  const profile = await mkdtemp(path.join(os.tmpdir(), "jithub-readme-edge-"));
  try {
    await stopBrowserProfileProcesses(profile);
  } finally {
    await rm(profile, { recursive: true, force: true });
  }
});
