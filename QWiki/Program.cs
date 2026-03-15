using Azure;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using QWiki.Components;
using QWiki.Ingestion;
using QWiki.Services;
using QWiki.Shared;
using OpenAI;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// You will need to set the endpoint and key to your own values
// You can do this using Visual Studio's "Manage User Secrets" UI, or on the command line:
//   cd this-project-directory
//   dotnet user-secrets set GitHubModels:Token YOUR-GITHUB-TOKEN
var credential = new ApiKeyCredential(builder.Configuration["GitHubModels:Token"] ?? throw new InvalidOperationException("Missing configuration: GitHubModels:Token. See the README for details."));
var openAIOptions = new OpenAIClientOptions()
{
    Endpoint = new Uri(EmbeddingConfig.GitHubModelsEndpoint)
};

var ghModelsClient = new OpenAIClient(credential, openAIOptions);
var chatClient = ghModelsClient.GetChatClient("gpt-4o-mini").AsIChatClient();
var embeddingGenerator = ghModelsClient.GetEmbeddingClient(EmbeddingConfig.ModelName).AsIEmbeddingGenerator();

builder.Services.AddAzureAISearchVectorStore(
    new Uri(builder.Configuration["AzureSearch:Endpoint"]
        ?? throw new InvalidOperationException("Missing configuration: AzureSearch:Endpoint. Set it in appsettings.json.")),
    new AzureKeyCredential(builder.Configuration["AzureSearch:ApiKey"]
        ?? throw new InvalidOperationException("Missing configuration: AzureSearch:ApiKey. Use 'dotnet user-secrets set AzureSearch:ApiKey YOUR-KEY'.")));

builder.Services.AddSingleton<SemanticSearch>();
builder.Services.AddChatClient(chatClient).UseFunctionInvocation().UseLogging();
builder.Services.AddEmbeddingGenerator(embeddingGenerator);

// Dev-mode: run ingestion in-process (production uses the separate Worker Service)
var runIngestionInProcess = builder.Configuration.GetValue<bool>("RunIngestionInProcess");
if (runIngestionInProcess)
{
    builder.Services.AddIngestionServices(builder.Configuration);
}

var app = builder.Build();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Fire-and-forget ingestion when running in dev-mode
if (runIngestionInProcess)
{
    _ = Task.Run(async () =>
    {
        try
        {
            await IngestionServiceExtensions.RunIngestionAsync(app.Services);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "In-process ingestion failed");
        }
    });
}

app.Run();
