using Azure;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using OpenAI;
using QWiki.Ingestion;
using QWiki.Ingestion.Worker;
using QWiki.Shared;
using System.ClientModel;

var builder = Host.CreateApplicationBuilder(args);

// Embedding generator — same model as the UI to ensure vector compatibility
var credential = new ApiKeyCredential(
    builder.Configuration["GitHubModels:Token"]
        ?? throw new InvalidOperationException("Missing GitHubModels:Token. Use 'dotnet user-secrets set GitHubModels:Token YOUR-TOKEN'."));
var openAIOptions = new OpenAIClientOptions
{
    Endpoint = new Uri(EmbeddingConfig.GitHubModelsEndpoint)
};
var ghModelsClient = new OpenAIClient(credential, openAIOptions);
var embeddingGenerator = ghModelsClient.GetEmbeddingClient(EmbeddingConfig.ModelName).AsIEmbeddingGenerator();

builder.Services.AddEmbeddingGenerator(embeddingGenerator);

// Azure AI Search vector store
builder.Services.AddAzureAISearchVectorStore(
    new Uri(builder.Configuration["AzureSearch:Endpoint"]
        ?? throw new InvalidOperationException("Missing AzureSearch:Endpoint.")),
    new AzureKeyCredential(builder.Configuration["AzureSearch:ApiKey"]
        ?? throw new InvalidOperationException("Missing AzureSearch:ApiKey.")));

// Register all ingestion services (cache, transcriber, sources, ingestor)
builder.Services.AddIngestionServices(builder.Configuration);

// Register the worker
builder.Services.AddHostedService<IngestionWorker>();

var host = builder.Build();
host.Run();
