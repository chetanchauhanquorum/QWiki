# QWiki Azure Deployment Guide

This guide covers deploying the QWiki solution to Azure. QWiki consists of two independently deployable services:

- **QWiki** (Blazor Server) — The chat UI
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
              +--------v---------+
              | Azure AI Search  |
              | (Vector Store)   |
              +--------+---------+
                       ^
              +--------+---------+
              | Azure Container  |
              | Apps: Worker     |
              | (scale-to-zero)  |
              +--+-----+-----+--+
                 |     |     |
           +-----+ +---+---+ +--------+
           |Wiki | |Share- | |Blob    |
           |API  | |Point  | |Cache   |
           +-----+ +-------+ +--------+
```

- **UI**: Scales based on HTTP traffic. Minimum 1 replica.
- **Worker**: Runs on a schedule (e.g., hourly). Scales to zero between runs. Scale up temporarily for initial bulk loads.
- **Shared state**: Azure AI Search (vectors) and Azure Table Storage (ingestion cache) are accessed by both services independently.

## Prerequisites

1. **Azure Account**: Active Azure subscription
2. **Azure CLI**: Install from [here](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli)
3. **.NET 9.0 SDK**: Ensure you have .NET 9.0 SDK installed

## Azure Resources

All resources in the `qwiki-rg` resource group (East US region):

| Resource | Type | Purpose | Used By |
|----------|------|---------|---------|
| `qwiki-search` | Azure AI Search (Free) | Vector store | UI + Worker |
| `qwiki-speech` | Cognitive Services (Free) | Video transcription | Worker only |
| `qwikistorage` | Storage Account | Table Storage (cache) + Blob Storage (transcripts) | Worker only |
| Container App: UI | Azure Container Apps | Blazor Server UI | - |
| Container App: Worker | Azure Container Apps | Ingestion Worker | - |

### Creating Resources

```bash
az login

# Resource group
az group create --name qwiki-rg --location "East US"

# Azure AI Search (Free tier)
az search service create --name qwiki-search --resource-group qwiki-rg --sku free --location "East US"

# Azure AI Speech (Free tier)
az cognitiveservices account create --name qwiki-speech --resource-group qwiki-rg \
  --kind SpeechServices --sku F0 --location eastus --yes

# Storage Account (Table + Blob)
az storage account create --name qwikistorage --resource-group qwiki-rg \
  --location eastus --sku Standard_LRS
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
    ASPNETCORE_ENVIRONMENT="Production"
```

#### Deploy Worker

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
    AzureSpeech__Key="your-key" \
    AzureSpeech__Region="eastus" \
    AzureStorage__ConnectionString="your-connection-string" \
    Ingestion__IntervalMinutes="60" \
    Ingestion__RunOnce="false"
```

#### Initial Bulk Load

For the first run with many documents/videos, use one-shot mode with more resources:

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
| `ASPNETCORE_ENVIRONMENT` | Yes | Set to `Production` |
| `RunIngestionInProcess` | No | Set to `false` in production (default) |

#### QWiki Worker

| Variable | Required | Purpose |
|----------|----------|---------|
| `GitHubModels__Token` | Yes | GitHub PAT for embeddings |
| `AzureSearch__Endpoint` | Yes | Azure AI Search endpoint URL |
| `AzureSearch__ApiKey` | Yes | Azure AI Search admin key |
| `AzureDevOps__Pat` | Yes | Azure DevOps PAT (Wiki: Read) |
| `AzureSpeech__Key` | Yes | Azure AI Speech key |
| `AzureSpeech__Region` | Yes | Azure AI Speech region (e.g., `eastus`) |
| `AzureStorage__ConnectionString` | Yes | Storage connection string |
| `Ingestion__RunOnce` | No | `true` for one-shot, `false` for periodic (default) |
| `Ingestion__IntervalMinutes` | No | Minutes between runs (default: 60) |
| `SharePointIngestion__TenantId` | For SharePoint | Azure AD tenant ID |
| `SharePointIngestion__ClientId` | For SharePoint | App registration client ID |
| `SharePointIngestion__ClientSecret` | For SharePoint | App registration client secret |

## Storage Architecture

QWiki uses three Azure storage services:

1. **Azure AI Search** — Vector store for document embeddings (1536-dimension vectors). Both UI and Worker read/write to the same index (`data-qwiki-ingested`).

2. **Azure Table Storage** — Ingestion cache tracking which documents have been processed and their versions. Enables incremental ingestion: only new/modified documents are re-processed.

3. **Azure Blob Storage** — Transcript cache. Video transcripts are saved as JSON blobs immediately after Speech SDK completes. This prevents re-transcription if the Worker crashes between transcription and vector store save. The blob cache also speeds up re-ingestion (e.g., after a cache reset) by loading transcripts in seconds instead of re-transcribing for minutes.

## Monitoring and Troubleshooting

### Logs

```bash
# App Service
az webapp log tail --name qwiki-app --resource-group qwiki-rg

# Container Apps
az containerapp logs show --name qwiki-worker --resource-group qwiki-rg --follow
```

### Common Issues

1. **Video transcription fails**: Check `AzureSpeech:Key` and that the Speech resource region matches `AzureSpeech:Region`
2. **Ingestion cache errors**: Verify `AzureStorage:ConnectionString` is correct
3. **Search returns no results**: Check that `AzureSearch:ApiKey` is set and index `data-qwiki-ingested` exists
4. **Wiki ingestion fails**: Verify `AzureDevOps:Pat` has Wiki: Read scope and hasn't expired
5. **FFmpeg not found**: On first run, FFmpeg binaries are auto-downloaded. Ensure write access to the `ffmpeg/` directory.
6. **Worker deletes local folder data**: Ensure `LocalFolderIngestion:Enabled` is `false` in the Worker's config. Local folder ingestion should only run in dev-mode via the Blazor app.

## Scaling Considerations

### Video Transcription

- Azure Speech Free tier (F0): 1 concurrent session, ~1x real-time processing
- 100 one-hour videos at serial processing = ~100 hours (~4 days)
- **Transcript blob cache** provides crash recovery — if the Worker stops at video #57, it resumes at #58 using cached transcripts
- Future: upgrade to S0 tier (100 concurrent sessions) or switch to OpenAI Whisper API (~60x faster)

### Cost Estimate

| Resource | Tier | Monthly Cost |
|----------|------|-------------|
| Azure AI Search | Free | $0 |
| Azure AI Speech | Free (5 hrs/month) | $0 |
| Azure Storage (Table + Blob) | Pay-as-you-go | ~$0.01 |
| Container Apps: UI | Consumption | ~$5-15 |
| Container Apps: Worker | Consumption (scale-to-zero) | ~$1-5 |
| GitHub Models API | Free | $0 |
| **Total** | | **~$6-20/month** |
