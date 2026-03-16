# QWiki - Blazor Server UI

This is the Blazor Server front-end for QWiki. It provides the chat UI, authentication, admin dashboard, and semantic search against Azure AI Search.

## What This Project Does

- Authenticates users via Microsoft Entra ID (OpenID Connect)
- Serves the interactive chat UI (Blazor Server with SSR)
- Accepts natural language queries from users
- Searches Azure AI Search using hybrid search (vector embeddings + BM25 full-text)
- Generates AI responses with citations via GPT-4o-mini
- Provides source links and page numbers for referenced documents
- Persists per-user chat history and feedback in Azure Table Storage
- Admin dashboard (`/admin`) with ingestion progress, document management, and feedback overview

## What This Project Does NOT Do (in production)

In production, this project does **not** run data ingestion. Ingestion is handled by the separate `QWiki.Ingestion.Worker` service. This keeps the UI lightweight and independently scalable.

## Dev-Mode: In-Process Ingestion

For local development, set `RunIngestionInProcess: true` in `appsettings.Development.json` to run ingestion inside the Blazor process. This avoids needing to run two processes locally.

When `RunIngestionInProcess` is enabled, the app requires the full set of secrets (see root README). When disabled, only the UI secrets are needed.

## Required Secrets (UI Only)

```bash
cd QWiki
dotnet user-secrets set "GitHubModels:Token" "your-github-pat"
dotnet user-secrets set "AzureSearch:ApiKey" "your-search-admin-key"
dotnet user-secrets set "AzureStorage:ConnectionString" "your-connection-string"
dotnet user-secrets set "AzureAd:ClientSecret" "your-entra-client-secret"
```

## Running

```bash
dotnet run --project QWiki
```

Access at `http://localhost:5123` (redirects to Entra ID login).

## Project Structure

```
QWiki/
  Program.cs                    # Web app + auth + optional dev-mode ingestion
  Services/
    SemanticSearch.cs           # Hybrid search against Azure AI Search
  Components/
    Pages/Chat/                 # Chat UI (Chat, ChatInput, ChatMessageList, ChatHeader, etc.)
    Pages/Admin/Admin.razor     # Content management & ingestion progress
    Layout/ChatHistorySidebar   # Conversation history sidebar
    RedirectToLogin.razor       # Unauthenticated user redirect
    Routes.razor                # AuthorizeRouteView with auth gating
  appsettings.json              # Entra ID + endpoint configuration
  appsettings.Development.json  # Dev-mode: RunIngestionInProcess + ingestion config
```

## Dependencies

- **QWiki.Shared** — Shared models (`SemanticSearchRecord`, `EmbeddingConfig`) and services (`ChatHistoryService`, `FeedbackService`)
- **QWiki.Ingestion** — Ingestion library (only used when `RunIngestionInProcess: true`)
- **Microsoft.Identity.Web** — Entra ID authentication
