# Production website subscription migration

The live hobby website is being moved to subscription `4023bbcf-2481-4b3c-916f-01017673502c` (Visual Studio Enterprise Subscription), resource group `rg-jithub-prod-centralus`, in Central US. The old `jithub-web-prod` app remains in the Pay-As-You-Go subscription during the seven-day compatibility window after the next Store release. The `jithubauth` Function App and the old group's other resources are not part of this migration.

West US has zero B1 and total regional App Service VM quota in this subscription. The self-service increase failed, and support request `2609260010000456` was closed at the owner's request. Central US had B1 and total regional quota of 30 before deployment. The existing `jithub` CNAME still points to the old app; its TTL was reduced from 3600 to 600 seconds in GoDaddy.

## Current cutover state (September 26, 2026)

The provider-validated what-if was reviewed and the Central US stack was deployed. The resource group and every resource, including the temporary certificate and the Application Insights generated Smart Detection action group, carry `Application=JitHub`, `Environment=Production`, `Owner=JitHubApp`, and `Repository=JitHubApp/JitHubV2`. The Smart Detection group was tagged after Azure created it. The current OAuth secret was transferred directly into the new Key Vault, its App Service reference is resolved, and the temporary operator Secrets Officer grant was removed. The website build from the passing pull-request CI run is deployed to `jithub-web-prod-4023bbcf.azurewebsites.net`; its `/healthz` returns `ok` over verified TLS. Application Insights has ingested requests from the new site. The three `JITHUB_AZURE_*` GitHub repository variables are set.

GoDaddy has the new `asuid.jithub` ownership TXT record. The custom hostname is bound to the new app with a DNS-validated temporary certificate expiring December 25, 2026. A pinned-host request to `https://jithub.zhuowencui.com/healthz` on the new app returned HTTP 200 with valid TLS. The one-time ACME challenge TXT record was removed after issuance. **The public `jithub` CNAME still targets the old app.** Keep it there until the OIDC workflow is merged and its deployment to the new app is verified.

## Infrastructure

Run `eng/Provision-JitHubWebApp.ps1` from a signed-in Azure CLI session. It verifies both globally unique names, registers providers, builds `infra/production.bicep`, and prints a subscription-scope what-if. Review the resource list, four tags on the resource group and all taggable resources, the web-app-only Website Contributor grant, and the vault-only Key Vault Secrets User grant. Run it again with `-Deploy` after the review. It tags the Smart Detection action group that Application Insights creates outside Bicep using the resource group's tag set. The deployment contains no OAuth secret, certificate key, or publish profile.

The stack uses a single Windows B1 instance with .NET 10, HTTPS-only, always on, TLS 1.2 minimum, and HTTP/2. Pending OAuth handoffs use the existing two-minute process-local store, so a restart can interrupt an in-progress sign-in. The new vault has RBAC, soft delete, and purge protection. The website's system identity reads the `JithubAppSecret` secret through an App Service Key Vault reference. The GitHub deployment identity trusts only `refs/heads/main` and has Website Contributor only on the new web app. SCM and FTP basic publishing are disabled.

After deployment, copy the current `JithubAppSecret` value from the old site's app settings directly into the new vault without echoing it, writing it into the repo, or passing it as a CLI argument. Grant the operator temporary Key Vault Secrets Officer on the new vault for this transfer, then remove that grant. Confirm that the app setting resolves to a healthy Key Vault reference. The public `JitHubClientId` and exact `JITHUB_OAUTH_CALLBACK_URL` are set by Bicep.

The three `JITHUB_AZURE_*` **repository variables** are set. Set the remaining two when the OIDC workflow lands on `main`, so the currently published workflow continues to deploy to the old app until then:

| Variable | Value |
| --- | --- |
| `JITHUB_AZURE_CLIENT_ID` | Client ID output of `id-jithub-deploy-prod` |
| `JITHUB_AZURE_TENANT_ID` | `5556ae28-2fa4-474a-a064-7e0a65a5296e` |
| `JITHUB_AZURE_SUBSCRIPTION_ID` | `4023bbcf-2481-4b3c-916f-01017673502c` |
| `JITHUB_WEBAPP_NAME` | `jithub-web-prod-4023bbcf` |
| `JITHUB_WEBAPP_HEALTH_URL` | `https://jithub-web-prod-4023bbcf.azurewebsites.net/healthz` until DNS cutover, then `https://jithub.zhuowencui.com/healthz` |

Run the website workflow on `main` and verify `/healthz`, `/authorize`, and Application Insights ingestion on the new Azure hostname. Keep `JITHUB_WEBAPP_PUBLISH_PROFILE` during the compatibility week for rollback, but the new workflow must never use it.

## Domain and certificate cutover

The `jithub` CNAME TTL is 600 seconds. Add the ownership TXT record `asuid.jithub` with the new app's `customDomainVerificationId`; retain it through the move. Bind `jithub.zhuowencui.com` to the new app while DNS still points at the old app. Use a short-lived DNS-01 certificate for `jithub.zhuowencui.com`, import its PFX into the new App Service, and bind it with SNI. Keep the private key and PFX password outside the repository and deployment outputs. Verify TLS and `/healthz` against the new app using host resolution pinned to its Azure endpoint before changing DNS.

The production GitHub OAuth app now lists `https://jithub.zhuowencui.com/authorize` and `https://jithub-web-prod.azurewebsites.net/authorize` as **exact** callbacks, with wildcard matching disabled on both. Retain both callbacks through the compatibility week and verify both OAuth handoffs. Switch the GoDaddy `jithub` CNAME to `jithub-web-prod-4023bbcf.azurewebsites.net`. Verify the custom domain's TLS, `/healthz`, sign-in, telemetry, and GitHub Actions deployment in the target subscription. Once Azure can issue a free App Service managed certificate for the active CNAME, bind it and remove the temporary certificate from the app.

If the cutover fails during the compatibility week, restore the old CNAME. The old site and dedicated plan remain online for this purpose. The new OIDC identity has access only to the new web app, so reverting repository variables alone cannot redeploy the old site. If old-site redeployment is needed, restore the previous publish-profile workflow from Git history while its secret is retained, then revert to OIDC for the new site after recovery.

## Desktop release and compatibility deadline

The packaged desktop callback in `JitHub.WinUI/appsettings.json` is `https://jithub.zhuowencui.com/authorize`. Build and review the Store package, exercise sign-in in a release candidate, publish the next valid Store version, and verify the published package and sign-in. Record the Store publication timestamp and schedule the cleanup **seven days after that publication**, not seven days after DNS cutover.

At the deadline, verify the new site and Store release again. Remove the old Azure callback from the GitHub OAuth app, delete only the old `jithub-web-prod` website, its dedicated `ASP-JitHub-Web` plan and site certificate, and remove the obsolete `JITHUB_WEBAPP_PUBLISH_PROFILE` repository secret. Users on older builds that still call the Azure hostname will lose sign-in after this cutoff. Keep the old shared `JitHubV2` resource group and its Function App, storage, telemetry, and code-signing resources.
