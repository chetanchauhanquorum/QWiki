using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using System.Text;

namespace QWiki.Services.Ingestion;

public class PPTDirectorySource(string sourceDirectory) : IIngestionSource
{
    public static string SourceFileId(string path) => Path.GetFileName(path);

    public string SourceId => $"{nameof(PPTDirectorySource)}:{sourceDirectory}";

    public async Task<IEnumerable<IngestedDocument>> GetNewOrModifiedDocumentsAsync(IQueryable<IngestedDocument> existingDocuments)
    {
        var results = new List<IngestedDocument>();
        // Only process .pptx, .pptm files (Open XML formats), not legacy .ppt files
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.pptx")
            .Concat(Directory.GetFiles(sourceDirectory, "*.pptm"))
            .ToArray();

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
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.pptx")
            .Concat(Directory.GetFiles(sourceDirectory, "*.pptm"))
            .ToArray();
        var sourceFileIds = sourceFiles.Select(SourceFileId).ToList();
        return await existingDocuments
            .Where(d => !sourceFileIds.Contains(d.Id))
            .ToListAsync();
    }

    public async Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string documentId)
    {
        var filePath = Path.Combine(sourceDirectory, documentId);
        var slideContents = ExtractTextFromPowerPoint(filePath);

        var paragraphs = new List<(int SlideNumber, int IndexOnSlide, string Text)>();

        foreach (var slide in slideContents)
        {
            if (!string.IsNullOrWhiteSpace(slide.Text))
            {
#pragma warning disable SKEXP0050 // Type is for evaluation purposes only
                var chunks = TextChunker.SplitPlainTextParagraphs([slide.Text], 200);
                paragraphs.AddRange(chunks.Select((text, index) => (slide.SlideNumber, index, text)));
#pragma warning restore SKEXP0050 // Type is for evaluation purposes only
            }
        }

        if (!paragraphs.Any())
        {
            return [];
        }

        var embeddings = await embeddingGenerator.GenerateAsync(paragraphs.Select(p => p.Text));

        return paragraphs.Zip(embeddings).Select((pair, index) => new SemanticSearchRecord
        {
            Key = $"{Path.GetFileNameWithoutExtension(documentId)}_slide{pair.First.SlideNumber}_{pair.First.IndexOnSlide}",
            FileName = documentId,
            PageNumber = pair.First.SlideNumber,
            Text = pair.First.Text,
            Vector = pair.Second.Vector,
            RecordType = "PPT",
            SourceUrl = $"/Data/{documentId}" // Relative URL to the PPT file
        });
    }

    // Wiki methods are not applicable for PPT files, but required by interface
    public Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForDocumentAsyncForWiki(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, string wikiLink)
    {
        return Task.FromResult(Enumerable.Empty<SemanticSearchRecord>());
    }

    public Task<IEnumerable<SemanticSearchRecord>> CreateRecordsForMultipleWikiLinksAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IEnumerable<string> wikiLinks)
    {
        return Task.FromResult(Enumerable.Empty<SemanticSearchRecord>());
    }

    private static List<(int SlideNumber, string Text)> ExtractTextFromPowerPoint(string filePath)
    {
        var slideContents = new List<(int SlideNumber, string Text)>();

        // Check if file extension is supported
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension == ".ppt")
        {
            Console.WriteLine($"Warning: Legacy PowerPoint format (.ppt) is not supported. Please convert '{Path.GetFileName(filePath)}' to .pptx format. Skipping file.");
            return slideContents;
        }

        if (extension != ".pptx" && extension != ".pptm")
        {
            Console.WriteLine($"Warning: Unsupported PowerPoint format '{extension}' for file '{Path.GetFileName(filePath)}'. Skipping file.");
            return slideContents;
        }

        try
        {
            using var presentationDocument = PresentationDocument.Open(filePath, false);
            var presentationPart = presentationDocument.PresentationPart;
            
            if (presentationPart?.Presentation?.SlideIdList == null)
            {
                Console.WriteLine($"Warning: No slides found in PowerPoint file '{Path.GetFileName(filePath)}'.");
                return slideContents;
            }

            var slideIdList = presentationPart.Presentation.SlideIdList;
            int slideNumber = 1;

            foreach (var slideId in slideIdList.Cast<SlideId>())
            {
                var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId!);
                var slideText = ExtractTextFromSlide(slidePart);
                
                if (!string.IsNullOrWhiteSpace(slideText))
                {
                    slideContents.Add((slideNumber, slideText));
                }
                
                slideNumber++;
            }

            Console.WriteLine($"Successfully extracted text from {slideContents.Count} slides in '{Path.GetFileName(filePath)}'.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting text from PowerPoint file '{filePath}': {ex.Message}");
            
            // Provide more specific guidance based on the error
            if (ex.Message.Contains("corrupted") || ex.Message.Contains("corrupt"))
            {
                Console.WriteLine($"Suggestion: The file '{Path.GetFileName(filePath)}' may be in legacy .ppt format or corrupted. Try converting it to .pptx format using PowerPoint.");
            }
        }

        return slideContents;
    }

    private static string ExtractTextFromSlide(SlidePart slidePart)
    {
        var textBuilder = new StringBuilder();

        if (slidePart.Slide?.CommonSlideData?.ShapeTree == null)
        {
            return string.Empty;
        }

        foreach (var shape in slidePart.Slide.CommonSlideData.ShapeTree.Elements())
        {
            var textFromShape = ExtractTextFromShape(shape);
            if (!string.IsNullOrWhiteSpace(textFromShape))
            {
                textBuilder.AppendLine(textFromShape);
            }
        }

        return textBuilder.ToString().Trim();
    }

    private static string ExtractTextFromShape(DocumentFormat.OpenXml.OpenXmlElement shape)
    {
        var textBuilder = new StringBuilder();

        // Find all text elements in the shape
        var textElements = shape.Descendants<DocumentFormat.OpenXml.Drawing.Text>();
        
        foreach (var textElement in textElements)
        {
            if (!string.IsNullOrWhiteSpace(textElement.Text))
            {
                textBuilder.Append(textElement.Text);
            }
        }

        // Add line breaks between different text runs
        var result = textBuilder.ToString();
        if (!string.IsNullOrWhiteSpace(result))
        {
            return result + Environment.NewLine;
        }

        return string.Empty;
    }
}