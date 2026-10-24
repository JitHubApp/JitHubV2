import { sha256 } from "./same-byte-corpus.mjs";

const MAX_IMAGE_COUNT = 15_000;
const MAX_FAILURE_EXAMPLES = 8;
const MAX_SOURCE_LENGTH = 64 * 1024;

/**
 * A failed live GitHub image blocks same-byte capture. Retain enough evidence
 * to distinguish broken upstream content from a capture regression, without
 * persisting the image URL, alt text, or any README content.
 */
export function createUnavailableVisibleImageEvidence(images) {
  if (!Array.isArray(images) || images.length > MAX_IMAGE_COUNT ||
      images.some(image => !image || typeof image !== "object" || Array.isArray(image))) {
    return null;
  }
  const unavailable = images.filter(image =>
    image?.complete !== true ||
    !Number.isFinite(image?.naturalWidth) || image.naturalWidth <= 0 ||
    !Number.isFinite(image?.naturalHeight) || image.naturalHeight <= 0);
  const boundedDimension = value => Number.isFinite(value) && value >= 0 &&
    value <= 100_000_000 ? Number(value.toFixed(2)) : null;
  const sourceDigest = value => typeof value === "string" && value.length > 0 &&
    value.length <= MAX_SOURCE_LENGTH
    ? sha256(Buffer.from(value, "utf8")) : null;
  const sourceKind = value => typeof value !== "string" || value.length === 0
    ? "empty"
    : /^data:/iu.test(value) ? "data"
      : /^https?:/iu.test(value) ? "http" : "other";
  return {
    visibleImageCount: images.length,
    unavailableImageCount: unavailable.length,
    failures: unavailable.slice(0, MAX_FAILURE_EXAMPLES).map(image => ({
      replayIndex: Number.isSafeInteger(image.replayIndex) &&
        image.replayIndex >= 0 && image.replayIndex < MAX_IMAGE_COUNT
        ? image.replayIndex : null,
      complete: image.complete === true,
      naturalWidth: boundedDimension(image.naturalWidth),
      naturalHeight: boundedDimension(image.naturalHeight),
      renderedWidth: boundedDimension(image.renderedWidth),
      renderedHeight: boundedDimension(image.renderedHeight),
      sourceKind: sourceKind(image.source),
      currentSourceKind: sourceKind(image.currentSource),
      sourceSha256: sourceDigest(image.source),
      currentSourceSha256: sourceDigest(image.currentSource),
    })),
  };
}
