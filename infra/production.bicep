targetScope = 'subscription'

@description('The Azure region for the website stack.')
param location string

param resourceGroupName string
param appServicePlanName string
param logAnalyticsName string
param applicationInsightsName string
param webAppName string
param keyVaultName string
param deploymentIdentityName string
param oauthClientId string
param oauthCallbackUrl string

@description('The only branch allowed to exchange GitHub Actions OIDC tokens.')
param githubBranch string = 'main'

var tags = {
  Application: 'JitHub'
  Environment: 'Production'
  Owner: 'JitHubApp'
  Repository: 'JitHubApp/JitHubV2'
}

resource productionGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module productionStack './production-stack.bicep' = {
  name: 'jithub-production-stack'
  scope: productionGroup
  params: {
    location: location
    tags: tags
    githubBranch: githubBranch
    appServicePlanName: appServicePlanName
    logAnalyticsName: logAnalyticsName
    applicationInsightsName: applicationInsightsName
    webAppName: webAppName
    keyVaultName: keyVaultName
    deploymentIdentityName: deploymentIdentityName
    oauthClientId: oauthClientId
    oauthCallbackUrl: oauthCallbackUrl
  }
}

output webAppName string = productionStack.outputs.webAppName
output webAppResourceId string = productionStack.outputs.webAppResourceId
output keyVaultName string = productionStack.outputs.keyVaultName
output deploymentClientId string = productionStack.outputs.deploymentClientId
