# QWiki Azure Deployment Guide

This guide will help you deploy the QWiki application to Azure App Service.

## Prerequisites

1. **Azure Account**: You need an active Azure subscription
2. **Azure CLI**: Install from [here](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli)
3. **.NET 9.0 SDK**: Ensure you have .NET 9.0 SDK installed
4. **GitHub Models Token**: Get your token from [GitHub Models](https://github.com/marketplace/models)

## Deployment Options

### Option 1: Quick Deployment (Recommended)

Use the PowerShell script for automated deployment:

```powershell
.\deploy-to-azure.ps1 -ResourceGroupName "qwiki-rg" -WebAppName "qwiki-app" -GitHubToken "your-github-token"
```

### Option 2: Manual Deployment

#### Step 1: Create Azure Resources

1. Login to Azure:
```bash
az login
```

2. Create a resource group:
```bash
az group create --name qwiki-rg --location "East US"
```

3. Deploy using ARM template:
```bash
az deployment group create \
  --resource-group qwiki-rg \
  --template-file azure-deploy.json \
  --parameters webAppName=qwiki-app gitHubModelsToken="your-github-token"
```

#### Step 2: Build and Deploy Application

1. Build the application:
```bash
cd QWiki
dotnet restore
dotnet build --configuration Release
dotnet publish --configuration Release --output ../publish
cd ..
```

2. Create deployment package:
```bash
# Compress the publish folder
Compress-Archive -Path "publish\*" -DestinationPath "deploy.zip"
```

3. Deploy to App Service:
```bash
az webapp deployment source config-zip \
  --resource-group qwiki-rg \
  --name your-actual-app-name \
  --src deploy.zip
```

### Option 3: GitHub Actions (CI/CD)

1. Fork or push this repository to GitHub
2. Go to your repository's Settings > Secrets and Variables > Actions
3. Add the following secrets:
   - `AZURE_WEBAPP_PUBLISH_PROFILE`: Download from Azure Portal
   - `GITHUB_MODELS_TOKEN`: Your GitHub Models API token

4. Update `.github/workflows/azure-deploy.yml`:
   - Change `AZURE_WEBAPP_NAME` to your app service name

5. Push to the `master` branch to trigger deployment

## Configuration

### Environment Variables

Set these in Azure App Service Configuration:

- `GitHubModels__Token`: Your GitHub Models API token
- `ASPNETCORE_ENVIRONMENT`: Set to "Production"

### Database

The application uses SQLite for caching, which will be stored in the app service file system. For production scenarios, consider using Azure SQL Database or Azure Database for PostgreSQL.

### File Storage

PDF and PowerPoint files are stored in the `wwwroot/Data` directory. For production:

1. Upload files via FTP/Kudu
2. Or implement Azure Blob Storage integration
3. Or use Azure Files for shared storage

## Monitoring and Troubleshooting

### Application Insights

Add Application Insights for monitoring:

```bash
az monitor app-insights component create \
  --app qwiki-insights \
  --location "East US" \
  --resource-group qwiki-rg
```

### Logs

View application logs:
```bash
az webapp log tail --name your-app-name --resource-group qwiki-rg
```

### Common Issues

1. **SQLite Database**: Ensure the connection string points to a writable location
2. **File Permissions**: Check that the app can write to the file system
3. **Memory Limits**: Monitor memory usage for vector store operations
4. **API Limits**: Monitor GitHub Models API usage

## Scaling Considerations

### App Service Plan

- **Basic/Standard**: Good for development and light production
- **Premium**: Better for production with auto-scaling
- **Isolated**: For high-security requirements

### Performance Optimization

1. **Vector Store**: Consider Azure Cognitive Search for production
2. **Caching**: Implement Redis for distributed caching
3. **CDN**: Use Azure CDN for static files
4. **Database**: Migrate to Azure SQL for better performance

## Security

### Best Practices

1. **API Keys**: Store in Azure Key Vault
2. **HTTPS**: Always enabled in App Service
3. **Authentication**: Consider Azure AD integration
4. **Network**: Use Private Endpoints for enhanced security

### Configuration

```json
{
  "GitHubModels": {
    "Token": "stored-in-key-vault"
  },
  "AllowedHosts": "your-domain.com",
  "ConnectionStrings": {
    "DefaultConnection": "azure-sql-connection-string"
  }
}
```

## Cost Optimization

### Estimated Monthly Costs

- **App Service (S1)**: ~$73/month
- **Storage**: ~$5/month
- **GitHub Models API**: Variable based on usage

### Cost Reduction Tips

1. Use **B1** or **F1** tiers for development
2. Implement auto-scaling to handle traffic spikes
3. Monitor API usage to avoid unexpected charges
4. Use Azure Cost Management for tracking

## Support

For issues:
1. Check Azure App Service logs
2. Monitor Application Insights
3. Review GitHub Models API quotas
4. Check Azure service health

## Next Steps

After deployment:
1. Configure custom domain
2. Set up SSL certificate
3. Configure backup policies
4. Set up monitoring alerts
5. Implement CI/CD pipeline
