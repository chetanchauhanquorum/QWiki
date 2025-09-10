using Microsoft.Extensions.VectorData;

namespace QWiki.Services;

public class SemanticSearchRecord
{
    [VectorStoreRecordKey]
    public required string Key { get; set; }

    [VectorStoreRecordData]
    public required string FileName { get; set; }

    [VectorStoreRecordData]
    public int PageNumber { get; set; }

    [VectorStoreRecordData]
    public string RecordType { get; set; } = "PDF";

    [VectorStoreRecordData]
    public string? SourceUrl { get; set; }

    [VectorStoreRecordData]
    public required string Text { get; set; }

    [VectorStoreRecordVector(1536, DistanceFunction.CosineSimilarity)] // 1536 is the default vector size for the OpenAI text-embedding-3-small model
    public ReadOnlyMemory<float> Vector { get; set; }
}
