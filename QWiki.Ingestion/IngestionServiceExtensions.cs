using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using QWiki.Ingestion.Sources;

namespace QWiki.Ingestion;

/// <summary>
/// DI registration and ingestion runner shared by the Worker Service and Blazor dev-mode.
/// </summary>
public static class IngestionServiceExtensions
{
    /// <summary>
    /// Registers all ingestion services (cache, ingestor, sources) into the DI container.
    /// </summary>
    public static IServiceCollection AddIngestionServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["AzureStorage:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Missing AzureStorage:ConnectionString. Use 'dotnet user-secrets set AzureStorage:ConnectionString YOUR-CONNECTION-STRING'.");

        // Persistent ingestion cache (Azure Table Storage) — skip if already registered (e.g., by QWiki UI Program.cs)
        services.TryAddSingleton(new AzureTableIngestionCache(connectionString));

        // Progress table store (shared between Worker and UI processes via Azure Table)
        services.TryAddSingleton(new AzureTableProgressStore(connectionString));

        // Ingestion progress tracking (singleton — shared with admin UI)
        services.TryAddSingleton<IngestionProgressService>();

        // Data ingestor
        services.AddTransient<DataIngestor>();

        // Audio transcriber for video files
        services.AddTransient<AudioTranscriber>();

        // Ingestion sources
        services.AddTransient<WikiIngestionSource>();
        services.AddTransient<SharePointIngestionSource>();

        return services;
    }

    /// <summary>
    /// Runs all configured ingestion sources sequentially with error isolation.
    /// Each source failure is logged but does not prevent other sources from running.
    /// </summary>
    public static async Task RunIngestionAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("QWiki.Ingestion");
        var progress = services.GetRequiredService<IngestionProgressService>();

        // Attach table store for cross-process progress sharing (write-through mode)
        var store = services.GetService<AzureTableProgressStore>();
        if (store != null)
        {
            progress.AttachStore(store, enableWriteThrough: true);
        }

        progress.StartIngestion();

        // Wiki ingestion
        try
        {
            progress.SetDiscovering("Wiki");
            logger.LogInformation("Starting wiki ingestion...");
            var ingestor = services.GetRequiredService<DataIngestor>();
            var wikiSource = services.GetRequiredService<WikiIngestionSource>();
            await ingestor.IngestDataAsync(wikiSource);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wiki ingestion failed");
            progress.SourceFailed("Wiki", ex.Message);
        }

        if (ct.IsCancellationRequested) { progress.IngestionCompleted(); return; }

        // SharePoint ingestion
        try
        {
            progress.SetDiscovering("SharePoint");
            logger.LogInformation("Starting SharePoint ingestion...");
            var ingestor = services.GetRequiredService<DataIngestor>();
            var spSource = services.GetRequiredService<SharePointIngestionSource>();
            await spSource.InitializeAsync();
            await ingestor.IngestDataAsync(spSource);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SharePoint ingestion failed");
            progress.SourceFailed("SharePoint", ex.Message);
        }

        progress.IngestionCompleted();
        logger.LogInformation("All ingestion sources processed");
    }
}
