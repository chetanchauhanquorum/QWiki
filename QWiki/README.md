# QWiki - Blazor Server UI

This is the Blazor Server front-end for QWiki. It provides the chat UI and semantic search against Azure AI Search.

## What This Project Does

- Serves the interactive chat UI (Blazor Server with SSR)
- Accepts natural language queries from users
- Searches Azure AI Search using vector embeddings (cosine similarity)
- Generates AI responses with citations via GPT-4o-mini
- Provides source links, page numbers, and video timestamps

## What This Project Does NOT Do (in production)

In production, this project does **not** run data ingestion. Ingestion is handled by the separate `QWiki.Ingestion.Worker` service. This keeps the UI lightweight and independently scalable.

## Dev-Mode: In-Process Ingestion

For local development, set `RunIngestionInProcess: true` in `appsettings.Development.json` to run ingestion inside the Blazor process. This avoids needing to run two processes locally.

When `RunIngestionInProcess` is enabled, the app requires the full set of secrets (see root README). When disabled, only `GitHubModels:Token` and `AzureSearch:ApiKey` are needed.

## Required Secrets (UI Only)

```bash
cd QWiki
dotnet user-secrets set "GitHubModels:Token" "your-github-pat"
dotnet user-secrets set "AzureSearch:ApiKey" "your-search-admin-key"
```

## Running

```bash
dotnet run --project QWiki
```

Access at `http://localhost:5123`.

## Project Structure

```
QWiki/
  Program.cs                    # Web app setup + optional dev-mode ingestion
  Services/
    SemanticSearch.cs           # Vector search against Azure AI Search
  Components/
    Pages/Chat/                 # Chat UI (Chat.razor, ChatInput, ChatMessageList)
  LocalData/SharePoint/         # Local files for dev ingestion (gitignored)
  appsettings.json              # Endpoint configuration
  appsettings.Development.json  # Dev-mode: RunIngestionInProcess + ingestion config
```

## Dependencies

- **QWiki.Shared** — Shared models (`SemanticSearchRecord`, `EmbeddingConfig`)
- **QWiki.Ingestion** — Ingestion library (only used when `RunIngestionInProcess: true`)
