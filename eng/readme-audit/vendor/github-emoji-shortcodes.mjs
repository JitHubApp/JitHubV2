import { createHash } from "node:crypto";
import { lstat, open } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const DEFAULT_VENDOR_DIRECTORY = path.dirname(fileURLToPath(import.meta.url));
const ALIAS_PATTERN = /^[a-z0-9_+-]{1,64}$/u;
const VALUE_PATTERN = /^[^\u0000-\u001f\u007f-\u009f<>]{1,64}$/u;
const UNPAIRED_SURROGATE_PATTERN = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u;

export const PINNED_GITHUB_EMOJI_SHORTCODES = Object.freeze({
  source: "github/gemoji/db/emoji.json",
  sourceCommit: "fadaeaf1f1a9be82b321316a6c5502e43138b2f6",
  sourceSha256: "b174ae2aeb321b52f64adb9ff412f966a7f338839d780784dd15dcad702c2dd6",
  sourceLicense: "MIT",
  sourceLicenseSha256: "bab932cd28676c262d38eb7aedd7c366ebae15a07d823cc51854ec16ec045dba",
  mapFile: "github-emoji-shortcodes.json",
  mapSha256: "44980486df12fa536479d4a8c28e57b65654b373a263a894f79ccc7ceb335cf5",
  extensionFile: "github-emoji-shortcodes.js",
  extensionSha256: "89f772b065e6ba0114a9a8aaba19de071b20e2c44af065f83d09865c5bcb4d87",
  licenseFile: "GITHUB-GEMOJI.LICENSE",
  aliasCount: 1913,
});

const MAX_MAP_BYTES = 128 * 1024;
const MAX_EXTENSION_BYTES = 64 * 1024;
const MAX_LICENSE_BYTES = 16 * 1024;

export async function loadPinnedGitHubEmojiShortcodes({ vendorDirectory = DEFAULT_VENDOR_DIRECTORY } = {}) {
  const [mapBytes, extensionBytes, licenseBytes] = await Promise.all([
    readBoundedFile(path.join(vendorDirectory, PINNED_GITHUB_EMOJI_SHORTCODES.mapFile), MAX_MAP_BYTES),
    readBoundedFile(path.join(vendorDirectory, PINNED_GITHUB_EMOJI_SHORTCODES.extensionFile), MAX_EXTENSION_BYTES),
    readBoundedFile(path.join(vendorDirectory, PINNED_GITHUB_EMOJI_SHORTCODES.licenseFile), MAX_LICENSE_BYTES),
  ]);
  verifyHash("GitHub emoji shortcode map", mapBytes, PINNED_GITHUB_EMOJI_SHORTCODES.mapSha256);
  verifyHash("GitHub emoji shortcode extension", extensionBytes, PINNED_GITHUB_EMOJI_SHORTCODES.extensionSha256);
  verifyHash("GitHub gemoji license", licenseBytes, PINNED_GITHUB_EMOJI_SHORTCODES.sourceLicenseSha256);

  let aliases;
  try {
    aliases = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(mapBytes));
  } catch {
    throw new Error("Pinned GitHub emoji shortcode map is not valid UTF-8 JSON.");
  }
  if (!aliases || typeof aliases !== "object" || Array.isArray(aliases) ||
      Object.getPrototypeOf(aliases) !== Object.prototype) {
    throw new Error("Pinned GitHub emoji shortcode map has an invalid root object.");
  }
  const keys = Object.keys(aliases);
  if (keys.length !== PINNED_GITHUB_EMOJI_SHORTCODES.aliasCount) {
    throw new Error("Pinned GitHub emoji shortcode map does not contain the expected alias count.");
  }
  for (const key of keys) {
    const value = aliases[key];
    if (!ALIAS_PATTERN.test(key) || typeof value !== "string" ||
        !VALUE_PATTERN.test(value) || Array.from(value).length > 32 ||
        UNPAIRED_SURROGATE_PATTERN.test(value)) {
      throw new Error("Pinned GitHub emoji shortcode map contains an invalid alias or Unicode value.");
    }
  }

  return Object.freeze({
    pins: PINNED_GITHUB_EMOJI_SHORTCODES,
    aliases: Object.freeze(aliases),
    mapBytes,
    extensionBytes,
  });
}

async function readBoundedFile(filePath, maximumBytes) {
  const fileInfo = await lstat(filePath);
  if (!fileInfo.isFile() || fileInfo.isSymbolicLink() ||
      fileInfo.size <= 0 || fileInfo.size > maximumBytes) {
    throw new Error(`Pinned GitHub emoji file exceeds its ${maximumBytes}-byte bound.`);
  }

  const file = await open(filePath, "r");
  try {
    const buffer = Buffer.allocUnsafe(maximumBytes + 1);
    let length = 0;
    while (length < buffer.length) {
      const { bytesRead } = await file.read(buffer, length, buffer.length - length, null);
      if (bytesRead === 0) break;
      length += bytesRead;
    }
    if (length <= 0 || length > maximumBytes) {
      throw new Error(`Pinned GitHub emoji file exceeds its ${maximumBytes}-byte bound.`);
    }
    return buffer.subarray(0, length);
  } finally {
    await file.close();
  }
}

function verifyHash(label, bytes, expectedHash) {
  const actualHash = createHash("sha256").update(bytes).digest("hex");
  if (actualHash !== expectedHash) throw new Error(`${label} does not match its pinned SHA-256.`);
}
