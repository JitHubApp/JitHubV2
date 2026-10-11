import { createHash } from "node:crypto";
import { lstat, readFile, stat, writeFile } from "node:fs/promises";
import { realpathSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const UPSTREAM_COMMIT = "fadaeaf1f1a9be82b321316a6c5502e43138b2f6";
const UPSTREAM_SHA256 = "b174ae2aeb321b52f64adb9ff412f966a7f338839d780784dd15dcad702c2dd6";
const ALIAS_PATTERN = /^[a-z0-9_+-]{1,64}$/u;
const DEFAULT_OUTPUT_PATH = path.join(path.dirname(fileURLToPath(import.meta.url)), "github-emoji-shortcodes.json");
const DEFAULT_FILE_SYSTEM = Object.freeze({ stat, lstat, readFile, writeFile });

function sameFileIdentity(leftPath, rightPath) {
  try {
    const left = statSync(leftPath, { bigint: true });
    const right = statSync(rightPath, { bigint: true });
    if (left.dev !== 0n && left.ino !== 0n && right.dev !== 0n && right.ino !== 0n) {
      return left.dev === right.dev && left.ino === right.ino;
    }
    return realpathSync.native(leftPath) === realpathSync.native(rightPath);
  } catch {
    return false;
  }
}

export function isGeneratorDirectInvocation(invokedPath, generatorPath, platform = process.platform) {
  if (typeof invokedPath !== "string" || !invokedPath ||
      typeof generatorPath !== "string" || !generatorPath) {
    return false;
  }
  const pathImplementation = platform === "win32" ? path.win32 : path;
  let resolvedInvocation;
  let resolvedGenerator;
  try {
    resolvedInvocation = pathImplementation.resolve(invokedPath);
    resolvedGenerator = pathImplementation.resolve(generatorPath);
  } catch {
    return false;
  }
  return platform === "win32"
    ? sameFileIdentity(resolvedInvocation, resolvedGenerator)
    : resolvedInvocation === resolvedGenerator;
}

export async function generateGitHubEmojiShortcodes(inputPath, outputPath = DEFAULT_OUTPUT_PATH, {
  platform = process.platform,
  fileSystem = DEFAULT_FILE_SYSTEM,
  log = message => console.log(message),
} = {}) {
  if (!inputPath) {
    throw new Error("Usage: node generate-github-emoji-shortcodes.mjs <pinned-gemoji-emoji.json> [output.json]");
  }
  const resolvedInput = path.resolve(inputPath);
  const resolvedOutput = path.resolve(outputPath);
  const samePath = platform === "win32"
    ? resolvedInput.toLowerCase() === resolvedOutput.toLowerCase()
    : resolvedInput === resolvedOutput;
  if (samePath) {
    throw new Error("The generated shortcode map must not overwrite the pinned upstream source file.");
  }
  const inputInfo = await fileSystem.stat(resolvedInput);
  try {
    const outputInfo = await fileSystem.lstat(resolvedOutput);
    if (outputInfo.isSymbolicLink() ||
        (inputInfo.dev === outputInfo.dev && inputInfo.ino === outputInfo.ino)) {
      throw new Error("The generated shortcode map must not overwrite the pinned upstream source file.");
    }
  } catch (error) {
    if (error?.code !== "ENOENT") throw error;
  }

  const sourceBytes = await fileSystem.readFile(inputPath);
  if (sourceBytes.length <= 0 || sourceBytes.length > 8 * 1024 * 1024) {
    throw new Error("Pinned Gemoji source exceeds the 8 MiB generation input bound.");
  }
  const sourceSha256 = createHash("sha256").update(sourceBytes).digest("hex");
  if (sourceSha256 !== UPSTREAM_SHA256) {
    throw new Error(`Gemoji source SHA-256 ${sourceSha256} does not match pinned ${UPSTREAM_COMMIT} data.`);
  }

  const records = JSON.parse(sourceBytes.toString("utf8"));
  if (!Array.isArray(records) || records.length < 1 || records.length > 10_000) {
    throw new Error("Pinned Gemoji source has an invalid record count.");
  }
  const aliases = new Map();
  for (const record of records) {
    if (typeof record?.emoji !== "string" || !record.emoji ||
        !Array.isArray(record.aliases) || record.aliases.length > 100) {
      throw new Error("Pinned Gemoji source contains an invalid emoji record.");
    }
    for (const alias of record.aliases) {
      if (typeof alias !== "string" || !ALIAS_PATTERN.test(alias)) {
        throw new Error(`Pinned Gemoji source contains an invalid shortcode alias: ${String(alias)}.`);
      }
      const prior = aliases.get(alias);
      if (prior !== undefined && prior !== record.emoji) {
        throw new Error(`Pinned Gemoji source maps shortcode :${alias}: to conflicting emoji values.`);
      }
      aliases.set(alias, record.emoji);
    }
  }

  const orderedAliases = Object.fromEntries([...aliases].sort(([left], [right]) =>
    left < right ? -1 : left > right ? 1 : 0));
  const outputBytes = Buffer.from(`${JSON.stringify(orderedAliases)}\n`, "utf8");
  if (aliases.size !== 1913) {
    throw new Error(`Pinned Gemoji source produced ${aliases.size} aliases; expected 1913.`);
  }
  await fileSystem.writeFile(outputPath, outputBytes, { flag: "w" });
  const outputSha256 = createHash("sha256").update(outputBytes).digest("hex");
  log(`Generated ${aliases.size} aliases from github/gemoji@${UPSTREAM_COMMIT}.`);
  log(`Source SHA-256: ${sourceSha256}`);
  log(`Map SHA-256: ${outputSha256}`);
  return Object.freeze({
    aliasCount: aliases.size,
    sourceSha256,
    outputSha256,
    outputPath: resolvedOutput,
  });
}

const invokedPath = process.argv[1] ? path.resolve(process.argv[1]) : null;
if (isGeneratorDirectInvocation(invokedPath, fileURLToPath(import.meta.url))) {
  await generateGitHubEmojiShortcodes(process.argv[2], process.argv[3]);
}
