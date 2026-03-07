using Azure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using QWiki.Components;
using QWiki.Services;
using QWiki.Services.Ingestion;
using OpenAI;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddControllers();

// You will need to set the endpoint and key to your own values
// You can do this using Visual Studio's "Manage User Secrets" UI, or on the command line:
//   cd this-project-directory
//   dotnet user-secrets set GitHubModels:Token YOUR-GITHUB-TOKEN
var credential = new ApiKeyCredential(builder.Configuration["GitHubModels:Token"] ?? throw new InvalidOperationException("Missing configuration: GitHubModels:Token. See the README for details."));
var openAIOptions = new OpenAIClientOptions()
{
    Endpoint = new Uri("https://models.inference.ai.azure.com")
};

var ghModelsClient = new OpenAIClient(credential, openAIOptions);
var chatClient = ghModelsClient.GetChatClient("gpt-4o-mini").AsIChatClient();
var embeddingGenerator = ghModelsClient.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

builder.Services.AddAzureAISearchVectorStore(
    new Uri(builder.Configuration["AzureSearch:Endpoint"]
        ?? throw new InvalidOperationException("Missing configuration: AzureSearch:Endpoint. Set it in appsettings.json.")),
    new AzureKeyCredential(builder.Configuration["AzureSearch:ApiKey"]
        ?? throw new InvalidOperationException("Missing configuration: AzureSearch:ApiKey. Use 'dotnet user-secrets set AzureSearch:ApiKey YOUR-KEY'.")));
builder.Services.AddScoped<DataIngestor>();
builder.Services.AddSingleton<SemanticSearch>();
builder.Services.AddChatClient(chatClient).UseFunctionInvocation().UseLogging();
builder.Services.AddEmbeddingGenerator(embeddingGenerator);

builder.Services.AddDbContext<IngestionCacheDbContext>(options =>
    options.UseSqlite("Data Source=ingestioncache.db"));

var app = builder.Build();
IngestionCacheDbContext.Initialize(app.Services);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.UseStaticFiles();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Run ingestion in the background so the web app starts immediately.
// Important: ensure that any content you ingest is trusted, as it may be reflected back
// to users or could be a source of prompt injection risk.
_ = Task.Run(async () =>
{
    try
    {
        await DataIngestor.IngestDataAsync(
            app.Services,
            new PDFDirectorySource(builder.Configuration, Path.Combine(builder.Environment.WebRootPath, "Data")));

        await DataIngestor.IngestDataAsync(
            app.Services,
            new PPTDirectorySource(Path.Combine(builder.Environment.WebRootPath, "Data")));

        await DataIngestor.IngestDataAsync(
            app.Services,
            new SharePointTranscriptSource(Path.Combine(builder.Environment.WebRootPath, "Data")));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Background ingestion failed");
    }
});

app.Run();
