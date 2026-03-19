using Azure.Data.Tables;

namespace QWiki.Ingestion;

public class IngestedDocument
{
    public required string Id { get; set; }
    public required string SourceId { get; set; }
    public required string Version { get; set; }
    public List<string> RecordKeys { get; set; } = [];
}

/// <summary>
/// Persistent ingestion cache backed by Azure Table Storage.
/// Replaces the local SQLite IngestionCacheDbContext so that cache survives redeployments.
/// </summary>
public class AzureTableIngestionCache
{
    private readonly TableClient _tableClient;

    public AzureTableIngestionCache(string connectionString)
    {
        var serviceClient = new TableServiceClient(connectionString);
        _tableClient = serviceClient.GetTableClient("IngestionCache");
        _tableClient.CreateIfNotExists();
    }

    /// <summary>
    /// Loads all ingested documents across all sources.
    /// </summary>
    public async Task<List<IngestedDocument>> LoadAllDocumentsAsync()
    {
        var results = new List<IngestedDocument>();

        await foreach (var entity in _tableClient.QueryAsync<TableEntity>())
        {
            var docId = UnsanitizeRowKey(entity.RowKey!);
            var version = entity.GetString("Version") ?? "";
            var recordKeysStr = entity.GetString("RecordKeys") ?? "";

            var recordKeys = string.IsNullOrEmpty(recordKeysStr)
                ? new List<string>()
                : recordKeysStr.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

            results.Add(new IngestedDocument
            {
                Id = docId,
                SourceId = entity.PartitionKey!,
                Version = version,
                RecordKeys = recordKeys
            });
        }

        return results;
    }

    /// <summary>
    /// Loads all ingested documents for a given source into a dictionary keyed by document ID.
    /// </summary>
    public async Task<Dictionary<string, IngestedDocument>> LoadDocumentsAsync(string sourceId)
    {
        var results = new Dictionary<string, IngestedDocument>();

        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{EscapeFilter(sourceId)}'"))
        {
            var docId = UnsanitizeRowKey(entity.RowKey);
            var version = entity.GetString("Version") ?? "";
            var recordKeysStr = entity.GetString("RecordKeys") ?? "";

            var recordKeys = string.IsNullOrEmpty(recordKeysStr)
                ? new List<string>()
                : recordKeysStr.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

            results[docId] = new IngestedDocument
            {
                Id = docId,
                SourceId = sourceId,
                Version = version,
                RecordKeys = recordKeys
            };
        }

        return results;
    }

    /// <summary>
    /// Upserts a document record to Azure Table Storage.
    /// </summary>
    public async Task SaveDocumentAsync(IngestedDocument doc)
    {
        var entity = new TableEntity(doc.SourceId, SanitizeRowKey(doc.Id))
        {
            ["Version"] = doc.Version,
            ["RecordKeys"] = string.Join("|", doc.RecordKeys)
        };

        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    /// <summary>
    /// Deletes a document record from Azure Table Storage.
    /// </summary>
    public async Task DeleteDocumentAsync(string sourceId, string documentId)
    {
        try
        {
            await _tableClient.DeleteEntityAsync(sourceId, SanitizeRowKey(documentId));
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted — safe to ignore
        }
    }

    /// <summary>
    /// Deletes all document records for a given source. Returns the deleted documents
    /// so callers can also clean up associated search index vectors.
    /// </summary>
    public async Task<List<IngestedDocument>> DeleteSourceAsync(string sourceId)
    {
        var docs = await LoadDocumentsAsync(sourceId);
        foreach (var doc in docs.Values)
        {
            try
            {
                await _tableClient.DeleteEntityAsync(sourceId, SanitizeRowKey(doc.Id));
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }
        }
        return docs.Values.ToList();
    }

    // Azure Table RowKey disallows: / \ # ?
    private static string SanitizeRowKey(string key)
        => key.Replace("/", "||").Replace("\\", "||").Replace("#", "--").Replace("?", "--");

    private static string UnsanitizeRowKey(string rowKey)
        => rowKey.Replace("||", "/").Replace("--", "#");

    // Escape single quotes in OData filter expressions
    private static string EscapeFilter(string value)
        => value.Replace("'", "''");
}
