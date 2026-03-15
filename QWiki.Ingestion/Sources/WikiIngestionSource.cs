using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QWiki.Shared;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QWiki.Ingestion.Sources;

public class WikiIngestionSource : IIngestionSource
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<WikiIngestionSource> _logger;
    private readonly string[] _rootPaths;

    // In-memory cache: pagePath -> (content, contentHash)
    // Populated during discovery, consumed by CreateRecordsForDocumentAsync to avoid double API calls
    private readonly ConcurrentDictionary<string, WikiPageContent> _pageCache = new();

    // All page paths discovered in the current run (used for deletion detection)
    private HashSet<string>? _discoveredPagePaths;

    public WikiIngestionSource(IConfiguration configuration, ILogger<WikiIngestionSource> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _rootPaths = configuration.GetSection("WikiIngestion:RootPaths").Get<string[]>() ?? [];
    }

    public string SourceId => "AzureDevOpsWiki";

    public async Task<IEnumerable<IngestedDocument>> GetDeletedDocumentsAsync(IDictionary<string, IngestedDocument> existingDocuments)
    {
        // Ensure page discovery has happened (DataIngestor calls this BEFORE GetNewOrModifiedDocumentsAsync)
        await EnsurePagesDiscoveredAsync();

        return existingDocuments.Values
            .Where(d => !_discoveredPagePaths!.Contains(d.Id))
            .ToList();
    }

    public async Task<IEnumerable<IngestedDocument>> GetNewOrModifiedDocumentsAsync(IDictionary<string, IngestedDocument> existingDocuments)
    {
        await EnsurePagesDiscoveredAsync();

        var results = new List<IngestedDocument>();
        var skippedCount = 0;

        foreach (var pagePath in _discoveredPagePaths!)
        {
            // Fetch content and compute hash
            string? content;
            try
            {
                content = await FetchWikiPageContentAsync(pagePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error fetching wiki page '{Page}': {Error}", pagePath, ex.Message);
                continue;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                continue; // Skip empty/folder pages
            }

            var contentHash = ComputeContentHash(content);
            _pageCache[pagePath] = new WikiPageContent(content, contentHash);

            existingDocuments.TryGetValue(pagePath, out var existingDoc);

            if (existingDoc is null)
            {
                results.Add(new IngestedDocument
                {
                    Id = pagePath,
                    Version = contentHash,
                    SourceId = SourceId
                });
            }
            else if (existingDoc.Version != contentHash)
            {
                existingDoc.Version = contentHash;
                results.Add(existingDoc);
            }
            else
            {
                skippedCount++;
            }
        }

        _logger.LogInformation(
            "Wiki scan complete: {Total} pages discovered, {Changed} new/modified, {Skipped} unchanged",
            _discoveredPagePaths.Count, results.Count, skippedCount);

        return results;
    }

    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string documentId)
    {
        // Try to use cached content first (avoids second API call)
        string content;
        if (_pageCache.TryRemove(documentId, out var cached))
        {
            content = cached.Content;
        }
        else
        {
            content = await FetchWikiPageContentAsync(documentId) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var chunker = new MarkdownChunker();
        var chunks = chunker.ChunkMarkdown(content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        if (chunks.Count == 0)
        {
            return [];
        }

        var userFriendlyUrl = BuildUserFriendlyUrl(documentId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var embeddings = await embeddingGenerator.GenerateAsync(chunks, cancellationToken: cts.Token);

        return chunks.Zip(embeddings).Select((pair, index) => new SemanticSearchRecord
        {
            Key = SanitizeKey($"{documentId}_{index}"),
            RecordType = "WIKI",
            FileName = documentId.Split('/').LastOrDefault() ?? documentId,
            SourceUrl = userFriendlyUrl,
            PageNumber = 1,
            Text = pair.First,
            Vector = pair.Second.Vector,
        });
    }

    // --- Discovery and API helpers ---

    private async Task EnsurePagesDiscoveredAsync()
    {
        if (_discoveredPagePaths != null) return;

        _discoveredPagePaths = new HashSet<string>();
        foreach (var rootPath in _rootPaths)
        {
            try
            {
                var pagePaths = await ListWikiPagesRecursivelyAsync(rootPath);
                foreach (var p in pagePaths)
                    _discoveredPagePaths.Add(p);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error listing wiki pages under '{Root}': {Error}", rootPath, ex.Message);
            }
        }
    }

    private async Task<string?> FetchWikiPageContentAsync(string pagePath)
    {
        string apiUrl = $"https://dev.azure.com/quorumsoftware/ecaedfc6-005f-4ee9-aa66-6da8c71a6ad1/_apis/wiki/wikis/7ce3a273-b700-4d7e-9f92-82579271086a/pages/{pagePath}?api-version=5.0&includeContent=true";

        string pat = _configuration.GetSection("AzureDevOps:Pat").Value!;
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

        HttpResponseMessage response = await client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();

        var wikiResponse = JsonSerializer.Deserialize<WikiResponse>(responseBody);
        return wikiResponse?.Content;
    }

    private async Task<IEnumerable<string>> ListWikiPagesRecursivelyAsync(string rootPath)
    {
        string apiUrl = $"https://dev.azure.com/quorumsoftware/ecaedfc6-005f-4ee9-aa66-6da8c71a6ad1/_apis/wiki/wikis/7ce3a273-b700-4d7e-9f92-82579271086a/pages?path=/{Uri.EscapeDataString(rootPath)}&recursionLevel=full&api-version=7.1";

        string pat = _configuration.GetSection("AzureDevOps:Pat").Value!;
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

        HttpResponseMessage response = await client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();

        var wikiPageResponse = JsonSerializer.Deserialize<WikiPageListResponse>(responseBody);
        if (wikiPageResponse == null) return [];

        var allPaths = new List<string>();
        CollectPagePaths(wikiPageResponse, allPaths);

        _logger.LogInformation("Found {Count} wiki pages under '{Root}'", allPaths.Count, rootPath);
        return allPaths;
    }

    private static string BuildUserFriendlyUrl(string pagePath) =>
        $"https://dev.azure.com/quorumsoftware/ecaedfc6-005f-4ee9-aa66-6da8c71a6ad1/_wiki/wikis/7ce3a273-b700-4d7e-9f92-82579271086a?pagePath=%2F{Uri.EscapeDataString(pagePath)}";

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    public static string SanitizeKey(string key) =>
        Regex.Replace(key, @"[^a-zA-Z0-9_\-=]", "-");

    // --- Recursive page path collection ---

    private static void CollectPagePaths(WikiPageListResponse page, List<string> paths)
    {
        var path = page.Path?.TrimStart('/');
        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);

        if (page.SubPages != null)
            foreach (var subPage in page.SubPages)
                CollectPagePathsFromWikiPage(subPage, paths);
    }

    private static void CollectPagePathsFromWikiPage(WikiPage page, List<string> paths)
    {
        var path = page.Path?.TrimStart('/');
        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);

        if (page.SubPages != null)
            foreach (var subPage in page.SubPages)
                CollectPagePathsFromWikiPage(subPage, paths);
    }

    // --- Inner types ---

    private record WikiPageContent(string Content, string ContentHash);

    public class WikiPageListResponse
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("isParentPage")]
        public bool IsParentPage { get; set; }

        [JsonPropertyName("gitItemPath")]
        public string GitItemPath { get; set; } = string.Empty;

        [JsonPropertyName("subPages")]
        public List<WikiPage>? SubPages { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("remoteUrl")]
        public string RemoteUrl { get; set; } = string.Empty;
    }

    public class WikiPage
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("isParentPage")]
        public bool IsParentPage { get; set; }

        [JsonPropertyName("gitItemPath")]
        public string GitItemPath { get; set; } = string.Empty;

        [JsonPropertyName("subPages")]
        public List<WikiPage> SubPages { get; set; } = [];

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("remoteUrl")]
        public string RemoteUrl { get; set; } = string.Empty;
    }

    public class WikiResponse
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    public class MarkdownChunker
    {
        public List<string> ChunkMarkdown(string content, int maxWords = 300, int overlapWords = 50)
        {
            var chunks = new List<string>();
            var sections = Regex.Split(content, @"(?=^#{1,6} .*)", RegexOptions.Multiline);

            foreach (var section in sections)
            {
                var paragraphs = Regex.Split(section.Trim(), @"\n\s*\n");
                var currentChunk = new List<string>();
                int currentWordCount = 0;

                for (int i = 0; i < paragraphs.Length; i++)
                {
                    string para = paragraphs[i].Trim();
                    int paraWordCount = CountWords(para);

                    if (currentWordCount + paraWordCount > maxWords)
                    {
                        chunks.Add(string.Join("\n\n", currentChunk));
                        currentChunk = GetOverlapParagraphs(currentChunk, overlapWords);
                        currentWordCount = CountWords(string.Join(" ", currentChunk));
                    }

                    currentChunk.Add(para);
                    currentWordCount += paraWordCount;
                }

                if (currentChunk.Count > 0)
                {
                    chunks.Add(string.Join("\n\n", currentChunk));
                }
            }

            return chunks;
        }

        private static int CountWords(string text) =>
            text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

        private static List<string> GetOverlapParagraphs(List<string> chunk, int targetOverlapWords)
        {
            var overlap = new List<string>();
            int wordCount = 0;

            for (int i = chunk.Count - 1; i >= 0; i--)
            {
                var para = chunk[i];
                int paraWords = CountWords(para);
                overlap.Insert(0, para);
                wordCount += paraWords;

                if (wordCount >= targetOverlapWords)
                    break;
            }

            return overlap;
        }
    }
}
