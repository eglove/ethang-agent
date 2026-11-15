using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using SkiaSharp;

namespace eThangAgent.ImageComposition.ACL;

/// <summary><see cref="IImageCompositionAccess"/> over SkiaSharp: one surface at the scene's
///     stated size, the background painted first, then every shape in list order, encoded as
///     PNG.
///     <para>SKIA IS ALREADY ON THIS MACHINE'S GRAPHICS PATH - Avalonia renders the desktop UI
///     with it - so this ACL adds no second rasterizer to the process, only the vocabulary that
///     describes what to draw.</para>
///     <para>THE SCENE IS TRUSTED BY THE TIME IT ARRIVES: dimensions, geometry, and paint were all
///     judged at the boundary, so nothing here re-checks a caller's arithmetic. What CAN still
///     fail is the machine's own state - no typeface to draw text with, an allocation the process
///     cannot make, an encoding fault - and each of those is a named typed failure so the caller
///     learns which one it hit.</para></summary>
public sealed class SkiaImageCompositionAccess : IImageCompositionAccess
{
  /// <summary>The family used when a text run names none, and the stand-in for a named family this
  ///     machine cannot resolve. A known-present UI face on Windows.</summary>
  private const string DefaultFontFamily = "Segoe UI";

  /// <inheritdoc />
  public Task<Result<ComposedImage>> RenderAsync(SceneImage scene, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(scene);
    ct.ThrowIfCancellationRequested();
    return Task.FromResult(Render(scene));
  }

  private static Result<ComposedImage> Render(SceneImage scene)
  {
    Result<SKTypeface?> resolved = TypefaceFor(scene);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<ComposedImage>(resolved.Error);
    }

    using SKTypeface? typeface = resolved.Value;
    SKImageInfo info = new(scene.Width, scene.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
    using SKSurface surface = SKSurface.Create(info);
    SKCanvas canvas = surface.Canvas;
    canvas.Clear(scene.Background is { } background ? ToSk(background) : SKColors.Transparent);

    foreach (SceneShape shape in scene.Shapes)
    {
      Draw(canvas, shape, typeface);
    }

    using SKImage image = surface.Snapshot();
    using SKData? data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data is null
      ? Result.Failure<ComposedImage>(new DomainError("ImageCompositionFailed",
          "the rendered surface could not be encoded as PNG."))
      : Result.Success(new ComposedImage(data.ToArray(), scene.Width, scene.Height));
  }

  /// <summary>The typeface any text run in the scene needs, resolved ONCE per render: the first
  ///     family a run names explicitly wins, otherwise the default. A named family this machine
  ///     does not have falls back to the default rather than failing the render, because a guessed
  ///     font name is a plausible caller mistake and text drawn in the wrong face still reads.
  ///     An empty result - no typeface at all - is a typed failure: text that cannot be drawn is
  ///     not text silently omitted.</summary>
  private static Result<SKTypeface?> TypefaceFor(SceneImage scene)
  {
    string? named = scene.Shapes
        .OfType<SceneShape.TextRun>()
        .Select(run => run.FontFamily)
        .FirstOrDefault(family => !string.IsNullOrWhiteSpace(family));

    return named is { } family && SKTypeface.FromFamilyName(family) is { } resolved
        ? Result.Success<SKTypeface?>(resolved)
        : FallbackTypeface();
  }

  /// <summary>The default face, or a typed failure when this machine exposes none at all - the one
  ///     case a renderer cannot work around, because text drawn without a face is not text.</summary>
  private static Result<SKTypeface?> FallbackTypeface() =>
      SKTypeface.FromFamilyName(DefaultFontFamily) is { } fallback
          ? Result.Success<SKTypeface?>(fallback)
          : Result.Failure<SKTypeface?>(new DomainError("ImageCompositionFailed",
              "this machine exposes no typeface, so text runs cannot be drawn."));

  private static void Draw(SKCanvas canvas, SceneShape shape, SKTypeface? typeface) =>
      Describe(shape, style => Paint(canvas, shape, style, typeface));

  /// <summary>The paint a shape draws with, derived once from the shape's own colours so the draw
  ///     sequence cannot disagree with itself about which style applies.</summary>
  /// <param name="Fill">The fill colour, or null for an outline only.</param>
  /// <param name="Stroke">The stroke, or null for a fill only.</param>
  private sealed record ShapeStyle(SceneColor? Fill, SceneStroke? Stroke);

  /// <summary>Runs <paramref name="draw"/> with the style the shape paints in.</summary>
  private static void Describe(SceneShape shape, Action<ShapeStyle> draw)
  {
    (SceneColor? fill, SceneStroke? stroke) = shape switch
    {
      SceneShape.Rectangle rectangle => (rectangle.Fill, rectangle.Stroke),
      SceneShape.RoundedRectangle rounded => (rounded.Fill, rounded.Stroke),
      SceneShape.Ellipse ellipse => (ellipse.Fill, ellipse.Stroke),
      SceneShape.Polyline polyline => (polyline.Fill, polyline.Stroke),
      SceneShape.Line line => (null, line.Stroke),
      SceneShape.TextRun text => (text.Color, null),
      _ => (null, null),
    };

    draw(new ShapeStyle(fill, stroke));
  }

  private static void Paint(SKCanvas canvas, SceneShape shape, ShapeStyle style, SKTypeface? typeface)
  {
    using SKPaint fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    using SKPaint line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };

    if (style.Fill is { } fillColor)
    {
      fill.Color = ToSk(fillColor);
    }

    if (style.Stroke is { } stroke)
    {
      line.Color = ToSk(stroke.Color);
      line.StrokeWidth = stroke.Width;
    }

    bool fills = style.Fill is not null;
    bool strokes = style.Stroke is not null;
    switch (shape)
    {
      case SceneShape.Rectangle rectangle:
        Emit(fill, line, fills, strokes, paint => canvas.DrawRect(ToSk(rectangle.Rect), paint));
        return;
      case SceneShape.RoundedRectangle rounded:
        Emit(fill, line, fills, strokes, paint =>
            canvas.DrawRoundRect(ToSk(rounded.Rect), rounded.CornerRadius, rounded.CornerRadius, paint));
        return;
      case SceneShape.Ellipse ellipse:
        Emit(fill, line, fills, strokes, paint => canvas.DrawOval(ToSk(ellipse.Rect), paint));
        return;
      case SceneShape.Line shapeLine:
        canvas.DrawLine(ToSk(shapeLine.From), ToSk(shapeLine.To), line);
        return;
      case SceneShape.Polyline polyline:
        EmitPath(canvas, polyline, fill, line, fills, strokes);
        return;
      case SceneShape.TextRun text:
        DrawText(canvas, text, fill, typeface);
        return;
      default:
        return;
    }
  }

  /// <summary>Draws the fill pass then the stroke pass, skipping whichever one the shape did not
  ///     ask for. The stroke goes second so an outline reads crisply over its own fill.</summary>
  private static void Emit(SKPaint fill, SKPaint line, bool fills, bool strokes,
      Action<SKPaint> draw)
  {
    if (fills)
    {
      draw(fill);
    }

    if (strokes)
    {
      draw(line);
    }
  }

  private static void EmitPath(SKCanvas canvas, SceneShape.Polyline polyline, SKPaint fill, SKPaint line,
      bool fills, bool strokes)
  {
    using SKPathBuilder builder = new();
    builder.MoveTo(ToSk(polyline.Points[0]));
    for (int i = 1; i < polyline.Points.Count; i++)
    {
      builder.LineTo(ToSk(polyline.Points[i]));
    }

    if (polyline.Closed)
    {
      builder.Close();
    }

    using SKPath path = builder.Detach();
    Emit(fill, line, fills, strokes, paint => canvas.DrawPath(path, paint));
  }

  /// <summary>Draws one text run placed by its TOP-LEFT corner, which is the point the caller
  ///     states. Skia measures a baseline, so the font's own ascent lifts the run onto its box
  ///     top - without that subtraction every run would sit one ascent too low.</summary>
  private static void DrawText(SKCanvas canvas, SceneShape.TextRun text, SKPaint paint,
      SKTypeface? typeface)
  {
    using SKFont font = new(typeface ?? SKTypeface.Default, text.FontSize);
    canvas.DrawText(text.Value, text.X, text.Y - font.Metrics.Ascent, SKTextAlign.Left, font, paint);
  }

  /// <summary>The Skia spelling of a scene colour: the same 8-bit channels, no colour-space
  ///     conversion, because the scene's spelling is already sRGB bytes.</summary>
  private static SKColor ToSk(SceneColor color) =>
      new(color.Red, color.Green, color.Blue, color.Alpha);

  private static SKPoint ToSk(ScenePoint point) => new(point.X, point.Y);

  private static SKRect ToSk(SceneRect rect) =>
      SKRect.Create(rect.X, rect.Y, rect.Width, rect.Height);
}
