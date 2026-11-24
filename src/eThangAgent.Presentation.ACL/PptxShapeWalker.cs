using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using P = DocumentFormat.OpenXml.Presentation;

namespace eThangAgent.Presentation.ACL;

/// <summary>One slide's shapes in document order with the address that reaches each one,
///     plus the map from a drawing shape id to that address: the timing tree targets
///     shapes by id, so an animation effect's target address is resolved through it.
///     </summary>
/// <param name="Shapes">Every shape in the slide's shape tree, in document order.</param>
/// <param name="AddressesByShapeId">The address of every shape that carries a drawing id.</param>
internal sealed record PptxShapeTree(IReadOnlyList<PptxShapeInfo> Shapes,
    IReadOnlyDictionary<uint, string> AddressesByShapeId);

/// <summary>Walks a slide's shape tree and reports every shape with its address and kind.
///     Only the five shape element types PowerPoint puts in a shape tree are counted, so a
///     group's properties child never consumes an index and an address always names the
///     shape a person sees in the selection pane.</summary>
internal static class PptxShapeWalker
{
  /// <summary>The chart families a chart's type is named from, in the order they are
  ///     looked for. A bar chart is handled before this list because its direction decides
  ///     between bar and column.</summary>
  private static readonly (string Name, Func<C.PlotArea, bool> Present)[] ChartFamilies =
  [
    ("line", area => area.GetFirstChild<C.LineChart>() is not null),
    ("pie", area => area.GetFirstChild<C.PieChart>() is not null),
    ("doughnut", area => area.GetFirstChild<C.DoughnutChart>() is not null),
    ("area", area => area.GetFirstChild<C.AreaChart>() is not null),
    ("scatter", area => area.GetFirstChild<C.ScatterChart>() is not null),
    ("radar", area => area.GetFirstChild<C.RadarChart>() is not null),
    ("bubble", area => area.GetFirstChild<C.BubbleChart>() is not null),
    ("surface", area => area.GetFirstChild<C.SurfaceChart>() is not null),
    ("stock", area => area.GetFirstChild<C.StockChart>() is not null),
  ];

  /// <summary>Walks the slide's shape tree. A slide with no shape tree has no shapes.
  ///     </summary>
  /// <param name="slidePart">The slide part to walk.</param>
  /// <param name="slide">The slide's 1-based number, used to build every address.</param>
  internal static PptxShapeTree Walk(SlidePart slidePart, int slide)
  {
    WalkContext context = new(slidePart, slide, []);
    P.ShapeTree? tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
    IReadOnlyList<PptxShapeInfo> shapes = tree is null ? [] : WalkLevel(context, tree, []);
    return new PptxShapeTree(shapes, context.AddressesByShapeId);
  }

  /// <summary>Resolves an address against a slide's shapes: a shape path walks the tree
  ///     level by level, and a table-cell address additionally requires the cell to sit
  ///     inside the table's dimensions. An address that names nothing is AddressNotFound.
  ///     </summary>
  /// <param name="shapes">The slide's shapes, in document order.</param>
  /// <param name="address">The parsed address to resolve.</param>
  internal static Result<PptxShapeInfo> Resolve(IReadOnlyList<PptxShapeInfo> shapes, PptxAddress address)
  {
    ArgumentNullException.ThrowIfNull(shapes);
    ArgumentNullException.ThrowIfNull(address);
    if (address.Kind is not (PptxAddressKind.Shape or PptxAddressKind.TableCell))
    {
      return Missing(address, "it does not name a shape");
    }

    PptxShapeInfo? shape = Descend(shapes, address.ShapePath);
    return shape is null ? Missing(address, "the slide has no such shape") : Cell(shape, address);
  }

  /// <summary>The slide's speaker notes as plain text: the notes body placeholder's
  ///     paragraphs joined by a newline. A slide with no notes part, or a notes part with
  ///     no text, reads as the empty string.</summary>
  /// <param name="slidePart">The slide part whose notes are read.</param>
  internal static string Notes(SlidePart slidePart)
  {
    P.ShapeTree? tree = slidePart.NotesSlidePart?.NotesSlide?.CommonSlideData?.ShapeTree;
    if (tree is null)
    {
      return string.Empty;
    }

    List<P.Shape> shapes = [.. tree.Elements<P.Shape>().Where(shape => shape.TextBody is not null)];
    P.Shape? body = shapes.FirstOrDefault(shape => PlaceholderType(shape) == P.PlaceholderValues.Body)
        ?? shapes.FirstOrDefault(shape => PlaceholderType(shape) is null);
    return body is null ? string.Empty : TextOf(body.TextBody);
  }

  /// <summary>One shape's full text: paragraphs joined by a newline, runs concatenated,
  ///     and an explicit line break inside a paragraph kept as a newline. An empty run
  ///     contributes nothing, so it never produces text of its own.</summary>
  /// <param name="textBody">The text body to read; null reads as the empty string.</param>
  internal static string TextOf(OpenXmlElement? textBody)
    => textBody is null
        ? string.Empty
        : string.Join("\n", textBody.Elements<A.Paragraph>().Select(ParagraphText));

  /// <summary>The slide's animation effects in playback order. An effect is a timing node
  ///     that carries a preset, and its target address comes from the shape id it names.
  ///     A timing element with no effect in it reads as no effects.</summary>
  /// <param name="slidePart">The slide part whose timing tree is read.</param>
  /// <param name="slide">The slide's 1-based number.</param>
  /// <param name="addressesByShapeId">The address of every shape that carries a drawing id.</param>
  internal static IReadOnlyList<PptxAnimationInfo> Animations(SlidePart slidePart, int slide,
      IReadOnlyDictionary<uint, string> addressesByShapeId)
  {
    P.TimeNodeList? list = slidePart.Slide?.Timing?.TimeNodeList;
    return list is null ? [] : [.. list.Descendants<P.CommonTimeNode>()
        .Where(node => node.PresetId is not null || node.PresetClass is not null)
        .Select((node, position) => new PptxAnimationInfo(position + 1, Target(node, slide, addressesByShapeId),
            EffectName(node), Trigger(node), node.Duration?.InnerText ?? string.Empty))];
  }

  private static List<PptxShapeInfo> WalkLevel(WalkContext context, OpenXmlElement container,
      IReadOnlyList<int> parentPath)
  {
    List<PptxShapeInfo> shapes = [];
    int index = 0;
    foreach (OpenXmlElement element in container.ChildElements)
    {
      if (!IsShape(element))
      {
        continue;
      }

      index++;
      int[] path = [.. parentPath, index];
      string address = PptxAddress.ForShape(context.Slide, path).Render();
      Register(context, element, address);
      shapes.Add(Describe(context, element, index, address, path));
    }

    return shapes;
  }

  private static bool IsShape(OpenXmlElement element)
    => element is P.Shape or P.Picture or P.GraphicFrame or P.GroupShape or P.ConnectionShape;

  private static void Register(WalkContext context, OpenXmlElement element, string address)
  {
    if (ShapeId(element) is { } id)
    {
      context.AddressesByShapeId[id] = address;
    }
  }

  /// <summary>A shape's drawing id: the id its own non-visual drawing properties carry,
  ///     which is the first cNvPr below the shape element for every shape type.</summary>
  private static uint? ShapeId(OpenXmlElement element)
    => element.Descendants<P.NonVisualDrawingProperties>().FirstOrDefault()?.Id?.Value;

  private static PptxShapeInfo Describe(WalkContext context, OpenXmlElement element, int index,
      string address, IReadOnlyList<int> path) => element switch
      {
        P.Picture picture => Picture(context.SlidePart, picture, index, address),
        P.GraphicFrame frame => Graphic(context.SlidePart, frame, index, address),
        P.GroupShape group => new PptxGroupShape(index, address, WalkLevel(context, group, path)),
        P.ConnectionShape connection => new PptxTextBoxShape(index, address,
            TextOf(connection.GetFirstChild<A.TextBody>()), false, false),
        P.Shape shape => TextShape(shape, index, address),
        _ => new PptxTextBoxShape(index, address, string.Empty, false, false),
      };

  private static PptxTextBoxShape TextShape(P.Shape shape, int index, string address)
  {
    P.PlaceholderValues? type = PlaceholderType(shape);
    return new PptxTextBoxShape(index, address, TextOf(shape.TextBody), IsTitle(type), type is not null);
  }

  /// <summary>The placeholder type a shape declares, or null when it is not a
  ///     placeholder.</summary>
  /// <param name="shape">The shape to read.</param>
  internal static P.PlaceholderValues? PlaceholderType(P.Shape shape)
    => shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;

  private static bool IsTitle(P.PlaceholderValues? type)
    => type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle;

  /// <summary>A picture reports the package name of the media part it displays. A picture
  ///     whose media relationship does not resolve is reported as a text shape with no
  ///     text: the vocabulary has no broken-picture kind, and dropping the shape would
  ///     shift the address of every shape after it.</summary>
  private static PptxShapeInfo Picture(SlidePart slidePart, P.Picture picture, int index, string address)
  {
    string? mediaName = MediaName(slidePart, picture.BlipFill?.Blip?.Embed?.Value);
    return mediaName is null
        ? new PptxTextBoxShape(index, address, string.Empty, false, false)
        : new PptxPictureShape(index, address, mediaName);
  }

  private static string? MediaName(SlidePart slidePart, string? relationshipId)
    => relationshipId is not null && RelatedPart(slidePart, relationshipId) is ImagePart image
        ? image.Uri.OriginalString
        : null;

  /// <summary>The part a relationship id reaches from the given container, or null when
  ///     the id names nothing.</summary>
  private static OpenXmlPart? RelatedPart(OpenXmlPartContainer container, string relationshipId)
    => container.Parts.FirstOrDefault(pair => pair.RelationshipId == relationshipId).OpenXmlPart;

  private static PptxShapeInfo Graphic(SlidePart slidePart, P.GraphicFrame frame, int index, string address)
    => frame.Graphic?.GraphicData switch
    {
      { } data when data.GetFirstChild<A.Table>() is { } table => TableShape(table, index, address),
      { } data when data.GetFirstChild<C.ChartReference>() is { } reference =>
          ChartShape(slidePart, reference, index, address),
      _ => new PptxTextBoxShape(index, address, string.Empty, false, false),
    };

  private static PptxTableShape TableShape(A.Table table, int index, string address)
  {
    List<A.TableRow> rows = [.. table.Elements<A.TableRow>()];
    int cols = Math.Max(1, table.TableGrid?.Elements<A.GridColumn>().Count() ?? 0);
    List<PptxCellText> cells = [];
    for (int row = 0; row < rows.Count; row++)
    {
      cells.AddRange(RowCells(rows[row], row + 1, cols));
    }

    if (rows.Count == 0)
    {
      cells.AddRange(RowCells(null, 1, cols));
    }

    return new PptxTableShape(index, address, Math.Max(1, rows.Count), cols, cells);
  }

  /// <summary>One table row's cells, placed on the grid by each cell's column span so a
  ///     merged table still reports exactly one entry per grid slot. A cell that continues
  ///     a merge carries no text of its own.</summary>
  private static List<PptxCellText> RowCells(A.TableRow? row, int rowNumber, int cols)
  {
    List<PptxCellText> cells = [];
    int col = 1;
    foreach (A.TableCell cell in row is null ? [] : row.Elements<A.TableCell>())
    {
      for (int taken = 0; taken < Span(cell) && col <= cols; taken++, col++)
      {
        cells.Add(new PptxCellText(rowNumber, col, CellText(cell)));
      }
    }

    while (col <= cols)
    {
      cells.Add(new PptxCellText(rowNumber, col, string.Empty));
      col++;
    }

    return cells;
  }

  private static int Span(A.TableCell cell) => Math.Max(1, cell.GridSpan?.Value ?? 1);

  private static string CellText(A.TableCell cell)
    => cell.HorizontalMerge?.Value == true || cell.VerticalMerge?.Value == true
        ? string.Empty
        : TextOf(cell.TextBody);

  private static PptxChartShape ChartShape(SlidePart slidePart, C.ChartReference reference, int index,
      string address)
  {
    C.PlotArea? plotArea = ChartPlotArea(slidePart, reference.Id?.Value);
    return plotArea is null
        ? new PptxChartShape(index, address, "unknown", [], [])
        : new PptxChartShape(index, address, ChartType(plotArea), SeriesNames(plotArea), CategoryNames(plotArea));
  }

  private static C.PlotArea? ChartPlotArea(SlidePart slidePart, string? relationshipId)
    => relationshipId is not null && RelatedPart(slidePart, relationshipId) is ChartPart chart
        ? chart.ChartSpace?.GetFirstChild<C.Chart>()?.PlotArea
        : null;

  /// <summary>The chart's type, named as PowerPoint's own chart gallery names the family.
  ///     A bar chart's direction decides between bar and column.</summary>
  private static string ChartType(C.PlotArea plotArea)
    => plotArea.GetFirstChild<C.BarChart>() is { } bar
      ? BarType(bar)
      : ChartFamilies.FirstOrDefault(family => family.Present(plotArea)).Name ?? "unknown";

  private static string BarType(C.BarChart bar)
    => bar.BarDirection?.Val?.Value == C.BarDirectionValues.Bar ? "bar" : "column";

  private static List<string> SeriesNames(C.PlotArea plotArea)
    => [.. plotArea.Descendants().Where(element => element.LocalName == "ser")
        .Select((series, position) => CachedText(Child(series, "tx")) ?? $"Series {position + 1}")];

  private static List<string> CategoryNames(C.PlotArea plotArea)
  {
    OpenXmlElement? series = plotArea.Descendants().FirstOrDefault(element => element.LocalName == "ser");
    OpenXmlElement? cache = Cache(series is null ? null : Child(series, "cat"));
    return cache is null
        ? []
        : [.. cache.ChildElements.Where(element => element.LocalName == "pt").Select(PointText)];
  }

  private static string? CachedText(OpenXmlElement? container)
  {
    OpenXmlElement? point = Cache(container)?.ChildElements.FirstOrDefault(element => element.LocalName == "pt");
    return point is null ? null : PointText(point);
  }

  private static OpenXmlElement? Cache(OpenXmlElement? container)
    => container?.Descendants().FirstOrDefault(element => element.LocalName is "strCache" or "numCache");

  private static string PointText(OpenXmlElement point)
    => point.ChildElements.FirstOrDefault(element => element.LocalName == "v")?.InnerText ?? string.Empty;

  private static OpenXmlElement? Child(OpenXmlElement element, string localName)
    => element.ChildElements.FirstOrDefault(child => child.LocalName == localName);

  private static string Target(P.CommonTimeNode node, int slide, IReadOnlyDictionary<uint, string> byShapeId)
  {
    string? target = node.Descendants<P.ShapeTarget>().FirstOrDefault()?.ShapeId?.Value;
    return uint.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id)
        && byShapeId.TryGetValue(id, out string? address)
            ? address
            : PptxAddress.ForSlide(slide).Render();
  }

  /// <summary>The effect's name as the file states it: the preset's class and id joined by
  ///     a colon. The package carries no readable effect name and the preset-id table is
  ///     not part of it, so the preset's own spelling is what is reported.</summary>
  private static string EffectName(P.CommonTimeNode node)
    => (node.PresetClass?.InnerText ?? "unknown") + ":" + (node.PresetId?.InnerText ?? "0");

  /// <summary>The effect's trigger, read from the nearest enclosing timing node that states
  ///     a start condition: a stated event is that event, an indefinite delay is a click,
  ///     a positive delay follows the previous effect, and no condition at all runs with
  ///     it.</summary>
  private static string Trigger(P.CommonTimeNode node)
  {
    P.Condition? click = node.Ancestors<P.CommonTimeNode>()
        .Select(parent => parent.StartConditionList?.Elements<P.Condition>().FirstOrDefault())
        .FirstOrDefault(condition => condition is not null && StartsOnAClick(condition));
    return TriggerOf(click ?? node.StartConditionList?.Elements<P.Condition>().FirstOrDefault());
  }

  /// <summary>Whether a start condition waits for the presenter: an indefinite delay or a
  ///     stated event. An effect's own node carries delay 0 and inherits the click from the
  ///     node above it, so the click is found on the enclosing node, not on the effect.
  ///     </summary>
  private static bool StartsOnAClick(P.Condition condition)
    => condition.Event?.InnerText is { Length: > 0 } || condition.Delay?.Value == "indefinite";

  private static string TriggerOf(P.Condition? condition)
    => condition is null
        ? "withPrevious"
        : EventOrDelay(condition);

  private static string EventOrDelay(P.Condition condition)
    => condition.Event?.InnerText is { Length: > 0 } stated ? stated : DelayTrigger(condition.Delay?.Value);

  private static string DelayTrigger(string? delay) => delay switch
  {
    null => "withPrevious",
    "indefinite" => "onClick",
    "0" => "withPrevious",
    _ => "afterPrevious",
  };

  /// <summary>The shape element a shape path reaches: the document-order numbering
  ///     <see cref="Walk"/> assigns, walked over the part's own element tree so an edit
  ///     mutates the element a read described. A path that runs past a shape with no shape
  ///     children reaches nothing.</summary>
  /// <param name="slidePart">The slide part whose shape tree is walked.</param>
  /// <param name="shapePath">The 1-based shape path, one entry per level of group nesting.</param>
  internal static OpenXmlElement? ElementAt(SlidePart slidePart, IReadOnlyList<int> shapePath)
  {
    ArgumentNullException.ThrowIfNull(shapePath);
    OpenXmlElement? container = slidePart.Slide?.CommonSlideData?.ShapeTree;
    OpenXmlElement? found = null;
    foreach (int index in shapePath)
    {
      found = container is null ? null : ShapesIn(container).ElementAtOrDefault(index - 1);
      if (found is null)
      {
        return null;
      }

      container = found;
    }

    return found;
  }

  /// <summary>A slide's top-level shapes in document order: the elements <see cref="Walk"/>
  ///     numbers from 1, so the Nth entry is the shape an address's first path entry names.
  ///     </summary>
  /// <param name="slidePart">The slide part whose shape tree is read.</param>
  internal static List<OpenXmlElement> TopLevelShapes(SlidePart slidePart)
  {
    ArgumentNullException.ThrowIfNull(slidePart);
    OpenXmlElement? tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
    return tree is null ? [] : [.. ShapesIn(tree)];
  }

  /// <summary>The shape children of a container, in the order <see cref="Walk"/> numbers
  ///     them.</summary>
  private static IEnumerable<OpenXmlElement> ShapesIn(OpenXmlElement container)
    => container.ChildElements.Where(IsShape);

  /// <summary>Walks a shape path level by level. A level that carries no shape at the
  ///     requested index ends the walk, so a path that runs past a non-group shape names
  ///     nothing rather than resolving to the shape it stopped on.</summary>
  private static PptxShapeInfo? Descend(IReadOnlyList<PptxShapeInfo> shapes, IReadOnlyList<int> path)
  {
    IReadOnlyList<PptxShapeInfo> level = shapes;
    PptxShapeInfo? found = null;
    foreach (int index in path)
    {
      found = level.FirstOrDefault(shape => shape.Index == index);
      if (found is null)
      {
        return null;
      }

      level = found is PptxGroupShape group ? group.Children : [];
    }

    return found;
  }

  private static Result<PptxShapeInfo> Cell(PptxShapeInfo shape, PptxAddress address)
    => address.Kind != PptxAddressKind.TableCell
        || (shape is PptxTableShape table && address.Row <= table.Rows && address.Col <= table.Cols)
            ? Result.Success(shape)
            : Missing(address, "the table has no such cell");

  private static Result<PptxShapeInfo> Missing(PptxAddress address, string reason)
    => Result.Failure<PptxShapeInfo>(new DomainError(PptxErrorCodes.AddressNotFound,
        $"Address '{address.Render()}' resolves to nothing: {reason}."));

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

  /// <summary>The slide part, its number, and the shape-id map the walk fills in as it
  ///     goes.</summary>
  private sealed record WalkContext(SlidePart SlidePart, int Slide, Dictionary<uint, string> AddressesByShapeId);
}
