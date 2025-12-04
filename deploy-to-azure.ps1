# PowerShell script to deploy QWiki to Azure
param(
    [Parameter(Mandatory=$true)]
    [string]$ResourceGroupName,
    
    [Parameter(Mandatory=$true)]
    [string]$WebAppName,
    
    [Parameter(Mandatory=$true)]
    [string]$GitHubToken,
    
    [Parameter(Mandatory=$false)]
    [string]$Location = "East US",
    
    [Parameter(Mandatory=$false)]
    [string]$Sku = "S1"
)

Write-Host "Starting Azure deployment for QWiki application..." -ForegroundColor Green

# Check if Azure CLI is installed
try {
    az --version | Out-Null
} catch {
    Write-Error "Azure CLI is not installed. Please install it first: https://docs.microsoft.com/en-us/cli/azure/install-azure-cli"
    exit 1
}

# Login to Azure (if not already logged in)
Write-Host "Checking Azure login status..." -ForegroundColor Yellow
$loginStatus = az account show 2>$null
if (-not $loginStatus) {
    Write-Host "Please login to Azure..." -ForegroundColor Yellow
    az login
}

# Create resource group if it doesn't exist
Write-Host "Creating/verifying resource group: $ResourceGroupName" -ForegroundColor Yellow
az group create --name $ResourceGroupName --location $Location

# Deploy using ARM template
Write-Host "Deploying Azure resources..." -ForegroundColor Yellow
$deploymentResult = az deployment group create `
    --resource-group $ResourceGroupName `
    --template-file "azure-deploy.json" `
    --parameters webAppName=$WebAppName location=$Location sku=$Sku gitHubModelsToken=$GitHubToken `
    --query 'properties.outputs' `
    --output json | ConvertFrom-Json

if ($LASTEXITCODE -eq 0) {
    $webAppUrl = $deploymentResult.webAppUrl.value
    $actualWebAppName = $deploymentResult.webAppName.value
    
    Write-Host "Azure resources deployed successfully!" -ForegroundColor Green
    Write-Host "Web App Name: $actualWebAppName" -ForegroundColor Cyan
    Write-Host "Web App URL: $webAppUrl" -ForegroundColor Cyan
    
    # Build and publish the application
    Write-Host "Building and publishing the application..." -ForegroundColor Yellow
    Set-Location "QWiki"
    
    dotnet restore
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to restore packages"
        exit 1
    }
    
    dotnet build --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build application"
        exit 1
    }
    
    dotnet publish --configuration Release --output "../publish"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish application"
        exit 1
    }
    
    Set-Location ".."
    
    # Deploy to Azure App Service
    Write-Host "Deploying application to Azure App Service..." -ForegroundColor Yellow
    az webapp deployment source config-zip `
        --resource-group $ResourceGroupName `
        --name $actualWebAppName `
        --src (Compress-Archive -Path "publish\*" -DestinationPath "deploy.zip" -Force -PassThru).FullName
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Application deployed successfully!" -ForegroundColor Green
        Write-Host "Your application is available at: $webAppUrl" -ForegroundColor Cyan
        Write-Host "" -ForegroundColor White
        Write-Host "Next steps:" -ForegroundColor Yellow
        Write-Host "1. Configure your GitHub Models token in the App Service configuration" -ForegroundColor White
        Write-Host "2. Upload your PDF and PowerPoint files to the Data directory" -ForegroundColor White
        Write-Host "3. Monitor the application logs for any issues" -ForegroundColor White
    } else {
        Write-Error "Failed to deploy application to Azure App Service"
        exit 1
    }
    
    # Clean up
    Remove-Item "deploy.zip" -ErrorAction SilentlyContinue
    Remove-Item "publish" -Recurse -ErrorAction SilentlyContinue
    
} else {
    Write-Error "Failed to deploy Azure resources"
    exit 1
}

Write-Host "Deployment completed!" -ForegroundColor Green
