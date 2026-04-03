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

| Function | Trigger | Description |
|---|---|---|
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
dotnet test
```

## Deployment

### First-time setup

Run the OIDC setup script to create the App Registration, federated credentials, and GitHub secrets:

```powershell
.\infra\setup-github-oidc.ps1
```

### CI/CD

Pushes to `main` trigger the full pipeline:

1. **Build & Test** — compile + run unit tests
2. **Deploy to Dev** — Bicep infra + zip deploy
3. **Deploy to Test** — automatic after dev succeeds
4. **Manual Approval** — required before production
5. **Deploy to Prod** — after approval

### Post-deployment

The Function App's Managed Identity needs **Files.Read.All** application permission on Microsoft Graph to download SharePoint documents. Grant this via Azure AD after the first deployment:

```bash
# Get the managed identity principal ID from Bicep outputs, then:
az ad app permission admin-consent --id <app-id>
```
