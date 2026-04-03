#Requires -Version 7.0
<#
.SYNOPSIS
    One-time setup: App Registration + GitHub OIDC federated credentials.

.DESCRIPTION
    Run this AFTER creating your Azure subscription.

.PREREQUISITES
    - Azure CLI installed and logged in (az login)
    - GitHub CLI installed and logged in (gh auth login)

.EXAMPLE
    .\infra\setup-github-oidc.ps1 -TenantId '9096fb11-ab2c-4e04-b90c-dc3ce88d59fb' -GitHubOrg 'manufaktura-wypieku' -GitHubRepo 'Manufaktura.Orders.Functions'
#>

param(
    [Parameter()]
    [string]$TenantId = '9096fb11-ab2c-4e04-b90c-dc3ce88d59fb',

    [Parameter()]
    [string]$GitHubOrg = 'manufaktura-wypieku',

    [Parameter()]
    [string]$GitHubRepo = 'Manufaktura.Orders.Functions'
)

$ErrorActionPreference = 'Stop'

# Configuration
$AppName = "github-$GitHubRepo"

Write-Host '==> Checking Azure CLI login...'
$SubscriptionId = az account show --query id -o tsv 2>$null
if (-not $SubscriptionId) {
    Write-Error "Not logged in. Run: az login --tenant $TenantId"
    exit 1
}
Write-Host "    Subscription: $SubscriptionId"
Write-Host "    Tenant:       $TenantId"

# Create App Registration (idempotent)
Write-Host ''
Write-Host "==> Looking up or creating App Registration: $AppName"
$AppId = az ad app list --display-name $AppName --query '[0].appId' -o tsv 2>$null
if (-not $AppId) {
    $AppId = az ad app create --display-name $AppName --query appId -o tsv
    Write-Host "    Created. App (client) ID: $AppId"
}
else {
    Write-Host "    Already exists. App (client) ID: $AppId"
}

# Create Service Principal (idempotent)
Write-Host ''
Write-Host '==> Looking up or creating Service Principal...'
$SpObjectId = az ad sp show --id $AppId --query id -o tsv 2>$null
if (-not $SpObjectId) {
    $SpObjectId = az ad sp create --id $AppId --query id -o tsv
    Write-Host "    Created. Service Principal Object ID: $SpObjectId"
}
else {
    Write-Host "    Already exists. Service Principal Object ID: $SpObjectId"
}

# Create resource groups and assign roles at RG scope (least privilege)
Write-Host ''
Write-Host '==> Creating resource groups and assigning roles (scoped per RG)...'
foreach ($EnvName in @('dev', 'test', 'prod')) {
    $RgName = "rg-manufaktura-orders-$EnvName"
    az group create --name $RgName --location uksouth `
        --tags "Environment=$EnvName" "Project=Manufaktura.Orders" --output none

    # Contributor — create/manage Azure resources
    # Role Based Access Control Administrator — create RBAC role assignments (needed by Bicep for storage RBAC)
    foreach ($RoleName in @('Contributor', 'Role Based Access Control Administrator')) {
        $ExistingRole = az role assignment list `
            --assignee $SpObjectId `
            --role $RoleName `
            --scope "/subscriptions/$SubscriptionId/resourceGroups/$RgName" `
            --query '[0].id' -o tsv 2>$null
        if (-not $ExistingRole) {
            az role assignment create `
                --assignee-object-id $SpObjectId `
                --assignee-principal-type ServicePrincipal `
                --role $RoleName `
                --scope "/subscriptions/$SubscriptionId/resourceGroups/$RgName" `
                --output none
            Write-Host "    Assigned $RoleName on: $RgName"
        }
        else {
            Write-Host "    $RoleName already assigned on: $RgName (skipped)"
        }
    }
}
Write-Host '    Done.'

# Add federated credentials for GitHub environments (dev, test, production)
$FedCreds = @(
    @{ Name = 'github-env-dev'; Subject = "repo:${GitHubOrg}/${GitHubRepo}:environment:dev" }
    @{ Name = 'github-env-test'; Subject = "repo:${GitHubOrg}/${GitHubRepo}:environment:test" }
    @{ Name = 'github-env-production'; Subject = "repo:${GitHubOrg}/${GitHubRepo}:environment:production" }
)

foreach ($Cred in $FedCreds) {
    Write-Host ''
    Write-Host "==> Adding federated credential: $($Cred.Name)..."
    $Existing = az ad app federated-credential list --id $AppId `
        --query "[?name=='$($Cred.Name)'].id" -o tsv 2>$null
    if (-not $Existing) {
        $Params = @{
            name      = $Cred.Name
            issuer    = 'https://token.actions.githubusercontent.com'
            subject   = $Cred.Subject
            audiences = @('api://AzureADTokenExchange')
        } | ConvertTo-Json -Compress

        $TempFile = [System.IO.Path]::GetTempFileName()
        $Params | Set-Content -Path $TempFile -Encoding utf8NoBOM
        az ad app federated-credential create --id $AppId --parameters "@$TempFile"
        Remove-Item $TempFile
    }
    else {
        Write-Host '    Already exists (skipped)'
    }
}

# Set GitHub secrets
Write-Host ''
Write-Host '==> Setting GitHub repository secrets...'
gh secret set AZURE_CLIENT_ID --repo "${GitHubOrg}/${GitHubRepo}" --body $AppId
gh secret set AZURE_TENANT_ID --repo "${GitHubOrg}/${GitHubRepo}" --body $TenantId
gh secret set AZURE_SUBSCRIPTION_ID --repo "${GitHubOrg}/${GitHubRepo}" --body $SubscriptionId
Write-Host '    Done.'

# Create GitHub environments
Write-Host ''
Write-Host '==> Creating GitHub environments...'
gh api --method PUT "repos/${GitHubOrg}/${GitHubRepo}/environments/dev" --silent
gh api --method PUT "repos/${GitHubOrg}/${GitHubRepo}/environments/test" --silent
gh api --method PUT "repos/${GitHubOrg}/${GitHubRepo}/environments/production" --silent
Write-Host '    Created: dev, test, production'

Write-Host ''
Write-Host '============================================='
Write-Host ' Setup complete!'
Write-Host '============================================='
Write-Host ''
