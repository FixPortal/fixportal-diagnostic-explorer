// Retro -> Log Analytics backend infrastructure (target tenant).
// Companion to docs/retro-log-analytics-backend-spec.md (Appendix A).
//
// Stands up a dedicated Log Analytics workspace + custom table + Data Collection
// Endpoint + Data Collection Rule, and grants the DiagnosticService identity the
// ingest + query roles. Deploy into a resource group; the DiagnosticService keeps
// running locally (RetroType=mongo) until you flip it to RetroType=loganalytics.
//
// Convention mirrors the host platform's own infrastructure baseline
// (log-<app>-<envName>, workspace API 2023-09-01, PerGB2018, eastus2 via RG location).

targetScope = 'resourceGroup'

@description('Short environment suffix, lowercase alphanumeric (e.g. "dev", "exp").')
@maxLength(13)
param envName string

param location string = resourceGroup().location

@description('Object (principal) ID of the identity the DiagnosticService authenticates as (the dedicated SP for the experiment). Receives ingest + query rights.')
param diagServicePrincipalId string

@description('Principal type for the role assignments. ServicePrincipal for an SP/MI; User for a signed-in user account.')
@allowed([ 'ServicePrincipal', 'User' ])
param diagServicePrincipalType string = 'ServicePrincipal'

@description('Retention (days) for both the workspace and the Retro custom table.')
param retentionInDays int = 30

var laName     = 'log-diag-${envName}'
var dceName    = 'dce-diag-${envName}'
var dcrName    = 'dcr-diag-${envName}'
var tableName  = 'DiagRetro_CL'
var streamName = 'Custom-DiagRetro_CL'

// Built-in role definition IDs
var monitoringMetricsPublisherId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
var logAnalyticsReaderId         = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '73c42c96-874c-492b-b04d-ab87d138a893')

// Column schema shared by the table and the DCR stream declaration (keep these in sync,
// and in sync with the LogAnalyticsRetroLogger ingest mapping). No MsgId: DiagnosticMsg
// carries no app-level id, so the read path synthesises one for UI selection.
var retroColumns = [
  { name: 'TimeGenerated', type: 'datetime' } // mandatory LA column; maps from DiagnosticMsg.Date
  { name: 'Level',        type: 'int' }
  { name: 'Machine',      type: 'string' }
  { name: 'Process',      type: 'string' }
  { name: 'User',         type: 'string' }
  { name: 'Category',     type: 'string' }
  { name: 'Message',      type: 'string' } // carries trace-scope text — watch truncation (spec §10.1)
  { name: 'Environment',  type: 'string' }
]

resource la 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: laName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionInDays
  }
}

// Analytics plan = full KQL (sort / matches regex / top). Trivial cost at experiment volume.
resource retroTable 'Microsoft.OperationalInsights/workspaces/tables@2023-09-01' = {
  parent: la
  name: tableName
  properties: {
    plan: 'Analytics'
    retentionInDays: retentionInDays
    schema: {
      name: tableName
      columns: retroColumns
    }
  }
}

resource dce 'Microsoft.Insights/dataCollectionEndpoints@2023-03-11' = {
  name: dceName
  location: location
  properties: {
    networkAcls: { publicNetworkAccess: 'Enabled' }
  }
}

resource dcr 'Microsoft.Insights/dataCollectionRules@2023-03-11' = {
  name: dcrName
  location: location
  // The custom table must exist before the DCR can target it as a destination output stream.
  dependsOn: [ retroTable ]
  properties: {
    dataCollectionEndpointId: dce.id
    streamDeclarations: {
      '${streamName}': {
        columns: retroColumns
      }
    }
    destinations: {
      logAnalytics: [
        {
          name: 'diagLa'
          workspaceResourceId: la.id
        }
      ]
    }
    dataFlows: [
      {
        streams: [ streamName ]
        destinations: [ 'diagLa' ]
        transformKql: 'source'      // identity transform; incoming columns already match the table
        outputStream: streamName    // 'Custom-<TableName>' routes to the custom table
      }
    ]
  }
}

// --- RBAC for the DiagnosticService identity ---
// Ingest: Monitoring Metrics Publisher on the DCR.
resource publisherAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dcr.id, diagServicePrincipalId, monitoringMetricsPublisherId)
  scope: dcr
  properties: {
    roleDefinitionId: monitoringMetricsPublisherId
    principalId: diagServicePrincipalId
    principalType: diagServicePrincipalType
  }
}

// Query: Log Analytics Reader on the workspace.
resource readerAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(la.id, diagServicePrincipalId, logAnalyticsReaderId)
  scope: la
  properties: {
    roleDefinitionId: logAnalyticsReaderId
    principalId: diagServicePrincipalId
    principalType: diagServicePrincipalType
  }
}

// --- Outputs map 1:1 to DiagServiceSettings:LogAnalytics in Config/settings.json ---
output dceEndpoint         string = dce.properties.logsIngestion.endpoint // -> LogAnalytics:DceEndpoint
output dcrImmutableId      string = dcr.properties.immutableId            // -> LogAnalytics:DcrImmutableId
output streamName          string = streamName                            // -> LogAnalytics:StreamName
output tableName           string = tableName                             // -> LogAnalytics:TableName
output workspaceId         string = la.properties.customerId              // -> LogAnalytics:WorkspaceId (query API)
output workspaceResourceId string = la.id
