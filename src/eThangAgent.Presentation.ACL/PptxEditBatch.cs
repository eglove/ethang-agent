using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

// S3220/S3878 flag every OOXML element-constructor call below: each element tree is
// built by handing its children to the constructor, which is the overload the SDK
// declares for it.
#pragma warning disable S3220, S3878
namespace eThangAgent.Presentation.ACL;

/// <summary>One edit operation resolved against the open package: the address it
///     changes, which de-duplicates the changed count, and the mutation it performs.
///     Resolution happens before any Apply() runs, which is what makes a batch atomic.
///     </summary>
/// <param name="Key">The canonical address the operation changes.</param>
/// <param name="Apply">The mutation, run only after every operation resolved.</param>
internal sealed record PptxResolvedEdit(string Key, Action Apply);

/// <summary>Resolves and applies a batch of pptx operations against one open package.
///     Every operation is resolved first and the first failure refuses the whole batch,
///     so a refused batch mutates nothing. Operations whose task has not landed are
///     refused as OperationUnsupported rather than approximated.</summary>
internal static class PptxEditBatch
{
  /// <summary>Resolves every operation against the open package and applies them. The
  ///     returned count is the number of distinct addresses the batch changed.</summary>
  /// <param name="doc">The open, editable package.</param>
  /// <param name="operations">The operations to apply, in order.</param>
  internal static Result<int> Apply(PresentationDocument doc, IReadOnlyList<PptxOperation> operations)
  {
    ArgumentNullException.ThrowIfNull(doc);
    ArgumentNullException.ThrowIfNull(operations);
    if (doc.PresentationPart?.Presentation is not { } presentation)
    {
      return FailedCount(PptxErrorCodes.CorruptPresentation, $"The package has no readable {PptxFormatSniffer.PresentationPartName} part.");
    }

    List<SlidePart> slides = PptxOutlineReader.SlideParts(doc.PresentationPart, presentation);
    List<PptxResolvedEdit> resolved = [];
    for (int index = 0; index < operations.Count; index++)
    {
      Result<PptxResolvedEdit> one = Resolve(slides, operations[index], index);
      if (!one.IsSuccess)
      {
        return FailedCount(one.Error.Code, $"operations[{index}]: {one.Error.Message}");
      }

      resolved.Add(one.Value);
    }

    foreach (PptxResolvedEdit edit in resolved)
    {
      edit.Apply();
    }

    return Result.Success(resolved
        .Select(edit => edit.Key)
        .Where(key => key.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .Count());
  }

  /// <summary>Resolves one operation to its mutation. The operation types whose task has
  ///     not landed are refused here, so a batch that names one changes nothing.</summary>
  private static Result<PptxResolvedEdit> Resolve(IReadOnlyList<SlidePart> slides, PptxOperation operation, int ordinal)
    => operation switch
    {
      ReplaceText replace => ResolveReplaceText(slides, replace),
      SetNotes notes => ResolveSetNotes(slides, notes),
      InsertSlide insert => ResolveInsertSlide(slides, insert),
      DeleteSlide delete => ResolveDeleteSlide(slides, delete),
      MoveSlide move => ResolveMoveSlide(slides, move),
      SetSlideLayout layout => ResolveSetSlideLayout(slides, layout),
      _ => PptxMediaWriter.Resolve(slides, operation, ordinal) ?? Unsupported(operation),
    };

  private static Result<PptxResolvedEdit> Unsupported(PptxOperation operation)
    => Failed(PptxErrorCodes.OperationUnsupported,
        $"The '{operation.GetType().Name}' operation is not supported yet; this build applies replaceText, setNotes, insertSlide, deleteSlide, moveSlide, setSlideLayout, replacePicture, insertPicture, insertTable and insertTextBox only.");

  /// <summary>Resolves a slide insertion. The slide is built on the named layout and its
  ///     id entry lands at the requested position; the key is the position the new slide
  ///     takes, so a batch that inserts twice counts two changed slides.</summary>
  private static Result<PptxResolvedEdit> ResolveInsertSlide(IReadOnlyList<SlidePart> slides, InsertSlide operation)
  {
    PresentationPart? presentationPart = PresentationOf(slides);
    if (presentationPart is null)
    {
      return Failed(PptxErrorCodes.CorruptPresentation, "The package has no readable presentation part.");
    }

    if (operation.AfterSlide < 0 || operation.AfterSlide > slides.Count)
    {
      return Failed(PptxErrorCodes.SlideNotFound,
          $"Slide {operation.AfterSlide} cannot be inserted after: the deck has {slides.Count} slide(s), so AfterSlide runs 0 to {slides.Count}.");
    }

    Result<SlideLayoutPart> layout = PptxSlideStructure.LayoutNamed(presentationPart, operation.LayoutName);
    if (!layout.IsSuccess)
    {
      return Failed(layout.Error.Code, layout.Error.Message);
    }

    string key = PptxAddress.ForSlide(operation.AfterSlide + 1).Render();
    return Result.Success(new PptxResolvedEdit(key, () =>
        _ = PptxSlideStructure.Insert(presentationPart, operation.AfterSlide, operation.LayoutName)));
  }

  /// <summary>Resolves a slide deletion. Deleting the deck's last slide is refused here,
  ///     before anything is removed, so the batch stays atomic.</summary>
  private static Result<PptxResolvedEdit> ResolveDeleteSlide(IReadOnlyList<SlidePart> slides, DeleteSlide operation)
  {
    if (operation.Slide < 1 || operation.Slide > slides.Count)
    {
      return Failed(PptxErrorCodes.SlideNotFound,
          $"Slide {operation.Slide} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}.");
    }

    if (slides.Count == 1)
    {
      return Failed(PptxErrorCodes.StrandedContent,
          $"Slide {operation.Slide} is the deck's only slide; a presentation always keeps at least one slide.");
    }

    PresentationPart? presentationPart = PresentationOf(slides);
    if (presentationPart is null)
    {
      return Failed(PptxErrorCodes.CorruptPresentation, "The package has no readable presentation part.");
    }

    int slide = operation.Slide;
    string key = PptxAddress.ForSlide(slide).Render();
    return Result.Success(new PptxResolvedEdit(key, () =>
        _ = PptxSlideStructure.Delete(presentationPart, slide)));
  }

  /// <summary>Resolves a slide move. A move to the position the slide already holds is
  ///     accepted and changes nothing, so it is resolved to a key that records the slide
  ///     without mutating it.</summary>
  private static Result<PptxResolvedEdit> ResolveMoveSlide(IReadOnlyList<SlidePart> slides, MoveSlide operation)
  {
    if (operation.From < 1 || operation.From > slides.Count)
    {
      return Failed(PptxErrorCodes.SlideNotFound,
          $"Slide {operation.From} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}.");
    }

    if (operation.To < 1 || operation.To > slides.Count)
    {
      return Failed(PptxErrorCodes.SlideNotFound,
          $"Slide {operation.To} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}.");
    }

    PresentationPart? presentationPart = PresentationOf(slides);
    if (presentationPart is null)
    {
      return Failed(PptxErrorCodes.CorruptPresentation, "The package has no readable presentation part.");
    }

    int from = operation.From;
    int to = operation.To;
    string key = from == to ? string.Empty : PptxAddress.ForSlide(from).Render();
    return Result.Success(new PptxResolvedEdit(key, () => _ = PptxSlideStructure.Move(presentationPart, from, to)));
  }

  /// <summary>Resolves a layout change. The mapping is computed before anything mutates, so
  ///     a change that would strand a shape refuses the whole batch and names the shapes.</summary>
  private static Result<PptxResolvedEdit> ResolveSetSlideLayout(IReadOnlyList<SlidePart> slides, SetSlideLayout operation)
  {
    if (operation.Slide < 1 || operation.Slide > slides.Count)
    {
      return Failed(PptxErrorCodes.SlideNotFound,
          $"Slide {operation.Slide} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}.");
    }

    PresentationPart? presentationPart = PresentationOf(slides);
    if (presentationPart is null)
    {
      return Failed(PptxErrorCodes.CorruptPresentation, "The package has no readable presentation part.");
    }

    Result<SlideLayoutPart> layout = PptxSlideStructure.LayoutNamed(presentationPart, operation.LayoutName);
    if (!layout.IsSuccess)
    {
      return Failed(layout.Error.Code, layout.Error.Message);
    }

    SlidePart slidePart = slides[operation.Slide - 1];
    List<PptxLayoutMapping> stranded = [.. PptxLayoutMapper.Map(slidePart, operation.Slide, layout.Value)
        .Where(mapping => mapping.IsStranded)];
    if (stranded.Count > 0)
    {
      return Failed(PptxErrorCodes.StrandedContent, StrandedMessage(operation.Slide, operation.LayoutName, stranded));
    }

    int slide = operation.Slide;
    string key = PptxAddress.ForSlide(slide).Render();
    return Result.Success(new PptxResolvedEdit(key, () =>
        _ = PptxSlideStructure.SetLayout(presentationPart, slide, operation.LayoutName)));
  }

  /// <summary>The presentation the slides belong to, or null when the package carries none.
  ///     </summary>
  private static PresentationPart? PresentationOf(IReadOnlyList<SlidePart> slides)
    => slides.Count > 0 && slides[0].OpenXmlPackage is PresentationDocument doc ? doc.PresentationPart : null;

  /// <summary>The refusal message for a layout change: every shape that would be left with
  ///     no position, by address and placeholder family.</summary>
  private static string StrandedMessage(int slide, string layoutName, List<PptxLayoutMapping> stranded)
  {
    StringBuilder message = new();
    _ = message.Append(CultureInfo.InvariantCulture,
        $"Moving slide {slide} onto layout '{layoutName}' would strand {stranded.Count} shape(s) with no place on the slide: ");
    _ = message.Append(string.Join(", ", stranded.Select(mapping =>
        mapping.Address + " (" + mapping.Family.ToString() + " placeholder)")));
    _ = message.Append(". Nothing was changed.");
    return message.ToString();
  }

  /// <summary>Resolves a text replacement to the addressed text box's text body. A
  ///     table cell is its own address, so a cell address reaches the cell's text body
  ///     rather than the table's.</summary>
  private static Result<PptxResolvedEdit> ResolveReplaceText(IReadOnlyList<SlidePart> slides, ReplaceText operation)
  {
    Result<SlidePart> slide = SlideAt(slides, operation.Address, out PptxAddress address);
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(slide.Error);
    }

    if (address.Kind is not (PptxAddressKind.Shape or PptxAddressKind.TableCell))
    {
      return Failed(PptxErrorCodes.InvalidAddress,
          $"Address '{operation.Address}' does not name a shape; replaceText addresses a text box, a shape or a table cell.");
    }

    OpenXmlElement? element = PptxShapeWalker.ElementAt(slide.Value, address.ShapePath);
    OpenXmlElement? textBody = element is null ? null : TextBodyOf(element, address);
    if (textBody is null)
    {
      return Failed(PptxErrorCodes.AddressNotFound,
          $"Address '{operation.Address}' resolves to no text box on slide {address.Slide}.");
    }

    string text = operation.Text;
    return Result.Success(new PptxResolvedEdit(address.Render(), () => WriteText(textBody, text)));
  }

  /// <summary>Resolves a notes replacement to the slide's notes body placeholder. A
  ///     slide with no notes part gains one, related to the deck's notes master and back
  ///     to its slide.</summary>
  private static Result<PptxResolvedEdit> ResolveSetNotes(IReadOnlyList<SlidePart> slides, SetNotes operation)
  {
    if (operation.Slide < 1 || operation.Slide > slides.Count)
    {
      return Failed(PptxErrorCodes.SlideNotFound,
          $"Slide {operation.Slide} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}.");
    }

    SlidePart slidePart = slides[operation.Slide - 1];
    string text = operation.Text;
    string key = PptxAddress.ForNotes(operation.Slide).Render();
    return Result.Success(new PptxResolvedEdit(key, () => WriteNotes(slidePart, text)));
  }

  /// <summary>The slide part an address names. A slide number outside the deck is
  ///     SlideNotFound; an address that does not parse is InvalidAddress.</summary>
  private static Result<SlidePart> SlideAt(IReadOnlyList<SlidePart> slides, string address, out PptxAddress parsed)
  {
    parsed = null!;
    Result<PptxAddress> result = PptxAddress.Parse(address);
    if (!result.IsSuccess)
    {
      return Result.Failure<SlidePart>(result.Error);
    }

    parsed = result.Value;
    return parsed.Slide <= slides.Count
      ? Result.Success(slides[parsed.Slide - 1])
      : FailedSlide(PptxErrorCodes.SlideNotFound,
          $"Slide {parsed.Slide} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}.");
  }

  /// <summary>The text body an address reaches: the shape's own body, or the addressed
  ///     table cell's body. A shape with no text body has none. The body is returned as a
  ///     plain element because a shape's p:txBody and a table cell's a:txBody are
  ///     unrelated SDK types with the same content model.</summary>
  private static OpenXmlElement? TextBodyOf(OpenXmlElement element, PptxAddress address)
    => address.Kind == PptxAddressKind.TableCell
        ? TableCellOf(element, address)?.TextBody
        : ShapeTextBody(element);

  /// <summary>A shape's text body. A p:sp carries p:txBody and a p:cxnSp carries
  ///     a:txBody, so both element types are looked for.</summary>
#pragma warning disable CA1859 // Named decision: the two body types share no base type, so one element-typed result serves both.
  private static OpenXmlElement? ShapeTextBody(OpenXmlElement element)
#pragma warning restore CA1859
    => (OpenXmlElement?)element.GetFirstChild<A.TextBody>() ?? element.GetFirstChild<P.TextBody>();

  /// <summary>The addressed table cell of a table shape, or null when the shape is not a
  ///     table or the cell is outside its dimensions.</summary>
  private static A.TableCell? TableCellOf(OpenXmlElement element, PptxAddress address)
  {
    A.Table? table = element.Descendants<A.Table>().FirstOrDefault();
    List<A.TableRow> rows = table is null ? [] : [.. table.Elements<A.TableRow>()];
    if (address.Row > rows.Count)
    {
      return null;
    }

    List<A.TableCell> cells = [.. rows[address.Row - 1].Elements<A.TableCell>()];
    return address.Col <= cells.Count ? cells[address.Col - 1] : null;
  }

  /// <summary>Writes text into a text body: the first paragraph keeps its paragraph
  ///     properties, the first run's character formatting carries onto the new text, and
  ///     the remaining paragraphs and runs are dropped. The body's shape properties are
  ///     never touched, so position, size and effect survive. An empty text leaves one
  ///     empty paragraph rather than removing the body.</summary>
  private static void WriteText(OpenXmlElement textBody, string text)
  {
    A.Paragraph? first = textBody.GetFirstChild<A.Paragraph>();
    A.RunProperties? format = first?.GetFirstChild<A.Run>()?.RunProperties?.CloneNode(true) as A.RunProperties;
    A.ParagraphProperties? paragraphFormat = first?.ParagraphProperties?.CloneNode(true) as A.ParagraphProperties;
    foreach (A.Paragraph paragraph in textBody.Elements<A.Paragraph>().ToList())
    {
      paragraph.Remove();
    }

    foreach (string line in Lines(text))
    {
      textBody.Append(Paragraph(line, paragraphFormat, format));
    }
  }

  /// <summary>The lines a text is written as: one paragraph per line, and an empty text
  ///     is one empty paragraph.</summary>
  private static string[] Lines(string text) => text.Length == 0 ? [string.Empty] : text.Split('\n');

  private static A.Paragraph Paragraph(string text, A.ParagraphProperties? paragraphFormat, A.RunProperties? format)
  {
    A.Paragraph paragraph = [];
    if (paragraphFormat is not null)
    {
      paragraph.Append(paragraphFormat.CloneNode(true));
    }

    if (text.Length > 0)
    {
      A.Run run = [];
      if (format is not null)
      {
        run.Append(format.CloneNode(true));
      }

      run.Append(new A.Text(text));
      paragraph.Append(run);
    }

    return paragraph;
  }

  /// <summary>Writes a slide's speaker notes: the notes part is created when the slide
  ///     has none, related to the deck's notes master and back to the slide, which is
  ///     the relationship pair PowerPoint writes. The notes body placeholder is the
  ///     destination; a notes part without one gains it.</summary>
  private static void WriteNotes(SlidePart slidePart, string text)
  {
    NotesSlidePart notesPart = slidePart.NotesSlidePart ?? AddNotesPart(slidePart);
    notesPart.NotesSlide ??= new P.NotesSlide();
    WriteText(NotesBody(notesPart.NotesSlide), text);
    notesPart.NotesSlide.Save();
  }

  private static NotesSlidePart AddNotesPart(SlidePart slidePart)
  {
    NotesSlidePart notesPart = slidePart.AddNewPart<NotesSlidePart>();
    _ = notesPart.AddPart(slidePart);
    if (slidePart.OpenXmlPackage is PresentationDocument doc && doc.PresentationPart?.NotesMasterPart is { } master)
    {
      _ = notesPart.AddPart(master);
    }

    return notesPart;
  }

  /// <summary>The notes body placeholder's text body: the body placeholder when the
  ///     notes slide has one, otherwise the first shape that is not a placeholder. A
  ///     notes slide with no shape tree at all gains one holding a single body
  ///     placeholder.</summary>
  private static P.TextBody NotesBody(P.NotesSlide notesSlide)
  {
    P.ShapeTree tree = notesSlide.CommonSlideData?.ShapeTree ?? NewShapeTree(notesSlide);
    List<P.Shape> shapes = [.. tree.Elements<P.Shape>().Where(shape => shape.TextBody is not null)];
    P.Shape body = shapes.FirstOrDefault(shape => PptxShapeWalker.PlaceholderType(shape) == P.PlaceholderValues.Body)
        ?? shapes.FirstOrDefault(shape => PptxShapeWalker.PlaceholderType(shape) is null)
        ?? AddBodyPlaceholder(tree);
    return body.TextBody ?? throw new InvalidOperationException("the notes body placeholder has no text body");
  }

  private static P.ShapeTree NewShapeTree(P.NotesSlide notesSlide)
  {
    P.ShapeTree tree = new(
    [
      new P.NonVisualGroupShapeProperties(
      [
        new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
        new P.NonVisualGroupShapeDrawingProperties(),
        new P.ApplicationNonVisualDrawingProperties(),
      ]),
      new P.GroupShapeProperties(
      [
        new A.TransformGroup(
        [
          new A.Offset { X = 0L, Y = 0L },
          new A.Extents { Cx = 0L, Cy = 0L },
          new A.ChildOffset { X = 0L, Y = 0L },
          new A.ChildExtents { Cx = 0L, Cy = 0L },
        ]),
      ]),
    ]);
    notesSlide.CommonSlideData = new P.CommonSlideData([tree]);
    return tree;
  }

  private static P.Shape AddBodyPlaceholder(P.ShapeTree tree)
  {
    P.Shape placeholder = new(
    [
      new P.NonVisualShapeProperties(
      [
        new P.NonVisualDrawingProperties { Id = NextShapeId(tree), Name = "Notes Placeholder" },
        new P.NonVisualShapeDrawingProperties([new A.ShapeLocks { NoGrouping = true }]),
        new P.ApplicationNonVisualDrawingProperties([new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1 }]),
      ]),
      new P.ShapeProperties(),
      new P.TextBody([new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()]),
    ]);
    tree.Append(placeholder);
    return placeholder;
  }

  private static uint NextShapeId(P.ShapeTree tree)
    => tree.Descendants<P.NonVisualDrawingProperties>().Select(properties => properties.Id?.Value ?? 0U).DefaultIfEmpty(0U).Max() + 1U;

  private static Result<int> FailedCount(string code, string message)
    => Result.Failure<int>(new DomainError(code, message));

  private static Result<PptxResolvedEdit> Failed(string code, string message)
    => Result.Failure<PptxResolvedEdit>(new DomainError(code, message));

  private static Result<SlidePart> FailedSlide(string code, string message)
    => Result.Failure<SlidePart>(new DomainError(code, message));
}
