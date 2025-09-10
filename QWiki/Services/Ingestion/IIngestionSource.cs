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
}
