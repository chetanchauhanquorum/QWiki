using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QWiki.Shared;

namespace QWiki.Ingestion.Sources;

/// <summary>
/// Ingestion source that reads files from a local folder on disk.
/// Dev/test alternative to SharePointIngestionSource while Graph API admin consent is pending.
/// </summary>
public class LocalFolderIngestionSource : IIngestionSource
{
    private readonly ILogger<LocalFolderIngestionSource> _logger;
    private readonly string _folderPath;
    private readonly string[] _supportedExtensions;
    private readonly string? _sourceUrlPrefix;
    private readonly AudioTranscriber _audioTranscriber;

    // All files discovered in the current run: relative path -> LocalFileInfo
    private Dictionary<string, LocalFileInfo>? _discoveredFiles;

    public LocalFolderIngestionSource(
        IConfiguration configuration,
        ILogger<LocalFolderIngestionSource> logger,
        AudioTranscriber audioTranscriber)
    {
        _logger = logger;
        _audioTranscriber = audioTranscriber;

        var configuredPath = configuration["LocalFolderIngestion:FolderPath"]
            ?? throw new InvalidOperationException("Missing LocalFolderIngestion:FolderPath in appsettings.json.");

        // Resolve relative paths from the app's content root
        _folderPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", configuredPath);
        _folderPath = Path.GetFullPath(_folderPath);

        _supportedExtensions = configuration.GetSection("LocalFolderIngestion:SupportedExtensions")
            .Get<string[]>() ?? [".pdf", ".pptx", ".docx", ".mp4", ".mkv"];

        _sourceUrlPrefix = configuration["LocalFolderIngestion:SourceUrlPrefix"];
    }

    public string SourceId => "LocalFolder";

    // --- IIngestionSource implementation ---

    public Task<IEnumerable<IngestedDocument>> GetDeletedDocumentsAsync(IDictionary<string, IngestedDocument> existingDocuments)
    {
        EnsureFilesDiscovered();

        var deleted = existingDocuments.Values
            .Where(d => !_discoveredFiles!.ContainsKey(d.Id))
            .ToList();

        return Task.FromResult<IEnumerable<IngestedDocument>>(deleted);
    }

    public Task<IEnumerable<IngestedDocument>> GetNewOrModifiedDocumentsAsync(IDictionary<string, IngestedDocument> existingDocuments)
    {
        EnsureFilesDiscovered();

        var results = new List<IngestedDocument>();
        var skippedCount = 0;

        foreach (var (docId, info) in _discoveredFiles!)
        {
            existingDocuments.TryGetValue(docId, out var existingDoc);

            if (existingDoc is null)
            {
                results.Add(new IngestedDocument
                {
                    Id = docId,
                    Version = info.LastModified,
                    SourceId = SourceId
                });
            }
            else if (existingDoc.Version != info.LastModified)
            {
                existingDoc.Version = info.LastModified;
                results.Add(existingDoc);
            }
            else
            {
                skippedCount++;
            }
        }

        _logger.LogInformation(
            "Local folder scan complete: {Total} files discovered, {Changed} new/modified, {Skipped} unchanged",
            _discoveredFiles.Count, results.Count, skippedCount);

        return Task.FromResult<IEnumerable<IngestedDocument>>(results);
    }

    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string documentId)
    {
        if (!_discoveredFiles!.TryGetValue(documentId, out var info))
        {
            _logger.LogWarning("Document {DocId} not found in local folder discovery cache", documentId);
            return [];
        }

        try
        {
            return info.Extension switch
            {
                ".pdf" => await ProcessPdfAsync(embeddingGenerator, documentId, info),
                ".pptx" => await ProcessPptxAsync(embeddingGenerator, documentId, info),
                ".docx" => await ProcessDocxAsync(embeddingGenerator, documentId, info),
                ".mp4" or ".mkv" => await ProcessVideoAsync(embeddingGenerator, documentId, info),
                _ => []
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing local file {DocId} ({Extension})", documentId, info.Extension);
            return [];
        }
    }

    // --- Discovery ---

    private void EnsureFilesDiscovered()
    {
        if (_discoveredFiles != null) return;

        _discoveredFiles = new Dictionary<string, LocalFileInfo>();

        if (!Directory.Exists(_folderPath))
        {
            _logger.LogWarning("Local folder path does not exist: {Path}", _folderPath);
            return;
        }

        var allFiles = Directory.EnumerateFiles(_folderPath, "*.*", SearchOption.AllDirectories);

        foreach (var filePath in allFiles)
        {
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            if (ext == null || !_supportedExtensions.Contains(ext)) continue;

            var fileInfo = new FileInfo(filePath);
            var relativePath = Path.GetRelativePath(_folderPath, filePath).Replace('\\', '/');

            _discoveredFiles[relativePath] = new LocalFileInfo(
                FullPath: filePath,
                Name: fileInfo.Name,
                LastModified: fileInfo.LastWriteTimeUtc.ToString("o"),
                Extension: ext,
                Size: fileInfo.Length);
        }

        _logger.LogInformation("Local folder discovery complete: {Count} files found in {Path}",
            _discoveredFiles.Count, _folderPath);
    }

    // --- Content extraction ---

    private async Task<IEnumerable<SemanticSearchRecord>> ProcessPdfAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string documentId, LocalFileInfo info)
    {
        using var stream = File.OpenRead(info.FullPath);
        var pages = ContentExtractor.ExtractTextFromPdf(stream);

        var allChunks = new List<(int PageNumber, int Index, string Text)>();
        foreach (var (pageNumber, text) in pages)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var chunks = ContentExtractor.ChunkPlainText(text);
            allChunks.AddRange(chunks.Select((t, idx) => (pageNumber, idx, t)));
        }

        if (allChunks.Count == 0) return [];

        return await EmbedAndCreateRecords(embeddingGenerator, allChunks, documentId, info.Name,
            "PDF", (pageNum, idx) => $"lf-{documentId}-p{pageNum}-{idx}");
    }

    private async Task<IEnumerable<SemanticSearchRecord>> ProcessPptxAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string documentId, LocalFileInfo info)
    {
        using var stream = File.OpenRead(info.FullPath);
        var slides = ContentExtractor.ExtractTextFromPowerPoint(stream);

        var allChunks = new List<(int PageNumber, int Index, string Text)>();
        foreach (var (slideNumber, text) in slides)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var chunks = ContentExtractor.ChunkPlainText(text);
            allChunks.AddRange(chunks.Select((t, idx) => (slideNumber, idx, t)));
        }

        if (allChunks.Count == 0) return [];

        return await EmbedAndCreateRecords(embeddingGenerator, allChunks, documentId, info.Name,
            "PPTX", (slideNum, idx) => $"lf-{documentId}-s{slideNum}-{idx}");
    }

    private async Task<IEnumerable<SemanticSearchRecord>> ProcessDocxAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string documentId, LocalFileInfo info)
    {
        using var stream = File.OpenRead(info.FullPath);
        var text = ContentExtractor.ExtractTextFromWord(stream);
        if (string.IsNullOrWhiteSpace(text)) return [];

        var chunks = ContentExtractor.ChunkPlainText(text).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (chunks.Count == 0) return [];

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var embeddings = await embeddingGenerator.GenerateAsync(chunks, cancellationToken: cts.Token);

        return chunks.Zip(embeddings).Select((pair, index) => new SemanticSearchRecord
        {
            Key = ContentExtractor.SanitizeKey($"lf-{documentId}-{index}"),
            FileName = info.Name,
            PageNumber = 1,
            RecordType = "DOCX",
            SourceUrl = BuildSourceUrl(documentId),
            Text = pair.First,
            Vector = pair.Second.Vector
        });
    }

    private async Task<IEnumerable<SemanticSearchRecord>> ProcessVideoAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string documentId, LocalFileInfo info)
    {
        _logger.LogInformation("Transcribing video {DocId}...", documentId);

        var segments = await _audioTranscriber.TranscribeVideoAsync(
            info.FullPath, cacheKey: documentId, version: info.LastModified);

        if (segments.Count == 0)
        {
            _logger.LogWarning("No transcript produced for video {DocId}", documentId);
            return [];
        }

        var totalChars = segments.Sum(s => s.Text.Length);
        _logger.LogInformation("Video {DocId} transcribed: {Segments} segments, {Length} characters",
            documentId, segments.Count, totalChars);

        var chunks = ContentExtractor.ChunkTranscriptWithTimestamps(segments);
        var chunkTexts = chunks.Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (chunkTexts.Count == 0) return [];

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var embeddings = await embeddingGenerator.GenerateAsync(chunkTexts, cancellationToken: cts.Token);

        var sourceUrl = BuildSourceUrl(documentId);

        return chunks.Zip(embeddings).Select((pair, index) => new SemanticSearchRecord
        {
            Key = ContentExtractor.SanitizeKey($"lf-{documentId}-v{index}"),
            FileName = info.Name,
            PageNumber = ParseTimestampSeconds(pair.First.TimestampLabel),
            RecordType = "VIDEO",
            SourceUrl = sourceUrl,
            Text = pair.First.Text,
            Vector = pair.Second.Vector
        });
    }

    // --- Helpers ---

    private string? BuildSourceUrl(string relativePath)
    {
        if (_sourceUrlPrefix == null) return null;
        var encoded = Uri.EscapeDataString(relativePath.Replace('/', '\\'));
        return $"{_sourceUrlPrefix.TrimEnd('/')}/{encoded}";
    }

    private static int ParseTimestampSeconds(string label)
    {
        // Parse "[MM:SS]" or "[H:MM:SS]" to total seconds
        var inner = label.Trim('[', ']');
        if (TimeSpan.TryParse(inner.Contains(':') && inner.Split(':').Length == 2
            ? $"00:{inner}" : inner, out var ts))
        {
            return (int)ts.TotalSeconds;
        }
        return 0;
    }

    private async Task<IEnumerable<SemanticSearchRecord>> EmbedAndCreateRecords(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        List<(int PageNumber, int Index, string Text)> allChunks,
        string documentId, string fileName, string recordType,
        Func<int, int, string> keyBuilder)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var embeddings = await embeddingGenerator.GenerateAsync(
            allChunks.Select(c => c.Text), cancellationToken: cts.Token);

        var sourceUrl = BuildSourceUrl(documentId);

        return allChunks.Zip(embeddings).Select((pair, _) => new SemanticSearchRecord
        {
            Key = ContentExtractor.SanitizeKey(keyBuilder(pair.First.PageNumber, pair.First.Index)),
            FileName = fileName,
            PageNumber = pair.First.PageNumber,
            RecordType = recordType,
            SourceUrl = sourceUrl,
            Text = pair.First.Text,
            Vector = pair.Second.Vector
        });
    }

    // --- Inner types ---

    private record LocalFileInfo(
        string FullPath,
        string Name,
        string LastModified,
        string Extension,
        long Size);
}
