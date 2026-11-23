using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL;

/// <summary>Builds one slide's snapshot from an open package: its shapes in document
///     order with their addresses, its speaker notes, the deck's media, its comments and
///     its animation effects in playback order.</summary>
internal static class PptxSlideReader
{
  /// <summary>Opens the package read-only and reads the slide numbered
  ///     <paramref name="number"/>. A number outside 1..SlideCount is SlideNotFound; a
  ///     package that cannot be read as a presentation is CorruptPresentation.</summary>
  /// <param name="path">The deck to open.</param>
  /// <param name="number">The 1-based slide number to read.</param>
  /// <param name="version">The file's content-true version at the read moment.</param>
  internal static Result<PptxSlideSnapshot> Read(string path, int number, FileVersion version)
  {
    try
    {
      using PresentationDocument doc = PresentationDocument.Open(path, false, new OpenSettings
      {
        MaxCharactersInPart = PptxLimits.MaxCharactersInPart,
      });
      return Build(doc, number, version);
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

  private static Result<PptxSlideSnapshot> Build(PresentationDocument doc, int number, FileVersion version)
  {
    if (doc.PresentationPart?.Presentation is not { } presentation)
    {
      return Failure(new DomainError(PptxErrorCodes.CorruptPresentation,
          $"The package has no readable {PptxFormatSniffer.PresentationPartName} part."));
    }

    List<SlidePart> slides = PptxOutlineReader.SlideParts(doc.PresentationPart, presentation);
    if (number < 1 || number > slides.Count)
    {
      return Failure(new DomainError(PptxErrorCodes.SlideNotFound,
          $"Slide {number} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}."));
    }

    SlidePart slidePart = slides[number - 1];
    PptxShapeTree tree = PptxShapeWalker.Walk(slidePart, number);
    PptxSlideContent content = new(number, LayoutName(slidePart), IsHidden(slidePart), tree.Shapes);
    return Result.Success(new PptxSlideSnapshot(version, content, PptxShapeWalker.Notes(slidePart),
        PptxOutlineReader.Media(doc.PresentationPart),
        PptxCommentReader.Read(doc.PresentationPart, slidePart, number, tree.AddressesByShapeId),
        PptxShapeWalker.Animations(slidePart, number, tree.AddressesByShapeId)));
  }

  private static bool IsHidden(SlidePart slidePart)
    => slidePart.Slide?.Show?.Value == false;

  private static string LayoutName(SlidePart slidePart)
    => slidePart.SlideLayoutPart?.SlideLayout?.CommonSlideData?.Name?.Value ?? string.Empty;

  private static DomainError Corrupt(string path, string message)
    => new(PptxErrorCodes.CorruptPresentation,
        $"'{path}' could not be read as a presentation: {(message.Length <= 300 ? message : message[..300] + "...")}");

  private static Result<PptxSlideSnapshot> Failure(DomainError error) => Result.Failure<PptxSlideSnapshot>(error);
}
