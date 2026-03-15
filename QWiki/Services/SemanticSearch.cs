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
        var vectorCollection = vectorStore.GetCollection<string, SemanticSearchRecord>(EmbeddingConfig.IndexName);

        var options = new VectorSearchOptions<SemanticSearchRecord>();
        if (filenameFilter is { Length: > 0 })
        {
            options.Filter = r => r.FileName == filenameFilter;
        }

        var results = new List<SemanticSearchRecord>();
        await foreach (var item in vectorCollection.SearchAsync(queryEmbedding, maxResults, options))
        {
            results.Add(item.Record);
        }

        return results;
    }
}
