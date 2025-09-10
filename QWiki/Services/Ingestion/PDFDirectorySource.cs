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
        });
    }

    //QuorumSoftware/Enterprise Platform/Platform Releases/QFC 2024.10/API Security Enhancements/Comprehensive Test Plan
    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsyncForWiki(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string wikiLink)
    {
        string apiUrl = $"https://dev.azure.com/quorumsoftware/ecaedfc6-005f-4ee9-aa66-6da8c71a6ad1/_apis/wiki/wikis/7ce3a273-b700-4d7e-9f92-82579271086a/pages/{wikiLink}?api-version=5.0&includeContent=true"; // RecursionLevel=OneLevel";
        string userFriendlyUrl = $"https://dev.azure.com/quorumsoftware/ecaedfc6-005f-4ee9-aa66-6da8c71a6ad1/_wiki/wikis/7ce3a273-b700-4d7e-9f92-82579271086a?pagePath=%2F{Uri.EscapeDataString(wikiLink)}";

        string pat = configuration.GetSection("AzureDevOps:Pat").Value!;
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}")));

        HttpResponseMessage response = await client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();

        WikiResponse wikiResponse = JsonSerializer.Deserialize<WikiResponse>(responseBody)!;
        string content = wikiResponse.Content;

        MarkdownChunker chunker = new MarkdownChunker();
        var chunks = chunker.ChunkMarkdown(content);

        var embeddings = await embeddingGenerator.GenerateAsync(chunks);

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
