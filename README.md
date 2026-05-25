# Manufaktura.Orders.Functions

Azure Functions service for the Manufaktura Orders system. Provides HTTP-triggered functions for document processing operations, including merging SharePoint-hosted delivery note Word documents into consolidated PDF delivery packs.

## Architecture

- **.NET 10** / Azure Functions v4 (isolated worker model)
- **Flex Consumption** (FC1) hosting plan
- **Managed Identity** + Microsoft Graph REST API for SharePoint file access
- **PDFsharp** for PDF merging
- **Bicep** infrastructure-as-code

## Project Structure

```text
├── .github/workflows/       # CI/CD pipelines
│   └── ci.yml               # PR validation + trusted PR/main deployment to dev
├── infra/                   # Bicep infrastructure
│   ├── main.bicep            # Resource definitions
│   ├── main.dev.bicepparam   # Dev parameters
│   ├── main.test.bicepparam  # Test parameters
│   ├── main.prod.bicepparam  # Prod parameters
│   └── setup-github-oidc.ps1 # One-time OIDC setup
├── src/Manufaktura.Orders.Functions/
│   ├── Functions/            # HTTP-triggered functions
│   ├── Models/               # Request/response DTOs
│   └── Services/             # Business logic
└── tests/
  ├── Manufaktura.Orders.Functions.Tests/
  └── Manufaktura.Orders.Functions.IntegrationTests/
```

## Functions

| Function             | Trigger   | Description                                                                                                                          |
| -------------------- | --------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| `MergeDeliveryNotes` | HTTP POST | Accepts an array of SharePoint document URLs, downloads them as PDF via Graph API, merges into a single PDF, and returns the result. |

## Local Development

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-tests-local)

### Run locally

```bash
cd src/Manufaktura.Orders.Functions
func start
```

### Run tests

```bash
dotnet test tests/Manufaktura.Orders.Functions.Tests
```

### Run integration tests

Integration tests run against real Dataverse environments and deployed Orders Functions. They require Azure credentials resolvable by `DefaultAzureCredential`, usually `az login` locally or GitHub Actions OIDC in CI.

The smoke integration suite reads the Function App base URL and key from Dataverse environment variables used by the Power Automate flows:

- `mb_OrdersFunctionAppBaseUrl`
- `mb_OrdersFunctionAppKey`

Set only the target Dataverse URL locally:

```powershell
$env:DATAVERSE_URL = "https://manufaktura-develop.crm11.dynamics.com"
dotnet test tests/Manufaktura.Orders.Functions.IntegrationTests --filter "Category=Smoke"
```

Optional local overrides are available for debugging a specific Function App without changing Dataverse configuration:

```powershell
$env:FUNCTION_APP_BASE_URL = "https://func-mfk-orders-dev.azurewebsites.net"
$env:FUNCTION_APP_KEY = "<function-key>"
```

Do not run write integration tests against production. Dev/test runs create uniquely named `[E2E]` records and clean them up on a best-effort basis.

## Deployment

### First-time setup

Run the OIDC setup script to create the App Registration, federated credentials, GitHub secrets, and Dataverse application users (with System Administrator role) in all three environments:

```powershell
.\infra\setup-github-oidc.ps1
```

The Dataverse environment URLs default to the Manufaktura dev/test/prod organisations. Override them if needed:

```powershell
.\infra\setup-github-oidc.ps1 `
    -DataverseDevUrl  'https://manufaktura-develop.crm11.dynamics.com' `
    -DataverseTestUrl 'https://manufaktura-test.crm11.dynamics.com' `
    -DataverseProdUrl 'https://manufaktura.crm11.dynamics.com'
```

To skip Dataverse setup entirely, pass empty strings for all three URL parameters.

### CI/CD

The `CI` workflow runs on pull requests to `main` and on pushes to `main`.

1. **Build & Test** — compile and run unit tests.
2. **Package** — publish the Function App and upload the validated zip package as a short-lived workflow artifact.
3. **Deploy to Dev** — deploy dev Bicep infrastructure, then publish the package to the shared dev Function App.
4. **Smoke Test** — call `GenerateDeliveryPack` with non-destructive invalid input and expect the function's validation response.
5. **Integration Smoke** — on `main`, create temporary Dataverse records in dev/test, call `RefreshEffectiveDrivers`, assert order driver fields, and clean up.

Trusted same-repository pull requests deploy to the shared dev Function App after build and tests pass. Pull requests from forks still run build and tests, but they do not deploy because the repository is public and dev deployment uses Azure OIDC credentials. When a PR is merged, the resulting push to `main` deploys `main` back to dev so the shared environment returns to the integration baseline.

Dev deployments use one global concurrency group. A newer dev deployment cancels any older in-flight dev deployment, so the shared dev environment always represents the latest successful deployment attempt. The workflow updates a sticky PR comment with the Function App URL, deployed revision, and workflow run link; function keys are never posted to PR comments.

Before enabling deployments, run the OIDC setup script and confirm the repository has these secrets:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

The setup script also creates the GitHub `dev` environment used by the deployment job's federated credential.

### Post-deployment

The Function App's Managed Identity needs the **Sites.ReadWrite.All** application role on Microsoft Graph to read and write SharePoint documents. Because this is a managed identity (not an app registration), the role must be assigned directly to the managed identity's service principal using `az rest`.

Retrieve the `managedIdentityPrincipalId` from the Bicep deployment output, then run:

```bash
# 1. Capture the managed identity's service principal ID (from Bicep outputs)
MI_SP_ID="<managedIdentityPrincipalId>"

# 2. Get Microsoft Graph's service principal ID in this tenant
GRAPH_SP_ID=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)

# 3. Assign the Sites.ReadWrite.All app role to the managed identity
#    App role ID 9492366f-7969-46a4-8d15-ed1a20078fff = Sites.ReadWrite.All (application)
az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${MI_SP_ID}/appRoleAssignments" \
  --headers "Content-Type=application/json" \
  --body "{
    \"principalId\": \"${MI_SP_ID}\",
    \"resourceId\": \"${GRAPH_SP_ID}\",
    \"appRoleId\": \"9492366f-7969-46a4-8d15-ed1a20078fff\"
  }"
```

Alternatively, use the Azure Portal:

1. Go to **Microsoft Entra ID → Enterprise applications**.
2. Search for the managed identity by the Function App name (or by the principal ID above).
3. Select the managed identity → **Permissions → Grant admin consent** is not applicable here; instead open **Microsoft Graph** under Enterprise applications, go to **App roles**, and use **Grant permissions** — or use the [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer) to POST the app-role assignment as above.
