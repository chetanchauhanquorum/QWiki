using Microsoft.Extensions.AI;
using QWiki.Shared;

namespace QWiki.Ingestion;

public interface IIngestionSource
{
    string SourceId { get; }

    Task<IEnumerable<IngestedDocument>> GetNewOrModifiedDocumentsAsync(
        IDictionary<string, IngestedDocument> existingDocuments);

    Task<IEnumerable<IngestedDocument>> GetDeletedDocumentsAsync(
        IDictionary<string, IngestedDocument> existingDocuments);

    Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string documentId);
}
