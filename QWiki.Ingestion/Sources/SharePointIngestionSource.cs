using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using QWiki.Shared;

namespace QWiki.Ingestion.Sources;

public class SharePointIngestionSource : IIngestionSource
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SharePointIngestionSource> _logger;
    private readonly GraphServiceClient _graphClient;
    private readonly string[] _supportedExtensions;
    private readonly long _maxFileSizeBytes;

    private string _siteId = default!;
    private string _driveId = default!;
    private string _rootFolderId = default!;

    // All files discovered in the current run: docId -> DriveItemInfo
    private Dictionary<string, DriveItemInfo>? _discoveredFiles;

    public SharePointIngestionSource(IConfiguration configuration, ILogger<SharePointIngestionSource> logger)
    {
        _configuration = configuration;
        _logger = logger;

        var tenantId = configuration["SharePointIngestion:TenantId"]
            ?? throw new InvalidOperationException("Missing SharePointIngestion:TenantId");
        var clientId = configuration["SharePointIngestion:ClientId"]
            ?? throw new InvalidOperationException("Missing SharePointIngestion:ClientId");
        var clientSecret = configuration["SharePointIngestion:ClientSecret"]
            ?? throw new InvalidOperationException("Missing SharePointIngestion:ClientSecret. Use 'dotnet user-secrets set SharePointIngestion:ClientSecret YOUR-SECRET'.");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        _graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);

        _supportedExtensions = configuration.GetSection("SharePointIngestion:SupportedExtensions")
            .Get<string[]>() ?? [".pdf", ".pptx", ".docx", ".mp4", ".mkv"];
        _maxFileSizeBytes = (configuration.GetValue<int?>("SharePointIngestion:MaxFileSizeMB") ?? 100) * 1024L * 1024L;
    }

    public string SourceId => "SharePoint";

    /// <summary>
    /// Resolves siteId, driveId, and rootFolderId from Graph API. Must be called before ingestion.
    /// </summary>
    public async Task InitializeAsync()
    {
        var siteHostname = _configuration["SharePointIngestion:SiteHostname"]
            ?? throw new InvalidOperationException("Missing SharePointIngestion:SiteHostname");
        var sitePath = _configuration["SharePointIngestion:SitePath"]
            ?? throw new InvalidOperationException("Missing SharePointIngestion:SitePath");

        // Resolve site ID
        var site = await _graphClient.Sites[$"{siteHostname}:{sitePath}"].GetAsync();
        _siteId = site?.Id ?? throw new InvalidOperationException($"Could not resolve SharePoint site: {siteHostname}{sitePath}");

        // Resolve drive ID for the document library
        var libraryName = _configuration["SharePointIngestion:DocumentLibraryName"] ?? "Documents";
        var drives = await _graphClient.Sites[_siteId].Drives.GetAsync();
        var drive = drives?.Value?.FirstOrDefault(d =>
            string.Equals(d.Name, libraryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Document library '{libraryName}' not found on site {_siteId}");
        _driveId = drive.Id!;

        // Resolve root folder item ID (optional — if not set, use drive root)
        var rootFolder = _configuration["SharePointIngestion:RootFolder"];
        if (!string.IsNullOrEmpty(rootFolder))
        {
            var folderItem = await _graphClient.Drives[_driveId].Root.ItemWithPath(rootFolder).GetAsync();
            _rootFolderId = folderItem?.Id
                ?? throw new InvalidOperationException($"Root folder '{rootFolder}' not found in drive {_driveId}");
        }
        else
        {
            _rootFolderId = "root";
        }

        _logger.LogInformation("SharePoint initialized: Site={SiteId}, Drive={DriveId}, RootFolder={FolderId}",
            _siteId, _driveId, _rootFolderId);
    }

    // --- IIngestionSource implementation ---

    public async Task<IEnumerable<IngestedDocument>> GetDeletedDocumentsAsync(IDictionary<string, IngestedDocument> existingDocuments)
    {
        await EnsureFilesDiscoveredAsync();

        return existingDocuments.Values
            .Where(d => !_discoveredFiles!.ContainsKey(d.Id))
            .ToList();
    }

    public async Task<IEnumerable<IngestedDocument>> GetNewOrModifiedDocumentsAsync(IDictionary<string, IngestedDocument> existingDocuments)
    {
        await EnsureFilesDiscoveredAsync();

        var results = new List<IngestedDocument>();
        var skippedCount = 0;
        var videoSkippedCount = 0;

        foreach (var (docId, info) in _discoveredFiles!)
        {
            // Videos are discovered for tracking but skipped for content extraction
            if (info.Extension is ".mp4" or ".mkv")
            {
                videoSkippedCount++;
                continue;
            }

            existingDocuments.TryGetValue(docId, out var existingDoc);

            if (existingDoc is null)
            {
                results.Add(new IngestedDocument
                {
                    Id = docId,
                    Version = info.LastModifiedDateTime,
                    SourceId = SourceId
                });
            }
            else if (existingDoc.Version != info.LastModifiedDateTime)
            {
                existingDoc.Version = info.LastModifiedDateTime;
                results.Add(existingDoc);
            }
            else
            {
                skippedCount++;
            }
        }

        _logger.LogInformation(
            "SharePoint scan complete: {Total} files discovered, {Changed} new/modified, {Skipped} unchanged, {Videos} videos skipped (Phase 3c)",
            _discoveredFiles.Count, results.Count, skippedCount, videoSkippedCount);

        return results;
    }

    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string documentId)
    {
        if (!_discoveredFiles!.TryGetValue(documentId, out var info))
        {
            _logger.LogWarning("Document {DocId} not found in discovery cache", documentId);
            return [];
        }

        try
        {
            return info.Extension switch
            {
                ".pdf" => await ProcessPdfAsync(embeddingGenerator, documentId, info),
                ".pptx" => await ProcessPptxAsync(embeddingGenerator, documentId, info),
                ".docx" => await ProcessDocxAsync(embeddingGenerator, documentId, info),
                ".mp4" or ".mkv" => LogVideoSkipped(documentId),
                _ => []
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing SharePoint document {DocId} ({Extension})", documentId, info.Extension);
            return [];
        }
    }

    // --- Discovery ---

    private async Task EnsureFilesDiscoveredAsync()
    {
        if (_discoveredFiles != null) return;

        _discoveredFiles = new Dictionary<string, DriveItemInfo>();
        await DiscoverFilesRecursivelyAsync(_rootFolderId, "");
        _logger.LogInformation("SharePoint discovery complete: {Count} files found", _discoveredFiles.Count);
    }

    private async Task DiscoverFilesRecursivelyAsync(string folderId, string parentPath)
    {
        var subfolders = new List<(string Id, string Name)>();

        var itemId = folderId == "root" ? "root" : folderId;
        var response = await _graphClient.Drives[_driveId].Items[itemId].Children.GetAsync(config =>
        {
            config.QueryParameters.Select = ["id", "name", "file", "folder", "size", "lastModifiedDateTime", "webUrl"];
            config.QueryParameters.Top = 200;
        });

        while (response?.Value != null)
        {
            foreach (var item in response.Value)
            {
                if (item.Folder != null)
                {
                    subfolders.Add((item.Id!, item.Name!));
                }
                else if (item.File != null)
                {
                    var ext = Path.GetExtension(item.Name)?.ToLowerInvariant();
                    if (ext != null && _supportedExtensions.Contains(ext) && (item.Size ?? 0) <= _maxFileSizeBytes)
                    {
                        var docId = BuildDocumentId(parentPath, item.Name!);
                        _discoveredFiles![docId] = new DriveItemInfo(
                            DriveItemId: item.Id!,
                            Name: item.Name!,
                            LastModifiedDateTime: item.LastModifiedDateTime?.ToString("o") ?? "",
                            WebUrl: item.WebUrl ?? "",
                            Extension: ext,
                            Size: item.Size ?? 0,
                            ParentPath: parentPath);
                    }
                }
            }

            // Get next page if available
            if (!string.IsNullOrEmpty(response.OdataNextLink))
            {
                response = await _graphClient.Drives[_driveId].Items[folderId].Children
                    .WithUrl(response.OdataNextLink).GetAsync();
            }
            else
            {
                break;
            }
        }

        // Recurse into subfolders
        foreach (var (subId, subName) in subfolders)
        {
            var subPath = string.IsNullOrEmpty(parentPath) ? subName : $"{parentPath}/{subName}";
            await DiscoverFilesRecursivelyAsync(subId, subPath);
        }
    }

    // --- Content extraction ---

    private async Task<Stream> DownloadFileAsync(string driveItemId)
    {
        var stream = await _graphClient.Drives[_driveId].Items[driveItemId].Content.GetAsync();
        return stream ?? throw new InvalidOperationException($"Empty content stream for drive item {driveItemId}");
    }

    private async Task<IEnumerable<SemanticSearchRecord>> ProcessPdfAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string documentId, DriveItemInfo info)
    {
        using var stream = await DownloadFileAsync(info.DriveItemId);
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        var pages = ContentExtractor.ExtractTextFromPdf(memoryStream);
        var allChunks = new List<(int PageNumber, int Index, string Text)>();

        foreach (var (pageNumber, text) in pages)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var chunks = ContentExtractor.ChunkPlainText(text);
            allChunks.AddRange(chunks.Select((t, idx) => (pageNumber, idx, t)));
        }

        if (allChunks.Count == 0) return [];

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var embeddings = await embeddingGenerator.GenerateAsync(
            allChunks.Select(c => c.Text), cancellationToken: cts.Token);

        return allChunks.Zip(embeddings).Select((pair, _) => new SemanticSearchRecord
        {
            Key = ContentExtractor.SanitizeKey($"sp-{documentId}-p{pair.First.PageNumber}-{pair.First.Index}"),
            FileName = info.Name,
            PageNumber = pair.First.PageNumber,
            RecordType = "PDF",
            SourceUrl = info.WebUrl,
            Text = pair.First.Text,
            Vector = pair.Second.Vector
        });
    }

    private async Task<IEnumerable<SemanticSearchRecord>> ProcessPptxAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string documentId, DriveItemInfo info)
    {
        using var stream = await DownloadFileAsync(info.DriveItemId);
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        var slideTexts = ContentExtractor.ExtractTextFromPowerPoint(memoryStream);
        var allChunks = new List<(int SlideNumber, int Index, string Text)>();

        foreach (var (slideNumber, text) in slideTexts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var chunks = ContentExtractor.ChunkPlainText(text);
            allChunks.AddRange(chunks.Select((t, idx) => (slideNumber, idx, t)));
        }

        if (allChunks.Count == 0) return [];

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var embeddings = await embeddingGenerator.GenerateAsync(
            allChunks.Select(c => c.Text), cancellationToken: cts.Token);

        return allChunks.Zip(embeddings).Select((pair, _) => new SemanticSearchRecord
        {
            Key = ContentExtractor.SanitizeKey($"sp-{documentId}-s{pair.First.SlideNumber}-{pair.First.Index}"),
            FileName = info.Name,
            PageNumber = pair.First.SlideNumber,
            RecordType = "PPTX",
            SourceUrl = info.WebUrl,
            Text = pair.First.Text,
            Vector = pair.Second.Vector
        });
    }

    private async Task<IEnumerable<SemanticSearchRecord>> ProcessDocxAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string documentId, DriveItemInfo info)
    {
        using var stream = await DownloadFileAsync(info.DriveItemId);
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        var text = ContentExtractor.ExtractTextFromWord(memoryStream);
        if (string.IsNullOrWhiteSpace(text)) return [];

        var chunks = ContentExtractor.ChunkPlainText(text).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (chunks.Count == 0) return [];

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var embeddings = await embeddingGenerator.GenerateAsync(chunks, cancellationToken: cts.Token);

        return chunks.Zip(embeddings).Select((pair, index) => new SemanticSearchRecord
        {
            Key = ContentExtractor.SanitizeKey($"sp-{documentId}-{index}"),
            FileName = info.Name,
            PageNumber = 1,
            RecordType = "DOCX",
            SourceUrl = info.WebUrl,
            Text = pair.First,
            Vector = pair.Second.Vector
        });
    }

    private IEnumerable<SemanticSearchRecord> LogVideoSkipped(string documentId)
    {
        _logger.LogInformation("Skipping video {DocId} — no transcript API available (deferred to Phase 3c)", documentId);
        return [];
    }

    // --- Helpers ---

    private static string BuildDocumentId(string parentPath, string fileName)
        => string.IsNullOrEmpty(parentPath) ? fileName : $"{parentPath}/{fileName}";

    // --- Inner types ---

    private record DriveItemInfo(
        string DriveItemId,
        string Name,
        string LastModifiedDateTime,
        string WebUrl,
        string Extension,
        long Size,
        string ParentPath);
}
