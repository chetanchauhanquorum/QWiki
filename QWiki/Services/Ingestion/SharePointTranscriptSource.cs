using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Text;
using System.Text.RegularExpressions;

namespace QWiki.Services.Ingestion;

public class SharePointTranscriptSource(string sourceDirectory) : IIngestionSource
{
    public static string SourceFileId(string path) => Path.GetFileName(path);
    public static string SourceFileVersion(string path) => File.GetLastWriteTimeUtc(path).ToString("o");

    public Task<IEnumerable<IngestedDocument>> GetDeletedDocumentsAsync(IQueryable<IngestedDocument> existingDocuments)
    {
        var currentFiles = Directory.GetFiles(sourceDirectory, "*.vtt", SearchOption.AllDirectories);
        var currentFileIds = new HashSet<string>(currentFiles.Select(SourceFileId));
        var existingDocsList = existingDocuments.ToList();
        var deletedDocuments = existingDocsList.Where(d => !currentFileIds.Contains(d.Id));
        return Task.FromResult(deletedDocuments);
    }

    public Task<IEnumerable<IngestedDocument>> GetNewOrModifiedDocumentsAsync(IQueryable<IngestedDocument> existingDocuments)
    {
        var results = new List<IngestedDocument>();
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.vtt", SearchOption.AllDirectories);
        var existingDocumentsById = existingDocuments.ToDictionary(d => d.Id);

        foreach (var sourceFile in sourceFiles)
        {
            var sourceFileId = SourceFileId(sourceFile);
            var sourceFileVersion = SourceFileVersion(sourceFile);
            var existingDocumentVersion = existingDocumentsById.TryGetValue(sourceFileId, out var existingDocument) ? existingDocument.Version : null;
            if (existingDocumentVersion != sourceFileVersion)
            {
                results.Add(new() { Id = sourceFileId, Version = sourceFileVersion, SourceId = SourceId });
            }
        }

        return Task.FromResult((IEnumerable<IngestedDocument>)results);
    }

    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string documentId)
    {
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.vtt", SearchOption.AllDirectories);
        var sourceFile = sourceFiles.FirstOrDefault(f => SourceFileId(f) == documentId);
        if (sourceFile == null)
        {
            return [];
        }

        var transcriptText = await ExtractTranscriptTextAsync(sourceFile);
        
#pragma warning disable SKEXP0050 // Type is for evaluation purposes only
        var chunks = TextChunker.SplitPlainTextParagraphs([transcriptText], 200);
#pragma warning restore SKEXP0050 // Type is for evaluation purposes only
        
        var results = new List<SemanticSearchRecord>();
        var index = 0;
        
        foreach (var chunk in chunks)
        {
            var embedding = await embeddingGenerator.GenerateEmbeddingVectorAsync(chunk);
            results.Add(new SemanticSearchRecord
            {
                Key = Guid.CreateVersion7().ToString(),
                FileName = documentId,
                PageNumber = index,
                RecordType = "SharePointTranscript",
                Text = chunk,
                Vector = embedding
            });
            index++;
        }

        return results;
    }

    public Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsyncForWiki(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string wikiLink)
    {
        // SharePoint transcript source doesn't handle wiki links
        return Task.FromResult(Enumerable.Empty<SemanticSearchRecord>());
    }

    public Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForMultipleWikiLinksAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IEnumerable<string> wikiLinks)
    {
        // SharePoint transcript source doesn't handle wiki links
        return Task.FromResult(Enumerable.Empty<SemanticSearchRecord>());
    }

    private static async Task<string> ExtractTranscriptTextAsync(string vttFilePath)
    {
        var lines = await File.ReadAllLinesAsync(vttFilePath);
        var transcriptText = new List<string>();
        
        // Skip WEBVTT header and process caption blocks
        var inCaptionBlock = false;
        var regex = new Regex(@"^\d+:\d+:\d+\.\d+ --> \d+:\d+:\d+\.\d+$");
        
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            // Skip empty lines and WEBVTT header
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine == "WEBVTT")
            {
                continue;
            }
            
            // Skip timestamp lines
            if (regex.IsMatch(trimmedLine))
            {
                inCaptionBlock = true;
                continue;
            }
            
            // Skip cue identifiers (lines that look like GUIDs or numbers)
            if (Guid.TryParse(trimmedLine, out _) || 
                trimmedLine.Contains("-") && trimmedLine.Length > 20)
            {
                continue;
            }
            
            // Collect actual caption text
            if (inCaptionBlock && !string.IsNullOrEmpty(trimmedLine))
            {
                transcriptText.Add(trimmedLine);
            }
        }
        
        return string.Join(" ", transcriptText);
    }

    public string SourceId => $"{nameof(SharePointTranscriptSource)}:{sourceDirectory}";
}
