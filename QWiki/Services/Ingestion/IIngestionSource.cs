using Microsoft.Extensions.AI;

namespace QWiki.Services.Ingestion;

public interface IIngestionSource
{
    string SourceId { get; }

    Task<IEnumerable<IngestedDocument>> GetNewOrModifiedDocumentsAsync(IQueryable<IngestedDocument> existingDocuments);

    Task<IEnumerable<IngestedDocument>> GetDeletedDocumentsAsync(IQueryable<IngestedDocument> existingDocuments);

    Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string documentId);

    Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsyncForWiki(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string wikiLink);

    Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForMultipleWikiLinksAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IEnumerable<string> wikiLinks);

    /// <summary>
    /// Creates semantic search records for all wiki pages under the specified root paths.
    /// This enables bulk ingestion of entire wiki collections.
    /// </summary>
    /// <param name="embeddingGenerator">The embedding generator to use</param>
    /// <param name="rootPaths">List of root wiki paths to ingest (e.g., ["Maintenance", "Development"])</param>
    /// <returns>All semantic search records for the wiki collection</returns>
    Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForWikiCollectionAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IEnumerable<string> rootPaths);
}
