import assert from "node:assert/strict";
import test from "node:test";
import {
  GITHUB_CREDENTIAL_ENVIRONMENT_NAMES,
  createCredentialFreeBrowserEnvironment,
} from "./browser-environment.mjs";

test("Edge child environment removes GitHub audit credentials and config paths", () => {
  const parent = Object.fromEntries(GITHUB_CREDENTIAL_ENVIRONMENT_NAMES.map(name => [name, `secret-${name}`]));
  parent.PATH = "C:\\Windows\\System32";
  parent.SystemRoot = "C:\\Windows";
  const child = createCredentialFreeBrowserEnvironment(parent);

  assert.equal(child.PATH, parent.PATH);
  assert.equal(child.SystemRoot, parent.SystemRoot);
  for (const name of GITHUB_CREDENTIAL_ENVIRONMENT_NAMES) {
    assert.equal(Object.hasOwn(child, name), false, `${name} must not reach Edge`);
    assert.equal(parent[name], `secret-${name}`, "the caller environment is not mutated");
  }
});

test("Edge environment filtering rejects non-object inputs", () => {
  assert.throws(() => createCredentialFreeBrowserEnvironment(null), TypeError);
  assert.throws(() => createCredentialFreeBrowserEnvironment([]), TypeError);
});
