using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL;

/// <summary>The three comment operations against a deck file: listing every comment, and
///     adding or deleting one under the same version guard and atomic save the edit batch
///     uses. A comment is written in the current shape only; a delete removes one from
///     whichever shape holds it.</summary>
internal static class PptxComments
{
  /// <summary>Lists the deck's comments, slide by slide in presentation order.</summary>
  /// <param name="path">The deck to read.</param>
  internal static Result<IReadOnlyList<PptxCommentInfo>> List(string path)
  {
    Result<string> sniff = Preflight(path);
    return sniff.IsSuccess ? ReadPath(path) : Result.Failure<IReadOnlyList<PptxCommentInfo>>(sniff.Error);
  }

  private static Result<IReadOnlyList<PptxCommentInfo>> ReadPath(string path)
  {
    try
    {
      using PresentationDocument doc = Open(path, false);
      return Read(doc);
    }
    catch (OpenXmlPackageException ex)
    {
      return Result.Failure<IReadOnlyList<PptxCommentInfo>>(Corrupt(path, ex.Message));
    }
    catch (InvalidDataException ex)
    {
      return Result.Failure<IReadOnlyList<PptxCommentInfo>>(Corrupt(path, ex.Message));
    }
    catch (IOException ex)
    {
      return Result.Failure<IReadOnlyList<PptxCommentInfo>>(Corrupt(path, ex.Message));
    }
  }

  /// <summary>Adds a comment in the current shape, anchored at the addressed shape.
  ///     </summary>
  /// <param name="path">The deck to change.</param>
  /// <param name="expectedVersion">The version the caller last read.</param>
  /// <param name="address">The address of the shape the comment anchors at.</param>
  /// <param name="author">The comment's author.</param>
  /// <param name="text">The comment's text.</param>
  internal static Result<PptxSaveOutcome> Add(string path, FileVersion expectedVersion, string address,
      string author, string text)
  {
    Result<PptxAddress> parsed = PptxAddress.Parse(address);
    return parsed.IsSuccess
        ? Save(path, expectedVersion, (_, slides) => AddTo(slides, parsed.Value, address, author, text))
        : Result.Failure<PptxSaveOutcome>(parsed.Error);
  }

  private static Result<bool> AddTo(IReadOnlyList<SlidePart> slides, PptxAddress parsed, string address,
      string author, string text)
    => AnchorAt(slides, parsed) is { } anchored
        ? AddAt(anchored, author, text)
        : Missing(address);

  /// <summary>Deletes one comment by its deck-wide id.</summary>
  /// <param name="path">The deck to change.</param>
  /// <param name="expectedVersion">The version the caller last read.</param>
  /// <param name="commentId">The 1-based deck-wide comment id.</param>
  internal static Result<PptxSaveOutcome> Delete(string path, FileVersion expectedVersion, int commentId)
    => Save(path, expectedVersion, (_, slides) => DeleteFrom(slides, commentId));

  private static Result<bool> DeleteFrom(IReadOnlyList<SlidePart> slides, int commentId)
    => PptxCommentReader.Delete(slides, commentId)
        ? Result.Success(true)
        : Result.Failure<bool>(new DomainError(PptxErrorCodes.CommentNotFound,
            $"No comment with id {commentId}: the deck's comments are numbered 1 to {Count(slides)}."));

  /// <summary>The comment and the slide it anchors at, resolved against the open package.
  ///     </summary>
  private sealed record Anchored(PresentationPart PresentationPart, SlidePart SlidePart, uint ShapeId);

  private static Result<IReadOnlyList<PptxCommentInfo>> Read(PresentationDocument doc)
  {
    if (doc.PresentationPart?.Presentation is not { } presentation)
    {
      return Result.Failure<IReadOnlyList<PptxCommentInfo>>(new DomainError(PptxErrorCodes.CorruptPresentation,
          $"The package has no readable {PptxFormatSniffer.PresentationPartName} part."));
    }

    List<SlidePart> slides = PptxOutlineReader.SlideParts(doc.PresentationPart, presentation);
    List<PptxCommentInfo> comments = [];
    for (int number = 1; number <= slides.Count; number++)
    {
      SlidePart slidePart = slides[number - 1];
      PptxShapeTree tree = PptxShapeWalker.Walk(slidePart, number);
      comments.AddRange(PptxCommentReader.Read(doc.PresentationPart, slidePart, number, tree.AddressesByShapeId,
          PptxCommentReader.Offset(slides, number)));
    }

    return Result.Success<IReadOnlyList<PptxCommentInfo>>(comments);
  }

  /// <summary>Resolves an address to the slide it names and the drawing id of the shape
  ///     the comment anchors at, or null when the address names nothing on the deck.
  ///     </summary>
  private static Anchored? AnchorAt(IReadOnlyList<SlidePart> slides, PptxAddress address)
  {
    if (address.Kind is not (PptxAddressKind.Shape or PptxAddressKind.TableCell)
        || address.Slide < 1 || address.Slide > slides.Count)
    {
      return null;
    }

    SlidePart slidePart = slides[address.Slide - 1];
    OpenXmlElement? element = PptxShapeWalker.ElementAt(slidePart, address.ShapePath);
    return element is null
        || PptxShapeWalker.ShapeId(element) is not { } id
        || slidePart.OpenXmlPackage is not PresentationDocument doc
        || doc.PresentationPart is not { } presentationPart
        ? null
        : new Anchored(presentationPart, slidePart, id);
  }

  private static Result<bool> AddAt(Anchored anchored, string author, string text)
  {
    PptxCommentReader.Add(anchored.PresentationPart, anchored.SlidePart, anchored.ShapeId, author, text);
    return Result.Success(true);
  }

  private static Result<bool> Missing(string address)
    => Result.Failure<bool>(new DomainError(PptxErrorCodes.AddressNotFound,
        $"Address '{address}' resolves to nothing: the slide has no such shape to anchor a comment at."));

  private static int Count(IReadOnlyList<SlidePart> slides)
    => PptxCommentReader.Offset(slides, slides.Count + 1);

  /// <summary>Applies one comment change to a copy of the deck under the version guard,
  ///     then moves the saved copy over the original. A refused change leaves the original
  ///     byte-identical, and the package is closed before the move.</summary>
  private static Result<PptxSaveOutcome> Save(string path, FileVersion expectedVersion,
      Func<PresentationDocument, IReadOnlyList<SlidePart>, Result<bool>> change)
  {
    Result<PptxSaveOutcome>? refused = Refusal(path, expectedVersion);
    if (refused is not null)
    {
      return refused;
    }

    string copy = path + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
      File.Copy(path, copy, true);
      Result<bool> applied = Apply(copy, change);
      if (!applied.IsSuccess)
      {
        return Result.Failure<PptxSaveOutcome>(applied.Error);
      }

      File.Move(copy, path, true);
      FileInfo after = new(path);
      return Result.Success(new PptxSaveOutcome(new FileVersion(after.Length, after.LastWriteTimeUtc)));
    }
    catch (OpenXmlPackageException ex)
    {
      return Result.Failure<PptxSaveOutcome>(Corrupt(path, ex.Message));
    }
    catch (InvalidDataException ex)
    {
      return Result.Failure<PptxSaveOutcome>(Corrupt(path, ex.Message));
    }
    catch (IOException ex)
    {
      return Result.Failure<PptxSaveOutcome>(Corrupt(path, ex.Message));
    }
    finally
    {
      TryDelete(copy);
    }
  }

  /// <summary>The preconditions that fail before the copy is written: the file's
  ///     existence, the sniffer gate and the version equality. Null when they all hold.
  ///     </summary>
  private static Result<PptxSaveOutcome>? Refusal(string path, FileVersion expectedVersion)
  {
    Result<string> preflight = Preflight(path);
    if (!preflight.IsSuccess)
    {
      return Result.Failure<PptxSaveOutcome>(preflight.Error);
    }

    FileInfo info = new(path);
    FileVersion current = new(info.Length, info.LastWriteTimeUtc);
    return current == expectedVersion
        ? null
        : Result.Failure<PptxSaveOutcome>(new DomainError(PptxErrorCodes.StalePresentation,
            $"The presentation changed since it was read (expected {expectedVersion.Token}, current {current.Token}); re-read and retry."));
  }

  private static Result<bool> Apply(string copy, Func<PresentationDocument, IReadOnlyList<SlidePart>, Result<bool>> change)
  {
    using PresentationDocument doc = Open(copy, true);
    if (doc.PresentationPart?.Presentation is not { } presentation)
    {
      return Result.Failure<bool>(new DomainError(PptxErrorCodes.CorruptPresentation,
          $"The package has no readable {PptxFormatSniffer.PresentationPartName} part."));
    }

    Result<bool> applied = change(doc, PptxOutlineReader.SlideParts(doc.PresentationPart, presentation));
    if (applied.IsSuccess)
    {
      doc.Save();
    }

    return applied;
  }

  private static PresentationDocument Open(string path, bool editable)
    => PresentationDocument.Open(path, editable, new OpenSettings { MaxCharactersInPart = PptxLimits.MaxCharactersInPart });

  /// <summary>The preconditions that fail before the package is opened: the file's
  ///     existence and the sniffer gate.</summary>
  private static Result<string> Preflight(string path)
    => File.Exists(path)
        ? PptxFormatSniffer.Inspect(path)
        : Result.Failure<string>(new DomainError(PptxErrorCodes.FileNotFound, $"File not found: '{path}'."));

  private static DomainError Corrupt(string path, string message)
    => new(PptxErrorCodes.CorruptPresentation,
        $"'{path}' could not be read as a presentation: {(message.Length <= 300 ? message : message[..300] + "...")}");

  private static void TryDelete(string path)
  {
    try
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
    catch (IOException)
    {
      // best-effort cleanup: the original is already intact
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort cleanup: the original is already intact
    }
  }
}
