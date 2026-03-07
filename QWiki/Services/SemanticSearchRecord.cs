using Microsoft.Extensions.VectorData;

namespace QWiki.Services;

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

    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)] // 1536 is the default vector size for the OpenAI text-embedding-3-small model
    public ReadOnlyMemory<float> Vector { get; set; }
}
