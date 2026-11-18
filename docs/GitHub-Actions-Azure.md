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
change GitHub's OIDC configuration. **The new deployment job uses the protected
environment `azure-release`, so it needs an additional federated credential:**

```text
repo:laithebao@160092784/Capstone_SAW_BE@1263519670:environment:azure-release
```

Add it to the same `id-saw-github-deploy` identity, with the same issuer and
audience. In Azure choose Entity = Environment, value = `azure-release`, and a
new credential name such as `github-azure-release`. Keep the branch credential
during cutover; after the new release succeeds, remove the old branch credential
if no other workflow uses it. That prevents older branch-only workflows from
bypassing the environment gate.

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

API SQL passwords, JWT keys and other runtime credentials stay in Azure. The
pipeline does not download app settings. The migration runner uses a **separate
database deployment credential** stored only as a protected environment secret.

## One-time setup before merging this workflow

The code does not configure remote GitHub/Azure settings. Complete these steps
before merging; otherwise the new deployment job cannot run successfully.

### 1. Protect the GitHub release environment

Repository > **Settings > Environments > New environment** > `azure-release`:

- Enable **Required reviewers**, select the person who reviews SQL, and save.
- If you are the only maintainer, leave **Prevent self-review** unchecked so you
  can approve your own release. A team should use another reviewer.
- Restrict **Deployment branches and tags** to selected branch `main`.
- Disable administrator bypass if you want the approval gate enforced for admins.
- Add environment secret `AZURE_SQL_MIGRATION_CONNECTION_STRING` (step 3).

An environment name alone does not require approval. **Required reviewers must
be configured in GitHub.** For a public repository this is available on GitHub
Free; private repositories have plan restrictions. Do not replace the gate with
an unprotected environment if the option is unavailable.

This first version requires approval for **every main release**, including
code-only changes. With no pending migration it only verifies SQL history before
deploying the API. This avoids using database credentials outside the approval
gate to decide whether approval is needed.

### 2. Add the environment OIDC credential

On Azure > Managed Identities > `id-saw-github-deploy` > Federated credentials,
add the environment credential shown above. The existing six repository
variables and the App Service image-pull identity stay the same.

### 3. Create the SQL migration user and environment secret

In SSMS, connect to Azure SQL and select **SmartAgriWarehouseDB**. An authorized
database administrator creates a dedicated deployment user, for example:

```sql
CREATE USER [saw_migrator] WITH PASSWORD = '<strong unique password>';
ALTER ROLE [db_owner] ADD MEMBER [saw_migrator];
```

`db_owner` is deliberately powerful because future reviewed migrations can alter
tables, triggers and data. Scope this user to this database, keep it exclusively
for approved deployment, and never configure it as the API runtime user. The
runtime API user should have its own data permissions.

Store this value in **azure-release > Environment secrets**, replacing only the
password. Do not commit it, put it in repository Variables, or paste it into logs:

```text
Server=tcp:saw.database.windows.net,1433;Initial Catalog=SmartAgriWarehouseDB;User ID=saw_migrator;Password=<password>;Encrypt=True;TrustServerCertificate=False;Persist Security Info=False;
```

Use connection-string quoting if the password contains `;` or quotes. The runner
checks the exact target server/database and TLS settings before connecting.

### 4. Allow the deployment identity to manage temporary SQL firewall rules

GitHub-hosted runners have changing IP addresses. The workflow creates a rule for
only the current runner's public IPv4, then deletes that same rule even if SQL
migration fails. It does not enable "Allow Azure services" or a wide IP range.

As an Azure administrator, run once from the backend root (these commands change
Azure IAM, not database data):

```powershell
az role definition create --role-definition scripts/ci/sql-firewall-role.json
$deployPrincipal = az identity show --resource-group saw --name id-saw-github-deploy --query principalId --output tsv
$sqlScope = az sql server show --resource-group saw --name saw --query id --output tsv
az role assignment create --assignee-object-id $deployPrincipal --assignee-principal-type ServicePrincipal --role 'SAW GitHub SQL Firewall Manager' --scope $sqlScope
```

If the custom role already exists, inspect/update it instead of recreating it.
The role file's assignable scope is this resource group; the assignment is only
on SQL server `saw`. It grants firewall rule management, not SQL data access or
permission to delete the server/database. Allow a few minutes for IAM changes.
Azure SQL public network access must permit selected networks; private-only SQL
requires a runner with private connectivity instead of this firewall approach.

## How it runs

1. A PR into `main` restores/builds the .NET 8 test runner, creates an isolated
   SQL Server Docker instance, and runs the integration profile (unit + SQL,
   excluding browser and Cloudinary). The runner rejects failures, skipped
   tests and zero selected tests. No frontend checkout is required.
2. It builds a Linux amd64 image once with `RELEASE_COMMIT` equal to the source
   commit and runs the existing container smoke script.
3. CI publishes `SAW.Migrations` and generates incremental SQL **offline**, once,
   into an immutable `migrations-<commit>-<run>-<attempt>` artifact. This contains
   `REVIEW.md`, `plan.json`, SQL checksums and the matching compiled runner.
   SQL is generated on PRs too, without Azure credentials or database access.
4. After a main build succeeds, the deployment job waits for the required
   environment reviewer. Download and review this run's artifact before approving.
   After approval it checks that image and SQL belong to the same commit, run,
   attempt and migration head; obsolete main commits are skipped.
5. Using OIDC, it temporarily permits this runner through the SQL firewall.
   It reads the real `dbo.__EFMigrationsHistory`, takes a SQL session application
   lock (EF Core 8 has no built-in migration lock), and executes only missing
   scripts from the approved artifact. SQL is never regenerated at deployment.
   It verifies the final history, records the result, then removes the firewall rule.
6. Only after database verification, the deployment job pushes a unique commit/run/attempt
   tag, resolves its digest, and updates **sitecontainer `main`** by digest.
   It preserves the existing image pull identity and restarts the app.
7. Both `/health/live` and `/health/ready` must return `Healthy` and the expected
   commit on two consecutive checks within 10 minutes. An old healthy release
   is not accepted as proof of a successful deployment.
8. Database migration and app deployment share one serialized job. A commit superseded on `main` before the app
   update is skipped. If an applied deployment fails, the job attempts to
   restore the previous image digest, checks its health, and still fails the
   workflow so the unsuccessful release remains visible. Image rollback never
   downgrades the database, so every approved migration must support that image.

Actions are pinned to verified full commit SHAs. The CI job has no OIDC write
permission. PRs cannot enter the Azure deployment job.

## First run

Commit `.github/workflows/backend-ci-cd.yml`, `scripts/ci/`, and this document on
a branch. Push and open a PR into `main`. Confirm **Test and build Linux image**
passes. Complete the one-time setup above, then merge. The main push starts
verification and waits at the `azure-release` approval gate. In the run, select
**Review deployments**, select `azure-release`, and approve after reviewing SQL.

Open GitHub > Actions > **Backend CI and Azure deployment**. Review both jobs
and the deployment summary. Artifacts include TRX test results and a
`deployment-<run>-<attempt>` record containing commit, digest, previous digest,
migration head and rollback outcome. The same artifact also contains
`migration-result.json` (database head before/after, migrations applied by this
run) and the temporary firewall rule metadata. Image and migration artifacts
expire after fourteen days; test results after seven days. If artifacts expire,
run the current main workflow again and review the new artifacts.

For a rerun of the current `main`, use Actions > Run workflow > branch `main`.
An old run for a commit that is no longer `main` will not overwrite a newer app.
If using the rerun menu, choose **Re-run all jobs**, not only failed jobs: image
and SQL artifacts are deliberately bound to the same run attempt. Re-review the
new artifact and approve the new attempt.

## Daily development and new migrations

1. Create a feature branch, change the model, add a migration, and use
   `dotnet ef database update` locally as before. Test the migration with
   representative data and check compatibility with the currently deployed API.
2. Open a PR. CI runs the tests and publishes SQL for inspection. Check for
   accidental drops, renames implemented as drop/add, data conversions, locking
   and changes to triggers. Do not edit migrations already applied to Azure.
3. Merge when the PR is approved and CI passes. The main run generates its own
   final artifact; inspect **that run's** SQL and `REVIEW.md` before approving
   `azure-release`. If necessary, use SSMS to read the latest migration on Azure
   and identify which scripts in the artifact are still pending.
4. The pipeline applies the missing migrations, verifies Azure SQL, then deploys
   the tested image and checks health. You no longer run Azure migrations by hand
   or advance an `APPROVED_MIGRATION_HEAD` constant.

`scripts/ci/database-baseline.txt` is the **fixed historical import boundary**,
not an approval marker. Do not change it each release. `InitialCreate.Up` is
empty in this project, so the pipeline cannot recreate the legacy schema in an
empty database. It accepts only a complete known migration prefix at or after
the imported baseline and refuses unknown migrations, gaps or a downgrade.
History checks do not detect manual schema drift: never fake EF history rows or
modify deployed migration files to make checks pass.

## Limits and recovery

- The B1 app restarts during deployment; this is not zero-downtime deployment.
- CI test databases are isolated from Azure SQL. The runner applies reviewed
  incremental SQL only after approval; SQL failure blocks API deployment. Open
  transactions are rolled back, but earlier committed migrations and statements
  explicitly outside a transaction may remain. Inspect the real schema/history
  before retrying; there is no blanket automatic retry or database Down migration.
- Review data-loss risk and Azure SQL restore readiness before approval. CI unit
  and integration tests do not replace rehearsing a migration on a representative
  database copy. Use expand/contract across releases for breaking schema changes;
  the old API is still running while SQL is being applied.
- A new main commit arriving during migration can leave schema ahead of the
  running API. The obsolete image is skipped; rerun/deploy the latest main after
  review. Backward-compatible migrations are necessary for this case too.
- Only this workflow is serialized by GitHub concurrency. Other migration tools
  must use the same application lock or run in a coordinated maintenance window.
- If a runner is forcibly terminated, cleanup may not execute. In Azure SQL
  Networking remove only its `gha-saw-<run>-<attempt>` firewall rule, identified
  by the workflow/run record. The runner waits up to about five minutes for a
  newly created rule to propagate (connection error 40615 only). It does not
  automatically retry SQL statements or a migration that has started executing.
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
- [EF Core deployment strategies](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying)
- [GitHub environment approval gates](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments)
