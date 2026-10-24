// Edge renders public repository pages and must never inherit audit credentials
// from the Automation/Node parent, even when a caller runs the live audit with a
// token for its separate GitHub API or pinned-source operations.
export const GITHUB_CREDENTIAL_ENVIRONMENT_NAMES = Object.freeze([
  "JITHUB_README_AUDIT_GITHUB_TOKEN",
  "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID",
  "JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY",
  "GITHUB_TOKEN",
  "GH_TOKEN",
  "GH_ENTERPRISE_TOKEN",
  "GITHUB_ENTERPRISE_TOKEN",
  "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
  "ACTIONS_ID_TOKEN_REQUEST_URL",
  "GITHUB_APP_TOKEN",
  "GITHUB_ACCESS_TOKEN",
  "GITHUB_CLIENT_SECRET",
  "GITHUB_APP_PRIVATE_KEY",
  "GH_ACCESS_TOKEN",
  "GH_CONFIG_DIR",
]);

export function createCredentialFreeBrowserEnvironment(sourceEnvironment) {
  if (!sourceEnvironment || typeof sourceEnvironment !== "object" || Array.isArray(sourceEnvironment)) {
    throw new TypeError("Browser environment source must be an environment-variable object.");
  }
  const browserEnvironment = { ...sourceEnvironment };
  for (const name of GITHUB_CREDENTIAL_ENVIRONMENT_NAMES) delete browserEnvironment[name];
  return browserEnvironment;
}
