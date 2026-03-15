# QAI & QWiki — Comprehensive Research Reference

**Date:** March 5, 2026
**Author:** Research compiled via Azure DevOps wiki analysis, codebase exploration, and sprint note review

---

## Table of Contents

1. [QAI Overview](#1-qai-overview)
2. [QAI Architecture](#2-qai-architecture)
3. [Knowledge Base](#3-knowledge-base)
4. [Data Pipeline — Extraction, Conversion & Indexing](#4-data-pipeline)
5. [Azure Infrastructure](#5-azure-infrastructure)
6. [GitHub Repositories](#6-github-repositories)
7. [QAI Integration in QFC Web](#7-qai-integration-in-qfc-web)
8. [Product Deployments & Status](#8-product-deployments--status)
9. [QPTM Timeline](#9-qptm-timeline)
10. [QWiki Project Overview](#10-qwiki-project-overview)
11. [QAI vs QWiki Comparison](#11-qai-vs-qwiki-comparison)
12. [Opportunities & Ideas](#12-opportunities--ideas)

---

## 1. QAI Overview

**QAI** (Q Assistant / QAssistant) is Quorum's **production conversational AI platform** designed to enhance user interactions within Quorum product applications. It is built and maintained by the **UX Team / QAI-GCC team**.

**Purpose:** Provide intelligent, context-aware answers to user questions by leveraging regularly updated product documentation through Azure-based AI services.

**Key Capabilities:**
- Conversational AI chat interface embedded in Quorum products
- RAG (Retrieval Augmented Generation) for contextually accurate responses
- Multi-product support with per-product knowledge segmentation
- Automatic feedback and support case logging to Salesforce
- Analytics and telemetry for continuous improvement
- Available in web products (MyQuorum, On Demand suite) and Microsoft Teams

**Source:** [QAI Overview Wiki](https://quorumsoftware.visualstudio.com/QuorumSoftware/_wiki/wikis/QuorumSoftware.wiki/8629/QAI-Overview)

---

## 2. QAI Architecture

### Component Diagram

```
User (in Quorum product)
  |
  v
Frontend Web Components (TypeScript, Azure CDN)
  |  - <q-assistant> custom web component
  |  - Bot Framework WebChat
  |  - jsPanel (movable/resizable chat)
  |  - Contextual setup (user, email, role, app context)
  |
  v
Microsoft Copilot Studio (Backend Management)
  |  - Session handling & conversation flows
  |  - Secure API routing
  |  - Power Automate integration
  |
  +---> Azure OpenAI "On Your Data" API
  |       - GPT-4o-mini (chat responses)
  |       - text-embedding-ada-002 / text-embedding-3 (document indexing)
  |       - RAG for contextual responses
  |       - Automatic daily indexing
  |
  +---> Azure AI Search
  |       - Vector + semantic + hybrid search
  |       - HNSW algorithm (cosine similarity)
  |       - Per-product indexes (qai-index-odl, qai-index-qptm, etc.)
  |
  +---> Azure Blob Storage (stassistantprod)
  |       - Centralized markdown documentation
  |       - Structured containers per product
  |
  +---> Azure Key Vault (qai-kv-prod)
  |       - API keys, AI prompts, configuration
  |
  +---> Power Automate --> Salesforce
  |       - Automatic feedback logging
  |       - Support case creation
  |
  +---> Azure Application Insights
          - User interaction monitoring
          - Response time analytics
          - Engagement dashboards
```

### Architecture Components Summary

| Component | Technology | Purpose |
|-----------|-----------|---------|
| Frontend | TypeScript web components + Bot Framework WebChat + jsPanel | Chat UI embedded in products |
| Conversation Engine | Microsoft Copilot Studio | Session management, conversation flows, routing |
| AI Model (Chat) | Azure OpenAI GPT-4o-mini | Response generation |
| AI Model (Embeddings) | text-embedding-ada-002 / text-embedding-3 (1536 dims) | Document vectorization |
| Search | Azure AI Search | Vector + semantic + hybrid search |
| Document Storage | Azure Blob Storage | Markdown docs with YAML frontmatter |
| Secrets | Azure Key Vault | API keys, prompts, config |
| Analytics | Azure Application Insights | Monitoring, dashboards, engagement |
| Support | Power Automate + Salesforce | Feedback & case management |
| Deployment | GitHub Actions | CI/CD for frontend and knowledge sync |

---

## 3. Knowledge Base

### Two Separate Knowledge Bases

| Repository | Purpose | Content Type |
|-----------|---------|-------------|
| `qai-knowledge-base` (GitHub, Quorum-AI org) | External (customer-facing) | Product documentation, Salesforce KB articles |
| `qai-knowledge-base-internal` (GitHub, Quorum-AI org) | Internal (Quorum staff only) | Audible/brownbag transcripts, internal guides |

**Knowledge Repo URL:** `https://github.com/Quorum-AI/qai-knowledge-base`

### Content Sources

| Source | Content Type | Extraction Method |
|--------|-------------|-------------------|
| Salesforce KB | Support articles, how-to guides | `qai-salesforce-extract` tool (automated cron) |
| ProProfs | Knowledge base articles (QPTM) | `qai-content-conversion-tools` |
| HTML web content | Web-based product docs | `qai-content-conversion-tools` (HTML to MD) |
| Word documents | .docx product docs | `qai-content-conversion-tools` |
| Excel spreadsheets | .xlsx reference data | `qai-content-conversion-tools` |
| PowerPoint | .pptx training materials | `qai-content-conversion-tools` |
| Audible/Brownbags | Internal training recordings | Manual capture/transcription |
| Microsoft Teams | Meeting content | Teams scraping tools (under investigation) |

### Document Format Standard

All knowledge base documents use **Markdown with YAML frontmatter**:

```markdown
---
title: "How to Configure Pipeline Nominations"
product: "QPTM"
sp_product: "pipeline"
role: "administrator"
region:
  - "NAM"
  - "EMEA"
---

# How to Configure Pipeline Nominations

Article content in standard markdown...
```

**Frontmatter Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `title` | string | Human-readable title (stored as `fm_title` in index) |
| `product` | string | Product code (QPTM, ODL, ODA, ODP, ODW, DD, Execute) |
| `sp_product` | string | Sub-product within a product |
| `role` | string | Target audience role (administrator, user, etc.) |
| `region` | string[] | Geographic regions (NAM, EMEA, etc.) |

The team created **"Shared Markdown Standards"** documentation to ensure consistency across all product teams.

---

## 4. Data Pipeline

### End-to-End Flow

```
STAGE 1: EXTRACTION & CONVERSION
  Salesforce KB  --> qai-salesforce-extract  --> markdown + frontmatter
  ProProfs       --> qai-content-conversion  --> markdown + frontmatter
  HTML/Word/PPT  --> qai-content-conversion  --> markdown + frontmatter
  Audible/Brown  --> Manual capture          --> markdown + frontmatter
            |
            v
STAGE 2: PR REVIEW (GitHub)
  Converted files --> PR to qai-knowledge-base repo
                  --> Product team reviews accuracy
                  --> QAI team reviews frontmatter + PII
                  --> Merge to main
            |
            v
STAGE 3: AUTOMATED SYNC (GitHub Actions)
  On merge:  sync.yml --> Azure Blob Storage (stassistantprod)
  On PR:     pr-image-optimization.yml --> compress images
  Config:    sync-config.json (maps folders to containers)
            |
            v
STAGE 4: INDEXING (Azure AI Search, daily P1D)
  Blob Storage --> Indexer --> Skillset Pipeline:
    Step 1: SplitSkill (6144 chars/page, no overlap, up to 2000 pages)
    Step 2: AzureOpenAIEmbeddingSkill (text-embedding-ada-002, 1536 dims)
    Step 3: Custom WebApiSkill extractFrontmatter (Azure Function App)
  --> Azure AI Search Index (per product)
```

### Extraction Tools

**`qai-salesforce-extract`**
- Automated extraction of Salesforce KB articles
- Runs as scheduled cron jobs (templated for scalability)
- Handles PII detection and removal
- Underwent security/privacy review (Sprint 25.16)
- Credentials require periodic rotation

**`qai-content-conversion-tools`**
- Multi-format converter (HTML, Word, Excel, PPT) to Markdown
- Handles image extraction and asset management
- Expanded over time to cover more formats (Sprint 25.15)

### GitHub Actions Workflows

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `[productCode]-external-upload-prod.yml` | Manual/scheduled | Upload product-specific content |
| `qai-images-assets-upload.yml` | Manual/scheduled | Upload images and assets |
| `sync.yml` | On PR merge to main | Incremental sync of changed files to Blob Storage |
| `pr-image-optimization.yml` | On every PR | Auto-resize and compress images |
| `Deploy OD Teams docs to Azure Blob Storage` | Manual | Full re-upload of OD Teams container |

**Sync Configuration:** `sync-config.json` maps which knowledge folders sync to which Azure Blob Storage containers.

### Azure AI Search Indexing

**Indexer Configuration:**
- Schedule: Every 24 hours (`P1D`)
- Supported file types: `.txt, .md, .html, .pdf, .docx, .pptx`
- Batch size: 1 document at a time
- Data extraction: `contentAndMetadata`
- Execution environment: `private`

**Skillset Pipeline (3 skills):**

1. **SplitSkill** — Chunks documents into pages
   - Max page length: 6,144 characters
   - No page overlap
   - Up to 2,000 pages per document

2. **AzureOpenAIEmbeddingSkill** — Generates vector embeddings
   - Model: `text-embedding-ada-002`
   - Dimensions: 1,536
   - Endpoint: `aoi-qassistant-prod-eastus.openai.azure.com`

3. **Custom WebApiSkill (extractFrontmatter)** — Extracts YAML frontmatter metadata
   - Hosted on: `qassistant-rbac-poc-...azurewebsites.net`
   - HTTP POST, 30s timeout, batch size 1
   - Input: document content + storage path
   - Output: `product`, `sp_product`, `fm_title`, `role`, `region`

**Index Schema:**

| Field | Type | Searchable | Filterable | Purpose |
|-------|------|-----------|-----------|---------|
| `chunk_id` | String (key) | Yes | Yes | Unique chunk identifier |
| `parent_id` | String | Yes | Yes | Parent document reference |
| `content` | String | Yes | No | Chunk text content |
| `title` | String | Yes | Yes | Document title |
| `url` | String | No | No | Source URL |
| `filepath` | String | No | No | Original file path |
| `contentVector` | Collection(Single) | Yes | No | 1536-dim embedding vector |
| `product` | String | No | Yes | Product code (from frontmatter) |
| `fm_title` | String | No | Yes | Frontmatter title |
| `role` | String | No | Yes | Target role |
| `region` | Collection(String) | No | Yes | Geographic regions |
| `sp_product` | String | No | Yes | Sub-product code |

**Vector Search Configuration:**
- Algorithm: HNSW
- Metric: Cosine
- m: 4, efConstruction: 400, efSearch: 1000
- Vectorizer: Azure OpenAI (`text-embedding-ada-002`)

**Semantic Search Configuration:**
- Similarity: BM25
- Ranking: BoostedRerankerScore
- Title field prioritized, then content field

### Freshness Mechanisms

| Layer | Mechanism | Trigger | Frequency |
|-------|-----------|---------|-----------|
| Content creation | Teams submit PRs with new/updated docs | Manual | As needed |
| Salesforce extraction | `qai-salesforce-extract` cron jobs | Scheduled | Regular intervals |
| GitHub to Blob sync | `sync.yml` workflow | On PR merge | Immediate |
| Full re-upload | Manual deploy workflows | Manual trigger | As needed |
| Blob to Index | Azure AI Search indexer | Scheduled | Daily (P1D) |
| Image optimization | `pr-image-optimization.yml` | On every PR | Before merge |
| Content gap filling | Analytics surface gaps, teams fill them | Manual | Ongoing |

---

## 5. Azure Infrastructure

| Resource | Name/Details |
|----------|-------------|
| Azure Subscription | `s017-qs-engineering-ai-prod` |
| Storage Account | `stassistantprod` |
| Azure OpenAI Endpoint | `aoi-qassistant-prod-eastus.openai.azure.com` |
| AI Search | PROD AI Search instance |
| Key Vault | `qai-kv-prod` |
| Function App (Frontmatter) | `qassistant-rbac-poc-...azurewebsites.net` |
| Chat Model | GPT-4o-mini |
| Embedding Model | text-embedding-ada-002 (upgrading to text-embedding-3) |
| Proxy (Test) | `azwebapp-qai-chatbot-proxy-test.azurewebsites.net` |
| Proxy (Prod) | `azwebapp-qai-chatbot-proxy-prod.azurewebsites.net` |
| CDN | On Demand Front Door service (test + prod) |
| App Registration | `qai-CopilotOpenAIServicePrincipal` (secret rotation: 9/4/2026) |
| Tenant ID | `ce68f836-c221-45ef-866b-38cda86b3d5e` |

---

## 6. GitHub Repositories

### Active Repos (GitHub, QuorumOnDemand / Quorum-AI orgs)

| Repo | Purpose |
|------|---------|
| `q-assistant-frontend` | TypeScript web components (chat UI) |
| `qai-functions` | Azure Function Apps |
| `qai-salesforce-extract` | Salesforce KB article extraction tool |
| `qai-knowledge-base` | External customer-facing knowledge (markdown + YAML frontmatter) |
| `qai-knowledge-base-internal` | Internal knowledge (audibles, internal guides) |
| `qai-content-conversion-tools` | Tools to convert various formats to markdown |

### Azure DevOps Repos

| Repo | Purpose |
|------|---------|
| `Quorum.QAI.CopilotStudio` | Copilot Studio backup/configuration |

### Archived Repos

| Repo | Purpose |
|------|---------|
| `qai-orchestrator-poc` | Early orchestrator proof-of-concept |
| `dev-qai-knowledge-base` | Development knowledge base |
| `q-assistant-docusaurus` | Earlier Docusaurus-based approach |
| `q-support-assistant` | Earlier support assistant |
| `q-assistant-knowledge-base` | Earlier knowledge base |

---

## 7. QAI Integration in QFC Web

QAI is embedded into the Quorum QFC Web application via an integration layer in `Quorum.QFC.Web.Core`.

### Backend Services

**Location:** `Quorum.QFC.Web.Core/QAssistantIntegration/`

| File | Purpose |
|------|---------|
| `QAIIntegrationTokenService.cs` | JWT token generation (HS256, 1hr expiry) |
| `QAIDirectLineTokenService.cs` | Direct Line token from Azure Bot Service |
| `DefaultQAIIntegrationDataProvider.cs` | Extracts context from QOperationContext |
| `QAIIntegrationDataProviderRegistrar.cs` | Registry for custom data providers |
| `QAIDataProviderRegistration.cs` | DI registration trigger |

### API Endpoints

| Endpoint | Purpose |
|----------|---------|
| `GET /api/QAssistantIntegration/QAssistantContextDataToken` | JWT with user context (userId, userName, email, module, account, environment, timestamp) |
| `GET /api/QAssistantIntegration/AuthProviderAccessToken` | OAuth access token from user session |
| `GET /api/QAssistantIntegration/DirectLineAccessToken` | Direct Line token + conversation ID |

### Frontend Integration

**Location:** `Quorum.QFC.Web.CoreScreens/Views/Shared/`

- **QFrame.cshtml** — Main layout with `<q-assistant>` web component and initialization JavaScript
- **_FFooter.cshtml** — "Start QAI" button in footer

**Two Connection Modes:**
- **Proxy mode** (default, `QASSISTANT-USE-BOT-PROXY: true`): Uses bot token endpoint directly
- **Direct mode**: Fetches Direct Line token separately

### Configuration Keys

| Key | Group | Purpose |
|-----|-------|---------|
| `QASSISTANT-ENABLED` | WEB | Master switch for QAI |
| `QASSISTANT-SCRIPT-URL` | WEB | URL to web component script |
| `QASSISTANT-USE-BOT-PROXY` | WEB | Use proxy for bot token (default: true) |
| `QASSISTANT-BOT-URL` | WEB | Bot token endpoint URL |
| `QASSISTANT-PRODUCTID` | WEB | Product ID for webChat |
| `QAssistant-TokenSecret` | Security | JWT signing secret (encrypted) |
| `QAssistant-DirectlineSecret` | Security | Direct Line bearer token (encrypted) |

### JWT Token Payload

```json
{
  "userId": "...",
  "userName": "...",
  "userEmail": "...",
  "userType": "common",
  "accountName": "...",
  "environment": "...",
  "module": "...",
  "timestamp": 1709654400,
  "context-*": "... (custom data from registered providers)"
}
```

### QPTM-Specific Production Resources

| Resource | Value |
|----------|-------|
| PowerApps Solution | `q-assistant-qptm-prod` |
| Copilot Studio | `QAssistant-QPTM-Prod` |
| Copilot Endpoint | `https://d08bc6c8e197eed1b5aa37f21a38c9.04.environment.api.powerplatform.com/...` |

---

## 8. Product Deployments & Status

| Product | Status (as of March 2026) | Notes |
|---------|--------------------------|-------|
| **ODL** (Land on Demand) | Live - V2 active for all users | Pendo guide active |
| **DD** (Dynamic Documents) | Live - Launched to all users | Production-ready since Sprint 25.25 |
| **ODP** (On Demand Production) | Pilot testing | Pilot testers being expanded |
| **ODW** (On Demand Wholesale) | Upgrading | CORS issues with CloudFlare |
| **ODA** (On Demand Accounting) | Active | Mentioned in analytics |
| **Execute** | Testing | Content concerns (outdated), Okta rollout mid-2026 |
| **QPTM** (Pipeline Transaction Mgmt) | In Masters, GA April 2026 | Production pipeline finalized |
| **TIPS** | In Masters, GA April 2026 | Alongside QPTM |
| **Teams** | Live - V2 released | Improved usability |
| **Field App** | Planned | New requirement, setup being prepared |
| **EC (Execute Cloud)** | Planned | Calls held to prepare infrastructure |

---

## 9. QPTM Timeline

### Official Release Schedule

```
QAI Integration:      2025-09-24 ──────────────────────────> 2026-03-30
QFC 2025.10 Release:  2025-10-24 ──────────────────────────> 2026-03-31
  Betas:              2025-12-17 ─────────────> 2026-03-05
  Masters:                                      2026-03-06 ──> 2026-03-31
  Hotfix 1:                                                    2026-04-01 -> 04-15
  GA:                 2026.04 (April 2026)
```

### Chronological Milestones

| When | Sprint | Milestone |
|------|--------|-----------|
| Jul 2025 | 25.14 | QAI Pipeline Kickoff Discovery Workshop for QPTM |
| Jul 2025 | 25.15 | Index-switching work for QPTM & TIPS; YAML frontmatter tagging; indexes with product/role/region fields |
| Aug 2025 | 25.16 | QPTM and TIPS base Copilot Studio services created; Intent-based AI logic designed; Salesforce KB conversion for QPTM started |
| Sep 2025 | 25.24 | QAI Integration work item started; QAI snippet work with Platform team |
| Oct 2025 | 25.21-22 | QAI for QPTM & TIPS content conversion focus; Salesforce KB conversion |
| Nov 2025 | 25.23 | QPTM wireframes; MyQuorum QAI Copilot features; Snippet requirements; QPTM PR review |
| Nov-Dec 2025 | 25.24-25 | TIPS and QPTM knowledge PRs processing; Snippet adjustments; Fixed long filenames and missing frontmatter |
| Jan 2026 | 26.02 | Snippet testing refinements; Region/version filtering (2025.10); Module-based index filtering; New QPTM PRs from Salesforce KB; Upgraded to text-embeddings 3 |
| Feb 2026 | 26.03 | QAI links finalized; QA testing with Platform team; Internal testing (region & user type); YAML frontmatter standardization |
| Feb-Mar 2026 | 26.04 | QA testing continued; Internal testing progressed; Analytics populating; **All Hands testing kickoff (2/24/26)**; **Production pipeline finalized**; Security & Privacy review completed |
| Mar 2026 | Current | **Masters phase** (started 3/6/2026) |
| Apr 2026 | Planned | **GA release** (QFC 2025.10 GA 2026.04) |

---

## 10. QWiki Project Overview

**Location:** `C:\Users\chetan.chauhan\source\repos\QWiki`

### Purpose
QWiki is a RAG (Retrieval Augmented Generation) AI Assistant prototype for developer documentation. It ingests documents, creates vector embeddings, and uses AI to answer questions with source citations.

### Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend | ASP.NET Core 9.0 (C# 12) |
| Frontend | Blazor Server + Tailwind CSS |
| AI Models | GPT-4o-mini + text-embedding-3-small via GitHub Models API |
| Vector Store | JSON file-based (prototype, NOT production-ready) |
| Database | SQLite (ingestion cache) |
| Deployment | Docker, Azure App Service, GitHub Actions CI/CD |

### Key Features

1. **RAG Chat Pipeline** — Query -> embedding -> vector search -> top 5 chunks -> GPT-4o-mini -> response with citations
2. **Multi-source Document Ingestion** — PDFs (PdfPig), PowerPoints (OpenXML), Azure DevOps Wiki pages (REST API)
3. **Citation Tracking** — XML-format citations with links to source pages/slides/wiki URLs
4. **Streaming Responses** — Token-by-token via Blazor Server
5. **Custom Viewers** — PDF.js viewer, HTML5 PowerPoint viewer
6. **Ingestion Cache** — SQLite tracks document versions to avoid re-ingestion

### Architecture

```
QWiki/
├── Components/Pages/Chat/    # Blazor chat UI (Chat.razor + sub-components)
├── Controllers/              # PowerPointController (REST API)
├── Services/
│   ├── Ingestion/            # DataIngestor, PDFDirectorySource, PPTDirectorySource
│   ├── SemanticSearch.cs     # Vector similarity search
│   └── JsonVectorStore.cs    # JSON-based vector store (prototype)
├── wwwroot/Data/             # Document storage (PDFs, PPTs)
└── Program.cs                # Startup, DI configuration
```

### Current Data Sources
- PDFs in `wwwroot/Data/` (e.g., `QPTM 101 v5.pdf`)
- PowerPoint files (e.g., `QPTM Product Overview-KJ2017.pptx`)
- Azure DevOps Wiki pages (from "Maintenance" root path)

---

## 11. QAI vs QWiki Comparison

### Architecture & Infrastructure

| Aspect | QAI | QWiki |
|--------|-----|-------|
| Status | Production, multi-product | Prototype/internal |
| AI Model (Chat) | Azure OpenAI GPT-4o-mini | GitHub Models GPT-4o-mini |
| Embeddings | text-embedding-ada-002/3 (Azure) | text-embedding-3-small (GitHub Models) |
| Vector Store | Azure AI Search (HNSW) | JSON files on disk |
| Search Type | Vector + semantic + hybrid | Cosine similarity only |
| Conversation Engine | Microsoft Copilot Studio | Custom Blazor (direct API) |
| Frontend | TypeScript web components + WebChat | Blazor Server |
| Hosting | Azure CDN + App Service | Standalone Docker/App Service |

### Knowledge Management

| Aspect | QAI | QWiki |
|--------|-----|-------|
| Content storage | Azure Blob Storage (cloud) | Local `wwwroot/Data/` |
| Content format | Markdown + YAML frontmatter | Raw PDFs, PPTs, wiki pages |
| Content management | GitHub repos with PR review | Drop files in folder |
| Content conversion | Dedicated tools (SF extract, conversion) | Built-in PdfPig/OpenXml |
| Content filtering | Per-product, per-role, per-region | Filename filter only |
| Freshness | Nightly indexer + GitHub Actions on merge | Restart to re-ingest |
| Scalability | Separate indexes per product | Single flat collection |
| Content review | PR-based with team review | No review process |

### Indexing & Search

| Aspect | QAI | QWiki |
|--------|-----|-------|
| Chunking | 6144 chars/page, no overlap | 200 tokens (PDF/PPT), 300 words + 50 overlap (wiki) |
| Metadata | Rich (product, role, region, title) | Minimal (filename, page, type) |
| Vector search | HNSW (O(log n)) | Brute-force cosine (O(n)) |
| Supported formats | .txt, .md, .html, .pdf, .docx, .pptx | .pdf, .pptx, wiki markdown |

### Enterprise Features

| Aspect | QAI | QWiki |
|--------|-----|-------|
| Authentication | Azure AD + JWT + Direct Line | None |
| Analytics | Azure Application Insights + dashboards | None |
| Feedback | Auto-logged to Salesforce via Power Automate | None |
| Multi-product | Per-product Copilot Studio + index | Single knowledge base |
| Image handling | Auto-optimization + CDN | Not handled |
| Teams integration | QAI for Teams (V2) | None |

---

## 12. Opportunities & Ideas

### Where They're Similar
- Both use RAG architecture
- Both use GPT-4o-mini for chat
- Both use 1536-dimensional embeddings
- Both chunk documents and create vector indexes
- Both provide source citations in responses

### Where QWiki Could Complement QAI

1. **Internal Developer Focus** — QAI serves external customers; QWiki could serve internal developers with Azure DevOps wiki content, internal process docs, and brownbag recordings that aren't in QAI's scope

2. **Real-time Wiki Ingestion** — QWiki already ingests Azure DevOps wiki pages directly via API; QAI doesn't do this (it uses pre-converted markdown in Blob Storage)

3. **Lightweight/Local Deployment** — QWiki can run locally without Azure infrastructure, useful for team-level or project-specific knowledge bases

4. **Rapid Prototyping** — QWiki's simpler architecture makes it faster to iterate on new RAG approaches before promoting to QAI's production infrastructure

### Patterns to Adopt from QAI

1. **YAML Frontmatter** — Add metadata tagging to QWiki documents for filtering
2. **Azure AI Search** — Replace JSON vector store with proper vector database
3. **Content Management** — Implement a structured content pipeline instead of ad-hoc file drops
4. **Analytics** — Track what questions users ask, which docs are cited, where gaps exist
5. **Feedback Loop** — Let users rate answers to improve content over time
6. **Hybrid Search** — Combine vector search with keyword/semantic search for better accuracy
7. **Per-product Filtering** — If serving multiple teams, segment knowledge by product/team

### Potential New Features for QWiki

- **Authentication** via Azure AD / Entra ID
- **Chat history persistence** (currently lost on refresh)
- **Content management UI** for uploading and tagging documents
- **Automated wiki sync** on a schedule (not just on startup)
- **Support for more document types** (Word, Excel, HTML)
- **Analytics dashboard** to surface knowledge gaps
- **Feedback mechanism** (thumbs up/down on answers)

---

*This document was compiled from Azure DevOps wiki pages, sprint notes (25.05 through 26.04), QWiki codebase analysis, and QAI integration code review in Quorum.QFC.Web.*
