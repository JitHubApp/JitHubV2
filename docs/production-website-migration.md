# Production website subscription migration

The production website is being moved to subscription `4023bbcf-2481-4b3c-916f-01017673502c` (Visual Studio Enterprise Subscription), resource group `rg-jithub-prod-westus`, in West US. The old `jithub-web-prod` app remains in the Pay-As-You-Go subscription during the seven-day compatibility window after the next Store release. The `jithubauth` Function App and the old group's other resources are not part of this migration.

As of September 25, 2026, the target subscription has a West US B1 App Service VM quota of zero. Azure denied a self-service increase. Support request `2609260010000456` is open for one non-zone-redundant B1 App Service VM in West US. Confirm that both the B1 SKU and total regional App Service VM limits reach at least one before deployment. The normal provider-validated what-if fails on this quota; a template-level what-if can still review the resources, but does not establish deployability. The existing `jithub` CNAME still points to the old app; its TTL was reduced from 3600 to 600 seconds in GoDaddy.

## Infrastructure

Run `eng/Provision-JitHubWebApp.ps1` from a signed-in Azure CLI session. It verifies both globally unique names, registers providers, builds `infra/production.bicep`, and prints a subscription-scope what-if. Review the resource list, four tags on the resource group and all taggable resources, the web-app-only Website Contributor grant, and the vault-only Key Vault Secrets User grant. Run it again with `-Deploy` after the review. The deployment contains no OAuth secret, certificate key, or publish profile.

The stack uses a single Windows B1 instance with .NET 10, HTTPS-only, always on, TLS 1.2 minimum, and HTTP/2. Pending OAuth handoffs use the existing two-minute process-local store, so a restart can interrupt an in-progress sign-in. The new vault has RBAC, soft delete, and purge protection. The website's system identity reads the `JithubAppSecret` secret through an App Service Key Vault reference. The GitHub deployment identity trusts only `refs/heads/main` and has Website Contributor only on the new web app. SCM and FTP basic publishing are disabled.

After deployment, copy the current `JithubAppSecret` value from the old site's app settings directly into the new vault without echoing it, writing it into the repo, or passing it as a CLI argument. Grant the operator temporary Key Vault Secrets Officer on the new vault for this transfer, then remove that grant. Confirm that the app setting resolves to a healthy Key Vault reference. The public `JitHubClientId` and exact `JITHUB_OAUTH_CALLBACK_URL` are set by Bicep.

Set these GitHub **repository variables** before the OIDC workflow lands on `main`:

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
