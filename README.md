# QWiki - RAG-Based AI Documentation Assistant

QWiki is a **Retrieval Augmented Generation (RAG)** based AI Assistant designed to answer documentation-related queries from developers and provide relevant wiki, brownbag sessions or knowledge base articles as references for further exploration.

## What is QWiki?

QWiki builds upon the concept of creating an intelligent documentation assistant that can:

- **Answer Process-Related Queries**: Help developers find information about internal processes, procedures, and best practices
- **Reference Documentation**: Provide relevant wiki articles and knowledge base entries as supporting references
- **Support Multiple Document Types**: Process PDFs, Word documents, PowerPoint presentations, video recordings, and wiki content
- **Video Transcription**: Transcribe MP4/MKV recordings using Azure AI Speech and provide timestamped search results
- **Enable Knowledge Discovery**: Allow users to explore documentation through natural language queries

## Architecture Overview

QWiki follows a modern **Retrieval Augmented Generation (RAG)** architecture with decoupled ingestion and UI layers:

```
                                 +-------------------+
                                 |  GitHub Models API |
                                 |  (GPT-4o-mini +   |
                                 |   embeddings)      |
                                 +--------+----------+
                                          |
              +-----------+      +--------v----------+      +------------------+
  User ------>| QWiki UI  |----->| Azure AI Search   |<-----| QWiki Worker     |
  (Browser)   | (Blazor)  |      | (Vector Store)    |      | (Ingestion)      |
              +-----------+      +-------------------+      +--------+---------+
                                                                     |
                                 +-------------------+      +--------v---------+
                                 | Azure Blob Storage|<---->| Azure Table      |
                                 | (Transcript Cache)|      | Storage (Cache)  |
                                 +-------------------+      +------------------+
                                                                     |
                                          +-------------+------------+----------+
                                          |             |            |          |
                                    +-----v---+  +-----v----+  +---v------+   |
                                    | Azure   |  | SharePoint|  | Local    |   |
                                    | DevOps  |  | (Graph    |  | Folder   |   |
                                    | Wiki    |  |  API)     |  | (Dev)    |   |
                                    +---------+  +----------+   +----------+   |
                                                                               |
                                                                 +-------------v--+
                                                                 | Azure AI Speech |
                                                                 | + FFmpeg        |
                                                                 | (Video -> Text) |
                                                                 +-----------------+
```

### Key Design Decisions

- **Decoupled Ingestion**: The Worker Service handles all document ingestion independently from the UI. This allows scaling ingestion (CPU-heavy video transcription) separately from the UI (lightweight HTTP traffic).
- **Shared Embedding Model**: Both UI and Worker use `text-embedding-3-small` (1536 dimensions) via `QWiki.Shared.EmbeddingConfig` to ensure vector compatibility.
- **Transcript Caching**: Video transcripts are cached in Azure Blob Storage immediately after Speech SDK completes, surviving crashes between transcription and vector store save.
- **Dev-Mode Toggle**: For local development, set `RunIngestionInProcess: true` to run everything in a single process.

### Data Flow

1. **Document Discovery**: Worker discovers documents from Wiki, SharePoint, and local folders
2. **Content Extraction**: Text extracted from PDFs (PdfPig), Office docs (OpenXml), videos (FFmpeg + Azure Speech SDK)
3. **Chunking & Embedding**: Content chunked (~300 words with overlap) and embedded via text-embedding-3-small
4. **Vector Storage**: Embeddings stored in Azure AI Search with metadata (filename, page/timestamp, record type, source URL)
5. **User Query**: Natural language questions entered through the Blazor chat UI
6. **Semantic Search**: Query embedded and matched against Azure AI Search using cosine similarity
7. **Response Generation**: GPT-4o-mini generates responses with citations, timestamps, and source links

### Data Sources

| Source | Document Types | Status |
|--------|---------------|--------|
| Azure DevOps Wiki | Wiki pages (Markdown) | Active |
| Local Folder | PDF, PPTX, DOCX, MP4, MKV | Active (dev/test) |
| SharePoint | Same as Local Folder | Pending admin consent for `Sites.Read.All` |

### Supported File Types

| Format | Extraction | Record Type | Features |
|--------|-----------|-------------|----------|
| PDF | PdfPig (page-by-page) | PDF | Page number citations |
| PPTX | OpenXml (slide-by-slide) | PPTX | Slide number citations |
| DOCX | OpenXml (paragraphs) | DOCX | Text chunk citations |
| MP4/MKV | FFmpeg + Azure AI Speech SDK | VIDEO | `[MM:SS]` timestamps, SharePoint links |
| Wiki | Azure DevOps REST API | WIKI | Direct wiki page links |

## Solution Structure

```
QWiki.sln
|
+-- QWiki.Shared/                        Shared models (leaf dependency)
|   +-- SemanticSearchRecord.cs          Vector record model
|   +-- EmbeddingConfig.cs               Embedding model constants (single source of truth)
|
+-- QWiki.Ingestion/                     Class library (all ingestion logic)
|   +-- IIngestionSource.cs              Interface for ingestion sources
|   +-- DataIngestor.cs                  Orchestrator: discovery -> extraction -> embedding -> storage
|   +-- AzureTableIngestionCache.cs      Persistent cache (Azure Table Storage)
|   +-- ContentExtractor.cs              PDF, PPTX, DOCX text extraction + chunking
|   +-- AudioTranscriber.cs              Video transcription (FFmpeg + Speech SDK + blob cache)
|   +-- IngestionServiceExtensions.cs    DI registration + RunIngestionAsync helper
|   +-- Sources/
|       +-- WikiIngestionSource.cs       Azure DevOps Wiki ingestion
|       +-- SharePointIngestionSource.cs SharePoint Graph API ingestion
|       +-- LocalFolderIngestionSource.cs Local file ingestion (dev/test)
|
+-- QWiki.Ingestion.Worker/             Worker Service (standalone ingestion host)
|   +-- Program.cs                       Host builder + DI setup
|   +-- IngestionWorker.cs               BackgroundService with interval/RunOnce config
|   +-- appsettings.json                 Worker-specific configuration
|
+-- QWiki/                               Blazor Server UI (slimmed down)
    +-- Program.cs                       Web app + optional dev-mode ingestion
    +-- Services/SemanticSearch.cs        Vector search against Azure AI Search
    +-- Components/Pages/Chat/           Chat UI (Chat.razor, ChatInput, ChatMessageList)
    +-- LocalData/SharePoint/            Local files for dev ingestion (gitignored)
```

### Project Dependencies

```
QWiki.Shared            (no dependencies)
    ^
    |
QWiki.Ingestion         (references: QWiki.Shared)
    ^           ^
    |           |
QWiki           QWiki.Ingestion.Worker
(references:    (references: QWiki.Ingestion, QWiki.Shared)
 QWiki.Shared,
 QWiki.Ingestion)
```

## Azure Resources

All resources are in the `qwiki-rg` resource group:

| Resource | Type | Purpose | Cost |
|----------|------|---------|------|
| `qwiki-search` | Azure AI Search | Vector store for semantic search | Free tier |
| `qwiki-speech` | Cognitive Services | Azure AI Speech SDK for video transcription | Free tier (5 hrs/month) |
| `qwikistorage` | Storage Account | Table Storage (ingestion cache) + Blob Storage (transcript cache) | ~$0.01/month |

>[!NOTE]
> Before running this project you need to configure the API keys and endpoints. See the Configuration section below.

# Configuration

## Prerequisites

QWiki uses two separate user-secrets stores — one for the UI and one for the Worker.

### QWiki UI Secrets

The UI only needs secrets for search and AI chat:

| Secret | Required | Purpose |
|--------|----------|---------|
| `GitHubModels:Token` | Yes | GitHub PAT for AI model access (GPT-4o-mini + embeddings) |
| `AzureSearch:ApiKey` | Yes | Azure AI Search admin key |

For dev-mode (with `RunIngestionInProcess: true`), the UI also needs all Worker secrets below.

### QWiki Worker Secrets

The Worker needs secrets for all ingestion sources and services:

| Secret | Required | Purpose |
|--------|----------|---------|
| `GitHubModels:Token` | Yes | GitHub PAT for embeddings (text-embedding-3-small) |
| `AzureSearch:ApiKey` | Yes | Azure AI Search admin key |
| `AzureDevOps:Pat` | Yes | Azure DevOps PAT for wiki ingestion (Wiki: Read scope) |
| `AzureSpeech:Key` | Yes | Azure AI Speech key for video transcription |
| `AzureStorage:ConnectionString` | Yes | Storage connection string (Table Storage cache + Blob transcript cache) |
| `SharePointIngestion:TenantId` | For SharePoint | Azure AD tenant ID |
| `SharePointIngestion:ClientId` | For SharePoint | Azure AD app registration client ID |
| `SharePointIngestion:ClientSecret` | For SharePoint | Azure AD app registration client secret |

### Setting Secrets

```bash
# QWiki UI
cd QWiki
dotnet user-secrets set "GitHubModels:Token" "your-github-pat"
dotnet user-secrets set "AzureSearch:ApiKey" "your-search-admin-key"

# For dev-mode, also set:
dotnet user-secrets set "AzureDevOps:Pat" "your-devops-pat"
dotnet user-secrets set "AzureSpeech:Key" "your-speech-key"
dotnet user-secrets set "AzureStorage:ConnectionString" "your-connection-string"

# QWiki Worker
cd ../QWiki.Ingestion.Worker
dotnet user-secrets set "GitHubModels:Token" "your-github-pat"
dotnet user-secrets set "AzureSearch:ApiKey" "your-search-admin-key"
dotnet user-secrets set "AzureDevOps:Pat" "your-devops-pat"
dotnet user-secrets set "AzureSpeech:Key" "your-speech-key"
dotnet user-secrets set "AzureStorage:ConnectionString" "your-connection-string"
```

Or use Visual Studio: Right-click project -> "Manage User Secrets".

## 1. GitHub Models Token Setup

GitHub Models provides free access to AI models for prototyping and development.

### Steps to Get Your GitHub Models Token:

1. **Sign in to GitHub**: Go to [GitHub.com](https://github.com) and sign in to your account
2. **Navigate to Settings**: Click on your profile picture -> Settings
3. **Access Developer Settings**: Scroll down and click "Developer settings" in the left sidebar
4. **Personal Access Tokens**: Click "Personal access tokens" -> "Tokens (classic)"
5. **Generate New Token**: Click "Generate new token" -> "Generate new token (classic)"
6. **Configure Token**:
   - **Note**: Enter a descriptive name like "QWiki GitHub Models Access"
   - **Expiration**: Choose your preferred expiration (recommend 90 days for development)
   - **Scopes**: **IMPORTANT - Leave all scopes unchecked** (no permissions needed)
7. **Generate Token**: Click "Generate token"
8. **Copy Token**: Copy the token immediately (you won't be able to see it again)

## 2. Azure DevOps PAT Token Setup

Required for wiki ingestion (Worker only).

1. Sign in to your Azure DevOps organization
2. Click profile picture -> "Personal access tokens"
3. Create new token with **Wiki: Read** scope
4. Copy the token immediately

## 3. Azure Services Setup

### Azure AI Search
Get the admin key from Azure Portal -> your search resource -> Settings -> Keys.

### Azure AI Speech
Get the key from Azure Portal -> your Cognitive Services resource -> Keys and Endpoint.

### Azure Storage
Get the connection string from Azure Portal -> your storage account -> Access keys.

## Running the Application

### Local Development (Single Process)

The recommended way to run locally — the Blazor app runs both the UI and ingestion in a single process:

1. **Configure Secrets** for the QWiki project (all secrets listed above)
2. **Ensure `RunIngestionInProcess: true`** in `QWiki/appsettings.Development.json` (default)
3. **Add Local Files** (optional): Place files in `QWiki/LocalData/SharePoint/`
4. **Run**:
   ```bash
   dotnet run --project QWiki
   ```
5. **Access**: Open `http://localhost:5123`

Ingestion runs automatically in the background. On subsequent runs, only new/modified documents are processed.

### Production (Separate Processes)

In production, the UI and Worker run independently:

```bash
# Terminal 1: Blazor UI (no ingestion)
dotnet run --project QWiki
# Set RunIngestionInProcess: false in appsettings.json

# Terminal 2: Ingestion Worker
dotnet run --project QWiki.Ingestion.Worker
```

### Worker Configuration

The Worker supports two modes via `appsettings.json`:

```json
{
  "Ingestion": {
    "RunOnce": false,
    "IntervalMinutes": 60
  }
}
```

- **Continuous** (`RunOnce: false`): Runs ingestion every N minutes (default: 60)
- **One-shot** (`RunOnce: true`): Runs once and exits. Useful for initial bulk loads.

## Deployment

### Azure Deployment

For detailed instructions on deploying QWiki to Azure, see [AZURE_DEPLOYMENT.md](AZURE_DEPLOYMENT.md).

### Container Deployment

Build and run the UI:
```bash
docker build -t qwiki -f QWiki/Dockerfile .
docker run -p 8080:8080 \
  -e GitHubModels__Token="your-token" \
  -e AzureSearch__ApiKey="your-key" \
  qwiki
```

Build and run the Worker:
```bash
docker build -t qwiki-worker -f QWiki.Ingestion.Worker/Dockerfile .
docker run \
  -e GitHubModels__Token="your-token" \
  -e AzureSearch__ApiKey="your-key" \
  -e AzureDevOps__Pat="your-pat" \
  -e AzureSpeech__Key="your-key" \
  -e AzureStorage__ConnectionString="your-connection-string" \
  qwiki-worker
```

## Glossary

### **RAG (Retrieval Augmented Generation)**
A machine learning approach that combines information retrieval with text generation. Instead of relying solely on the AI model's training data, RAG first retrieves relevant documents from a knowledge base, then uses that context to generate more accurate and factual responses.

### **Semantic Search**
A search technique that understands the meaning and context of queries rather than just matching keywords. QWiki uses Azure AI Search with 1536-dimension vector embeddings from OpenAI's text-embedding-3-small model.

### **Embeddings**
Mathematical representations of text as vectors (arrays of numbers) in high-dimensional space. Text with similar meanings is positioned close together in vector space, enabling semantic similarity search.

### **Azure AI Search**
Microsoft's cloud search service used as QWiki's vector store. Stores document embeddings and supports vector similarity search using cosine similarity.

### **Azure Table Storage**
A NoSQL key-value store used for QWiki's ingestion cache. Tracks which documents have been processed and their versions, enabling incremental ingestion that survives app redeployments.

### **Azure Blob Storage (Transcript Cache)**
Stores video transcripts as JSON blobs immediately after Azure Speech SDK completes. This prevents expensive re-transcription if the app crashes between transcription and vector store save. The cache survives restarts and redeployments.

### **Document Chunking**
The process of breaking down large documents into smaller, manageable pieces (~300 words with 50-word overlap) before converting them to embeddings. Video transcripts are chunked with `[MM:SS]` timestamp labels preserved.

### **Incremental Ingestion**
QWiki tracks document versions (file modification time for local files, content hash for wiki pages) in Azure Table Storage. On restart, only new or modified documents are re-processed, avoiding expensive re-transcription of video files.

### **Worker Service**
A .NET `BackgroundService` that runs data ingestion independently from the UI. Can be deployed as a separate container with its own scaling rules — scale up for initial bulk loads, scale to zero for maintenance mode.

## Learn More

- [GitHub Models Documentation](https://docs.github.com/github-models/prototyping-with-ai-models)
- [Azure DevOps Personal Access Tokens](https://docs.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate)
- [Azure AI Search Documentation](https://learn.microsoft.com/en-us/azure/search/)
- [Azure AI Speech SDK](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/)
- [.NET User Secrets](https://docs.microsoft.com/en-us/aspnet/core/security/app-secrets)
