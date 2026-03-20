using Azure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using QWiki.Shared;

namespace QWiki.Ingestion;

public class DataIngestor(
    ILogger<DataIngestor> logger,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    VectorStore vectorStore,
    AzureTableIngestionCache ingestionCache,
    IngestionProgressService progress)
{
    public async Task IngestDataAsync(IIngestionSource source)
    {
        var vectorCollection = vectorStore.GetCollection<string, SemanticSearchRecord>(EmbeddingConfig.IndexName);
        try
        {
            await vectorCollection.EnsureCollectionExistsAsync();
        }
        catch (VectorStoreException ex) when (ex.InnerException is RequestFailedException { Status: 409 })
        {
            // Index was already created by a concurrent ingestion request — safe to continue
        }

        // Load existing documents from Azure Table Storage
        var existingDocs = await ingestionCache.LoadDocumentsAsync(source.SourceId);

        // Handle deletions
        var deletedDocs = await source.GetDeletedDocumentsAsync(existingDocs);
        foreach (var deletedDoc in deletedDocs)
        {
            logger.LogInformation("Removing ingested data for {File}", deletedDoc.Id);
            if (deletedDoc.RecordKeys.Count > 0)
            {
                await vectorCollection.DeleteAsync(deletedDoc.RecordKeys);
            }
            await ingestionCache.DeleteDocumentAsync(deletedDoc.SourceId, deletedDoc.Id);
            existingDocs.Remove(deletedDoc.Id);
        }

        // Handle new/modified
        var modifiedDocs = await source.GetNewOrModifiedDocumentsAsync(existingDocs);
        var modifiedList = modifiedDocs.ToList();
        progress.SetProcessing(source.SourceId, modifiedList.Count);

        int errorCount = 0;
        foreach (var modifiedDoc in modifiedList)
        {
            progress.FileStarted(modifiedDoc.Id);
            logger.LogInformation("Processing {File}", modifiedDoc.Id);

            try
            {
                if (modifiedDoc.RecordKeys.Count > 0)
                {
                    await vectorCollection.DeleteAsync(modifiedDoc.RecordKeys);
                }

                var newRecords = await CreateRecordsWithRetryAsync(source, modifiedDoc.Id);

                if (newRecords.Count == 0)
                {
                    logger.LogWarning("Document {File} produced 0 records — skipping cache save so it will be retried", modifiedDoc.Id);
                    progress.FileCompleted(success: false);
                    continue;
                }

                await vectorCollection.UpsertAsync(newRecords);

                modifiedDoc.RecordKeys = newRecords.Select(r => r.Key).ToList();
                await ingestionCache.SaveDocumentAsync(modifiedDoc);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing {File}", modifiedDoc.Id);
                errorCount++;
                progress.FileCompleted(success: false);
                continue;
            }

            progress.FileCompleted(success: true);
        }

        progress.SourceCompleted(source.SourceId, modifiedList.Count - errorCount, existingDocs.Count, errorCount);
        logger.LogInformation("Ingestion is up-to-date for source {SourceId}", source.SourceId);
    }

    private async Task<List<SemanticSearchRecord>> CreateRecordsWithRetryAsync(
        IIngestionSource source, string documentId, int maxRetries = 3)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var records = (await source.CreateRecordsForDocumentAsync(embeddingGenerator, documentId)).ToList();
                return records;
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransientError(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 2s, 4s, 8s
                logger.LogWarning("Transient error on attempt {Attempt}/{Max} for {File}, retrying in {Delay}s: {Error}",
                    attempt + 1, maxRetries + 1, documentId, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay);
            }
        }

        return []; // unreachable, last attempt throws
    }

    private static bool IsTransientError(Exception ex) =>
        ex is TaskCanceledException or OperationCanceledException or HttpRequestException ||
        (ex.InnerException is TaskCanceledException or OperationCanceledException or HttpRequestException);
}
