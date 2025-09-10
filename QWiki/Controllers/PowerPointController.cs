using Microsoft.AspNetCore.Mvc;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using System.Drawing;
using System.Drawing.Imaging;

namespace QWiki.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PowerPointController : ControllerBase
{
    private readonly IWebHostEnvironment _environment;

    public PowerPointController(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    [HttpGet("slide")]
    public async Task<IActionResult> GetSlide(string file, int slide = 1)
    {
        try
        {
            // Handle both full paths and filenames
            string fileName = file.StartsWith("/Data/") ? file.Substring(6) : file;
            var filePath = Path.Combine(_environment.WebRootPath, "Data", fileName);
            
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound($"PowerPoint file not found: {fileName}");
            }

            var slideInfo = GetSlideInfo(filePath, slide);
            if (slideInfo == null)
            {
                return NotFound("Slide not found");
            }

            return Ok(new
            {
                slideNumber = slide,
                title = slideInfo.Title,
                content = slideInfo.Content,
                totalSlides = slideInfo.TotalSlides
            });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error processing PowerPoint file: {ex.Message}");
        }
    }

    private SlideInfo? GetSlideInfo(string filePath, int slideNumber)
    {
        try
        {
            using var presentationDocument = PresentationDocument.Open(filePath, false);
            var presentationPart = presentationDocument.PresentationPart;
            
            if (presentationPart?.Presentation?.SlideIdList == null)
            {
                return null;
            }

            var slideIdList = presentationPart.Presentation.SlideIdList;
            var totalSlides = slideIdList.Count();

            if (slideNumber < 1 || slideNumber > totalSlides)
            {
                return null;
            }

            var slideId = slideIdList.ElementAt(slideNumber - 1) as SlideId;
            if (slideId?.RelationshipId == null)
            {
                return null;
            }

            var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId);
            var slideContent = ExtractSlideContent(slidePart);

            return new SlideInfo
            {
                Title = ExtractSlideTitle(slidePart),
                Content = slideContent,
                TotalSlides = totalSlides
            };
        }
        catch
        {
            return null;
        }
    }

    private string ExtractSlideTitle(SlidePart slidePart)
    {
        if (slidePart.Slide?.CommonSlideData?.ShapeTree == null)
        {
            return "Slide";
        }

        // Look for title shapes (typically the first text shape)
        foreach (var shape in slidePart.Slide.CommonSlideData.ShapeTree.Elements())
        {
            var textElements = shape.Descendants<DocumentFormat.OpenXml.Drawing.Text>();
            if (textElements.Any())
            {
                var titleText = string.Join(" ", textElements.Take(3).Select(t => t.Text)).Trim();
                if (!string.IsNullOrEmpty(titleText) && titleText.Length > 5)
                {
                    return titleText.Length > 50 ? titleText.Substring(0, 50) + "..." : titleText;
                }
            }
        }

        return "Slide";
    }

    private List<string> ExtractSlideContent(SlidePart slidePart)
    {
        var content = new List<string>();

        if (slidePart.Slide?.CommonSlideData?.ShapeTree == null)
        {
            return content;
        }

        foreach (var shape in slidePart.Slide.CommonSlideData.ShapeTree.Elements())
        {
            var textElements = shape.Descendants<DocumentFormat.OpenXml.Drawing.Text>();
            if (textElements.Any())
            {
                var shapeText = string.Join(" ", textElements.Select(t => t.Text)).Trim();
                if (!string.IsNullOrEmpty(shapeText))
                {
                    content.Add(shapeText);
                }
            }
        }

        return content;
    }

    private class SlideInfo
    {
        public string Title { get; set; } = string.Empty;
        public List<string> Content { get; set; } = new();
        public int TotalSlides { get; set; }
    }
}
