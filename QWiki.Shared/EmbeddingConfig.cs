namespace QWiki.Shared;

/// <summary>
/// Centralized embedding model configuration.
/// Both the UI and ingestion Worker MUST use the same model to ensure vector compatibility.
/// </summary>
public static class EmbeddingConfig
{
    public const string ModelName = "text-embedding-3-small";
    public const int VectorDimension = 1536;
    public const string IndexName = "data-qwiki-ingested";
    public const string GitHubModelsEndpoint = "https://models.inference.ai.azure.com";
}
