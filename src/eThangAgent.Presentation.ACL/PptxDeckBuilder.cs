using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

// S3220/S3878 flag every OOXML element-constructor call below: each element tree is
// built by handing its children to the constructor, which is the overload the SDK
// declares for it. S1075 flags the xml namespace URI, which is the schema's own
// namespace and not a path.
#pragma warning disable S3220, S3878, S1075
namespace eThangAgent.Presentation.ACL;

/// <summary>One inline span of a slide's text: the text plus its bold/italic flags, the
///     pinned subset's only inline formatting.</summary>
internal sealed record PptxSourceSpan(string Text, bool Bold, bool Italic);

/// <summary>One body line of a slide: its 1-based outline level and its spans.</summary>
internal sealed record PptxSourceBullet(int Level, IReadOnlyList<PptxSourceSpan> Spans);

/// <summary>One slide of the create source: the title's spans and the body bullets, both
///     empty when the slide carries no text.</summary>
internal sealed record PptxSourceSlide(IReadOnlyList<PptxSourceSpan> TitleSpans,
    IReadOnlyList<PptxSourceBullet> Bullets);

/// <summary>Parses the create source into a slide list: a line whose trimmed text is
///     exactly three hyphens separates slides; the first non-empty line of a slide is its
///     title; every other non-empty line is a body bullet, at level 2 when it is indented
///     by a tab or by at least two spaces. Heading and list markers are stripped and
///     **bold** / *italic* spans are kept. A source with no text, and a slide with no
///     text, are MarkdownInvalid: a deck is never created with an empty slide.</summary>
internal static class PptxDeckSource
{
  private const string SlideSeparator = "---";
  private const string BoldMarker = "**";
  private const string BulletMarker = "- ";
  private const int SecondLevelIndent = 2;

  /// <summary>Parses the source into its slide list.</summary>
  /// <param name="markdown">The create source.</param>
  internal static Result<IReadOnlyList<PptxSourceSlide>> Parse(string markdown)
  {
    ArgumentNullException.ThrowIfNull(markdown);
    if (markdown.Length > PptxLimits.MaxMarkdownChars)
    {
      return Result.Failure<IReadOnlyList<PptxSourceSlide>>(new DomainError(PptxErrorCodes.MarkdownInvalid,
          $"The source is longer than the {PptxLimits.MaxMarkdownChars}-character cap."));
    }

    string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    List<PptxSourceSlide> slides = [];
    List<(string Line, int Number)> pending = [];
    for (int i = 0; i < lines.Length; i++)
    {
      string line = lines[i].TrimEnd();
      if (line.Trim() != SlideSeparator)
      {
        pending.Add((line, i + 1));
        continue;
      }

      Result<PptxSourceSlide> flushed = SlideOf(pending, slides.Count + 1);
      if (!flushed.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<PptxSourceSlide>>(flushed.Error);
      }

      slides.Add(flushed.Value);
      pending.Clear();
    }

    Result<PptxSourceSlide> last = SlideOf(pending, slides.Count + 1);
    if (!last.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<PptxSourceSlide>>(last.Error);
    }

    slides.Add(last.Value);
    return Result.Success<IReadOnlyList<PptxSourceSlide>>(slides);
  }

  /// <summary>Builds one slide from its source lines: the first non-empty line is the
  ///     title and every other non-empty line is a bullet.</summary>
  private static Result<PptxSourceSlide> SlideOf(IReadOnlyList<(string Line, int Number)> lines, int number)
  {
    List<PptxSourceSpan> title = [];
    List<PptxSourceBullet> bullets = [];
    foreach ((string line, int lineNumber) in lines)
    {
      if (line.Trim().Length == 0)
      {
        continue;
      }

      if (title.Count == 0)
      {
        Result<IReadOnlyList<PptxSourceSpan>> heading = SpansOf(MarkerStripped(line.TrimStart(' ', '\t')), lineNumber);
        if (!heading.IsSuccess)
        {
          return Result.Failure<PptxSourceSlide>(heading.Error);
        }

        if (heading.Value.Count > 0 && heading.Value.Any(span => span.Text.Trim().Length > 0))
        {
          title.AddRange(heading.Value);
          continue;
        }
      }

      Result<PptxSourceBullet> bullet = BulletOf(line, lineNumber);
      if (!bullet.IsSuccess)
      {
        return Result.Failure<PptxSourceSlide>(bullet.Error);
      }

      bullets.Add(bullet.Value);
    }

    return title.Count == 0 && bullets.Count == 0
      ? Failure(number, $"slide {number} carries no text")
      : Result.Success(new PptxSourceSlide(title, bullets));
  }

  /// <summary>One body line: its outline level from the indentation, its list or heading
  ///     marker stripped, and its spans.</summary>
  private static Result<PptxSourceBullet> BulletOf(string line, int lineNumber)
  {
    (int level, string text) = IndentOf(line);
    Result<IReadOnlyList<PptxSourceSpan>> spans = SpansOf(MarkerStripped(text), lineNumber);
    return spans.IsSuccess
      ? Result.Success(new PptxSourceBullet(level, spans.Value))
      : Result.Failure<PptxSourceBullet>(spans.Error);
  }

  /// <summary>The level a line's indentation states and the text it leaves: a tab or at
  ///     least two leading spaces is level 2, anything less is level 1.</summary>
  private static (int Level, string Text) IndentOf(string line)
  {
    int spaces = 0;
    while (spaces < line.Length && line[spaces] == ' ')
    {
      spaces++;
    }

    bool tabbed = line.Length > 0 && line[0] == '\t';
    return tabbed || spaces >= SecondLevelIndent
      ? (2, line.TrimStart(' ', '\t'))
      : (1, line.TrimStart(' '));
  }

  /// <summary>A line's text with its leading bullet, numbered-item or heading marker
  ///     removed. A line that carries none is returned unchanged.</summary>
  private static string MarkerStripped(string text)
  {
    if (text.StartsWith(BulletMarker, StringComparison.Ordinal))
    {
      return text[BulletMarker.Length..];
    }

    int hashes = 0;
    while (hashes < text.Length && text[hashes] == '#')
    {
      hashes++;
    }

    if (hashes > 0 && hashes < text.Length && text[hashes] == ' ')
    {
      return text[(hashes + 1)..];
    }

    int dot = text.IndexOf(". ", StringComparison.Ordinal);
    return dot > 0 && text[..dot].All(char.IsAsciiDigit) ? text[(dot + 2)..] : text;
  }

  /// <summary>Splits one line's text into bold/italic spans. Bold wins at an overlap and
  ///     an unclosed marker is MarkdownInvalid naming the line.</summary>
  private static Result<IReadOnlyList<PptxSourceSpan>> SpansOf(string text, int lineNumber)
  {
    List<PptxSourceSpan> spans = [];
    int at = 0;
    while (at < text.Length)
    {
      int next = NextMarker(text, at);
      if (next < 0)
      {
        spans.Add(new PptxSourceSpan(text[at..], false, false));
        break;
      }

      if (next > at)
      {
        spans.Add(new PptxSourceSpan(text[at..next], false, false));
      }

      Result<int> consumed = SpanAt(text, next, spans, lineNumber);
      if (!consumed.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<PptxSourceSpan>>(consumed.Error);
      }

      at = consumed.Value;
    }

    return Result.Success<IReadOnlyList<PptxSourceSpan>>(spans);
  }

  /// <summary>The next bold or italic marker at or after <paramref name="from"/>, or -1
  ///     when the text carries none. A bold marker wins when both start at the same
  ///     position.</summary>
  private static int NextMarker(string text, int from)
  {
    int bold = text.IndexOf(BoldMarker, from, StringComparison.Ordinal);
    int italic = text.IndexOf('*', from, StringComparison.Ordinal);
    return (bold >= 0 && (italic < 0 || bold <= italic)) ? bold : italic;
  }

  /// <summary>Consumes the span that opens at <paramref name="open"/> and returns the
  ///     index after its closing marker. An unclosed marker is MarkdownInvalid naming the
  ///     line.</summary>
  private static Result<int> SpanAt(string text, int open, List<PptxSourceSpan> spans, int lineNumber)
  {
    bool bold = text.AsSpan(open).StartsWith(BoldMarker, StringComparison.Ordinal);
    int start = bold ? open + BoldMarker.Length : open + 1;
    int close = bold
      ? text.IndexOf(BoldMarker, start, StringComparison.Ordinal)
      : text.IndexOf('*', start, StringComparison.Ordinal);
    if (close < 0)
    {
      return Result.Failure<int>(new DomainError(PptxErrorCodes.MarkdownInvalid,
          $"The source violates the pinned markdown subset at line {lineNumber}: "
          + (bold ? "a **bold** marker is not closed." : "an *italic* marker is not closed.")));
    }

    spans.Add(new PptxSourceSpan(text[start..close], bold, !bold));
    return Result.Success(close + (bold ? BoldMarker.Length : 1));
  }

  private static Result<PptxSourceSlide> Failure(int line, string reason)
    => Result.Failure<PptxSourceSlide>(new DomainError(PptxErrorCodes.MarkdownInvalid,
        line == 0
          ? $"The source violates the pinned markdown subset: {reason}."
          : $"The source violates the pinned markdown subset at line {line}: {reason}."));
}

/// <summary>A shape's position and size in English Metric Units.</summary>
internal sealed record PptxBox(long X, long Y, long Width, long Height);

/// <summary>Builds a complete new .pptx package from the create source: a widescreen
///     deck on a built-in Office theme, with one master, one title-and-content layout
///     shared by every slide, and one slide per source slide. The file must not exist
///     (defense in depth - the caller guards too).</summary>
internal static class PptxDeckBuilder
{
  /// <summary>The slide width a 16:9 deck states, in English Metric Units.</summary>
  internal const long SlideWidthEmu = 12_192_000L;

  /// <summary>The slide height a 16:9 deck states, in English Metric Units.</summary>
  internal const long SlideHeightEmu = 6_858_000L;

  /// <summary>The display name of the one layout every created slide is built on.</summary>
  internal const string TitleAndContentLayoutName = "Title and Content";

  private const long NotesWidthEmu = 6_858_000L;
  private const long NotesHeightEmu = 9_144_000L;
  private const uint FirstSlideId = 256U;
  private const uint MasterId = 2_147_483_648U;
  private const uint FirstLayoutId = 2_147_483_649U;
  private const uint TitleShapeId = 2U;
  private const uint BodyShapeId = 3U;
  private const long TitleX = 838_200L;
  private const long TitleY = 365_125L;
  private const long TitleWidth = 10_515_600L;
  private const long TitleHeight = 1_325_563L;
  private const long BodyX = 838_200L;
  private const long BodyY = 1_825_625L;
  private const long BodyWidth = 10_515_600L;
  private const long BodyHeight = 4_351_338L;
  private const string XmlNamespaceUri = "http://www.w3.org/XML/1998/namespace";
  private const int TitleFontSize = 4400;
  private const int BodyFontSize = 2800;
  private const int BulletIndent = 342_900;
  private const int SecondLevelIndent = 457_200;

  /// <summary>The position and size a created deck gives its title placeholder.</summary>
  private static readonly PptxBox TitleBox = new(TitleX, TitleY, TitleWidth, TitleHeight);

  /// <summary>The position and size a created deck gives its body placeholder.</summary>
  private static readonly PptxBox BodyBox = new(BodyX, BodyY, BodyWidth, BodyHeight);

  /// <summary>Parses the source and writes the deck to a path that does not exist yet.
  ///     A source violation is MarkdownInvalid and nothing is written; a package that
  ///     cannot be written leaves no partial file behind.</summary>
  /// <param name="path">The file to create.</param>
  /// <param name="markdown">The create source.</param>
  internal static Result<PptxSaveOutcome> Build(string path, string markdown)
  {
    ArgumentNullException.ThrowIfNull(path);
    Result<IReadOnlyList<PptxSourceSlide>> source = PptxDeckSource.Parse(markdown);
    if (!source.IsSuccess)
    {
      return Result.Failure<PptxSaveOutcome>(source.Error);
    }

    try
    {
      Write(path, source.Value);
    }
    catch (OpenXmlPackageException ex)
    {
      return Failure(path, ex.Message);
    }
    catch (IOException ex)
    {
      return Failure(path, ex.Message);
    }
    catch (UnauthorizedAccessException ex)
    {
      return Failure(path, ex.Message);
    }

    FileInfo info = new(path);
    return Result.Success(new PptxSaveOutcome(new FileVersion(info.Length, info.LastWriteTimeUtc)));
  }

  /// <summary>Writes the package: the presentation part, its master, the master's theme
  ///     and layout, and one slide per source slide in source order.</summary>
  private static void Write(string path, IReadOnlyList<PptxSourceSlide> slides)
  {
    using PresentationDocument doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
    PresentationPart presentationPart = doc.AddPresentationPart();
    SlideMasterPart master = AddMaster(presentationPart);
    SlideLayoutPart layout = AddLayout(master);
    List<string> slideRelationshipIds = [];
    foreach (PptxSourceSlide slide in slides)
    {
      SlidePart slidePart = presentationPart.AddNewPart<SlidePart>();
      _ = slidePart.AddPart(layout);
      slidePart.Slide = SlideOf(slide);
      slidePart.Slide.Save();
      slideRelationshipIds.Add(presentationPart.GetIdOfPart(slidePart));
    }

    presentationPart.Presentation = PresentationOf(presentationPart, master, slideRelationshipIds);
    doc.Save();
  }

  /// <summary>The presentation part: the master reference, the slide-id list in source
  ///     order, the widescreen slide size and the notes size.</summary>
  private static P.Presentation PresentationOf(PresentationPart presentationPart, SlideMasterPart master,
      List<string> slideRelationshipIds)
  {
    P.SlideIdList idList = new();
    for (int i = 0; i < slideRelationshipIds.Count; i++)
    {
      idList.Append(new P.SlideId { Id = FirstSlideId + (uint)i, RelationshipId = slideRelationshipIds[i] });
    }

    return new P.Presentation(
    [
      new P.SlideMasterIdList(new P.SlideMasterId { Id = MasterId, RelationshipId = presentationPart.GetIdOfPart(master) }),
      idList,
      new P.SlideSize { Cx = (int)SlideWidthEmu, Cy = (int)SlideHeightEmu, Type = P.SlideSizeValues.Screen16x9 },
      new P.NotesSize { Cx = (int)NotesWidthEmu, Cy = (int)NotesHeightEmu },
    ]);
  }

  /// <summary>The master, its theme part and its colour map. The master carries no shapes
  ///     of its own: the design lives in the theme and in the layout's placeholders.
  ///     </summary>
  private static SlideMasterPart AddMaster(PresentationPart presentationPart)
  {
    SlideMasterPart master = presentationPart.AddNewPart<SlideMasterPart>();
    ThemePart theme = master.AddNewPart<ThemePart>();
    theme.FeedData(new MemoryStream(Encoding.UTF8.GetBytes(OfficeTheme)));
    master.SlideMaster = new P.SlideMaster(
    [
      new P.CommonSlideData(new P.ShapeTree(ShapeTreeProperties())),
      ColorMap(),
      new P.SlideLayoutIdList(),
      TextStyles(),
    ]);
    return master;
  }

  /// <summary>The one layout every slide is built on: a title placeholder and a body
  ///     placeholder carrying the bullet characters a body line renders with.</summary>
  private static SlideLayoutPart AddLayout(SlideMasterPart master)
  {
    SlideLayoutPart layout = master.AddNewPart<SlideLayoutPart>();
    _ = layout.AddPart(master);
    P.ShapeTree tree = new(ShapeTreeProperties());
    tree.Append(TitlePlaceholder());
    tree.Append(BodyPlaceholder());
    layout.SlideLayout = new P.SlideLayout(
    [
      new P.CommonSlideData(tree) { Name = TitleAndContentLayoutName },
      new P.ColorMapOverride(new A.MasterColorMapping()),
    ])
    {
      Type = P.SlideLayoutValues.Object,
      Preserve = true,
    };
    master.SlideMaster!.SlideLayoutIdList!.Append(
        new P.SlideLayoutId { Id = FirstLayoutId, RelationshipId = master.GetIdOfPart(layout) });
    master.SlideMaster.Save();
    return layout;
  }

  /// <summary>One slide: its title text box and its body text box, each carrying the
  ///     layout's placeholder identity and geometry and the source's text.</summary>
  private static P.Slide SlideOf(PptxSourceSlide source)
  {
    P.ShapeTree tree = new(ShapeTreeProperties());
    tree.Append(PlaceholderShape(TitleShapeId, "Title 1", P.PlaceholderValues.Title, null,
        TitleBox, [ParagraphOf(source.TitleSpans, 1)]));
    tree.Append(PlaceholderShape(BodyShapeId, "Content Placeholder 2", P.PlaceholderValues.Body, 1U,
        BodyBox, BulletParagraphsOf(source.Bullets)));
    return new P.Slide(new P.CommonSlideData(tree))
    {
      ColorMapOverride = new P.ColorMapOverride(new A.MasterColorMapping()),
    };
  }

  /// <summary>One paragraph per body line, at the outline level the source's indentation
  ///     states.</summary>
  private static List<A.Paragraph> BulletParagraphsOf(IReadOnlyList<PptxSourceBullet> bullets)
    => [.. bullets.Select(bullet => ParagraphOf(bullet.Spans, bullet.Level))];

  /// <summary>One paragraph carrying the spans as runs, at the given 1-based outline
  ///     level.</summary>
  private static A.Paragraph ParagraphOf(IReadOnlyList<PptxSourceSpan> spans, int level)
  {
    A.Paragraph paragraph = new();
    if (level > 1)
    {
      paragraph.ParagraphProperties = new A.ParagraphProperties { Level = level - 1 };
    }

    foreach (PptxSourceSpan span in spans)
    {
      paragraph.Append(span.Text.Length == 0 ? new A.EndParagraphRunProperties() : RunOf(span));
    }

    return paragraph;
  }

  /// <summary>One run, carrying the span's bold and italic flags when it states them.
  ///     </summary>
  private static A.Run RunOf(PptxSourceSpan span)
  {
    A.Text text = new(span.Text);
    text.SetAttribute(new OpenXmlAttribute("xml", "space", XmlNamespaceUri, "preserve"));
    if (!span.Bold && !span.Italic)
    {
      return new A.Run(text);
    }

    A.RunProperties properties = new()
    {
      Bold = span.Bold ? true : null,
      Italic = span.Italic ? true : null,
    };
    return new A.Run(properties, text);
  }

  /// <summary>The layout's title placeholder: the title type, the title geometry and an
  ///     empty text body.</summary>
  private static P.Shape TitlePlaceholder()
    => PlaceholderShape(TitleShapeId, "Title 1", P.PlaceholderValues.Title, null,
        TitleBox, [new A.Paragraph()]);

  /// <summary>The layout's body placeholder: the body type at index 1, the body geometry
  ///     and a text list style whose two levels carry the bullet characters a body line
  ///     renders with.</summary>
  private static P.Shape BodyPlaceholder()
  {
    P.Shape shape = PlaceholderShape(BodyShapeId, "Content Placeholder 2", P.PlaceholderValues.Body, 1U,
        BodyBox, [new A.Paragraph()]);
    shape.TextBody!.ListStyle = BulletListStyle();
    return shape;
  }

  /// <summary>The two bullet levels a body placeholder states: a round bullet at level 1
  ///     and a dash at level 2, indented one step further.</summary>
  private static A.ListStyle BulletListStyle()
    => new(BulletLevel1(), BulletLevel2());

  /// <summary>The first bullet level: a round bullet one indent step in.</summary>
  private static A.Level1ParagraphProperties BulletLevel1()
  {
    A.Level1ParagraphProperties level = new() { LeftMargin = BulletIndent, Indent = -BulletIndent };
    BulletChildren(level, "\u2022");
    return level;
  }

  /// <summary>The second bullet level: a dash indented one step past the first.</summary>
  private static A.Level2ParagraphProperties BulletLevel2()
  {
    A.Level2ParagraphProperties level = new()
    {
      LeftMargin = SecondLevelIndent + BulletIndent,
      Indent = -BulletIndent,
    };
    BulletChildren(level, "\u2013");
    return level;
  }

  /// <summary>The bullet a level renders with: its colour, size, font and character, in
  ///     the order the schema states them.</summary>
  private static void BulletChildren(OpenXmlElement level, string bullet)
  {
    level.Append(new A.BulletColor(new A.SchemeColor { Val = A.SchemeColorValues.Text1 }));
    level.Append(new A.BulletSizePercentage { Val = 100 });
    level.Append(new A.BulletFont { Typeface = "Arial" });
    level.Append(new A.CharacterBullet { Char = bullet });
  }

  /// <summary>A placeholder shape: its identity, its placeholder declaration, its
  ///     position and size, and its text body.</summary>
  private static P.Shape PlaceholderShape(uint id, string name, P.PlaceholderValues type,
      uint? index, PptxBox box, IReadOnlyList<A.Paragraph> paragraphs)
  {
    P.PlaceholderShape placeholder = new() { Type = type };
    if (index is { } value)
    {
      placeholder.Index = value;
    }

    A.Transform2D transform = new()
    {
      Offset = new A.Offset { X = box.X, Y = box.Y },
      Extents = new A.Extents { Cx = box.Width, Cy = box.Height },
    };
    P.TextBody body = new(new A.BodyProperties(), new A.ListStyle());
    foreach (A.Paragraph paragraph in paragraphs)
    {
      body.Append(paragraph);
    }

    return new P.Shape(
    [
      new P.NonVisualShapeProperties(
      [
        new P.NonVisualDrawingProperties { Id = id, Name = name },
        new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
        new P.ApplicationNonVisualDrawingProperties(placeholder),
      ]),
      new P.ShapeProperties(transform,
          new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
      body,
    ]);
  }

  /// <summary>The shape tree's own properties: the group id and the group's transform,
  ///     which every slide's shape tree carries.</summary>
  private static OpenXmlElement[] ShapeTreeProperties() =>
  [
    new P.NonVisualGroupShapeProperties(
    [
      new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
      new P.NonVisualGroupShapeDrawingProperties(),
      new P.ApplicationNonVisualDrawingProperties(),
    ]),
    new P.GroupShapeProperties(new A.TransformGroup(
    [
      new A.Offset { X = 0L, Y = 0L },
      new A.Extents { Cx = 0L, Cy = 0L },
      new A.ChildOffset { X = 0L, Y = 0L },
      new A.ChildExtents { Cx = 0L, Cy = 0L },
    ])),
  ];

  /// <summary>The master's colour map: the Office theme's own scheme indices.</summary>
  private static P.ColorMap ColorMap()
    => new()
    {
      Background1 = A.ColorSchemeIndexValues.Light1,
      Text1 = A.ColorSchemeIndexValues.Dark1,
      Background2 = A.ColorSchemeIndexValues.Light2,
      Text2 = A.ColorSchemeIndexValues.Dark2,
      Accent1 = A.ColorSchemeIndexValues.Accent1,
      Accent2 = A.ColorSchemeIndexValues.Accent2,
      Accent3 = A.ColorSchemeIndexValues.Accent3,
      Accent4 = A.ColorSchemeIndexValues.Accent4,
      Accent5 = A.ColorSchemeIndexValues.Accent5,
      Accent6 = A.ColorSchemeIndexValues.Accent6,
      Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
      FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
    };

  /// <summary>The master's text styles: a title size and a body size, so a slide's text
  ///     renders at the sizes a deck's text renders at rather than at the schema default.
  ///     </summary>
  private static P.TextStyles TextStyles()
    => new(
      new P.TitleStyle(new A.Level1ParagraphProperties(
          new A.DefaultRunProperties { FontSize = TitleFontSize, Bold = true })),
      new P.BodyStyle(new A.Level1ParagraphProperties(
          new A.DefaultRunProperties { FontSize = BodyFontSize })),
      new P.OtherStyle());

  private static Result<PptxSaveOutcome> Failure(string path, string message)
  {
    DeletePartial(path);
    return Result.Failure<PptxSaveOutcome>(new DomainError(PptxErrorCodes.CorruptPresentation,
        $"'{path}' could not be written as a presentation: {message}"));
  }

  /// <summary>Removes a partially written file, so a failed create leaves nothing behind.
  ///     </summary>
  private static void DeletePartial(string path)
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
      // best-effort cleanup of a file that was never a valid deck
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort cleanup of a file that was never a valid deck
    }
  }

  /// <summary>The built-in Office theme a created deck carries: the colour scheme, the
  ///     font scheme and the format scheme PowerPoint applies when no design is chosen.
  ///     </summary>
  private const string OfficeTheme = """
      <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
      <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Office Theme">
        <a:themeElements>
          <a:clrScheme name="Office">
            <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
            <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
            <a:dk2><a:srgbClr val="44546A"/></a:dk2>
            <a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>
            <a:accent1><a:srgbClr val="4472C4"/></a:accent1>
            <a:accent2><a:srgbClr val="ED7D31"/></a:accent2>
            <a:accent3><a:srgbClr val="A5A5A5"/></a:accent3>
            <a:accent4><a:srgbClr val="FFC000"/></a:accent4>
            <a:accent5><a:srgbClr val="5B9BD5"/></a:accent5>
            <a:accent6><a:srgbClr val="70AD47"/></a:accent6>
            <a:hlink><a:srgbClr val="0563C1"/></a:hlink>
            <a:folHlink><a:srgbClr val="954F72"/></a:folHlink>
          </a:clrScheme>
          <a:fontScheme name="Office">
            <a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
            <a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
          </a:fontScheme>
          <a:formatScheme name="Office">
            <a:fillStyleLst>
              <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              <a:gradFill rotWithShape="1">
                <a:gsLst>
                  <a:gs pos="0"><a:schemeClr val="phClr"><a:tint val="50000"/><a:satMod val="300000"/></a:schemeClr></a:gs>
                  <a:gs pos="100000"><a:schemeClr val="phClr"><a:shade val="50000"/><a:satMod val="200000"/></a:schemeClr></a:gs>
                </a:gsLst>
                <a:lin ang="16200000" scaled="1"/>
              </a:gradFill>
              <a:gradFill rotWithShape="1">
                <a:gsLst>
                  <a:gs pos="0"><a:schemeClr val="phClr"><a:tint val="100000"/><a:shade val="100000"/><a:satMod val="130000"/></a:schemeClr></a:gs>
                  <a:gs pos="100000"><a:schemeClr val="phClr"><a:tint val="50000"/><a:shade val="100000"/><a:satMod val="350000"/></a:schemeClr></a:gs>
                </a:gsLst>
                <a:lin ang="16200000" scaled="0"/>
              </a:gradFill>
            </a:fillStyleLst>
            <a:lnStyleLst>
              <a:ln w="6350" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>
              <a:ln w="12700" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>
              <a:ln w="19050" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>
            </a:lnStyleLst>
            <a:effectStyleLst>
              <a:effectStyle><a:effectLst/></a:effectStyle>
              <a:effectStyle><a:effectLst/></a:effectStyle>
              <a:effectStyle><a:effectLst><a:outerShdw blurRad="57150" dist="19050" dir="5400000" algn="ctr" rotWithShape="0"><a:srgbClr val="000000"><a:alpha val="63000"/></a:srgbClr></a:outerShdw></a:effectLst></a:effectStyle>
            </a:effectStyleLst>
            <a:bgFillStyleLst>
              <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              <a:solidFill><a:schemeClr val="phClr"><a:tint val="95000"/><a:satMod val="170000"/></a:schemeClr></a:solidFill>
              <a:gradFill rotWithShape="1">
                <a:gsLst>
                  <a:gs pos="0"><a:schemeClr val="phClr"><a:tint val="93000"/><a:satMod val="150000"/><a:shade val="98000"/><a:lumMod val="102000"/></a:schemeClr></a:gs>
                  <a:gs pos="50000"><a:schemeClr val="phClr"><a:tint val="98000"/><a:satMod val="130000"/><a:shade val="90000"/><a:lumMod val="103000"/></a:schemeClr></a:gs>
                  <a:gs pos="100000"><a:schemeClr val="phClr"><a:shade val="63000"/><a:satMod val="120000"/></a:schemeClr></a:gs>
                </a:gsLst>
                <a:lin ang="16200000" scaled="0"/>
              </a:gradFill>
            </a:bgFillStyleLst>
          </a:formatScheme>
        </a:themeElements>
      </a:theme>
      """;
}
