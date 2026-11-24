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

/// <summary>The structural slide edits: insert, delete, move and change layout. Every one
///     leaves the deck's theme, masters and layouts untouched and renumbers the slide-id
///     list, which is the deck's order; the slide parts keep whatever names they already
///     carry. Deleting a slide removes its part and every part that hangs off it, so no
///     orphaned part is left for PowerPoint to repair.
///     </summary>
internal static class PptxSlideStructure
{
  /// <summary>The first slide id PowerPoint writes in a deck's id list. Every id in the
  ///     list must be unique and at least 256.</summary>
  private const uint FirstSlideId = 256U;

  /// <summary>Inserts a slide built on the named layout immediately after
  ///     <paramref name="afterSlide"/> (0 inserts at the front), carrying that layout's
  ///     text placeholders in the order the layout states them.</summary>
  /// <param name="presentationPart">The presentation the slide joins.</param>
  /// <param name="afterSlide">The 1-based slide the new one follows; 0 inserts at the front.</param>
  /// <param name="layoutName">The layout's display name.</param>
  internal static Result<SlidePart> Insert(PresentationPart presentationPart, int afterSlide, string layoutName)
  {
    ArgumentNullException.ThrowIfNull(presentationPart);
    ArgumentNullException.ThrowIfNull(layoutName);
    P.Presentation? presentation = presentationPart.Presentation;
    if (presentation is null)
    {
      return Result.Failure<SlidePart>(CorruptError());
    }

    int count = PptxOutlineReader.SlideParts(presentationPart, presentation).Count;
    if (afterSlide < 0 || afterSlide > count)
    {
      return Result.Failure<SlidePart>(new DomainError(PptxErrorCodes.SlideNotFound,
          $"Slide {afterSlide} cannot be inserted after: the deck has {count} slide(s), so AfterSlide runs 0 to {count}."));
    }

    Result<SlideLayoutPart> layout = LayoutNamed(presentationPart, layoutName);
    if (!layout.IsSuccess)
    {
      return Result.Failure<SlidePart>(layout.Error);
    }

    SlidePart slidePart = presentationPart.AddNewPart<SlidePart>();
    _ = slidePart.AddPart(layout.Value);
    slidePart.Slide = NewSlide(layout.Value);
    slidePart.Slide.Save();
    InsertIdEntry(presentationPart, presentation, slidePart, afterSlide);
    return Result.Success(slidePart);
  }

  /// <summary>Deletes a slide, its part and every part that hangs off it, then renumbers
  ///     the id list. Deleting the deck's last slide is refused: a deck always has at
  ///     least one slide.</summary>
  /// <param name="presentationPart">The presentation the slide belongs to.</param>
  /// <param name="slide">The 1-based slide number.</param>
  internal static Result<bool> Delete(PresentationPart presentationPart, int slide)
  {
    ArgumentNullException.ThrowIfNull(presentationPart);
    P.Presentation? presentation = presentationPart.Presentation;
    if (presentation is null)
    {
      return Result.Failure<bool>(CorruptError());
    }

    List<SlidePart> slides = PptxOutlineReader.SlideParts(presentationPart, presentation);
    Result<SlidePart> target = SlideAt(slides, slide);
    if (!target.IsSuccess)
    {
      return Result.Failure<bool>(target.Error);
    }

    if (slides.Count == 1)
    {
      return Result.Failure<bool>(new DomainError(PptxErrorCodes.StrandedContent,
          $"Slide {slide} is the deck's only slide; a presentation always keeps at least one slide."));
    }

    RemoveIdEntry(presentation, presentationPart, target.Value);
    _ = presentationPart.DeletePart(target.Value);
    Renumber(presentation);
    return Result.Success(true);
  }

  /// <summary>Moves a slide to a new position and renumbers the id list. A move to the
  ///     position the slide already holds changes nothing.</summary>
  /// <param name="presentationPart">The presentation the slide belongs to.</param>
  /// <param name="from">The slide's current 1-based number.</param>
  /// <param name="to">The 1-based number it moves to.</param>
  internal static Result<bool> Move(PresentationPart presentationPart, int from, int to)
  {
    ArgumentNullException.ThrowIfNull(presentationPart);
    P.Presentation? presentation = presentationPart.Presentation;
    if (presentation is null)
    {
      return Result.Failure<bool>(CorruptError());
    }

    List<SlidePart> slides = PptxOutlineReader.SlideParts(presentationPart, presentation);
    Result<SlidePart> source = SlideAt(slides, from);
    if (!source.IsSuccess)
    {
      return Result.Failure<bool>(source.Error);
    }

    if (to < 1 || to > slides.Count)
    {
      return Result.Failure<bool>(new DomainError(PptxErrorCodes.SlideNotFound,
          $"Slide {to} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}."));
    }

    if (from == to)
    {
      return Result.Success(false);
    }

    P.SlideIdList? list = presentation.SlideIdList;
    if (list is null)
    {
      return Result.Failure<bool>(CorruptError());
    }

    List<P.SlideId> entries = [.. list.Elements<P.SlideId>()];
    P.SlideId moved = entries[from - 1];
    moved.Remove();
    Place(list, moved, to);
    Renumber(presentation);
    return Result.Success(true);
  }

  /// <summary>Moves a slide's content onto the named layout's placeholders. Content that
  ///     maps is re-identified and repositioned; content that does not map is left exactly
  ///     where it is and stays visible. A change that would leave a content placeholder
  ///     with no position on the slide is refused, naming every shape it would strand.
  ///     </summary>
  /// <param name="presentationPart">The presentation the slide belongs to.</param>
  /// <param name="slide">The 1-based slide number.</param>
  /// <param name="layoutName">The layout's display name.</param>
  internal static Result<bool> SetLayout(PresentationPart presentationPart, int slide, string layoutName)
  {
    ArgumentNullException.ThrowIfNull(presentationPart);
    ArgumentNullException.ThrowIfNull(layoutName);
    P.Presentation? presentation = presentationPart.Presentation;
    if (presentation is null)
    {
      return Result.Failure<bool>(CorruptError());
    }

    List<SlidePart> slides = PptxOutlineReader.SlideParts(presentationPart, presentation);
    Result<SlidePart> target = SlideAt(slides, slide);
    if (!target.IsSuccess)
    {
      return Result.Failure<bool>(target.Error);
    }

    Result<SlideLayoutPart> layout = LayoutNamed(presentationPart, layoutName);
    if (!layout.IsSuccess)
    {
      return Result.Failure<bool>(layout.Error);
    }

    IReadOnlyList<PptxLayoutMapping> mappings = PptxLayoutMapper.Map(target.Value, slide, layout.Value);
    List<PptxLayoutMapping> stranded = [.. mappings.Where(mapping => mapping.IsStranded)];
    if (stranded.Count > 0)
    {
      return Result.Failure<bool>(new DomainError(PptxErrorCodes.StrandedContent, StrandedMessage(slide, layoutName, stranded)));
    }

    PptxLayoutMapper.ApplyLayoutChange(target.Value, layout.Value, mappings);
    return Result.Success(true);
  }

  /// <summary>The layouts the deck offers, in master order, with the display name each is
  ///     addressed by.</summary>
  /// <param name="presentationPart">The presentation to read.</param>
  internal static List<SlideLayoutPart> Layouts(PresentationPart presentationPart)
  {
    ArgumentNullException.ThrowIfNull(presentationPart);
    return [.. presentationPart.SlideMasterParts.SelectMany(master => master.SlideLayoutParts)];
  }

  /// <summary>The layout a display name resolves to. A name that matches no layout, or
  ///     matches more than one, is LayoutNotFound and the message lists every name the
  ///     deck offers.</summary>
  /// <param name="presentationPart">The presentation to read.</param>
  /// <param name="layoutName">The display name to resolve.</param>
  internal static Result<SlideLayoutPart> LayoutNamed(PresentationPart presentationPart, string layoutName)
  {
    List<SlideLayoutPart> layouts = Layouts(presentationPart);
    List<SlideLayoutPart> matches = [.. layouts.Where(layout => NameOf(layout) == layoutName)];
    if (matches.Count == 1)
    {
      return Result.Success(matches[0]);
    }

    string names = layouts.Count == 0
        ? "the deck carries no layouts"
        : "available layouts: " + string.Join(", ", layouts.Select(layout => "'" + NameOf(layout) + "'"));
    string reason = matches.Count == 0
        ? $"The deck has no layout named '{layoutName}'"
        : $"The name '{layoutName}' matches {matches.Count} layouts and names none of them uniquely";
    return Result.Failure<SlideLayoutPart>(new DomainError(PptxErrorCodes.LayoutNotFound, $"{reason}; {names}."));
  }

  /// <summary>A layout's display name: the name its common slide data carries, which is
  ///     what a read reports and what an operation is addressed by.</summary>
  /// <param name="layout">The layout to read.</param>
  internal static string NameOf(SlideLayoutPart layout)
  {
    ArgumentNullException.ThrowIfNull(layout);
    return layout.SlideLayout?.CommonSlideData?.Name?.Value ?? string.Empty;
  }

  /// <summary>A new slide carrying the layout's text placeholders, in the order the layout
  ///     states them, each with the layout's own position and size. A layout placeholder
  ///     that is not a text placeholder is not copied: the vocabulary has no way to address
  ///     one, and a slide carrying an empty picture placeholder would report a picture the
  ///     deck does not have.</summary>
  private static P.Slide NewSlide(SlideLayoutPart layout)
  {
    P.ShapeTree tree = new(ShapeTreeProperties());
    foreach (PptxPlaceholderTarget target in PptxLayoutMapper.Placeholders(layout)
        .Where(target => target.Family is PptxPlaceholderFamily.Title or PptxPlaceholderFamily.Body))
    {
      tree.Append(PlaceholderShape(target, NextShapeId(tree)));
    }

    return new P.Slide(new P.CommonSlideData(tree))
    {
      ColorMapOverride = new P.ColorMapOverride(new A.MasterColorMapping()),
    };
  }

  /// <summary>The shape tree's own properties: the group id and the group's transform, which
  ///     every slide's shape tree carries.</summary>
  private static OpenXmlElement[] ShapeTreeProperties() =>
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
  ];

  /// <summary>A slide placeholder built from a layout placeholder: the layout's identity,
  ///     its position and size, and an empty text body.</summary>
  private static P.Shape PlaceholderShape(PptxPlaceholderTarget target, uint id)
  {
    P.PlaceholderShape placeholder = new();
    if (target.Type is { } type)
    {
      placeholder.Type = type;
    }

    if (target.Index != 0)
    {
      placeholder.Index = (uint)target.Index;
    }

    P.ShapeProperties properties = new();
    if (target.Transform is { } transform)
    {
      properties.Transform2D = transform.CloneNode(true) as A.Transform2D;
    }

    return new P.Shape(
    [
      new P.NonVisualShapeProperties(
      [
        new P.NonVisualDrawingProperties { Id = id, Name = target.Name },
        new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
        new P.ApplicationNonVisualDrawingProperties(placeholder),
      ]),
      properties,
      new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()),
    ]);
  }

  private static uint NextShapeId(P.ShapeTree tree)
    => tree.Descendants<P.NonVisualDrawingProperties>().Select(properties => properties.Id?.Value ?? 0U).DefaultIfEmpty(1U).Max() + 1U;

  /// <summary>Adds the slide's id entry at the position the insert asked for, then
  ///     renumbers the list so every id is unique and increasing.</summary>
  private static void InsertIdEntry(PresentationPart presentationPart, P.Presentation presentation,
      SlidePart slidePart, int afterSlide)
  {
    P.SlideIdList list = presentation.SlideIdList ?? presentation.AppendChild(new P.SlideIdList());
    string relationshipId = presentationPart.GetIdOfPart(slidePart);
    P.SlideId entry = new() { Id = NextSlideId(list), RelationshipId = relationshipId };
    Place(list, entry, afterSlide + 1);
    Renumber(presentation);
  }

  /// <summary>Puts an entry at the 1-based position the deck's order asks for. Position 1
  ///     is the head of the list; any other position follows the entry that precedes it.
  ///     </summary>
  private static void Place(P.SlideIdList list, P.SlideId entry, int position)
  {
    if (position <= 1)
    {
      _ = list.InsertAt(entry, 0);
      return;
    }

    _ = list.InsertAfter(entry, list.Elements<P.SlideId>().ElementAt(position - 2));
  }

  /// <summary>Removes the id entry that names the slide part, so the part and its entry
  ///     leave the deck together.</summary>
  private static void RemoveIdEntry(P.Presentation presentation, PresentationPart presentationPart,
      SlidePart slidePart)
  {
    string relationshipId = presentationPart.GetIdOfPart(slidePart);
    P.SlideId? entry = presentation.SlideIdList?.Elements<P.SlideId>()
        .FirstOrDefault(candidate => candidate.RelationshipId?.Value == relationshipId);
    entry?.Remove();
  }

  /// <summary>Renumbers the id list from the first id PowerPoint writes, so the ids are
  ///     unique and increasing. The list's order is the deck's order and is not changed
  ///     here; only the ids are.</summary>
  private static void Renumber(P.Presentation presentation)
  {
    P.SlideIdList? list = presentation.SlideIdList;
    if (list is null)
    {
      return;
    }

    uint id = FirstSlideId;
    foreach (P.SlideId entry in list.Elements<P.SlideId>())
    {
      entry.Id = id;
      id++;
    }
  }

  private static uint NextSlideId(P.SlideIdList list)
    => list.Elements<P.SlideId>().Select(entry => entry.Id?.Value ?? 0U).DefaultIfEmpty(FirstSlideId - 1U).Max() + 1U;

  private static Result<SlidePart> SlideAt(List<SlidePart> slides, int slide)
    => slide >= 1 && slide <= slides.Count
        ? Result.Success(slides[slide - 1])
        : Result.Failure<SlidePart>(new DomainError(PptxErrorCodes.SlideNotFound,
            $"Slide {slide} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}."));

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

  private static DomainError CorruptError()
    => new(PptxErrorCodes.CorruptPresentation,
        $"The package has no readable {PptxFormatSniffer.PresentationPartName} part.");
}
