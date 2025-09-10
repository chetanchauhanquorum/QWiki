# QWiki - RAG-Based AI Documentation Assistant

QWiki is a **Retrieval Augmented Generation (RAG)** based AI Assistant designed to answer documentation-related queries from developers and provide relevant wiki or knowledge base articles as references for further exploration.

## What is QWiki?

QWiki builds upon the concept of creating an intelligent documentation assistant that can:

- **Answer Process-Related Queries**: Help developers find information about internal processes, procedures, and best practices
- **Reference Documentation**: Provide relevant wiki articles and knowledge base entries as supporting references
- **Support Multiple Document Types**: Process PDFs, Word documents, PowerPoint presentations, and wiki content
- **Enable Knowledge Discovery**: Allow users to explore documentation through natural language queries

## Key Features

- **RAG Architecture**: Uses Retrieval Augmented Generation to combine document search with AI-generated responses
- **Multi-Source Ingestion**: Supports PDF files, Azure DevOps wiki pages, and can be extended for other document types
- **Semantic Search**: Uses vector embeddings to find relevant content based on meaning, not just keywords
- **Reference Tracking**: Provides source citations and page numbers for verification and further reading
- **Real-time Updates**: Automatically detects and processes new or modified documents

## Future Extensions

The application is designed to be extensible and can support:

- **Office Documents**: PowerPoint presentations and Word documents for product documentation
- **Meeting Transcripts**: Processing brownbag session transcripts and meeting notes
- **Custom Data Sources**: Any structured or unstructured documentation sources

>[!NOTE]
> Before running this project you need to configure the API keys or endpoints for the providers you have chosen. See below for details specific to your choices.

# Configuration

## Prerequisites

QWiki requires two types of tokens depending on your data sources:

1. **GitHub Models Token** - For AI model access (required)
2. **Azure DevOps PAT Token** - For wiki ingestion (optional, only if using Azure DevOps wikis)

## 1. GitHub Models Token Setup

GitHub Models provides free access to AI models for prototyping and development.

### Steps to Get Your GitHub Models Token:

1. **Sign in to GitHub**: Go to [GitHub.com](https://github.com) and sign in to your account
2. **Navigate to Settings**: Click on your profile picture → Settings
3. **Access Developer Settings**: Scroll down and click "Developer settings" in the left sidebar
4. **Personal Access Tokens**: Click "Personal access tokens" → "Tokens (classic)"
5. **Generate New Token**: Click "Generate new token" → "Generate new token (classic)"
6. **Configure Token**:
   - **Note**: Enter a descriptive name like "QWiki GitHub Models Access"
   - **Expiration**: Choose your preferred expiration (recommend 90 days for development)
   - **Scopes**: **IMPORTANT - Leave all scopes unchecked** (no permissions needed)
7. **Generate Token**: Click "Generate token"
8. **Copy Token**: Copy the token immediately (you won't be able to see it again)

### Configure GitHub Models Token:

```bash
cd QWiki
dotnet user-secrets set "GitHubModels:Token" "your-github-token-here"
```

## 2. Azure DevOps PAT Token Setup (Optional)

Only needed if you want to ingest content from Azure DevOps wikis.

### Steps to Get Your Azure DevOps PAT Token:

1. **Sign in to Azure DevOps**: Go to your Azure DevOps organization (e.g., `https://dev.azure.com/yourorg`)
2. **Access User Settings**: Click on your profile picture → "Personal access tokens"
3. **Create New Token**: Click "New Token"
4. **Configure Token**:
   - **Name**: Enter "QWiki Wiki Access" or similar
   - **Organization**: Select your organization or "All accessible organizations"
   - **Expiration**: Choose your preferred expiration
   - **Scopes**: Select "Custom defined" and check:
     - **Wiki**: Read
     - **Project and Team**: Read (if needed for project access)
5. **Create Token**: Click "Create"
6. **Copy Token**: Copy the token immediately

### Configure Azure DevOps PAT Token:

```bash
cd QWiki
dotnet user-secrets set "AzureDevOps:Pat" "your-azure-devops-pat-here"
```

## Alternative Configuration Methods

### Using Visual Studio:
1. Right-click on the QWiki project in Solution Explorer
2. Select "Manage User Secrets"
3. Add your tokens to the `secrets.json` file:

```json
{
   "GitHubModels:Token": "your-github-token-here",
   "AzureDevOps:Pat": "your-azure-devops-pat-here"
}
```

### Using appsettings.Development.json (Not Recommended):
For testing only - **never commit tokens to source control**:

```json
{
   "GitHubModels:Token": "YOUR-TOKEN",
   "AzureDevOps:Pat": "YOUR-TOKEN"
}
```

## Running the Application

1. **Configure Tokens** (see above)
2. **Add Documents**: Place PDF files in the `wwwroot/Data` directory
3. **Configure Wiki Links** (optional): Update `appsettings.json` or `appsettings.Development.json` to specify which Azure DevOps wiki pages to ingest:
   ```json
   {
     "WikiIngestion": {
       "WikiLinks": [
         "Maintenance/For Developers/Process to debug QPEC locally through QPEC Assignment screen on Classic",
         "Your/Wiki/Page/Path/Here"
       ]
     }
   }
   ```
4. **Build and Run**:
   ```bash
   cd QWiki
   dotnet build
   dotnet run
   ```
5. **Access the Application**: Open your browser to `http://localhost:5100`

## Wiki Links Configuration

The application now supports ingesting multiple Azure DevOps wiki pages through configuration. You can specify wiki links in your `appsettings.json` or `appsettings.Development.json` file under the `WikiIngestion:WikiLinks` section.

### Wiki Link Format
Use the relative path of the wiki page as it appears in the Azure DevOps URL:
- Example URL: `https://dev.azure.com/yourorg/project/_wiki/wikis/wiki-name/123/Page-Title`
- Configuration value: `"Page-Title"` or `"Folder/Subfolder/Page-Title"`

### Benefits of Multiple Wiki Links
- **Batch Processing**: All configured wiki pages are processed during ingestion
- **Error Resilience**: If one wiki link fails, others continue to process
- **Centralized Configuration**: Easy to manage which wiki content gets ingested
- **Environment-Specific**: Different wiki links can be configured for development vs production

## Learn More

- [GitHub Models Documentation](https://docs.github.com/github-models/prototyping-with-ai-models)
- [Azure DevOps Personal Access Tokens](https://docs.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate)
- [.NET User Secrets](https://docs.microsoft.com/en-us/aspnet/core/security/app-secrets)

