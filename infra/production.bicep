targetScope = 'subscription'

@description('The production Azure region.')
param location string = 'centralus'

@description('The only branch allowed to exchange GitHub Actions OIDC tokens.')
param githubBranch string = 'main'

var tags = {
  Application: 'JitHub'
  Environment: 'Production'
  Owner: 'JitHubApp'
  Repository: 'JitHubApp/JitHubV2'
}

resource productionGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-jithub-prod-centralus'
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
  }
}

output webAppName string = productionStack.outputs.webAppName
output webAppResourceId string = productionStack.outputs.webAppResourceId
output keyVaultName string = productionStack.outputs.keyVaultName
output deploymentClientId string = productionStack.outputs.deploymentClientId
