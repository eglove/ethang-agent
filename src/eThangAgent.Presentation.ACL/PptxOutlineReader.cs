using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace eThangAgent.Presentation.ACL;

/// <summary>Builds a deck's outline from an open package. Slide order comes from the
///     slide-id list in ppt/presentation.xml, never from the slide parts' names: the id
///     list is the deck's own order and the part names are an accident of how the file
///     was written.</summary>
internal static class PptxOutlineReader
{
  private const string ImageContentTypePrefix = "image/";

  /// <summary>Opens the package read-only and builds the outline. A package that opens
  ///     but carries no presentation part, or cannot be read at all, is
  ///     CorruptPresentation.</summary>
  internal static Result<PptxOutline> Read(string path, FileVersion version)
  {
    try
    {
      using PresentationDocument doc = PresentationDocument.Open(path, false, new OpenSettings
      {
        MaxCharactersInPart = PptxLimits.MaxCharactersInPart,
      });
      return Build(doc, version);
    }
    catch (OpenXmlPackageException ex)
    {
      return Failure(Corrupt(path, ex.Message));
    }
    catch (InvalidDataException ex)
    {
      return Failure(Corrupt(path, ex.Message));
    }
    catch (IOException ex)
    {
      return Failure(Corrupt(path, ex.Message));
    }
  }

  private static Result<PptxOutline> Build(PresentationDocument doc, FileVersion version)
  {
    if (doc.PresentationPart?.Presentation is not { } presentation)
    {
      return Failure(new DomainError(PptxErrorCodes.CorruptPresentation,
          $"The package has no readable {PptxFormatSniffer.PresentationPartName} part."));
    }

    List<SlidePart> slides = SlideParts(doc.PresentationPart, presentation);
    if (slides.Count > PptxLimits.MaxSlides)
    {
      return Failure(new DomainError(PptxErrorCodes.OversizePresentation,
          $"The deck carries {slides.Count} slides; the maximum is {PptxLimits.MaxSlides}."));
    }

    List<PptxSlideSummary> summaries = [];
    List<string> layoutNames = [];
    for (int i = 0; i < slides.Count; i++)
    {
      PptxSlideSummary summary = Summarize(slides[i], i + 1);
      summaries.Add(summary);
      if (!layoutNames.Contains(summary.LayoutName, StringComparer.Ordinal))
      {
        layoutNames.Add(summary.LayoutName);
      }
    }

    return Result.Success(new PptxOutline(version, summaries.Count, summaries,
        Media(doc.PresentationPart), layoutNames));
  }

  /// <summary>The deck's slides in presentation order, resolved through the slide-id
  ///     list. A relationship the list names but the package does not carry is skipped
  ///     rather than throwing.</summary>
  internal static List<SlidePart> SlideParts(PresentationPart presentationPart, P.Presentation presentation)
  {
    List<SlidePart> slides = [];
    if (presentation.SlideIdList is null)
    {
      return slides;
    }

    foreach (P.SlideId id in presentation.SlideIdList.Elements<P.SlideId>())
    {
      if (id.RelationshipId?.Value is { } relationshipId
          && presentationPart.GetPartById(relationshipId) is SlidePart slidePart)
      {
        slides.Add(slidePart);
      }
    }

    return slides;
  }

  private static PptxSlideSummary Summarize(SlidePart slidePart, int number)
  {
    string title = string.Empty;
    string firstLine = string.Empty;
    foreach (P.Shape shape in TopLevelShapes(slidePart))
    {
      string text = ShapeText(shape);
      if (title.Length == 0 && IsTitlePlaceholder(shape))
      {
        title = text.Trim();
      }
      else if (firstLine.Length == 0 && !IsTitlePlaceholder(shape))
      {
        firstLine = FirstNonBlankLine(text);
      }
    }

    return new PptxSlideSummary(number, title, firstLine, IsHidden(slidePart), LayoutName(slidePart));
  }

  private static IEnumerable<P.Shape> TopLevelShapes(SlidePart slidePart)
    => slidePart.Slide?.CommonSlideData?.ShapeTree?.Elements<P.Shape>() ?? [];

  private static bool IsTitlePlaceholder(P.Shape shape)
  {
    P.PlaceholderValues? type = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties
        ?.PlaceholderShape?.Type?.Value;
    return type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle;
  }

  private static bool IsHidden(SlidePart slidePart)
    => slidePart.Slide?.Show?.Value == false;

  private static string LayoutName(SlidePart slidePart)
    => slidePart.SlideLayoutPart?.SlideLayout?.CommonSlideData?.Name?.Value ?? string.Empty;

  /// <summary>The shape's full text: paragraphs joined by a newline, runs within a
  ///     paragraph concatenated, and an explicit line break inside a paragraph kept as a
  ///     newline.</summary>
  private static string ShapeText(P.Shape shape)
    => shape.TextBody is null
      ? string.Empty
      : string.Join("\n", shape.TextBody.Elements<A.Paragraph>().Select(ParagraphText));

  private static string ParagraphText(A.Paragraph paragraph)
  {
    StringBuilder text = new();
    foreach (OpenXmlElement child in paragraph.Descendants())
    {
      if (child is A.Text run)
      {
        _ = text.Append(run.Text);
      }
      else if (child is A.Break)
      {
        _ = text.Append('\n');
      }
    }

    return text.ToString();
  }

  private static string FirstNonBlankLine(string text)
    => text.Split('\n')
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => line.Trim())
        .FirstOrDefault() ?? string.Empty;

  /// <summary>Every image part in the package, in part-graph order, with its content
  ///     type and byte count.</summary>
  internal static List<PptxMediaInfo> Media(PresentationPart presentationPart)
    => [.. ImageParts(presentationPart)
        .Select(part => new PptxMediaInfo(part.Uri.OriginalString, part.ContentType, ByteCount(part)))];

  /// <summary>Every image part in the package, in part-graph order: the parts the outline
  ///     reports as media and the parts an extraction reads the bytes from.</summary>
  internal static List<ImagePart> ImageParts(PresentationPart presentationPart)
    => [.. Descendants(presentationPart)
        .OfType<ImagePart>()
        .Where(part => part.ContentType.StartsWith(ImageContentTypePrefix, StringComparison.OrdinalIgnoreCase))];

  /// <summary>Every part reachable from the container, each visited once. The part graph is
  ///     cyclic - a slide references its notes and the notes reference the slide back - so
  ///     the walk carries the set of parts it has already yielded.</summary>
  private static List<OpenXmlPart> Descendants(OpenXmlPartContainer container)
  {
    List<OpenXmlPart> parts = [];
    HashSet<string> visited = [];
    Collect(container, parts, visited);
    return parts;
  }

  private static void Collect(OpenXmlPartContainer container, List<OpenXmlPart> parts, HashSet<string> visited)
  {
    OpenXmlPart[] fresh = [.. container.Parts
        .Select(pair => pair.OpenXmlPart)
        .Where(part => visited.Add(part.Uri.OriginalString))];
    foreach (OpenXmlPart part in fresh)
    {
      parts.Add(part);
      Collect(part, parts, visited);
    }
  }

  private static long ByteCount(OpenXmlPart part)
  {
    try
    {
      using Stream stream = part.GetStream(FileMode.Open, FileAccess.Read);
      return stream.Length;
    }
    catch (IOException)
    {
      return 0;
    }
  }

  private static DomainError Corrupt(string path, string message)
    => new(PptxErrorCodes.CorruptPresentation, $"'{path}' could not be read as a presentation: {Clamp(message)}");

  private static Result<PptxOutline> Failure(DomainError error) => Result.Failure<PptxOutline>(error);

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }
}
