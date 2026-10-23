// Infraestrutura do Protocolo Digital no Azure.
//
//   Function App (Flex Consumption, identidade gerenciada)
//     ├─ Storage: Blob (anexos) + Table (protocolos, contadores)   ← sem chaves: acesso por RBAC
//     ├─ Service Bus: fila "triagem" + tópico "protocolos" com uma assinatura por secretaria
//     └─ Application Insights (Log Analytics) + alertas (dead-letter e falhas)
//
// Implantação: az deployment group create -g <grupo> -f infra/main.bicep -p emailAlertas=<voce@exemplo.com>

@description('Prefixo dos nomes dos recursos (3 a 11 letras minúsculas).')
@minLength(3)
@maxLength(11)
param prefixo string = 'protocolo'

param location string = resourceGroup().location

@description('Quem recebe os alertas operacionais.')
param emailAlertas string

var sufixo = uniqueString(resourceGroup().id)
var secretarias = ['Saude', 'Educacao', 'Obras', 'Fazenda', 'Assistencia', 'Ouvidoria']

// ------------------------------------------------------------------ Observabilidade
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefixo}-logs-${sufixo}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefixo}-ai-${sufixo}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

// ------------------------------------------------------------------ Storage
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${prefixo}st${sufixo}', 24)
  location: location
  sku: { name: 'Standard_ZRS' }
  kind: 'StorageV2'
  properties: {
    allowSharedKeyAccess: false // só identidade (Entra ID): não existe chave para vazar
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 30 } // anexo apagado por engano pode ser recuperado
  }
}

resource containerAnexos 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'anexos'
  properties: { publicAccess: 'None' }
}

resource containerDeploy 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'deployments'
  properties: { publicAccess: 'None' }
}

// ------------------------------------------------------------------ Service Bus
resource serviceBus 'Microsoft.ServiceBus/namespaces@2021-11-01' = {
  name: '${prefixo}-sb-${sufixo}'
  location: location
  sku: { name: 'Standard', tier: 'Standard' } // Standard: necessário para tópicos
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

resource filaTriagem 'Microsoft.ServiceBus/namespaces/queues@2021-11-01' = {
  parent: serviceBus
  name: 'triagem'
  properties: {
    requiresDuplicateDetection: true // MessageId = número do protocolo: reenvio não duplica
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    maxDeliveryCount: 5 // depois de 5 falhas, vai para a dead-letter queue
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
  }
}

resource topico 'Microsoft.ServiceBus/namespaces/topics@2021-11-01' = {
  parent: serviceBus
  name: 'protocolos'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
  }
}

// Cada secretaria recebe só os eventos dela, por filtro SQL na propriedade "Secretaria" da mensagem.
resource assinaturasSecretarias 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2021-11-01' = [for s in secretarias: {
  parent: topico
  name: toLower(s)
  properties: {
    maxDeliveryCount: 10
    deadLetteringOnFilterEvaluationExceptions: true
  }
}]

resource filtrosSecretarias 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2021-11-01' = [for (s, i) in secretarias: {
  parent: assinaturasSecretarias[i]
  name: 'apenas-${toLower(s)}'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: { sqlExpression: 'Secretaria = \'${s}\'' }
  }
}]

resource assinaturaNotificacoes 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2021-11-01' = {
  parent: topico
  name: 'notificacoes'
  properties: { maxDeliveryCount: 5 }
}

// ------------------------------------------------------------------ Function App
resource plano 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${prefixo}-plan-${sufixo}'
  location: location
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

resource funcoes 'Microsoft.Web/sites@2024-04-01' = {
  name: '${prefixo}-func-${sufixo}'
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plano.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}deployments'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 2048 }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      cors: { allowedOrigins: ['https://jvcamargo10.github.io'] }
      appSettings: [
        // Conexões por identidade: o sufixo "__" diz ao runtime para usar a Managed Identity.
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__blobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'AzureWebJobsStorage__tableServiceUri', value: storage.properties.primaryEndpoints.table }
        { name: 'AzureWebJobsStorage__queueServiceUri', value: storage.properties.primaryEndpoints.queue }
        { name: 'ServiceBus__fullyQualifiedNamespace', value: '${serviceBus.name}.servicebus.windows.net' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

// ------------------------------------------------------------------ Permissões (privilégio mínimo)
var papeis = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  serviceBusDataOwner: '090c5cfd-751d-490a-894a-3ce6f1109419'
}

resource papeisStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for papel in [papeis.storageBlobDataOwner, papeis.storageTableDataContributor, papeis.storageQueueDataContributor]: {
  name: guid(storage.id, funcoes.id, papel)
  scope: storage
  properties: {
    principalId: funcoes.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', papel)
  }
}]

resource papelServiceBus 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, funcoes.id, papeis.serviceBusDataOwner)
  scope: serviceBus
  properties: {
    principalId: funcoes.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', papeis.serviceBusDataOwner)
  }
}

// ------------------------------------------------------------------ Alertas
resource grupoAlertas 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${prefixo}-alertas'
  location: 'global'
  properties: {
    groupShortName: 'protocolo'
    enabled: true
    emailReceivers: [{ name: 'operacao', emailAddress: emailAlertas, useCommonAlertSchema: true }]
  }
}

resource alertaDeadLetter 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefixo}-mensagens-mortas'
  location: 'global'
  properties: {
    description: 'Há mensagens na dead-letter queue: um protocolo não foi triado ou notificado.'
    severity: 2
    enabled: true
    scopes: [serviceBus.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'deadletter'
          criterionType: 'StaticThresholdCriterion'
          metricName: 'DeadletteredMessages'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Maximum'
        }
      ]
    }
    actions: [{ actionGroupId: grupoAlertas.id }]
  }
}

resource alertaFalhas 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefixo}-falhas-http'
  location: 'global'
  properties: {
    description: 'Mais de 5 requisições com erro em 15 minutos.'
    severity: 2
    enabled: true
    scopes: [appInsights.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'falhas'
          criterionType: 'StaticThresholdCriterion'
          metricName: 'requests/failed'
          metricNamespace: 'microsoft.insights/components'
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Count'
        }
      ]
    }
    actions: [{ actionGroupId: grupoAlertas.id }]
  }
}

output functionAppUrl string = 'https://${funcoes.properties.defaultHostName}/api'
output serviceBusNamespace string = serviceBus.name
