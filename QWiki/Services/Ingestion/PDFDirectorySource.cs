using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Text;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace QWiki.Services.Ingestion;

public class PDFDirectorySource(IConfiguration configuration, string sourceDirectory) : IIngestionSource
{

    public static string SourceFileId(string path) => Path.GetFileName(path);

    public string SourceId => $"{nameof(PDFDirectorySource)}:{sourceDirectory}";

    public async Task<IEnumerable<IngestedDocument>> GetNewOrModifiedDocumentsAsync(IQueryable<IngestedDocument> existingDocuments)
    {
        var results = new List<IngestedDocument>();
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.pdf");

        foreach (var sourceFile in sourceFiles)
        {
            var sourceFileId = SourceFileId(sourceFile);
            var sourceFileVersion = File.GetLastWriteTimeUtc(sourceFile).ToString("o");

            var existingDocument = await existingDocuments.Where(d => d.SourceId == SourceId && d.Id == sourceFileId).FirstOrDefaultAsync();
            if (existingDocument is null)
            {
                results.Add(new() { Id = sourceFileId, Version = sourceFileVersion, SourceId = SourceId });
            }
            else if (existingDocument.Version != sourceFileVersion)
            {
                existingDocument.Version = sourceFileVersion;
                results.Add(existingDocument);
            }
        }

        return results;
    }

    public async Task<IEnumerable<IngestedDocument>> GetDeletedDocumentsAsync(IQueryable<IngestedDocument> existingDocuments)
    {
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.pdf");
        var sourceFileIds = sourceFiles.Select(SourceFileId).ToList();
        return await existingDocuments
            .Where(d => !sourceFileIds.Contains(d.Id))
            .ToListAsync();
    }

    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string documentId)
    {
        using var pdf = PdfDocument.Open(Path.Combine(sourceDirectory, documentId));
        var paragraphs = pdf.GetPages().SelectMany(GetPageParagraphs).ToList();

        var embeddings = await embeddingGenerator.GenerateAsync(paragraphs.Select(c => c.Text));

        return paragraphs.Zip(embeddings).Select((pair, index) => new SemanticSearchRecord
        {
            Key = $"{Path.GetFileNameWithoutExtension(documentId)}_{pair.First.PageNumber}_{pair.First.IndexOnPage}",
            FileName = documentId,
            PageNumber = pair.First.PageNumber,
            Text = pair.First.Text,
            Vector = pair.Second.Vector,
            SourceUrl = $"/Data/{documentId}" // Relative URL to the PDF file
        });
    }

    //QuorumSoftware/Enterprise Platform/Platform Releases/QFC 2024.10/API Security Enhancements/Comprehensive Test Plan
    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsyncForWiki(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string wikiLink)
    {
        string apiUrl = $"https://dev.azure.com/quorumsoftware/ecaedfc6-005f-4ee9-aa66-6da8c71a6ad1/_apis/wiki/wikis/7ce3a273-b700-4d7e-9f92-82579271086a/pages/{wikiLink}?api-version=5.0&includeContent=true";
        string userFriendlyUrl = $"https://dev.azure.com/quorumsoftware/ecaedfc6-005f-4ee9-aa66-6da8c71a6ad1/_wiki/wikis/7ce3a273-b700-4d7e-9f92-82579271086a?pagePath=%2F{Uri.EscapeDataString(wikiLink)}";

        string pat = configuration.GetSection("AzureDevOps:Pat").Value!;
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}")));

        Console.Write("fetching... "); Console.Out.Flush();
        HttpResponseMessage response = await client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();

        WikiResponse wikiResponse = JsonSerializer.Deserialize<WikiResponse>(responseBody)!;
        string content = wikiResponse.Content;

        // Skip pages with empty or whitespace-only content (parent/folder pages)
        if (string.IsNullOrWhiteSpace(content))
        {
            Console.WriteLine($"Skipping wiki page '{wikiLink}' - no content (parent/folder page)");
            return [];
        }

        MarkdownChunker chunker = new MarkdownChunker();
        var chunks = chunker.ChunkMarkdown(content);

        // Filter out empty chunks
        chunks = chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        if (chunks.Count == 0)
        {
            Console.WriteLine($"Skipping wiki page '{wikiLink}' - no meaningful text after chunking");
            return [];
        }

        Console.Write($"embedding {chunks.Count} chunks... "); Console.Out.Flush();
        using var embeddingCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var embeddings = await embeddingGenerator.GenerateAsync(chunks, cancellationToken: embeddingCts.Token);

        return chunks.Zip(embeddings).Select((pair, index) => new SemanticSearchRecord
        {
            Key = $"{wikiLink}_{index}",
            RecordType = "WIKI",
            FileName = wikiLink.Split('/').LastOrDefault() ?? wikiLink, // Use the last part of the path as display name
            SourceUrl = userFriendlyUrl,
            PageNumber = 1,
            Text = pair.First,
            Vector = pair.Second.Vector,
        });
    }

    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForMultipleWikiLinksAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IEnumerable<string> wikiLinks)
    {
        var allRecords = new List<SemanticSearchRecord>();

        foreach (var wikiLink in wikiLinks)
        {
            try
            {
                var records = await CreateRecordsForDocumentAsyncForWiki(embeddingGenerator, wikiLink);
                allRecords.AddRange(records);
            }
            catch (Exception ex)
            {
                // Log the error but continue processing other wiki links
                Console.WriteLine($"Error processing wiki link '{wikiLink}': {ex.Message}");
            }
        }

        return allRecords;
    }

    /// <summary>
    /// Lists all wiki pages recursively under a given root path using Azure DevOps API.
    /// </summary>
    /// <param name="rootPath">The root wiki path (e.g., "Maintenance")</param>
    /// <returns>List of all page paths under the root</returns>
    public async Task<IEnumerable<string>> ListWikiPagesRecursivelyAsync(string rootPath)
    {
        // Azure DevOps Wiki API with recursionLevel=full returns all pages in hierarchy
        string apiUrl = $"https://dev.azure.com/quorumsoftware/ecaedfc6-005f-4ee9-aa66-6da8c71a6ad1/_apis/wiki/wikis/7ce3a273-b700-4d7e-9f92-82579271086a/pages?path=/{Uri.EscapeDataString(rootPath)}&recursionLevel=full&api-version=7.1";

        string pat = configuration.GetSection("AzureDevOps:Pat").Value!;
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}")));

        HttpResponseMessage response = await client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();

        var wikiPageResponse = JsonSerializer.Deserialize<WikiPageListResponse>(responseBody);
        if (wikiPageResponse == null)
        {
            return [];
        }

        // Flatten the page hierarchy into a list of paths
        var allPaths = new List<string>();
        CollectPagePaths(wikiPageResponse, allPaths);

        Console.WriteLine($"Found {allPaths.Count} wiki pages under '{rootPath}'");
        return allPaths;
    }

    /// <summary>
    /// Recursively collects all page paths from the wiki page hierarchy.
    /// </summary>
    private static void CollectPagePaths(WikiPageListResponse page, List<string> paths)
    {
        // Add the current page path (remove leading slash if present)
        var path = page.Path?.TrimStart('/');
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path);
        }

        // Recursively process sub-pages
        if (page.SubPages != null)
        {
            foreach (var subPage in page.SubPages)
            {
                CollectPagePathsFromWikiPage(subPage, paths);
            }
        }
    }

    /// <summary>
    /// Recursively collects page paths from WikiPage objects.
    /// </summary>
    private static void CollectPagePathsFromWikiPage(WikiPage page, List<string> paths)
    {
        var path = page.Path?.TrimStart('/');
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path);
        }

        if (page.SubPages != null)
        {
            foreach (var subPage in page.SubPages)
            {
                CollectPagePathsFromWikiPage(subPage, paths);
            }
        }
    }

    /// <summary>
    /// Creates semantic search records for all wiki pages under the specified root paths.
    /// This is the bulk ingestion method for Strategy A.
    /// </summary>
    /// <param name="embeddingGenerator">The embedding generator to use</param>
    /// <param name="rootPaths">List of root wiki paths to ingest (e.g., ["Maintenance", "Development"])</param>
    /// <returns>All semantic search records for the wiki collection</returns>
    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForWikiCollectionAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IEnumerable<string> rootPaths)
    {
        var allRecords = new List<SemanticSearchRecord>();
        var processedCount = 0;
        var errorCount = 0;

        foreach (var rootPath in rootPaths)
        {
            Console.WriteLine($"Discovering wiki pages under '{rootPath}'...");

            try
            {
                // Get all page paths under this root
                var pagePaths = await ListWikiPagesRecursivelyAsync(rootPath);
                var pagePathsList = pagePaths.ToList();
                var totalPages = pagePathsList.Count;

                Console.WriteLine($"Starting ingestion of {totalPages} pages from '{rootPath}'...");

                foreach (var pagePath in pagePathsList)
                {
                    try
                    {
                        Console.Write($"[{processedCount + errorCount + 1}/{totalPages}] Processing: {pagePath}... ");
                        Console.Out.Flush();
                        var records = await CreateRecordsForDocumentAsyncForWiki(embeddingGenerator, pagePath);
                        allRecords.AddRange(records);
                        processedCount++;
                        Console.WriteLine($"OK ({records.Count()} chunks)");
                        Console.Out.Flush();

                        // Log progress every 10 pages
                        if (processedCount % 10 == 0)
                        {
                            Console.WriteLine($"Wiki ingestion progress: {processedCount} pages processed, {allRecords.Count} chunks created");
                            Console.Out.Flush();
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        Console.WriteLine($"ERROR: {ex.Message}");
                        Console.Out.Flush();
                        // Continue processing other pages
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listing wiki pages under '{rootPath}': {ex.Message}");
            }
        }

        Console.WriteLine($"Wiki collection ingestion complete: {processedCount} pages processed, {allRecords.Count} chunks created, {errorCount} errors");
        return allRecords;
    }

    /// <summary>
    /// Response model for the wiki pages list API endpoint.
    /// </summary>
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


    private static IEnumerable<(int PageNumber, int IndexOnPage, string Text)> GetPageParagraphs(Page pdfPage)
    {
        var letters = pdfPage.Letters;
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters);
        var textBlocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var pageText = string.Join(Environment.NewLine + Environment.NewLine,
            textBlocks.Select(t => t.Text.ReplaceLineEndings(" ")));

#pragma warning disable SKEXP0050 // Type is for evaluation purposes only
        return TextChunker.SplitPlainTextParagraphs([pageText], 200)
            .Select((text, index) => (pdfPage.Number, index, text));
#pragma warning restore SKEXP0050 // Type is for evaluation purposes only
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

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    public class WikiResponse
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

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    public class MarkdownChunker
    {
        public List<string> ChunkMarkdown(string content, int maxWords = 300, int overlapWords = 50)
        {
            var chunks = new List<string>();

            // Split into sections based on markdown headers
            var sections = Regex.Split(content, @"(?=^#{1,6} .*)", RegexOptions.Multiline);

            foreach (var section in sections)
            {
                // Split section into paragraphs (double newlines)
                var paragraphs = Regex.Split(section.Trim(), @"\n\s*\n");

                var currentChunk = new List<string>();
                int currentWordCount = 0;

                for (int i = 0; i < paragraphs.Length; i++)
                {
                    string para = paragraphs[i].Trim();
                    int paraWordCount = CountWords(para);

                    if (currentWordCount + paraWordCount > maxWords)
                    {
                        // Finalize the current chunk
                        chunks.Add(string.Join("\n\n", currentChunk));

                        // Start next chunk with overlap from previous
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

        private int CountWords(string text)
        {
            return text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private List<string> GetOverlapParagraphs(List<string> chunk, int targetOverlapWords)
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
