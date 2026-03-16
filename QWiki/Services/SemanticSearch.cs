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

        var searchOptions = new SearchOptions
        {
            Size = maxResults,
            // Hybrid: vector + full-text (BM25). No semantic ranking (requires Basic tier).
            // To enable semantic ranking later, uncomment:
            // QueryType = SearchQueryType.Semantic,
            // SemanticSearch = new() { SemanticConfigurationName = "default" },
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

        if (filenameFilter is { Length: > 0 })
        {
            searchOptions.Filter = $"FileName eq '{EscapeODataString(filenameFilter)}'";
        }

        // Hybrid: passing text as the search query activates BM25 full-text matching
        // alongside the vector query above — Azure AI Search fuses both result sets
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
