using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QWiki.Ingestion.Sources;

namespace QWiki.Ingestion;

/// <summary>
/// DI registration and ingestion runner shared by the Worker Service and Blazor dev-mode.
/// </summary>
public static class IngestionServiceExtensions
{
    /// <summary>
    /// Registers all ingestion services (cache, transcriber, ingestor, sources) into the DI container.
    /// </summary>
    public static IServiceCollection AddIngestionServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Persistent ingestion cache (Azure Table Storage)
        services.AddSingleton(new AzureTableIngestionCache(
            configuration["AzureStorage:ConnectionString"]
                ?? throw new InvalidOperationException(
                    "Missing AzureStorage:ConnectionString. Use 'dotnet user-secrets set AzureStorage:ConnectionString YOUR-CONNECTION-STRING'.")));

        // Audio transcriber (singleton — caches FFmpeg download + blob container client)
        services.AddSingleton<AudioTranscriber>();

        // Data ingestor
        services.AddTransient<DataIngestor>();

        // Ingestion sources
        services.AddTransient<WikiIngestionSource>();
        services.AddTransient<SharePointIngestionSource>();

        if (configuration.GetValue<bool>("LocalFolderIngestion:Enabled"))
        {
            services.AddTransient<LocalFolderIngestionSource>();
        }

        return services;
    }

    /// <summary>
    /// Runs all configured ingestion sources sequentially with error isolation.
    /// Each source failure is logged but does not prevent other sources from running.
    /// </summary>
    public static async Task RunIngestionAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("QWiki.Ingestion");
        var configuration = services.GetRequiredService<IConfiguration>();

        // Wiki ingestion
        try
        {
            logger.LogInformation("Starting wiki ingestion...");
            var ingestor = services.GetRequiredService<DataIngestor>();
            var wikiSource = services.GetRequiredService<WikiIngestionSource>();
            await ingestor.IngestDataAsync(wikiSource);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wiki ingestion failed");
        }

        if (ct.IsCancellationRequested) return;

        // SharePoint ingestion
        try
        {
            logger.LogInformation("Starting SharePoint ingestion...");
            var ingestor = services.GetRequiredService<DataIngestor>();
            var spSource = services.GetRequiredService<SharePointIngestionSource>();
            await spSource.InitializeAsync();
            await ingestor.IngestDataAsync(spSource);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SharePoint ingestion failed");
        }

        if (ct.IsCancellationRequested) return;

        // Local folder ingestion (if enabled)
        if (configuration.GetValue<bool>("LocalFolderIngestion:Enabled"))
        {
            try
            {
                logger.LogInformation("Starting local folder ingestion...");
                var ingestor = services.GetRequiredService<DataIngestor>();
                var lfSource = services.GetRequiredService<LocalFolderIngestionSource>();
                await ingestor.IngestDataAsync(lfSource);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Local folder ingestion failed");
            }
        }

        logger.LogInformation("All ingestion sources processed");
    }
}
