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
    AzureTableIngestionCache ingestionCache)
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
        foreach (var modifiedDoc in modifiedDocs)
        {
            logger.LogInformation("Processing {File}", modifiedDoc.Id);

            if (modifiedDoc.RecordKeys.Count > 0)
            {
                await vectorCollection.DeleteAsync(modifiedDoc.RecordKeys);
            }

            var newRecords = (await source.CreateRecordsForDocumentAsync(embeddingGenerator, modifiedDoc.Id)).ToList();
            await vectorCollection.UpsertAsync(newRecords);

            modifiedDoc.RecordKeys = newRecords.Select(r => r.Key).ToList();
            await ingestionCache.SaveDocumentAsync(modifiedDoc);
        }

        logger.LogInformation("Ingestion is up-to-date for source {SourceId}", source.SourceId);
    }
}
