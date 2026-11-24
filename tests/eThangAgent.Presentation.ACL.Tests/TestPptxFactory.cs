using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>One slide a fixture deck is built from: the text its title and body
///     placeholders carry, its speaker notes, whether it is hidden, the layout it sits
///     on, and the extra content it carries. Every later test task extends this spec
///     rather than hand-writing part XML.</summary>
internal sealed record TestSlideSpec
{
  internal string Title { get; init; } = string.Empty;

  internal string Body { get; init; } = string.Empty;

  internal string Notes { get; init; } = string.Empty;

  internal bool Hidden { get; init; }

  internal string LayoutName { get; init; } = "Title and Content";

  internal bool WithPicture { get; init; }

  /// <summary>The table's cells, row by row, when <see cref="WithTable"/> is set. Null
  ///     builds a 2x2 table.</summary>
  internal IReadOnlyList<IReadOnlyList<string>>? TableCells { get; init; }

  /// <summary>A plain text box whose single paragraph carries a line break.</summary>
  internal bool WithLineBreak { get; init; }

  /// <summary>A plain text box with five runs, two of them empty.</summary>
  internal bool WithEmptyRuns { get; init; }

  /// <summary>A timing element with no effect in it, as a deck that has a sequence but no
  ///     animation writes.</summary>
  internal bool WithEmptyTiming { get; init; }

  /// <summary>A legacy comment anchored at the slide, authored by 'Reviewer'.</summary>
  internal bool WithLegacyComment { get; init; }

  /// <summary>The title placeholder carries explicit character formatting (24pt bold,
  ///     centred) and an explicit position and size, as a deck authored in PowerPoint
  ///     does.</summary>
  internal bool WithFormattedTitle { get; init; }

  internal bool WithTable { get; init; }

  internal bool WithChart { get; init; }

  internal bool WithGroup { get; init; }

  internal bool WithAnimation { get; init; }
}

/// <summary>Builds fixture decks from <see cref="TestSlideSpec"/> values. The package is
///     assembled part by part through the SDK so relationships, content types and part
///     names are what PowerPoint itself writes; only the part XML is authored here.
///     </summary>
internal static class TestPptxFactory
{
  /// <summary>An 8x8 PNG: a real image part, so a picture shape resolves to real bytes.
  ///     </summary>
  internal const string PngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAABHNCSVQICAgIfAhkiAAAACpJREFUGJVjNE47858BD2DCJ8nAwMDAAmOcmWmMImGSfpY4EyhXwEixLwAuhwfZcPTyDwAAAABJRU5ErkJggg==";

  /// <summary>Builds a deck whose slide-id list carries the slides in the order given.
  ///     </summary>
  internal static string CreateDeck(string path, IReadOnlyList<TestSlideSpec> slides)
    => CreateDeck(path, slides, [.. Enumerable.Range(0, slides.Count)]);

  /// <summary>Builds a deck whose slide PARTS are written in <paramref name="slides"/>
  ///     order while the slide-id list carries them in <paramref name="presentationOrder"/>
  ///     order (indices into <paramref name="slides"/>). Part names and presentation order
  ///     are independent, which is what a real deck relies on.</summary>
  internal static string CreateDeck(string path, IReadOnlyList<TestSlideSpec> slides,
      IReadOnlyList<int> presentationOrder)
  {
    ArgumentNullException.ThrowIfNull(slides);
    ArgumentNullException.ThrowIfNull(presentationOrder);
    using PresentationDocument doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation, true);
    PresentationPart presentationPart = doc.PresentationPart ?? doc.AddPresentationPart();
    List<string> layoutNames = [.. slides.Select(slide => slide.LayoutName).Distinct(StringComparer.Ordinal)];
    Dictionary<string, SlideLayoutPart> layouts = AddLayouts(presentationPart, layoutNames);
    List<string> slideRelationshipIds = AddSlides(presentationPart, slides, layouts);
    List<string> orderedRelationshipIds = [.. presentationOrder.Select(index => slideRelationshipIds[index])];
    if (slides.Any(slide => slide.WithLegacyComment))
    {
      CommentAuthorsPart authorsPart = presentationPart.AddNewPart<CommentAuthorsPart>("rIdAuthors");
      authorsPart.FeedData(Utf8(TestPptxXml.CommentAuthors()));
    }

    presentationPart.FeedData(Utf8(TestPptxXml.Presentation(orderedRelationshipIds, MasterRelationshipId)));
    return path;
  }

  /// <summary>A one-slide deck: a title reading 'Minimal' over a body reading 'Body'.
  ///     </summary>
  internal static string CreateMinimalDeck(string path)
    => CreateDeck(path, [new TestSlideSpec { Title = "Minimal", Body = "Body" }]);

  private const string MasterRelationshipId = "rIdMaster1";

  private static Dictionary<string, SlideLayoutPart> AddLayouts(PresentationPart presentationPart,
      IReadOnlyList<string> layoutNames)
  {
    SlideMasterPart masterPart = presentationPart.AddNewPart<SlideMasterPart>(MasterRelationshipId);
    Dictionary<string, SlideLayoutPart> layouts = [];
    List<string> layoutRelationshipIds = [];
    foreach (string name in layoutNames)
    {
      string relationshipId = "rIdLayout" + (layouts.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
      SlideLayoutPart layoutPart = masterPart.AddNewPart<SlideLayoutPart>(relationshipId);
      layoutPart.FeedData(Utf8(TestPptxXml.Layout(name)));
      layouts[name] = layoutPart;
      layoutRelationshipIds.Add(relationshipId);
    }

    masterPart.FeedData(Utf8(TestPptxXml.Master(layoutRelationshipIds)));
    return layouts;
  }

  private static List<string> AddSlides(PresentationPart presentationPart, IReadOnlyList<TestSlideSpec> slides,
      Dictionary<string, SlideLayoutPart> layouts)
  {
    List<string> relationshipIds = [];
    for (int index = 0; index < slides.Count; index++)
    {
      TestSlideSpec spec = slides[index];
      string relationshipId = "rIdSlide" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
      SlidePart slidePart = presentationPart.AddNewPart<SlidePart>(relationshipId);
      _ = slidePart.AddPart(layouts[spec.LayoutName], "rIdLayoutRef");
      slidePart.FeedData(Utf8(BuildSlide(slidePart, spec)));
      relationshipIds.Add(relationshipId);
    }

    return relationshipIds;
  }

  private static string BuildSlide(SlidePart slidePart, TestSlideSpec spec)
  {
    StringBuilder shapes = new();
    _ = shapes.Append(TestPptxXml.SlidePlaceholders(spec.LayoutName, spec.Title, spec.Body, spec.WithFormattedTitle));
    int nextId = PlaceholderCount(spec.LayoutName) + 2;
    int firstShapeId = 2;
    if (spec.WithPicture)
    {
      ImagePart imagePart = slidePart.AddImagePart(ImagePartType.Png, "rIdImage1");
      using MemoryStream image = new(Convert.FromBase64String(PngBase64));
      imagePart.FeedData(image);
      _ = shapes.Append(TestPptxXml.PictureShape(nextId++, "rIdImage1"));
    }

    if (spec.WithTable)
    {
      _ = shapes.Append(TestPptxXml.TableShape(nextId++, spec.TableCells ?? [["R1C1", "R1C2"], ["R2C1", "R2C2"]]));
    }

    if (spec.WithChart)
    {
      ChartPart chartPart = slidePart.AddNewPart<ChartPart>("rIdChart1");
      chartPart.FeedData(Utf8(TestPptxXml.Chart()));
      _ = shapes.Append(TestPptxXml.ChartShape(nextId++, "rIdChart1"));
    }

    if (spec.WithGroup)
    {
      _ = shapes.Append(TestPptxXml.GroupShape(nextId, "Grouped one", "Grouped two"));
    }

    if (spec.WithLineBreak)
    {
      _ = shapes.Append(TestPptxXml.BreakShape(nextId++, "First line", "Second line"));
    }

    if (spec.WithEmptyRuns)
    {
      _ = shapes.Append(TestPptxXml.EmptyRunsShape(nextId));
    }

    if (spec.WithLegacyComment)
    {
      SlideCommentsPart commentsPart = slidePart.AddNewPart<SlideCommentsPart>("rIdComments");
      commentsPart.FeedData(Utf8(TestPptxXml.LegacyComments("Check this figure")));
    }

    if (spec.Notes.Length > 0)
    {
      NotesSlidePart notesPart = slidePart.AddNewPart<NotesSlidePart>("rIdNotes");
      _ = notesPart.AddPart(slidePart);
      notesPart.FeedData(Utf8(TestPptxXml.Notes(spec.Notes)));
    }

    return TestPptxXml.Slide(shapes.ToString(), spec.Hidden, Timing(spec, firstShapeId));
  }

  private static string? Timing(TestSlideSpec spec, int firstShapeId)
    => spec.WithAnimation ? TestPptxXml.Timing(firstShapeId) : EmptyTiming(spec);

  private static string? EmptyTiming(TestSlideSpec spec)
    => spec.WithEmptyTiming ? TestPptxXml.EmptyTiming() : null;

  /// <summary>The number of placeholders a layout kind writes, so the shapes after them take
  ///     the next drawing id.</summary>
  private static int PlaceholderCount(string layoutName) => layoutName switch
  {
    "Title Only" => 1,
    _ => 2,
  };

  private static MemoryStream Utf8(string xml) => new(Encoding.UTF8.GetBytes(xml));
}
