using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using QWiki.Shared;

namespace QWiki.Services;

public class SemanticSearch(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    SearchClient searchClient)
{
    public async Task<IReadOnlyList<SemanticSearchRecord>> SearchAsync(string text, string? filenameFilter, int maxResults)
    {
        var queryEmbedding = await embeddingGenerator.GenerateVectorAsync(text);

        // Run two queries in parallel: one excluding videos, one including everything.
        // This guarantees wiki/document results appear even when video chunks dominate the index.
        var nonVideoTask = RunSearchAsync(text, queryEmbedding, filenameFilter, maxResults, excludeVideos: true);
        var allResultsTask = RunSearchAsync(text, queryEmbedding, filenameFilter, maxResults, excludeVideos: false);

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

    private async Task<List<SemanticSearchRecord>> RunSearchAsync(
        string text, ReadOnlyMemory<float> queryEmbedding, string? filenameFilter,
        int maxResults, bool excludeVideos)
    {
        var searchOptions = new SearchOptions
        {
            Size = maxResults,
            VectorSearch = new()
            {
                Queries =
                {
                    new VectorizedQuery(queryEmbedding.ToArray())
                    {
                        Fields = { "Vector" },
                        KNearestNeighborsCount = maxResults * 2
                    }
                }
            },
            Select =
            {
                "Key", "FileName", "PageNumber", "RecordType",
                "SourceUrl", "Text", "SourceType", "DocumentTitle",
                "LastModified", "FolderPath"
            }
        };

        // Build OData filter combining filename filter and video exclusion
        var filters = new List<string>();
        if (filenameFilter is { Length: > 0 })
            filters.Add($"FileName eq '{EscapeODataString(filenameFilter)}'");
        if (excludeVideos)
            filters.Add("RecordType ne 'VIDEO'");
        if (filters.Count > 0)
            searchOptions.Filter = string.Join(" and ", filters);

        var response = await searchClient.SearchAsync<SemanticSearchRecord>(text, searchOptions);

        var results = new List<SemanticSearchRecord>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            results.Add(result.Document);
        }
        return results;
    }

    private static string EscapeODataString(string value)
        => value.Replace("'", "''");
}
