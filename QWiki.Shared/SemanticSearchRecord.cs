using Microsoft.Extensions.VectorData;

namespace QWiki.Shared;

public class SemanticSearchRecord
{
    [VectorStoreKey]
    public required string Key { get; set; }

    [VectorStoreData(IsIndexed = true)]
    public required string FileName { get; set; }

    [VectorStoreData]
    public int PageNumber { get; set; }

    [VectorStoreData(IsIndexed = true)]
    public string RecordType { get; set; } = "PDF";

    [VectorStoreData]
    public string? SourceUrl { get; set; }

    [VectorStoreData(IsFullTextIndexed = true)]
    public required string Text { get; set; }

    [VectorStoreVector(EmbeddingConfig.VectorDimension, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Vector { get; set; }
}
