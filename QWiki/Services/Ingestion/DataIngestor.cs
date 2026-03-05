using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace QWiki.Services.Ingestion;

public class DataIngestor(
    ILogger<DataIngestor> logger,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IVectorStore vectorStore,
    IngestionCacheDbContext ingestionCacheDb,
    IConfiguration configuration)
{
    public static async Task IngestDataAsync(IServiceProvider services, IIngestionSource source)
    {
        using var scope = services.CreateScope();
        var ingestor = scope.ServiceProvider.GetRequiredService<DataIngestor>();
        await ingestor.IngestDataAsync(source);
    }

    public async Task IngestDataAsync(IIngestionSource source)
    {
        var vectorCollection = vectorStore.GetCollection<string, SemanticSearchRecord>("data-qwiki-ingested");
        await vectorCollection.CreateCollectionIfNotExistsAsync();

        var documentsForSource = ingestionCacheDb.Documents
            .Where(d => d.SourceId == source.SourceId)
            .Include(d => d.Records);

        var deletedFiles = await source.GetDeletedDocumentsAsync(documentsForSource);
        foreach (var deletedFile in deletedFiles)
        {
            logger.LogInformation("Removing ingested data for {file}", deletedFile.Id);
            await vectorCollection.DeleteBatchAsync(deletedFile.Records.Select(r => r.Id));
            ingestionCacheDb.Documents.Remove(deletedFile);
        }
        await ingestionCacheDb.SaveChangesAsync();

        var modifiedDocs = await source.GetNewOrModifiedDocumentsAsync(documentsForSource);
        foreach (var modifiedDoc in modifiedDocs)
        {
            logger.LogInformation("Processing {file}", modifiedDoc.Id);

            if (modifiedDoc.Records.Count > 0)
            {
                await vectorCollection.DeleteBatchAsync(modifiedDoc.Records.Select(r => r.Id));
            }

            var newRecords = await source.CreateRecordsForDocumentAsync(embeddingGenerator, modifiedDoc.Id);
            await foreach (var id in vectorCollection.UpsertBatchAsync(newRecords)) { }

            modifiedDoc.Records.Clear();
            modifiedDoc.Records.AddRange(newRecords.Select(r => new IngestedRecord { Id = r.Key, DocumentSourceId = modifiedDoc.SourceId, DocumentId = modifiedDoc.Id }));

            if (ingestionCacheDb.Entry(modifiedDoc).State == EntityState.Detached)
            {
                ingestionCacheDb.Documents.Add(modifiedDoc);
            }
        }

        await ingestionCacheDb.SaveChangesAsync();

        // Process wiki ingestion - check for RootPaths first (bulk collection), then fall back to WikiLinks
        var rootPaths = configuration.GetSection("WikiIngestion:RootPaths").Get<string[]>();
        var wikiLinks = configuration.GetSection("WikiIngestion:WikiLinks").Get<string[]>();

        if (rootPaths != null && rootPaths.Length > 0)
        {
            // Strategy A: Bulk wiki collection ingestion from root paths
            logger.LogInformation("Starting bulk wiki ingestion from {count} root path(s): {paths}",
                rootPaths.Length, string.Join(", ", rootPaths));

            var newWikiRecords = await source.CreateRecordsForWikiCollectionAsync(embeddingGenerator, rootPaths);
            var recordsList = newWikiRecords.ToList();

            logger.LogInformation("Upserting {count} wiki records to vector store...", recordsList.Count);
            await foreach (var id in vectorCollection.UpsertBatchAsync(recordsList)) { }

            logger.LogInformation("Wiki collection ingestion complete: {count} records indexed", recordsList.Count);
        }
        else if (wikiLinks != null && wikiLinks.Length > 0)
        {
            // Backward compatibility: Process individual wiki links
            logger.LogInformation("Processing {count} individual wiki links from configuration", wikiLinks.Length);
            var newWikiRecords = await source.CreateRecordsForMultipleWikiLinksAsync(embeddingGenerator, wikiLinks);
            await foreach (var id in vectorCollection.UpsertBatchAsync(newWikiRecords)) { }
        }
        else
        {
            logger.LogWarning("No wiki configuration found. Add WikiIngestion:RootPaths or WikiIngestion:WikiLinks to appsettings.json");
        }

        logger.LogInformation("Ingestion is up-to-date");
    }
}
