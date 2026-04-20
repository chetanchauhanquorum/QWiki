using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using QWiki.Ingestion;
using QWiki.Ingestion.Worker;
using QWiki.Shared;

var builder = Host.CreateApplicationBuilder(args);

// Embedding generator — Azure OpenAI for high-throughput ingestion (1000 RPM, 1M TPM)
// Uses the same model (text-embedding-3-small) as the UI to ensure vector compatibility
var aoaiEndpoint = new Uri(
    builder.Configuration["AzureOpenAI:Endpoint"]
        ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint."));
var aoaiKey = new AzureKeyCredential(
    builder.Configuration["AzureOpenAI:ApiKey"]
        ?? throw new InvalidOperationException("Missing AzureOpenAI:ApiKey."));
var aoaiClient = new AzureOpenAIClient(aoaiEndpoint, aoaiKey);
var embeddingGenerator = aoaiClient.GetEmbeddingClient(EmbeddingConfig.ModelName).AsIEmbeddingGenerator();

builder.Services.AddEmbeddingGenerator(embeddingGenerator);

// Cosmos DB vector store (free tier — permanently $0)
builder.Services.AddCosmosNoSqlVectorStore(
    builder.Configuration["CosmosDb:ConnectionString"]
        ?? throw new InvalidOperationException("Missing CosmosDb:ConnectionString."),
    "qwiki-db");

// Register all ingestion services (cache, transcriber, sources, ingestor)
builder.Services.AddIngestionServices(builder.Configuration);

// Register the worker
builder.Services.AddHostedService<IngestionWorker>();

var host = builder.Build();
host.Run();
