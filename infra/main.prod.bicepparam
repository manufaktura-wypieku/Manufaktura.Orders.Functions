using './main.bicep'

param environmentName = 'prod'
param location = 'uksouth'
param maximumInstanceCount = 100
param dataverseUrl = 'https://<org>.crm.dynamics.com'
param sharePointSiteUrl = 'https://<tenant>.sharepoint.com/sites/<site-name>'
