# Backend CI/CD for the existing Azure Web App

Targets: GitHub `laithebao/Capstone_SAW_BE`, branch `main`, resource group `saw`,
ACR `saw2026`, Web App `saw-api`, sidecar main container `main`, port `8080`.
This workflow deploys the one existing environment. A separate production
environment and promotion gate are not provisioned by this change.

## Azure and GitHub prerequisites

- Deployment identity: `id-saw-github-deploy`.
- ACR uses **RBAC Registry Permissions**. Grant the deployment identity `AcrPush`
  and `Reader` on `saw2026`; grant `Website Contributor` on `saw-api`.
- Keep the app's pull identity `id-saw-api-acrpull` and its `AcrPull` assignment.
- Federated credential issuer: `https://token.actions.githubusercontent.com`.
- Audience: `api://AzureADTokenExchange`.
- For immutable subjects, the branch credential is:

```text
repo:laithebao@160092784/Capstone_SAW_BE@1263519670:ref:refs/heads/main
```

The repository must emit the matching subject. It was created before July 15,
2026, so check its Actions/OIDC settings and opt into immutable subjects if it
still emits the older name-only prefix. Creating the Azure credential does not
change GitHub's OIDC configuration. The deployment job intentionally does not
declare a GitHub `environment`, because that would change the token subject.

If the immutable-subject toggle is unavailable in the GitHub UI, the repository
administrator can use the official GitHub CLI. Install it with
`winget install --exact --id GitHub.cli`, reopen PowerShell, then run:

```powershell
gh auth login --hostname github.com --git-protocol https --web
gh api --method PUT repos/laithebao/Capstone_SAW_BE/actions/oidc/customization/sub -H "X-GitHub-Api-Version: 2026-03-10" -F use_default=true -F use_immutable_subject=true
gh api repos/laithebao/Capstone_SAW_BE/actions/oidc/customization/sub -H "X-GitHub-Api-Version: 2026-03-10"
```

This uses the default repository subject template and opts this repository into
immutable subjects. Check `use_immutable_subject` and `sub_claim_prefix` in the
response; the prefix must match the owner and repository IDs above. Do not use
this reset command on another repository with an intentional custom template.

Configure these **repository Variables** under Settings > Secrets and variables
> Actions > Variables:

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | Client ID of `id-saw-github-deploy` |
| `AZURE_TENANT_ID` | Microsoft Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | `7b2c5d9c-4647-4424-a705-29ff07d2cc17` |
| `AZURE_RESOURCE_GROUP` | `saw` |
| `AZURE_ACR_NAME` | `saw2026` |
| `AZURE_WEBAPP_NAME` | `saw-api` |

SQL passwords, JWT keys and service credentials stay in Azure. The pipeline
does not download app settings or require those secrets in GitHub.

## How it runs

1. A PR into `main` restores/builds the .NET 8 test runner, creates an isolated
   SQL Server Docker instance, and runs the integration profile (unit + SQL,
   excluding browser and Cloudinary). The runner rejects failures, skipped
   tests and zero selected tests. No frontend checkout is required.
2. It builds a Linux amd64 image once with `RELEASE_COMMIT` equal to the source
   commit and runs the existing container smoke script.
3. A push to `main` or a manual run on `main` additionally transfers the tested
   image to a separate deployment job through a same-run artifact ID. The job
   verifies its checksum, source run, commit and loaded Docker image ID.
4. The deployment job signs in using OIDC, pushes a unique commit/run/attempt
   tag, resolves its digest, and updates **sitecontainer `main`** by digest.
   It preserves the existing image pull identity and restarts the app.
5. Both `/health/live` and `/health/ready` must return `Healthy` and the expected
   commit on two consecutive checks within 10 minutes. An old healthy release
   is not accepted as proof of a successful deployment.
6. App updates are serialized. A commit superseded on `main` before the app
   update is skipped. If an applied deployment fails, the job attempts to
   restore the previous image digest, checks its health, and still fails the
   workflow so the unsuccessful release remains visible.

Actions are pinned to verified full commit SHAs. The CI job has no OIDC write
permission. PRs cannot enter the Azure deployment job.

## First run

Commit `.github/workflows/backend-ci-cd.yml`, `scripts/ci/`, and this document on
a branch. Push and open a PR into `main`. Confirm **Test and build Linux image**
passes, then merge. The main push starts verification and the deployment job.

Open GitHub > Actions > **Backend CI and Azure deployment**. Review both jobs
and the deployment summary. Artifacts include TRX test results and a
`deployment-<run>-<attempt>` record containing commit, digest, previous digest,
migration head and rollback outcome. Image transfer artifacts expire after one
day; test results after seven days; deployment records after fourteen days.

For a rerun of the current `main`, use Actions > Run workflow > branch `main`.
An old run for a commit that is no longer `main` will not overwrite a newer app.

## Limits and recovery

- The B1 app restarts during deployment; this is not zero-downtime deployment.
- CI test databases are isolated from Azure SQL. Database migrations are not
  applied by this workflow. Deployment stops before accessing Azure when the
  build requires a migration other than `APPROVED_MIGRATION_HEAD` in
  `scripts/ci/deploy_azure.py`. Review and rehearse the database migration and
  compatibility with the previous image, apply the reviewed schema changes,
  then update that approved head in a PR. Image rollback does not undo schema
  or data. Existing migration files should not be edited after application.
- Persistent application uploads need the separate storage configuration from
  the Azure deployment plan. This workflow does not create or replace mounts.
- Keep previous ACR image digests while they are needed for rollback.
- If rollback fails, read the deployment artifact and Azure Log stream. The
  artifact's `previous_image` can be restored manually, without rebuilding:

```powershell
az webapp sitecontainers update --resource-group saw --name saw-api --container-name main --image '<previous_image digest from deployment.json>' --output none
az webapp restart --resource-group saw --name saw-api
```

Recheck both health endpoints after recovery. Restore only a reviewed previous
image compatible with the database schema currently installed.

If Azure login reports `AADSTS700213`/`AADSTS70021`, compare the token subject
in the error with the Azure federated credential, including names, numeric IDs,
branch, issuer and audience. Never paste an OIDC token into chat or logs.

If health reports the previous release after an image update, also check whether
an Azure app setting named `Release__Commit` overrides the image's baked-in
commit. Keep release metadata supplied by the built image.

## References

- [Azure OIDC](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-azure)
- [GitHub immutable subjects](https://docs.github.com/en/actions/reference/security/oidc#immutable-subject-claims)
- [App Service sitecontainers CLI](https://learn.microsoft.com/en-us/cli/azure/webapp/sitecontainers)
- [ACR role permissions](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-rbac-built-in-roles-overview)
