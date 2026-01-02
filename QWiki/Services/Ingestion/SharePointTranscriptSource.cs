using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Text;
using System.Text.RegularExpressions;

namespace QWiki.Services.Ingestion;

public class SharePointTranscriptSource(string sourceDirectory) : IIngestionSource
{
    public string SourceId => $"{nameof(SharePointTranscriptSource)}:{sourceDirectory}";

    public static string SourceFileId(string path, string sourceDir)
    {
        // Use relative path from sourceDirectory to ensure uniqueness across subdirectories
        var relativePath = Path.GetRelativePath(sourceDir, path);
        return Path.GetFileNameWithoutExtension(relativePath).Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
    }
    
    public static string SourceFileVersion(string path) => File.GetLastWriteTimeUtc(path).ToString("o");

    public async Task<IEnumerable<IngestedDocument>> GetDeletedDocumentsAsync(IQueryable<IngestedDocument> existingDocuments)
    {
        var currentFiles = Directory.GetFiles(sourceDirectory, "*.vtt", SearchOption.AllDirectories);
        var currentFileIds = currentFiles.Select(f => SourceFileId(f, sourceDirectory)).ToList();
        return await existingDocuments
            .Where(d => d.SourceId == SourceId && !currentFileIds.Contains(d.Id))
            .ToListAsync();
    }

    public async Task<IEnumerable<IngestedDocument>> GetNewOrModifiedDocumentsAsync(IQueryable<IngestedDocument> existingDocuments)
    {
        var results = new List<IngestedDocument>();
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.vtt", SearchOption.AllDirectories);

        foreach (var sourceFile in sourceFiles)
        {
            var sourceFileId = SourceFileId(sourceFile, sourceDirectory);
            var sourceFileVersion = SourceFileVersion(sourceFile);

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

    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string documentId)
    {
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.vtt", SearchOption.AllDirectories);
        var sourceFile = sourceFiles.FirstOrDefault(f => SourceFileId(f, sourceDirectory) == documentId);
        if (sourceFile == null)
        {
            return [];
        }

        var transcriptText = await ExtractTranscriptTextAsync(sourceFile);
        
#pragma warning disable SKEXP0050 // Type is for evaluation purposes only
        var chunks = TextChunker.SplitPlainTextParagraphs([transcriptText], 200).ToList();
#pragma warning restore SKEXP0050 // Type is for evaluation purposes only
        
        var embeddings = await embeddingGenerator.GenerateAsync(chunks);

        return chunks.Zip(embeddings).Select((pair, index) => new SemanticSearchRecord
        {
            Key = $"{Path.GetFileNameWithoutExtension(documentId)}_{index}",
            FileName = Path.GetFileName(sourceFile),
            PageNumber = index,
            RecordType = "SharePointTranscript",
            Text = pair.First,
            Vector = pair.Second.Vector
        });
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
}
