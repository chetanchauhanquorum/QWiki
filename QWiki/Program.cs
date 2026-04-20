using Azure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.AI;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.SemanticKernel;
using QWiki.Components;
using QWiki.Ingestion;
using QWiki.Services;
using QWiki.Shared;
using OpenAI;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);

// Trust forwarded headers from Azure App Service reverse proxy (required for OIDC correlation cookies over HTTPS)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Fix SameSite cookie issue: Entra ID uses response_mode=form_post (cross-site POST),
// browsers drop SameSite=Lax cookies on cross-site POSTs, breaking OIDC correlation.
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.None;
    options.Secure = CookieSecurePolicy.Always;
});
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

var cosmosConnectionString = builder.Configuration["CosmosDb:ConnectionString"]
    ?? throw new InvalidOperationException("Missing configuration: CosmosDb:ConnectionString. Use 'dotnet user-secrets set CosmosDb:ConnectionString YOUR-CONNECTION-STRING'.");
builder.Services.AddCosmosNoSqlVectorStore(cosmosConnectionString, "qwiki-db");
builder.Services.AddSingleton<SemanticSearch>();
builder.Services.AddChatClient(chatClient).UseFunctionInvocation().UseLogging();
builder.Services.AddEmbeddingGenerator(embeddingGenerator);

// Azure Table Storage services (feedback, chat history, admin)
var storageConnectionString = builder.Configuration["AzureStorage:ConnectionString"]
    ?? throw new InvalidOperationException("Missing AzureStorage:ConnectionString. Use 'dotnet user-secrets set AzureStorage:ConnectionString YOUR-CONNECTION-STRING'.");
builder.Services.AddSingleton(new AzureTableIngestionCache(storageConnectionString));
builder.Services.AddSingleton(new FeedbackService(storageConnectionString));
builder.Services.AddSingleton(new ChatHistoryService(storageConnectionString));

// Authentication & Authorization (Microsoft Entra ID)
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPolicy("AdminOnly", policy =>
        policy.RequireClaim("http://schemas.microsoft.com/identity/claims/objectidentifier",
            builder.Configuration["AdminSettings:AdminObjectId"]!));
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();

// Ingestion progress tracking (always registered so admin page can inject it)
builder.Services.AddSingleton<IngestionProgressService>();
builder.Services.AddSingleton(new AzureTableProgressStore(storageConnectionString));

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

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseCookiePolicy();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllers();
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
