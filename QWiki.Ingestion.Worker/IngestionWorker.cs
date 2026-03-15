namespace QWiki.Ingestion.Worker;

/// <summary>
/// Background service that runs data ingestion on a configurable interval.
/// Supports one-shot mode (RunOnce) for initial bulk loads.
/// </summary>
public class IngestionWorker(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runOnce = configuration.GetValue<bool>("Ingestion:RunOnce");
        var intervalMinutes = configuration.GetValue("Ingestion:IntervalMinutes", 60);

        logger.LogInformation("Ingestion worker starting (RunOnce={RunOnce}, IntervalMinutes={Interval})",
            runOnce, intervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await IngestionServiceExtensions.RunIngestionAsync(services, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ingestion run failed");
            }

            if (runOnce)
            {
                logger.LogInformation("Ingestion complete (RunOnce mode). Shutting down.");
                break;
            }

            logger.LogInformation("Next ingestion run in {Minutes} minutes", intervalMinutes);
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }
}
