import { createSameByteReplayServer } from "./same-byte-corpus.mjs";

const corpusArgument = process.argv.slice(2).find(argument => argument.startsWith("--corpus="));
if (!corpusArgument || !corpusArgument.slice("--corpus=".length)) {
  throw new Error("Usage: node same-byte-replay-server.mjs --corpus=<captured-case-directory>");
}

const replay = await createSameByteReplayServer(corpusArgument.slice("--corpus=".length));
process.stdout.write(`${JSON.stringify({
  baseUrl: replay.baseUrl,
  manifestSha256: replay.manifestSha256,
  policy: "loopback-only; no network fallback; uncaptured resources return 404",
})}\n`);

const close = async () => {
  await replay.close();
  process.exit(0);
};
process.once("SIGINT", close);
process.once("SIGTERM", close);
