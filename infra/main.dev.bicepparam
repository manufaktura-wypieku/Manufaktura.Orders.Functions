using './main.bicep'

param environmentName = 'dev'
param location = 'uksouth'
param maximumInstanceCount = 10
param dataverseUrl = 'https://<org>.crm.dynamics.com'
param sharePointSiteUrl = 'https://<tenant>.sharepoint.com/sites/<site-name>'
