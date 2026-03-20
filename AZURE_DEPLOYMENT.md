# QWiki Azure Deployment Guide

This guide covers deploying the QWiki solution to Azure. QWiki consists of two independently deployable services:

- **QWiki** (Blazor Server) — The chat UI with authentication
- **QWiki.Ingestion.Worker** — The data ingestion service

## Architecture: Production Deployment

```
                    Internet
                       |
              +--------v---------+
              | Azure Container  |
              | Apps: QWiki UI   |
              | (scales on HTTP) |
              +--------+---------+
                       |
         +-------------+-------------+
         |                           |
+--------v---------+       +--------v---------+
| Azure AI Search  |       | Azure Table      |
| (Vector Store)   |       | Storage          |
+--------+---------+       | - IngestionCache |
         ^                 | - ChatHistory    |
+--------+---------+       | - Feedback       |
| Azure Container  |       +--------+---------+
| Apps: Worker     |                ^
| (scale-to-zero)  |                |
+--+-----------+---+----------------+
   |           |
+--v---+  +---v----+
|Wiki  |  |Share-  |
|API   |  |Point   |
+------+  +--------+
```

- **UI**: Scales based on HTTP traffic. Minimum 1 replica. Requires Entra ID authentication.
- **Worker**: Runs on a schedule (e.g., hourly). Scales to zero between runs. Scale up temporarily for initial bulk loads.
- **Shared state**: Azure AI Search (vectors) and Azure Table Storage (ingestion cache) are accessed by both services independently. Chat history and feedback tables are UI-only.

## Prerequisites

1. **Azure Account**: Active Azure subscription
2. **Azure CLI**: Install from [here](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli)
3. **.NET 9.0 SDK**: Ensure you have .NET 9.0 SDK installed
4. **Entra ID App Registration**: Required for authentication (see below)

### Entra ID App Registration

Before deploying, create an app registration:

```bash
az ad app create \
  --display-name "QWiki" \
  --web-redirect-uris "https://your-app-url/signin-oidc" "http://localhost:5123/signin-oidc" \
  --sign-in-audience AzureADMyOrg
```

Then create a client secret and note the Client ID, Tenant ID, and your admin Object ID.

## Azure Resources

All resources in the `qwiki-rg` resource group (East US region):

| Resource | Type | Purpose | Used By |
|----------|------|---------|---------|
| `qwiki-search` | Azure AI Search (Free) | Vector store | UI + Worker |
| `qwikistorage` | Storage Account | Table Storage (cache, chat history, feedback, progress) + Blob Storage (transcript cache) | UI + Worker |
| `qwiki-speech` | Azure Speech Service (S0) | Video transcription (speech-to-text) | Worker |
| `qwikiacr` | Azure Container Registry (Basic) | Docker images for Worker | Worker |
| `qwiki-app` | App Service (B1 Linux) | Blazor Server UI | - |
| `qwiki-worker` | Container App | Ingestion Worker (scale-to-zero) | - |

### Creating Resources

```bash
az login

# Resource group
az group create --name qwiki-rg --location "East US"

# Azure AI Search (Free tier)
az search service create --name qwiki-search --resource-group qwiki-rg --sku free --location "East US"

# Storage Account (Table Storage + Blob Storage)
az storage account create --name qwikistorage --resource-group qwiki-rg \
  --location eastus --sku Standard_LRS

# Azure Speech Service (for video transcription)
az cognitiveservices account create --name qwiki-speech --resource-group qwiki-rg \
  --kind SpeechServices --sku S0 --location eastus --yes

# Container Registry (for Worker Docker images)
az acr create --name qwikiacr --resource-group qwiki-rg --sku Basic --admin-enabled true
```

## Deployment Options

### Option 1: Azure Container Apps (Recommended)

#### Build Container Images

```bash
# UI
docker build -t qwiki-ui -f QWiki/Dockerfile .

# Worker
docker build -t qwiki-worker -f QWiki.Ingestion.Worker/Dockerfile .
```

#### Deploy UI

```bash
az containerapp create \
  --name qwiki-ui \
  --resource-group qwiki-rg \
  --image qwiki-ui \
  --target-port 8080 \
  --ingress external \
  --min-replicas 1 \
  --max-replicas 3 \
  --env-vars \
    GitHubModels__Token="your-token" \
    AzureSearch__Endpoint="https://qwiki-search.search.windows.net" \
    AzureSearch__ApiKey="your-key" \
    AzureStorage__ConnectionString="your-connection-string" \
    AzureAd__Instance="https://login.microsoftonline.com/" \
    AzureAd__TenantId="your-tenant-id" \
    AzureAd__ClientId="your-client-id" \
    AzureAd__ClientSecret="your-client-secret" \
    AdminSettings__AdminObjectId="your-admin-object-id" \
    ASPNETCORE_ENVIRONMENT="Production"
```

#### Deploy Worker

> The Worker Dockerfile installs FFmpeg for video audio extraction.

```bash
az containerapp create \
  --name qwiki-worker \
  --resource-group qwiki-rg \
  --image qwiki-worker \
  --min-replicas 0 \
  --max-replicas 1 \
  --env-vars \
    GitHubModels__Token="your-token" \
    AzureSearch__Endpoint="https://qwiki-search.search.windows.net" \
    AzureSearch__ApiKey="your-key" \
    AzureDevOps__Pat="your-pat" \
    AzureStorage__ConnectionString="your-connection-string" \
    AzureSpeech__Key="your-speech-key" \
    AzureSpeech__Region="eastus" \
    WikiIngestion__RootPaths__0="Maintenance" \
    SharePointIngestion__TenantId="your-tenant-id" \
    SharePointIngestion__ClientId="your-client-id" \
    SharePointIngestion__ClientSecret="your-client-secret" \
    Ingestion__IntervalMinutes="60" \
    Ingestion__RunOnce="false"
```

#### Initial Bulk Load

For the first run with many documents, use one-shot mode with more resources:

```bash
az containerapp update \
  --name qwiki-worker \
  --resource-group qwiki-rg \
  --min-replicas 1 \
  --cpu 2.0 --memory 4Gi \
  --set-env-vars Ingestion__RunOnce="true"
```

After the bulk load completes, switch back to periodic mode:

```bash
az containerapp update \
  --name qwiki-worker \
  --resource-group qwiki-rg \
  --min-replicas 0 \
  --cpu 0.5 --memory 1Gi \
  --set-env-vars Ingestion__RunOnce="false" Ingestion__IntervalMinutes="60"
```

### Option 2: Azure App Service (UI) + Container Apps (Worker)

Deploy the UI to App Service for simpler management, and the Worker to Container Apps for scale-to-zero:

```bash
# App Service for UI
az appservice plan create --name qwiki-plan --resource-group qwiki-rg --sku B1 --is-linux
az webapp create --name qwiki-app --resource-group qwiki-rg \
  --plan qwiki-plan --runtime "DOTNETCORE:9.0"

# Publish and deploy
dotnet publish QWiki/QWiki.csproj --configuration Release --output publish
cd publish && zip -r ../deploy.zip . && cd ..
az webapp deployment source config-zip --resource-group qwiki-rg --name qwiki-app --src deploy.zip
```

### Option 3: Manual Deployment (Both Services)

```bash
# Build both
dotnet publish QWiki/QWiki.csproj -c Release -o publish-ui
dotnet publish QWiki.Ingestion.Worker/QWiki.Ingestion.Worker.csproj -c Release -o publish-worker

# Deploy UI
# (copy publish-ui to your hosting environment)

# Deploy Worker
# (copy publish-worker to your hosting environment or run as a Windows/Linux service)
```

## Configuration

### Environment Variables

> Use double underscores (`__`) instead of colons (`:`) for nested keys in environment variables.

#### QWiki UI

| Variable | Required | Purpose |
|----------|----------|---------|
| `GitHubModels__Token` | Yes | GitHub PAT (no scopes needed) |
| `AzureSearch__Endpoint` | Yes | Azure AI Search endpoint URL |
| `AzureSearch__ApiKey` | Yes | Azure AI Search admin key |
| `AzureStorage__ConnectionString` | Yes | Storage connection string (chat history, feedback, admin) |
| `AzureAd__Instance` | Yes | `https://login.microsoftonline.com/` |
| `AzureAd__TenantId` | Yes | Entra ID tenant ID |
| `AzureAd__ClientId` | Yes | Entra ID app registration client ID |
| `AzureAd__ClientSecret` | Yes | Entra ID app registration client secret |
| `AdminSettings__AdminObjectId` | Yes | Entra Object ID for admin access |
| `ASPNETCORE_ENVIRONMENT` | Yes | Set to `Production` |
| `RunIngestionInProcess` | No | Set to `false` in production (default) |

#### QWiki Worker

| Variable | Required | Purpose |
|----------|----------|---------|
| `GitHubModels__Token` | Yes | GitHub PAT for embeddings |
| `AzureSearch__Endpoint` | Yes | Azure AI Search endpoint URL |
| `AzureSearch__ApiKey` | Yes | Azure AI Search admin key |
| `AzureDevOps__Pat` | Yes | Azure DevOps PAT (Wiki: Read) |
| `AzureStorage__ConnectionString` | Yes | Storage connection string (ingestion cache) |
| `Ingestion__RunOnce` | No | `true` for one-shot, `false` for periodic (default) |
| `Ingestion__IntervalMinutes` | No | Minutes between runs (default: 60) |
| `SharePointIngestion__TenantId` | For SharePoint | Azure AD tenant ID |
| `SharePointIngestion__ClientId` | For SharePoint | App registration client ID |
| `SharePointIngestion__ClientSecret` | For SharePoint | App registration client secret |
| `AzureSpeech__Key` | For video transcription | Azure Speech API key |
| `AzureSpeech__Region` | For video transcription | Azure Speech region (e.g., `eastus`) |
| `WikiIngestion__RootPaths__0` | For wiki | Wiki root path to ingest (e.g., `Maintenance`) |

## Storage Architecture

QWiki uses two Azure storage services:

1. **Azure AI Search** — Vector store for document embeddings (1536-dimension vectors). Both UI and Worker read/write to the same index (`data-qwiki-ingested`). Supports hybrid search (vector similarity + BM25 full-text).

2. **Azure Table Storage** — Four tables:
   - **IngestionCache**: Tracks which documents have been processed and their versions. Enables incremental ingestion: only new/modified documents are re-processed. Used by both Worker and UI (admin page).
   - **ChatHistory**: Per-user conversation history, partitioned by Entra Object ID (`Chat-{userId}`). UI-only.
   - **Feedback**: User feedback (thumbs up/down) with associated queries and responses. UI-only.
   - **IngestionProgress**: Cross-process progress state, enabling the Admin UI to show live Worker progress.

3. **Azure Blob Storage** — One container:
   - **transcript-cache**: Cached video transcriptions as JSON files, keyed by document ID + version. Avoids re-transcribing unchanged videos on subsequent ingestion cycles.

## CI/CD with GitHub Actions

Both services have automated deployment workflows that trigger on push to `master` or via manual dispatch.

### UI Workflow (`.github/workflows/azure-deploy.yml`)

Deploys the Blazor Server app to Azure App Service:

1. Restores, builds, and publishes the QWiki project
2. Deploys to App Service using a publish profile

**Required GitHub secret:**
- `AZURE_WEBAPP_PUBLISH_PROFILE` — Download from Azure Portal → `qwiki-app` → Overview → "Download publish profile". Paste the full XML as the secret value.

### Worker Workflow (`.github/workflows/azure-deploy-worker.yml`)

Builds a Docker image and deploys to Azure Container Apps:

1. Logs into Azure using a service principal
2. Builds the Docker image remotely on ACR (`az acr build`) — tags with commit SHA for traceability
3. Updates the Container App to use the new image

**Required GitHub secret:**
- `AZURE_CREDENTIALS` — A JSON service principal credential (see setup below)

### One-Time Setup: Create Service Principal

Run this once to create the credentials for the Worker workflow:

```bash
# Get your subscription ID
az account show --query id -o tsv

# Create service principal with Contributor role on qwiki-rg
az ad sp create-for-rbac \
  --name "qwiki-github-actions" \
  --role contributor \
  --scopes /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/qwiki-rg \
  --sdk-auth
```

Copy the entire JSON output and save it as GitHub secret `AZURE_CREDENTIALS`:
- GitHub repo → Settings → Secrets and variables → Actions → New repository secret
- Name: `AZURE_CREDENTIALS`
- Value: the full JSON output

### Manual Trigger

Both workflows support `workflow_dispatch` — you can trigger them manually from the GitHub Actions tab without pushing a commit. Useful for redeployments or rollbacks.

### Summary

| Workflow | Service | Target | Auth Secret |
|----------|---------|--------|-------------|
| `azure-deploy.yml` | UI (Blazor) | App Service | `AZURE_WEBAPP_PUBLISH_PROFILE` |
| `azure-deploy-worker.yml` | Worker (Ingestion) | Container Apps via ACR | `AZURE_CREDENTIALS` |

## Monitoring and Troubleshooting

### Logs

```bash
# App Service
az webapp log tail --name qwiki-app --resource-group qwiki-rg

# Container Apps
az containerapp logs show --name qwiki-worker --resource-group qwiki-rg --follow
```

### Common Issues

1. **Ingestion cache errors**: Verify `AzureStorage:ConnectionString` is correct
2. **Search returns no results**: Check that `AzureSearch:ApiKey` is set and index `data-qwiki-ingested` exists
3. **Wiki ingestion fails**: Verify `AzureDevOps:Pat` has Wiki: Read scope and hasn't expired
4. **Authentication redirect loop**: Verify `AzureAd` settings (TenantId, ClientId, ClientSecret) and that the redirect URI matches the app registration
5. **Admin page access denied**: Confirm `AdminSettings:AdminObjectId` matches your Entra Object ID (`az ad signed-in-user show --query id`)

## Scaling Considerations

### Cost Estimate

| Resource | Tier | Monthly Cost |
|----------|------|-------------|
| Azure AI Search | Free | $0 |
| Azure Storage (Table + Blob) | Pay-as-you-go | ~$0.01 |
| App Service: UI | B1 Linux | ~$13 |
| Container Apps: Worker | Consumption (scale-to-zero) | ~$1-5 |
| Container Registry | Basic | ~$5 |
| Azure Speech Service | S0 (pay-per-use) | ~$1-10 (depends on video hours) |
| GitHub Models API | Free | $0 |
| **Total** | | **~$20-33/month** |
