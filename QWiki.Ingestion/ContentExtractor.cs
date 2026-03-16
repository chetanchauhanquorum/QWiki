using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using A = DocumentFormat.OpenXml.Drawing;

namespace QWiki.Ingestion;

/// <summary>
/// Shared content extraction helpers for PDF, PPTX, DOCX files.
/// Used by SharePointIngestionSource for text extraction and chunking.
/// </summary>
public static class ContentExtractor
{
    public static List<(int PageNumber, string Text)> ExtractTextFromPdf(Stream stream)
    {
        var results = new List<(int, string)>();
        using var pdf = PdfDocument.Open(stream);

        foreach (var page in pdf.GetPages())
        {
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var pageText = string.Join(Environment.NewLine + Environment.NewLine,
                blocks.Select(b => b.Text.ReplaceLineEndings(" ")));

            if (!string.IsNullOrWhiteSpace(pageText))
            {
                results.Add((page.Number, pageText));
            }
        }

        return results;
    }

    public static List<(int SlideNumber, string Text)> ExtractTextFromPowerPoint(Stream stream)
    {
        var results = new List<(int, string)>();
        using var doc = PresentationDocument.Open(stream, false);
        var presentationPart = doc.PresentationPart;
        if (presentationPart?.Presentation?.SlideIdList == null) return results;

        int slideNumber = 0;
        foreach (var slideId in presentationPart.Presentation.SlideIdList.Elements<SlideId>())
        {
            slideNumber++;
            var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId!);
            var sb = new StringBuilder();

            foreach (var shape in slidePart.Slide.Descendants<Shape>())
            {
                foreach (var paragraph in shape.Descendants<A.Paragraph>())
                {
                    var paragraphText = string.Join("", paragraph.Descendants<A.Run>().Select(r => r.Text?.Text ?? ""));
                    if (!string.IsNullOrWhiteSpace(paragraphText))
                    {
                        sb.AppendLine(paragraphText);
                    }
                }
            }

            results.Add((slideNumber, sb.ToString().Trim()));
        }

        return results;
    }

    public static string ExtractTextFromWord(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var paragraph in body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            var text = paragraph.InnerText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text);
                sb.AppendLine();
            }
        }

        return sb.ToString().Trim();
    }

    public static List<string> ChunkPlainText(string text, int maxWords = 300, int overlapWords = 50)
    {
        var chunks = new List<string>();
        var words = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0) return chunks;

        if (words.Length <= maxWords)
        {
            chunks.Add(string.Join(" ", words));
            return chunks;
        }

        int start = 0;
        while (start < words.Length)
        {
            int end = Math.Min(start + maxWords, words.Length);
            var chunk = string.Join(" ", words[start..end]);
            chunks.Add(chunk);

            if (end >= words.Length) break;
            start = end - overlapWords;
        }

        return chunks;
    }

    public static string SanitizeKey(string key) =>
        Regex.Replace(key, @"[^a-zA-Z0-9_\-=]", "-");
}
