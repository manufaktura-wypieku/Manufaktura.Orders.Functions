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
    # Without Dataverse
    .\infra\setup-github-oidc.ps1 -TenantId '9096fb11-ab2c-4e04-b90c-dc3ce88d59fb' -GitHubOrg 'manufaktura-wypieku' -GitHubRepo 'Manufaktura.Orders.Functions'

    # With Dataverse application users
    .\infra\setup-github-oidc.ps1 `
        -TenantId '9096fb11-ab2c-4e04-b90c-dc3ce88d59fb' `
        -GitHubOrg 'manufaktura-wypieku' `
        -GitHubRepo 'Manufaktura.Orders.Functions' `
        -DataverseDevUrl  'https://manufaktura-develop.crm11.dynamics.com' `
        -DataverseTestUrl 'https://manufaktura-test.crm11.dynamics.com' `
        -DataverseProdUrl 'https://manufaktura.crm11.dynamics.com'
#>

param(
    [Parameter()]
    [string]$TenantId = '9096fb11-ab2c-4e04-b90c-dc3ce88d59fb',

    [Parameter()]
    [string]$GitHubOrg = 'manufaktura-wypieku',

    [Parameter()]
    [string]$GitHubRepo = 'Manufaktura.Orders.Functions',

    # Optional: Dataverse environment URLs for creating application users.
    # When provided, the script will create an application user for the App Registration
    # in each Dataverse environment and assign the System Administrator security role.
    [Parameter()]
    [string]$DataverseDevUrl = 'https://manufaktura-develop.crm11.dynamics.com',

    [Parameter()]
    [string]$DataverseTestUrl = 'https://manufaktura-test.crm11.dynamics.com',

    [Parameter()]
    [string]$DataverseProdUrl = 'https://manufaktura.crm11.dynamics.com'
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
$AppId = az ad app list --filter "displayName eq '$AppName'" --query '[0].appId' -o tsv 2>$null
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

# Create Dataverse application users (only when URLs are supplied)
$DataverseEnvs = @(
    @{ Name = 'dev'; Url = $DataverseDevUrl }
    @{ Name = 'test'; Url = $DataverseTestUrl }
    @{ Name = 'prod'; Url = $DataverseProdUrl }
) | Where-Object { $_.Url }

if ($DataverseEnvs) {
    Write-Host ''
    Write-Host '==> Creating Dataverse application users (Function App managed identities)...'

    foreach ($Env in $DataverseEnvs) {
        $OrgUrl = $Env.Url.TrimEnd('/')
        $FuncName = "func-mfk-orders-$($Env.Name)"
        $RgName = "rg-manufaktura-orders-$($Env.Name)"
        Write-Host ''
        Write-Host "    Environment ($($Env.Name)): $OrgUrl"

        # Look up the Function App's managed identity principal ID, then resolve its app (client) ID
        $MiPrincipalId = az functionapp identity show `
            --name $FuncName --resource-group $RgName `
            --query 'principalId' -o tsv 2>$null

        if (-not $MiPrincipalId) {
            Write-Host "    Function App '$FuncName' not deployed yet — skipping. Re-run after deploying infrastructure."
            continue
        }

        $MiAppId = az ad sp show --id $MiPrincipalId --query appId -o tsv 2>$null
        if (-not $MiAppId) {
            Write-Warning "    Could not resolve appId for managed identity $MiPrincipalId — skipping."
            continue
        }
        Write-Host "    Function App managed identity (appId): $MiAppId"

        # Obtain an access token for this Dataverse org using the current az login
        $Token = (az account get-access-token --resource $OrgUrl | ConvertFrom-Json).accessToken

        $Headers = @{
            Authorization      = "Bearer $Token"
            'Content-Type'     = 'application/json; charset=utf-8'
            Accept             = 'application/json'
            'OData-MaxVersion' = '4.0'
            'OData-Version'    = '4.0'
        }

        # Check whether the application user already exists
        $ExistingUser = (Invoke-RestMethod `
                -Uri "$OrgUrl/api/data/v9.2/systemusers?`$filter=applicationid eq $MiAppId&`$select=systemuserid" `
                -Headers $Headers).value

        if ($ExistingUser.Count -gt 0) {
            $UserId = $ExistingUser[0].systemuserid
            Write-Host "    Application user already exists (skipped). User ID: $UserId"
        }
        else {
            # Bind to the root business unit
            $RootBu = (Invoke-RestMethod `
                    -Uri "$OrgUrl/api/data/v9.2/businessunits?`$filter=_parentbusinessunitid_value eq null&`$select=businessunitid" `
                    -Headers $Headers).value[0].businessunitid

            $Body = @{
                applicationid               = $MiAppId
                'businessunitid@odata.bind' = "/businessunits($RootBu)"
            } | ConvertTo-Json

            # POST and retrieve the new record via a follow-up GET
            $CreateHeaders = $Headers.Clone()
            $CreateHeaders['Prefer'] = 'return=representation'
            $NewUser = Invoke-RestMethod `
                -Uri "$OrgUrl/api/data/v9.2/systemusers" `
                -Method Post -Headers $CreateHeaders -Body $Body
            $UserId = $NewUser.systemuserid
            Write-Host "    Created application user. User ID: $UserId"
        }

        # Resolve the root-level System Administrator role
        $SysAdminRole = (Invoke-RestMethod `
                -Uri "$OrgUrl/api/data/v9.2/roles?`$filter=name eq 'System Administrator' and _parentroleid_value eq null&`$select=roleid" `
                -Headers $Headers).value
        if (-not $SysAdminRole) {
            Write-Warning "    Could not find 'System Administrator' role in $OrgUrl — skipping role assignment."
            continue
        }
        $RoleId = $SysAdminRole[0].roleid

        # Check whether the role is already assigned
        $AssignedRole = (Invoke-RestMethod `
                -Uri "$OrgUrl/api/data/v9.2/systemusers($UserId)/systemuserroles_association?`$filter=roleid eq $RoleId&`$select=roleid" `
                -Headers $Headers).value

        if ($AssignedRole.Count -gt 0) {
            Write-Host '    System Administrator role already assigned (skipped)'
        }
        else {
            $AssocBody = @{ '@odata.id' = "$OrgUrl/api/data/v9.2/roles($RoleId)" } | ConvertTo-Json
            Invoke-RestMethod `
                -Uri "$OrgUrl/api/data/v9.2/systemusers($UserId)/systemuserroles_association/`$ref" `
                -Method Post -Headers $Headers -Body $AssocBody | Out-Null
            Write-Host '    Assigned System Administrator role'
        }
    }

    Write-Host '    Done.'
}

# Grant Microsoft Graph API permissions to each Function App managed identity.
# The system-assigned managed identity only exists after main.bicep has been deployed,
# so this section gracefully skips environments where the Function App doesn't exist yet.
# Re-run this script after the first deployment to pick up the Graph permissions.
$GraphAppId = '00000003-0000-0000-c000-000000000000'
# Sites.ReadWrite.All application permission — read/write items in all site collections
$SitesReadWriteAllRoleId = '9492366f-7969-46a4-8d15-ed1a20078fff'

Write-Host ''
Write-Host '==> Granting Microsoft Graph permissions to Function App managed identities...'

$GraphSpId = az ad sp show --id $GraphAppId --query id -o tsv 2>$null
if (-not $GraphSpId) {
    Write-Warning 'Could not find Microsoft Graph service principal — skipping Graph permissions.'
}
else {
    foreach ($EnvName in @('dev', 'test', 'prod')) {
        $FuncName = "func-mfk-orders-$EnvName"
        $RgName = "rg-manufaktura-orders-$EnvName"

        Write-Host ''
        Write-Host "    $FuncName ($RgName):"

        $MiPrincipalId = az functionapp identity show `
            --name $FuncName --resource-group $RgName `
            --query principalId -o tsv 2>$null

        if (-not $MiPrincipalId) {
            Write-Host '    Function App not deployed yet — skipping. Re-run after deploying infrastructure.'
            continue
        }
        Write-Host "    Managed Identity: $MiPrincipalId"

        $AssignmentsJson = az rest --method GET `
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$MiPrincipalId/appRoleAssignments" `
            -o json 2>&1

        $AlreadyGranted = $false
        if ($LASTEXITCODE -eq 0) {
            $Assignments = $AssignmentsJson | ConvertFrom-Json
            $AlreadyGranted = $Assignments.value | Where-Object {
                $_.appRoleId -eq $SitesReadWriteAllRoleId -and $_.resourceId -eq $GraphSpId
            }
        }

        if ($AlreadyGranted) {
            Write-Host '    Sites.ReadWrite.All already granted (skipped)'
        }
        else {
            $Body = (@{
                    principalId = $MiPrincipalId
                    resourceId  = $GraphSpId
                    appRoleId   = $SitesReadWriteAllRoleId
                } | ConvertTo-Json -Compress) -replace '"', '\"'

            az rest --method POST `
                --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$MiPrincipalId/appRoleAssignments" `
                --body "$Body" `
                --headers "Content-Type=application/json" `
                --output none

            Write-Host '    Granted Sites.ReadWrite.All'
        }
    }
}

Write-Host ''
Write-Host '============================================='
Write-Host ' Setup complete!'
Write-Host '============================================='
Write-Host ''
