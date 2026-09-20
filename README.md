# Manufaktura.Orders.Functions

Azure Functions service for the Manufaktura Orders system. Provides HTTP-triggered functions for document processing operations, including merging SharePoint-hosted delivery note Word documents into consolidated PDF delivery packs.

## Architecture

- **.NET 10** / Azure Functions v4 (isolated worker model)
- **Flex Consumption** (FC1) hosting plan
- **Managed Identity** + Microsoft Graph REST API for SharePoint file access
- **PDFsharp** for PDF merging
- **Bicep** infrastructure-as-code

## Project Structure

```
├── .github/workflows/       # CI/CD pipelines
│   ├── ci.yml               # PR validation (build + test)
│   ├── ci-cd.yml            # Main pipeline (build → dev → test → prod)
│   └── deploy.yml           # Reusable deployment workflow
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
└── tests/Manufaktura.Orders.Functions.Tests/
```

## Functions

| Function                        | Trigger   | Description                                                                                                                          |
| ------------------------------- | --------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| `MergeDeliveryNotes`            | HTTP POST | Accepts an array of SharePoint document URLs, downloads them as PDF via Graph API, merges into a single PDF, and returns the result. |
| `GenerateDeliveryPack`          | HTTP POST | When all delivery notes for a route/date have URLs, merges them into a Delivery Pack PDF and updates Dataverse.                      |
| `GenerateDeliveryNoteDocument`  | HTTP POST | Fills the SharePoint Word template, converts to PDF via Graph, stores PDF under `Delivery notes/`, and sets `mb_url` / `mb_name`.  |

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
dotnet test
```

### GenerateDeliveryNoteDocument

`POST /api/GenerateDeliveryNoteDocument`

```json
{ "deliveryNoteId": "<guid>", "force": false }
```

- Skips with `already_has_url` when `mb_url` is set unless `force` is true.
- Template path: config `DeliveryNoteTemplatePath` (default `Templates/delivery_note_patterns_footer.docx`) on `SharePointSiteUrl`.
- Repo copy: [`templates/delivery_note_patterns_footer.docx`](../templates/delivery_note_patterns_footer.docx) (sync to each SharePoint site’s `Templates/` after edits).
- Writes PDF only under `Delivery notes/`; temporary `.docx` is deleted after Graph PDF conversion.

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

**Deploy the current branch to DEV**

1. GitHub → **Actions** → **Deploy DEV from branch**
2. **Run workflow** → choose the branch
3. Builds, tests, and deploys to the shared DEV Function App (reuses `deploy.yml`)

Optional input `skip_tests` skips unit tests (emergency only).

Pushes to `main` trigger the full pipeline:

1. **Build & Test** — compile + run unit tests
2. **Deploy to Dev** — Bicep infra + zip deploy
3. **Deploy to Test** — automatic after dev succeeds
4. **Manual Approval** — required before production
5. **Deploy to Prod** — after approval

### Post-deployment

The Function App's Managed Identity needs the **Files.Read.All** application role on Microsoft Graph to download SharePoint documents. Because this is a managed identity (not an app registration), the role must be assigned directly to the managed identity's service principal using `az rest`.

Retrieve the `managedIdentityPrincipalId` from the Bicep deployment output, then run:

```bash
# 1. Capture the managed identity's service principal ID (from Bicep outputs)
MI_SP_ID="<managedIdentityPrincipalId>"

# 2. Get Microsoft Graph's service principal ID in this tenant
GRAPH_SP_ID=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)

# 3. Assign the Files.Read.All app role to the managed identity
#    App role ID 01d4889c-1287-42c6-ac1f-5d1e02578ef6 = Files.Read.All (application)
az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${MI_SP_ID}/appRoleAssignments" \
  --headers "Content-Type=application/json" \
  --body "{
    \"principalId\": \"${MI_SP_ID}\",
    \"resourceId\": \"${GRAPH_SP_ID}\",
    \"appRoleId\": \"01d4889c-1287-42c6-ac1f-5d1e02578ef6\"
  }"
```

Alternatively, use the Azure Portal:

1. Go to **Microsoft Entra ID → Enterprise applications**.
2. Search for the managed identity by the Function App name (or by the principal ID above).
3. Select the managed identity → **Permissions → Grant admin consent** is not applicable here; instead open **Microsoft Graph** under Enterprise applications, go to **App roles**, and use **Grant permissions** — or use the [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer) to POST the app-role assignment as above.
