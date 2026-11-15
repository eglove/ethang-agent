using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One drawable thing in a scene. A closed family: every shape the renderer can draw
///     is a case here, and a shape that is not a case cannot be asked for.
///     <para>EVERY PRIMITIVE DRAWS A FILL AND/OR A STROKE, and nothing else. That is the whole
///     paint model on purpose: the vocabulary that drew the real featured image this tool
///     replaces - a paper sheet, a pen, a chip - is rectangles, rounded rectangles, ellipses,
///     lines, paths, polygons, and text. Adding gradients, images, or blend modes later is a
///     decision with a case of its own, not a hole to leave open now.</para></summary>
// CA1034: the nested shape cases ARE the vocabulary (the McpOutcome/ManagementOutcome
// decision, restated so every closed family in this domain reads the same way).
#pragma warning disable CA1034
public abstract record SceneShape
{
  /// <summary>A filled and/or stroked rectangle.</summary>
  /// <param name="Rect">Position and extent.</param>
  /// <param name="Fill">Fill colour, or null for an outline only.</param>
  /// <param name="Stroke">Stroke, or null for a fill only.</param>
  public sealed record Rectangle(SceneRect Rect, SceneColor? Fill, SceneStroke? Stroke) : SceneShape;

  /// <summary>A filled and/or stroked rounded rectangle.</summary>
  /// <param name="Rect">Position and extent.</param>
  /// <param name="CornerRadius">Corner radius in pixels; must be >= 0 and no more than half the
  ///     shorter side, so the shape stays a rectangle rather than collapsing into a capsule the
  ///     caller did not describe.</param>
  /// <param name="Fill">Fill colour, or null for an outline only.</param>
  /// <param name="Stroke">Stroke, or null for a fill only.</param>
  public sealed record RoundedRectangle(SceneRect Rect, float CornerRadius, SceneColor? Fill,
      SceneStroke? Stroke) : SceneShape;

  /// <summary>A filled and/or stroked ellipse inscribed in <paramref name="Rect"/>.</summary>
  /// <param name="Rect">The bounding box the ellipse is inscribed in.</param>
  /// <param name="Fill">Fill colour, or null for an outline only.</param>
  /// <param name="Stroke">Stroke, or null for a fill only.</param>
  public sealed record Ellipse(SceneRect Rect, SceneColor? Fill, SceneStroke? Stroke) : SceneShape;

  /// <summary>A straight line from one point to another.</summary>
  /// <param name="From">Start point.</param>
  /// <param name="To">End point.</param>
  /// <param name="Stroke">The stroke; a line with no stroke draws nothing, so it is required.</param>
  public sealed record Line(ScenePoint From, ScenePoint To, SceneStroke Stroke) : SceneShape;

  /// <summary>A polyline: the points joined in order. With <paramref name="Closed"/> the last point
  ///     joins the first (a polygon); with <paramref name="Fill"/> the enclosed area is filled. At
  ///     least two points are required, so a path always describes something to draw.</summary>
  /// <param name="Points">At least two points, in draw order.</param>
  /// <param name="Closed">True to close the outline back to the first point.</param>
  /// <param name="Fill">Fill colour, or null. Meaningful only with Closed = true.</param>
  /// <param name="Stroke">Stroke, or null for a fill only.</param>
  public sealed record Polyline(IReadOnlyList<ScenePoint> Points, bool Closed, SceneColor? Fill,
      SceneStroke? Stroke) : SceneShape;

  /// <summary>One run of text on a single line: the renderer places it by its top-left corner and
  ///     wraps nothing. Font size and family are explicit because the drawn result is the
  ///     caller's composition, not the renderer's guess.</summary>
  /// <param name="Value">The text; must be non-empty.</param>
  /// <param name="X">Left edge of the text box.</param>
  /// <param name="Y">Top edge of the text box.</param>
  /// <param name="FontSize">Type size in pixels; must be > 0.</param>
  /// <param name="FontFamily">A font family name, or null for the renderer's default sans-serif.</param>
  /// <param name="Color">Text colour.</param>
  public sealed record TextRun(string Value, float X, float Y, float FontSize, string? FontFamily,
      SceneColor Color) : SceneShape;
}
#pragma warning restore CA1034

/// <summary>One scene to draw: a canvas of a stated size, a background, and the shapes in draw
///     order - later shapes paint over earlier ones, so the list order IS the z-order.
///     <para>THE DIMENSIONS ARE MANDATORY AND BOUNDED. A caller composing a featured image knows
///     the size it needs; an absent or absurd size is not a default to guess at, and the bound
///     keeps one call from allocating a canvas the machine cannot hold.</para></summary>
/// <param name="Width">Canvas width in pixels; 1 to <see cref="SceneImageLimits.MaxDimension"/>.</param>
/// <param name="Height">Canvas height in pixels; 1 to <see cref="SceneImageLimits.MaxDimension"/>.</param>
/// <param name="Background">Background colour; null leaves the canvas transparent.</param>
/// <param name="Shapes">Shapes in draw order. May be empty - a solid background is a valid image.</param>
public sealed record SceneImage(int Width, int Height, SceneColor? Background,
    IReadOnlyList<SceneShape> Shapes)
{
  /// <summary>Validates the whole scene and returns the first refusal: dimensions, then each
  ///     shape's own geometry. Validation lives on the scene because a shape cannot know the
  ///     canvas it will be drawn on, and a stroke width or a corner radius that only looks wrong
  ///     against a particular canvas is still wrong.</summary>
  /// <param name="width">Canvas width.</param>
  /// <param name="height">Canvas height.</param>
  /// <param name="background">Background colour, or null for a transparent canvas.</param>
  /// <param name="shapes">Shapes in draw order.</param>
  public static Result<SceneImage> Create(int width, int height, SceneColor? background,
      IReadOnlyList<SceneShape> shapes)
  {
    ArgumentNullException.ThrowIfNull(shapes);
    DomainError? refusal = SceneImageValidation.Refusal(width, height, shapes);
    return refusal is not null
      ? Result.Failure<SceneImage>(refusal)
      : Result.Success(new SceneImage(width, height, background, [.. shapes]));
  }
}

/// <summary>The scene bounds: a dimension ceiling and a shape ceiling.
///     <para>THE CEILING IS 4096 PIXELS PER SIDE. It is not a taste limit: it is the largest
///     square whose RGBA buffer (64 MB) this renderer is willing to allocate per call, so a
///     request that exceeds it is refused at the boundary instead of failing inside the
///     allocator.</para></summary>
public static class SceneImageLimits
{
  /// <summary>The largest width or height one scene may state.</summary>
  public const int MaxDimension = 4096;

  /// <summary>The largest number of shapes one scene may carry, so a pathological input cannot ask
  ///     for work the caller cannot have meant.</summary>
  public const int MaxShapes = 4096;
}

/// <summary>The one place a scene's geometry is judged, shared by every constructor that needs
///     it so the renderer never has to defend itself against a shape the boundary admitted.
///     <para>EVERY REFUSAL NAMES THE SHAPE'S INDEX. A caller composing a hundred-shape diagram
///     needs to know which one, and an index is the only handle a JSON array gives it.</para></summary>
public static class SceneImageValidation
{
  /// <summary>The typed refusal for a scene that cannot be drawn, or null when it can.</summary>
  /// <param name="width">Canvas width.</param>
  /// <param name="height">Canvas height.</param>
  /// <param name="shapes">The shapes in draw order.</param>
  public static DomainError? Refusal(int width, int height, IReadOnlyList<SceneShape> shapes)
  {
    ArgumentNullException.ThrowIfNull(shapes);
    return First(FrameRefusal(width, height, shapes.Count), ShapeRefusals(shapes));
  }

  /// <summary>The canvas-level rules: dimensions in range, then a shape count the renderer can
  ///     take.</summary>
  private static DomainError? FrameRefusal(int width, int height, int shapeCount) =>
      First(WidthRefusal(width), HeightRefusal(height), ShapeCountRefusal(shapeCount));

  private static DomainError? WidthRefusal(int width) =>
      width is < 1 or > SceneImageLimits.MaxDimension
          ? Refuse($"'width' must be between 1 and {SceneImageLimits.MaxDimension} pixels (got {width}).")
          : null;

  private static DomainError? HeightRefusal(int height) =>
      height is < 1 or > SceneImageLimits.MaxDimension
          ? Refuse($"'height' must be between 1 and {SceneImageLimits.MaxDimension} pixels (got {height}).")
          : null;

  private static DomainError? ShapeCountRefusal(int shapeCount) =>
      shapeCount > SceneImageLimits.MaxShapes
          ? Refuse($"a scene may carry at most {SceneImageLimits.MaxShapes} shapes (got {shapeCount}).")
          : null;

  /// <summary>Each shape's refusal in order, the first one winning. The index is prefixed so a
  ///     caller composing many shapes learns WHICH one, not merely that one was wrong.</summary>
  private static DomainError? ShapeRefusals(IReadOnlyList<SceneShape> shapes)
  {
    for (int i = 0; i < shapes.Count; i++)
    {
      DomainError? refusal = ShapeRefusal(shapes[i]);
      if (refusal is not null)
      {
        return Refuse($"shape {i}: {refusal.Message}");
      }
    }

    return null;
  }

  private static DomainError? ShapeRefusal(SceneShape shape) => shape switch
  {
    SceneShape.Rectangle rectangle => RectRefusal(rectangle.Rect, rectangle.Fill, rectangle.Stroke,
        "rectangle"),
    SceneShape.RoundedRectangle rounded => RoundedRefusal(rounded),
    SceneShape.Ellipse ellipse => RectRefusal(ellipse.Rect, ellipse.Fill, ellipse.Stroke, "ellipse"),
    SceneShape.Line line => First(StrokeRefusal(line.Stroke), Finite(line.From, "from"),
        Finite(line.To, "to")),
    SceneShape.Polyline polyline => PolylineRefusal(polyline),
    SceneShape.TextRun text => TextRefusal(text),
    _ => Refuse("an unknown shape reached validation."),
  };

  /// <summary>A box-shaped primitive: geometry first, then the paint rule. One helper because a
  ///     rectangle and an ellipse judge their box identically.</summary>
  private static DomainError? RectRefusal(SceneRect rect, SceneColor? fill, SceneStroke? stroke,
      string shape) => First(Geometry(rect), Paint(fill, stroke, shape));

  /// <summary>A rounded rectangle adds one rule to the box: the radius must fit inside it.</summary>
  private static DomainError? RoundedRefusal(SceneShape.RoundedRectangle rounded) =>
      First(Geometry(rounded.Rect), Radius(rounded), Paint(rounded.Fill, rounded.Stroke,
          "rounded rectangle"));

  private static DomainError? PolylineRefusal(SceneShape.Polyline polyline) =>
      PolylineCountRefusal(polyline)
      ?? PolylinePointsRefusal(polyline.Points)
      ?? Paint(polyline.Fill, polyline.Stroke, "polyline")
      ?? OpenFillRefusal(polyline);

  private static DomainError? PolylineCountRefusal(SceneShape.Polyline polyline) =>
      polyline.Points.Count < 2
          ? Refuse($"a polyline needs at least 2 points (got {polyline.Points.Count}).")
          : null;

  private static DomainError? PolylinePointsRefusal(IReadOnlyList<ScenePoint> points)
  {
    for (int i = 0; i < points.Count; i++)
    {
      DomainError? refusal = Finite(points[i], $"points[{i}]")
          ?? PointRefusal(points[i], $"points[{i}]");
      if (refusal is not null)
      {
        return refusal;
      }
    }

    return null;
  }

  private static DomainError? PointRefusal(ScenePoint point, string name) =>
      !float.IsFinite(point.X) || !float.IsFinite(point.Y)
          ? Refuse($"{name} must be finite (got {point.X}, {point.Y}).")
          : null;

  private static DomainError? OpenFillRefusal(SceneShape.Polyline polyline) =>
      polyline.Fill is not null && !polyline.Closed
          ? Refuse("a fill needs a closed polyline; set closed = true, or drop the fill.")
          : null;

  private static DomainError? TextRefusal(SceneShape.TextRun text) =>
      First(TextValueRefusal(text), FontSizeRefusal(text),
          Finite(new ScenePoint(text.X, text.Y), "position"));

  private static DomainError? TextValueRefusal(SceneShape.TextRun text) =>
      string.IsNullOrEmpty(text.Value) ? Refuse("a text run needs a non-empty value.") : null;

  private static DomainError? FontSizeRefusal(SceneShape.TextRun text) =>
      text.FontSize <= 0 || !float.IsFinite(text.FontSize)
          ? Refuse($"font size must be greater than 0 (got {text.FontSize}).")
          : null;

  private static DomainError? Geometry(SceneRect rect) =>
      First(Finite(new ScenePoint(rect.X, rect.Y), "position"),
          ExtentFiniteRefusal(rect), ExtentSignRefusal(rect));

  private static DomainError? ExtentFiniteRefusal(SceneRect rect) =>
      !float.IsFinite(rect.Width) || !float.IsFinite(rect.Height)
          ? Refuse($"extent must be finite (got {rect.Width} x {rect.Height}).")
          : null;

  private static DomainError? ExtentSignRefusal(SceneRect rect) =>
      rect.Width < 0 || rect.Height < 0
          ? Refuse($"extent must not be negative (got {rect.Width} x {rect.Height}).")
          : null;

  private static DomainError? Radius(SceneShape.RoundedRectangle rounded) =>
      First(RadiusValueRefusal(rounded), RadiusFitRefusal(rounded));

  private static DomainError? RadiusValueRefusal(SceneShape.RoundedRectangle rounded) =>
      rounded.CornerRadius < 0 || !float.IsFinite(rounded.CornerRadius)
          ? Refuse($"corner radius must be a finite number >= 0 (got {rounded.CornerRadius}).")
          : null;

  private static DomainError? RadiusFitRefusal(SceneShape.RoundedRectangle rounded)
  {
    float max = MathF.Min(rounded.Rect.Width, rounded.Rect.Height) / 2f;
    return rounded.CornerRadius > max
        ? Refuse($"corner radius {rounded.CornerRadius} exceeds half the shorter side ({max}).")
        : null;
  }

  /// <summary>A shape that neither fills nor strokes paints nothing at all, which is almost never
  ///     what a caller meant - so it is refused rather than drawn as a silent no-op.</summary>
  private static DomainError? Paint(SceneColor? fill, SceneStroke? stroke, string shape) =>
      First(NoPaintRefusal(fill, stroke, shape),
          stroke is null ? null : StrokeRefusal(stroke));

  private static DomainError? NoPaintRefusal(SceneColor? fill, SceneStroke? stroke, string shape) =>
      fill is null && stroke is null
          ? Refuse($"a {shape} needs a fill, a stroke, or both; neither draws nothing.")
          : null;

  private static DomainError? StrokeRefusal(SceneStroke stroke) =>
      stroke.Width <= 0 || !float.IsFinite(stroke.Width)
          ? Refuse($"stroke width must be greater than 0 (got {stroke.Width}).")
          : null;

  private static DomainError? Finite(ScenePoint point, string name) =>
      !float.IsFinite(point.X) || !float.IsFinite(point.Y)
          ? Refuse($"{name} must be finite (got {point.X}, {point.Y}).")
          : null;

  /// <summary>The first non-null refusal among the rules, in the order given: one shape, one
  ///     reported reason, and the order of the checks IS the precedence a caller sees.</summary>
  private static DomainError? First(params DomainError?[] checks) =>
      checks.FirstOrDefault(check => check is not null);

  private static DomainError Refuse(string message) =>
      new(ToolErrorCodes.InvalidParameterValue, "The scene could not be drawn: " + message);
}
