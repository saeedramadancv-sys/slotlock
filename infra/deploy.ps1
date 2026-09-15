<#
.SYNOPSIS
    Provisions SlotLock on Azure and deploys the API to it.

.DESCRIPTION
    Everything here is free-tier: an F1 App Service plan and an Azure SQL database on the
    free serverless offer. Both sleep when idle, so the first request after a quiet spell is
    slow; neither one bills.

    The script is safe to re-run. The ARM deployment is declarative, the firewall rule is
    upserted, and a second run simply redeploys the current build.

.PARAMETER ResourceGroup
    Resource group to create or reuse.

.PARAMETER Location
    Azure region. The SQL free offer is not available in every region.

.EXAMPLE
    az login
    ./infra/deploy.ps1 -ResourceGroup slotlock-rg -Location westeurope
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = 'slotlock-rg',
    [string] $Location = 'westeurope',
    [string] $NamePrefix = 'slotlock'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

function Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

Step 'Checking the Azure CLI is signed in'
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) { throw "Not signed in. Run 'az login' first." }
Write-Host "    subscription: $($account.name)"

Step 'Reading the signed-in Entra account'
# This account becomes the SQL server's administrator, so migrations can be applied as a
# person while the application itself only ever connects as its managed identity.
$me = az ad signed-in-user show | ConvertFrom-Json
Write-Host "    admin: $($me.userPrincipalName)"

Step "Creating resource group $ResourceGroup"
az group create --name $ResourceGroup --location $Location --output none

Step 'Deploying infrastructure'
$deployment = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file (Join-Path $PSScriptRoot 'main.bicep') `
    --parameters namePrefix=$NamePrefix `
                 sqlAdminLogin=$($me.userPrincipalName) `
                 sqlAdminObjectId=$($me.id) `
    --query properties.outputs | ConvertFrom-Json

$siteName = $deployment.siteName.value
$siteUrl = $deployment.siteUrl.value
$sqlFqdn = $deployment.sqlServerFqdn.value
$sqlServer = $deployment.sqlServerName.value
$database = $deployment.databaseName.value

Step 'Opening the firewall for this machine'
# Needed only to run migrations from here. The application reaches the database through the
# 'allow Azure services' rule in the template instead.
$myIp = (Invoke-RestMethod 'https://api.ipify.org?format=json').ip
az sql server firewall-rule create `
    --resource-group $ResourceGroup --server $sqlServer `
    --name 'deploy-machine' --start-ip-address $myIp --end-ip-address $myIp `
    --output none

Step 'Applying migrations'
# The schema is applied by a human, deliberately: the application's own identity has no
# rights to change it. 'Active Directory Default' picks up the Azure CLI login above.
$migrationConnection = "Server=tcp:$sqlFqdn,1433;Initial Catalog=$database;Encrypt=True;Connection Timeout=120;Authentication=Active Directory Default;"
dotnet ef database update `
    --project (Join-Path $repoRoot 'src/SlotLock.Infrastructure') `
    --startup-project (Join-Path $repoRoot 'src/SlotLock.Api') `
    --connection $migrationConnection
if ($LASTEXITCODE -ne 0) { throw 'Migrations failed.' }

Step 'Granting the managed identity access to the database'
Write-Host "    Run infra/grant-managed-identity.sql against $database as the Entra admin," -ForegroundColor Yellow
Write-Host "    substituting $siteName for `$(AppName). The Portal's Query editor is the" -ForegroundColor Yellow
Write-Host "    quickest route; it needs nothing installed locally." -ForegroundColor Yellow

Step 'Publishing the API'
$publish = Join-Path $repoRoot 'artifacts/publish'
$zip = Join-Path $repoRoot 'artifacts/slotlock.zip'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $repoRoot 'src/SlotLock.Api') -c Release -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip

Step "Deploying to $siteName"
az webapp deploy --resource-group $ResourceGroup --name $siteName --src-path $zip --type zip --output none

Step 'Done'
Write-Host "    $siteUrl/swagger" -ForegroundColor Green
Write-Host "    $siteUrl/health/ready" -ForegroundColor Green
