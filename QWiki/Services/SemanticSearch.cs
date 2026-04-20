using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using QWiki.Shared;

namespace QWiki.Services;

public class SemanticSearch(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    VectorStore vectorStore)
{
    public async Task<IReadOnlyList<SemanticSearchRecord>> SearchAsync(string text, string? filenameFilter, int maxResults)
    {
        var queryEmbedding = await embeddingGenerator.GenerateVectorAsync(text);
        var collection = vectorStore.GetCollection<string, SemanticSearchRecord>(EmbeddingConfig.IndexName);

        // Run two queries in parallel: one excluding videos, one including everything.
        // This guarantees wiki/document results appear even when video chunks dominate the index.
        var nonVideoTask = RunSearchAsync(collection, queryEmbedding, filenameFilter, maxResults, excludeVideos: true);
        var allResultsTask = RunSearchAsync(collection, queryEmbedding, filenameFilter, maxResults, excludeVideos: false);

        await Task.WhenAll(nonVideoTask, allResultsTask);

        var nonVideoResults = nonVideoTask.Result;
        var allResults = allResultsTask.Result;

        // Merge: take all non-video results first, then fill remaining slots from the
        // general query (which includes videos), skipping duplicates.
        var seen = new HashSet<string>();
        var merged = new List<SemanticSearchRecord>();

        foreach (var r in nonVideoResults)
        {
            if (merged.Count >= maxResults) break;
            if (seen.Add(r.Key))
                merged.Add(r);
        }

        foreach (var r in allResults)
        {
            if (merged.Count >= maxResults) break;
            if (seen.Add(r.Key))
                merged.Add(r);
        }

        return merged;
    }

    private static async Task<List<SemanticSearchRecord>> RunSearchAsync(
        VectorStoreCollection<string, SemanticSearchRecord> collection,
        ReadOnlyMemory<float> queryEmbedding, string? filenameFilter,
        int maxResults, bool excludeVideos)
    {
        var searchOptions = new VectorSearchOptions<SemanticSearchRecord>();

        // Build filter expression based on parameters
        if (excludeVideos && filenameFilter is { Length: > 0 })
        {
            searchOptions.Filter = r => r.RecordType != "VIDEO" && r.FileName == filenameFilter;
        }
        else if (excludeVideos)
        {
            searchOptions.Filter = r => r.RecordType != "VIDEO";
        }
        else if (filenameFilter is { Length: > 0 })
        {
            searchOptions.Filter = r => r.FileName == filenameFilter;
        }

        var results = new List<SemanticSearchRecord>();
        await foreach (var result in collection.SearchAsync(queryEmbedding, top: maxResults, searchOptions))
        {
            if (result.Record is not null)
                results.Add(result.Record);
        }
        return results;
    }
}
