# Continuous deployment with GitHub Actions

`.github/workflows/deploy.yml` deploys to Azure on every push to `main` that changes `collector/`, `infra/` or the
workflow itself. You can also run it by hand (**Actions → Deploy to Azure → Run workflow**). Each run:

1. builds and tests the collector (the deployment stops if a test fails);
2. deploys `infra/bicep/main.bicep` (idempotent; unchanged resources are left as they are);
3. opens the SQL firewall to the runner's IP, applies the schema scripts `001`–`003` (idempotent), and removes the rule;
4. deploys the Function App code;
5. checks `GET /api/health`.

Runs never overlap: a second push waits for the running deployment to finish.

The workflow signs in to Azure with **OpenID Connect**: GitHub exchanges a short-lived token for Azure access, so
there is no Azure password or client secret stored in GitHub.

## One-time setup

Run these with the Azure CLI as someone who can create app registrations, groups and role assignments.
Adjust the names; the examples use the resource group `rg-chromebook-poc`.

### 1. Resource group and deployment identity

```bash
RG=rg-chromebook-poc
LOCATION=eastus
REPO=nrkey420/chromebook-agent-extension

az group create -n "$RG" -l "$LOCATION" -o none

APP_ID=$(az ad app create --display-name chromebook-collector-deploy --query appId -o tsv)
az ad sp create --id "$APP_ID" -o none
SP_OBJECT_ID=$(az ad sp show --id "$APP_ID" --query id -o tsv)

# Trust GitHub Actions jobs that run in this repo's "azure-poc" environment.
az ad app federated-credential create --id "$APP_ID" --parameters "{
  \"name\": \"github-azure-poc\",
  \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"repo:${REPO}:environment:azure-poc\",
  \"audiences\": [\"api://AzureADTokenExchange\"]
}"

# Owner on the resource group only: the Bicep creates role assignments (Key Vault, storage), which needs more
# than Contributor. Alternative: Contributor + "Role Based Access Control Administrator".
az role assignment create --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
  --role Owner --scope "$(az group show -n "$RG" --query id -o tsv)" -o none
```

### 2. SQL admin group

Azure SQL uses Microsoft Entra-only sign-in. Make the SQL admin a **group** that contains both the people who
administer the database and the deployment identity, so the workflow can apply the schema.

```bash
GROUP_ID=$(az ad group create --display-name "Chromebook SQL Admins" --mail-nickname chromebook-sql-admins --query id -o tsv)
az ad group member add --group "$GROUP_ID" --member-id "$(az ad signed-in-user show --query id -o tsv)"
az ad group member add --group "$GROUP_ID" --member-id "$SP_OBJECT_ID"
```

### 3. GitHub environment, secrets and variables

In the repository: **Settings → Environments → New environment → `azure-poc`**. Optionally add required
reviewers so every deployment waits for an approval.

Add these to the `azure-poc` environment (or with the GitHub CLI, as shown):

| Name | Kind | Value |
|---|---|---|
| `AZURE_CLIENT_ID` | secret | `$APP_ID` |
| `AZURE_TENANT_ID` | secret | `az account show --query tenantId -o tsv` |
| `AZURE_SUBSCRIPTION_ID` | secret | `az account show --query id -o tsv` |
| `HMAC_KEY_B64` | secret | `openssl rand -base64 32` (the same value goes in the extension policy) |
| `SQL_ENTRA_ADMIN_OBJECT_ID` | secret | `$GROUP_ID` |
| `SQL_ENTRA_ADMIN_NAME` | secret | `Chromebook SQL Admins` |
| `SQL_ENTRA_ADMIN_TYPE` | secret | `Group` |
| `AZURE_RESOURCE_GROUP` | variable | `rg-chromebook-poc` |
| `AZURE_LOCATION` | variable | `eastus` (optional; default `eastus`) |

```bash
gh secret set AZURE_CLIENT_ID --env azure-poc --body "$APP_ID"
gh secret set AZURE_TENANT_ID --env azure-poc --body "$(az account show --query tenantId -o tsv)"
gh secret set AZURE_SUBSCRIPTION_ID --env azure-poc --body "$(az account show --query id -o tsv)"
gh secret set HMAC_KEY_B64 --env azure-poc --body "$(openssl rand -base64 32)"
gh secret set SQL_ENTRA_ADMIN_OBJECT_ID --env azure-poc --body "$GROUP_ID"
gh secret set SQL_ENTRA_ADMIN_NAME --env azure-poc --body "Chromebook SQL Admins"
gh secret set SQL_ENTRA_ADMIN_TYPE --env azure-poc --body "Group"
gh variable set AZURE_RESOURCE_GROUP --env azure-poc --body "$RG"
gh variable set AZURE_LOCATION --env azure-poc --body "$LOCATION"
```

GitHub secrets cannot be read back. If you need the HMAC key later (for the extension policy), read it from Key
Vault: `az keyvault secret show --vault-name <keyVaultName> -n HmacKey1 --query value -o tsv`.

### 4. First deployment

1. Run the workflow by hand (**Actions → Deploy to Azure → Run workflow**). The first run creates everything;
   the code deployment step retries for a few minutes while the new role assignments take effect.
2. **Once, as a person**, create the Function App's database user. The workflow skips this step because resolving
   the app's name in Entra ID from a service principal requires the SQL server to have *Directory Readers*.
   Add your IP to the SQL firewall, then:
   ```bash
   SQL_SERVER=<sqlServerFqdn> SQL_DATABASE=<sqlDatabaseName> FUNCTION_APP_NAME=<functionAppName> \
     bash infra/scripts/init-sql.sh
   ```
   The workflow prints these names in its "Deploy infrastructure" step. Until this runs, the collector answers
   batches with 503 (SQL sign-in fails); `/api/health` still passes.
3. Send a signed test batch (`infra/scripts/send-test-batch.sh`) and expect `202` with `"sqlWrites":1`.

After that, every merge to `main` that changes the collector or infrastructure deploys automatically.

## Notes

- **Rotating the HMAC key:** change the `HMAC_KEY_B64` secret and re-run the workflow; the deployment writes the
  new value to Key Vault. Update the extension policy at the same time, or devices get 401 until it matches.
- **Sentinel** is not part of the workflow; run `infra/scripts/create-dce-dcr.*` once when you enable it.
- **Changes outside these paths** (for example `docs/` or `extension/`) do not trigger a deployment.
- **Failures:** the run stops at the failing step and the SQL firewall rule for the runner is always removed.
