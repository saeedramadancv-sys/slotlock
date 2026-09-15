// SlotLock on Azure: an App Service on the free tier, and a serverless Azure SQL database
// on the free offer, joined by a managed identity rather than a password.
//
// Deploy with infra/deploy.ps1, which fills in the Entra administrator parameters from
// whoever is signed in to the Azure CLI.

@description('Region for every resource. The Azure SQL free offer is not available everywhere; westeurope, northeurope and uaenorth all carry it.')
param location string = resourceGroup().location

@description('Prefix for resource names. Two of these become public hostnames, so a random suffix is appended to keep them globally unique.')
@minLength(3)
@maxLength(17)
param namePrefix string = 'slotlock'

@description('User Principal Name of the Entra account that will administer the SQL server.')
param sqlAdminLogin string

@description('Object ID of that same Entra account.')
param sqlAdminObjectId string

@description('Tenant the administrator belongs to.')
param tenantId string = subscription().tenantId

var suffix = uniqueString(resourceGroup().id)
var sqlServerName = '${namePrefix}-sql-${suffix}'
var siteName = '${namePrefix}-${suffix}'
var databaseName = 'SlotLock'

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'

    // Entra-only: no SQL login is ever created, so there is no administrator password to
    // store in configuration, rotate, or leak. The application authenticates as its managed
    // identity and migrations run as the developer's own account.
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: tenantId
      azureADOnlyAuthentication: true
    }
  }
}

// 0.0.0.0 is the documented sentinel for "any Azure service", which is how the web app
// reaches the database without a fixed outbound address.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    // The free offer: 100,000 vCore-seconds a month on serverless. Past that the database
    // pauses until the month rolls over rather than starting to bill.
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'

    // Serverless pauses after an hour idle. That is the whole reason this costs nothing, and
    // the price of it is a cold start of roughly half a minute on the next request - which is
    // why the application's connection timeout is raised below.
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    zoneRedundant: false
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${namePrefix}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: 'F1'
    tier: 'Free'
  }
  properties: {
    reserved: true // 'reserved' is how ARM spells Linux.
  }
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: siteName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|9.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'

      // Not available on the free tier. The app sleeps when idle and the first request after
      // that pays for the wake-up; the background workers stop while it sleeps, which is
      // acceptable here only because nothing about correctness depends on them having run.
      alwaysOn: false

      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          // No password, no secret, nothing worth stealing out of the configuration blade:
          // 'Active Directory Default' resolves to the site's managed identity at run time.
          // The long connection timeout absorbs a serverless database resuming from pause.
          name: 'ConnectionStrings__SlotLock'
          value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${databaseName};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;Authentication=Active Directory Default;'
        }
      ]
    }
  }
}

output siteName string = site.name
output siteUrl string = 'https://${site.properties.defaultHostName}'
output sitePrincipalId string = site.identity.principalId
output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = database.name
