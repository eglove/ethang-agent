using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strict input for the compose_image tool: a canvas size, an optional background, and the
///     shapes to draw, plus the shared mandatory execution budget.
///     <para>THE SCENE IS PARSED HERE AND VALIDATED BY THE DOMAIN, not by the tool: the shape
///     vocabulary, the paint rules, and the geometry checks belong to <see cref="SceneImage"/>, so
///     a second caller of the renderer inherits exactly the same contract.</para>
///     <para>EVERY REFUSAL NAMES THE PARAMETER AND THE SHAPE, because a composing caller is
///     writing a document: 'shape 7: a rectangle needs a fill, a stroke, or both' is actionable,
///     and 'invalid scene' is not.</para></summary>
public sealed record ComposeImageToolInput(SceneImage Scene)
{
  private const string WidthName = "width";
  private const string HeightName = "height";
  private const string BackgroundName = "background";
  private const string ShapesName = "shapes";

  private static readonly string[] AllowedNames =
      [WidthName, HeightName, BackgroundName, ShapesName, ToolTimeout.ParameterName];

  /// <summary>Parses the raw arguments, or reports the first refusal.</summary>
  public static Result<ComposeImageToolInput> Create(string jsonArguments)
  {
    return ToolArguments.ParseObject(jsonArguments)
        .Bind(json => ToolArguments.RejectUnknownParameters(json, AllowedNames) is { } unknown
            ? Result.Failure<JsonElement>(unknown)
            : Result.Success(json))
        .Bind(ParseScene)
        .Map(scene => new ComposeImageToolInput(scene));
  }

  private static Result<SceneImage> ParseScene(JsonElement json)
  {
    Result<int> width = ToolArguments.RequireInt(json, WidthName, Requirement);
    if (!width.IsSuccess)
    {
      return Result.Failure<SceneImage>(width.Error);
    }

    Result<int> height = ToolArguments.RequireInt(json, HeightName, Requirement);
    if (!height.IsSuccess)
    {
      return Result.Failure<SceneImage>(height.Error);
    }

    Result<SceneColor?> background = OptionalColor(json, BackgroundName);
    if (!background.IsSuccess)
    {
      return Result.Failure<SceneImage>(background.Error);
    }

    Result<IReadOnlyList<SceneShape>> shapes = ParseShapes(json);
    return !shapes.IsSuccess
      ? Result.Failure<SceneImage>(shapes.Error)
      : SceneImage.Create(width.Value, height.Value, background.Value, shapes.Value);
  }

  private const string Requirement =
      "This tool requires width, height, and timeoutSeconds.";

  /// <summary>The shape array; absent reads as none, which draws just the background. Each entry
  ///     is parsed by kind, and an unknown kind is refused with the admitted set echoed back so a
  ///     caller can correct the spelling rather than guess at it.</summary>
  private static Result<IReadOnlyList<SceneShape>> ParseShapes(JsonElement json)
  {
    if (!json.TryGetProperty(ShapesName, out JsonElement shapes))
    {
      return Result.Success<IReadOnlyList<SceneShape>>([]);
    }

    if (shapes.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<SceneShape>>(new DomainError(
          ToolErrorCodes.InvalidParameterType,
          $"'shapes' must be an array, but got {shapes.ValueKind}."));
    }

    List<SceneShape> parsed = [];
    int index = 0;
    foreach (JsonElement entry in shapes.EnumerateArray())
    {
      Result<SceneShape> shape = ParseShape(entry);
      if (!shape.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<SceneShape>>(new DomainError(shape.Error.Code,
            $"shapes[{index}]: {shape.Error.Message}"));
      }

      parsed.Add(shape.Value);
      index++;
    }

    return Result.Success<IReadOnlyList<SceneShape>>(parsed);
  }
  /// <summary>One shape, dispatched on its 'kind'. The kind names are the vocabulary a caller
  ///     writes, so they are the same words the tool description uses.</summary>
  private static Result<SceneShape> ParseShape(JsonElement entry)
  {
    if (entry.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<SceneShape>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"a shape must be an object, but got {entry.ValueKind}."));
    }

    Result<string> kind = ToolArguments.RequireString(entry, "kind", ShapeRequirement);
    return !kind.IsSuccess ? Result.Failure<SceneShape>(kind.Error) : kind.Value switch
    {
      "rectangle" => Rectangle(entry),
      "rounded_rectangle" => RoundedRectangle(entry),
      "ellipse" => Ellipse(entry),
      "line" => Line(entry),
      "polyline" => Polyline(entry),
      "text" => Text(entry),
      _ => Result.Failure<SceneShape>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"unknown kind '{kind.Value}'. Admitted: rectangle, rounded_rectangle, ellipse, line, "
          + "polyline, text.")),
    };
  }

  private const string ShapeRequirement =
      "Every shape requires kind. Admitted: rectangle, rounded_rectangle, ellipse, line, "
      + "polyline, text.";

  private static Result<SceneShape> Rectangle(JsonElement entry)
  {
    Result<SceneRect> rect = Rect(entry);
    if (!rect.IsSuccess)
    {
      return Result.Failure<SceneShape>(rect.Error);
    }

    Result<(SceneColor? Fill, SceneStroke? Stroke)> paint = Paint(entry);
    return !paint.IsSuccess
        ? Result.Failure<SceneShape>(paint.Error)
        : Result.Success<SceneShape>(new SceneShape.Rectangle(
            rect.Value, paint.Value.Fill, paint.Value.Stroke));
  }

  private static Result<SceneShape> RoundedRectangle(JsonElement entry)
  {
    Result<SceneRect> rect = Rect(entry);
    if (!rect.IsSuccess)
    {
      return Result.Failure<SceneShape>(rect.Error);
    }

    Result<float> radius = Number(entry, "corner_radius", 0f);
    if (!radius.IsSuccess)
    {
      return Result.Failure<SceneShape>(radius.Error);
    }

    Result<(SceneColor? Fill, SceneStroke? Stroke)> paint = Paint(entry);
    return !paint.IsSuccess
        ? Result.Failure<SceneShape>(paint.Error)
        : Result.Success<SceneShape>(new SceneShape.RoundedRectangle(
            rect.Value, radius.Value, paint.Value.Fill, paint.Value.Stroke));
  }

  private static Result<SceneShape> Ellipse(JsonElement entry)
  {
    Result<SceneRect> rect = Rect(entry);
    if (!rect.IsSuccess)
    {
      return Result.Failure<SceneShape>(rect.Error);
    }

    Result<(SceneColor? Fill, SceneStroke? Stroke)> paint = Paint(entry);
    return !paint.IsSuccess
        ? Result.Failure<SceneShape>(paint.Error)
        : Result.Success<SceneShape>(new SceneShape.Ellipse(
            rect.Value, paint.Value.Fill, paint.Value.Stroke));
  }

  private static Result<SceneShape> Line(JsonElement entry)
  {
    Result<ScenePoint> from = Point(entry, "from");
    if (!from.IsSuccess)
    {
      return Result.Failure<SceneShape>(from.Error);
    }

    Result<ScenePoint> to = Point(entry, "to");
    if (!to.IsSuccess)
    {
      return Result.Failure<SceneShape>(to.Error);
    }

    Result<SceneStroke> stroke = Stroke(entry, required: true);
    return !stroke.IsSuccess
        ? Result.Failure<SceneShape>(stroke.Error)
        : Result.Success<SceneShape>(new SceneShape.Line(from.Value, to.Value, stroke.Value));
  }

  private static Result<SceneShape> Polyline(JsonElement entry)
  {
    if (!entry.TryGetProperty("points", out JsonElement points)
        || points.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<SceneShape>(new DomainError(ToolErrorCodes.MissingParameter,
          "a polyline requires points: an array of {x, y} objects."));
    }

    List<ScenePoint> parsed = [];
    int index = 0;
    foreach (JsonElement entryPoint in points.EnumerateArray())
    {
      Result<ScenePoint> point = PointOf(entryPoint);
      if (!point.IsSuccess)
      {
        return Result.Failure<SceneShape>(new DomainError(point.Error.Code,
            $"points[{index}]: {point.Error.Message}"));
      }

      parsed.Add(point.Value);
      index++;
    }

    Result<bool> closed = Flag(entry, "closed");
    if (!closed.IsSuccess)
    {
      return Result.Failure<SceneShape>(closed.Error);
    }

    Result<(SceneColor? Fill, SceneStroke? Stroke)> paint = Paint(entry);
    return !paint.IsSuccess
        ? Result.Failure<SceneShape>(paint.Error)
        : Result.Success<SceneShape>(new SceneShape.Polyline(
            parsed, closed.Value, paint.Value.Fill, paint.Value.Stroke));
  }

  private static Result<SceneShape> Text(JsonElement entry)
  {
    Result<string> value = ToolArguments.RequireString(entry, "text", TextRequirement);
    if (!value.IsSuccess)
    {
      return Result.Failure<SceneShape>(value.Error);
    }

    Result<float> x = Number(entry, "x", 0f);
    if (!x.IsSuccess)
    {
      return Result.Failure<SceneShape>(x.Error);
    }

    Result<float> y = Number(entry, "y", 0f);
    if (!y.IsSuccess)
    {
      return Result.Failure<SceneShape>(y.Error);
    }

    Result<float> size = Number(entry, "font_size", 16f);
    if (!size.IsSuccess)
    {
      return Result.Failure<SceneShape>(size.Error);
    }

    Result<SceneColor> color = RequiredColor(entry, "color");
    return !color.IsSuccess
        ? Result.Failure<SceneShape>(color.Error)
        : Result.Success<SceneShape>(new SceneShape.TextRun(value.Value, x.Value, y.Value,
            size.Value, OptionalStringOf(entry, "font_family"), color.Value));
  }

  private const string TextRequirement =
      "A text shape requires text, x, y, font_size, and color.";

  /// <summary>The box a shape occupies: x, y, width, height, all required.</summary>
  private static Result<SceneRect> Rect(JsonElement entry)
  {
    Result<float> x = Number(entry, "x", 0f);
    if (!x.IsSuccess)
    {
      return Result.Failure<SceneRect>(x.Error);
    }

    Result<float> y = Number(entry, "y", 0f);
    if (!y.IsSuccess)
    {
      return Result.Failure<SceneRect>(y.Error);
    }

    Result<float> width = Number(entry, "width", 0f);
    if (!width.IsSuccess)
    {
      return Result.Failure<SceneRect>(width.Error);
    }

    Result<float> height = Number(entry, "height", 0f);
    return !height.IsSuccess
        ? Result.Failure<SceneRect>(height.Error)
        : Result.Success(new SceneRect(x.Value, y.Value, width.Value, height.Value));
  }

  /// <summary>The fill and stroke a shape paints with. Both are optional here; the domain
  ///     refuses a shape with neither, because that shape draws nothing.</summary>
  private static Result<(SceneColor? Fill, SceneStroke? Stroke)> Paint(JsonElement entry)
  {
    Result<SceneColor?> fill = OptionalColor(entry, "fill");
    if (!fill.IsSuccess)
    {
      return Result.Failure<(SceneColor?, SceneStroke?)>(fill.Error);
    }

    Result<SceneStroke?> stroke = OptionalStroke(entry);
    return !stroke.IsSuccess
        ? Result.Failure<(SceneColor? Fill, SceneStroke? Stroke)>(stroke.Error)
        : Result.Success<(SceneColor? Fill, SceneStroke? Stroke)>((fill.Value, stroke.Value));
  }

  /// <summary>An optional stroke, spelled as stroke_width plus stroke_color. Naming only one of
  ///     the two is a refusal: half a stroke is a caller who meant something else.</summary>
  private static Result<SceneStroke?> OptionalStroke(JsonElement entry)
  {
    bool hasWidth = entry.TryGetProperty("stroke_width", out _);
    bool hasColor = entry.TryGetProperty("stroke_color", out _);
    if (!hasWidth && !hasColor)
    {
      return Result.Success<SceneStroke?>(null);
    }

    if (!hasWidth || !hasColor)
    {
      return Result.Failure<SceneStroke?>(new DomainError(ToolErrorCodes.MissingParameter,
          "a stroke needs BOTH stroke_width and stroke_color."));
    }

    Result<float> width = Number(entry, "stroke_width", 1f);
    if (!width.IsSuccess)
    {
      return Result.Failure<SceneStroke?>(width.Error);
    }

    Result<SceneColor> color = RequiredColor(entry, "stroke_color");
    return !color.IsSuccess
        ? Result.Failure<SceneStroke?>(color.Error)
        : Result.Success<SceneStroke?>(new SceneStroke(width.Value, color.Value));
  }

  /// <summary>A stroke that a shape cannot do without (a line paints nothing else).</summary>
  private static Result<SceneStroke> Stroke(JsonElement entry, bool required)
  {
    Result<SceneStroke?> optional = OptionalStroke(entry);
    return !optional.IsSuccess
        ? Result.Failure<SceneStroke>(optional.Error)
        : RequiredStroke(optional.Value, required);
  }

  /// <summary>The stroke, or the refusal for a shape that cannot paint without one.</summary>
  private static Result<SceneStroke> RequiredStroke(SceneStroke? stroke, bool required) =>
      stroke is { } present ? Result.Success(present) : MissingStroke(required);

  private static Result<SceneStroke> MissingStroke(bool required) =>
      Result.Failure<SceneStroke>(new DomainError(ToolErrorCodes.MissingParameter,
          required
              ? "this shape requires stroke_width and stroke_color; a line with no stroke paints nothing."
              : "a stroke was requested but not supplied."));

  private static Result<ScenePoint> Point(JsonElement entry, string name)
  {
    return !entry.TryGetProperty(name, out JsonElement point)
        ? Result.Failure<ScenePoint>(new DomainError(ToolErrorCodes.MissingParameter,
            $"this shape requires '{name}': an object with x and y."))
        : PointOf(point);
  }

  private static Result<ScenePoint> PointOf(JsonElement point)
  {
    if (point.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<ScenePoint>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"a point must be an object, but got {point.ValueKind}."));
    }

    Result<float> x = Number(point, "x", 0f);
    if (!x.IsSuccess)
    {
      return Result.Failure<ScenePoint>(x.Error);
    }

    Result<float> y = Number(point, "y", 0f);
    return !y.IsSuccess
        ? Result.Failure<ScenePoint>(y.Error)
        : Result.Success(new ScenePoint(x.Value, y.Value));
  }

  /// <summary>A required number: integers and decimals both read as coordinates, because a
  ///     caller placing shapes writes 40 as often as 40.5.</summary>
  private static Result<float> Number(JsonElement entry, string name, float fallback)
  {
    return entry.TryGetProperty(name, out JsonElement value)
        ? NumberOf(value, name)
        : Result.Success(fallback);
  }

  private static Result<bool> Flag(JsonElement entry, string name)
  {
    return entry.TryGetProperty(name, out JsonElement value)
        ? FlagOf(value, name)
        : Result.Success(false);
  }

  private static Result<SceneColor> RequiredColor(JsonElement entry, string name)
  {
    Result<SceneColor?> color = OptionalColor(entry, name);
    return color.IsSuccess ? PresentColor(color.Value, name) : Result.Failure<SceneColor>(color.Error);
  }

  private static Result<SceneColor?> OptionalColor(JsonElement entry, string name)
  {
    return entry.TryGetProperty(name, out JsonElement value)
        ? ColorOf(value, name)
        : Result.Success<SceneColor?>(null);
  }

  private static string? OptionalStringOf(JsonElement entry, string name) =>
      entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
          ? value.GetString()
          : null;

  /// <summary>A present value read as a number. Integers and decimals both read as coordinates,
  ///     because a caller placing shapes writes 40 as often as 40.5.</summary>
  private static Result<float> NumberOf(JsonElement value, string name) =>
      value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
          ? Result.Success((float)number)
          : Result.Failure<float>(new DomainError(ToolErrorCodes.InvalidParameterType,
              $"'{name}' must be a number, but got {value.ValueKind}."));

  /// <summary>A present value read as a boolean.</summary>
  private static Result<bool> FlagOf(JsonElement value, string name) =>
      value.ValueKind is JsonValueKind.True or JsonValueKind.False
          ? Result.Success(value.GetBoolean())
          : Result.Failure<bool>(new DomainError(ToolErrorCodes.InvalidParameterType,
              $"'{name}' must be a boolean, but got {value.ValueKind}."));

  /// <summary>An optional colour that the caller DID supply, so its absence is now a refusal: the
  ///     shape cannot paint in a colour it was not given.</summary>
  private static Result<SceneColor> PresentColor(SceneColor? color, string name) =>
      color is { } present
          ? Result.Success(present)
          : Result.Failure<SceneColor>(new DomainError(ToolErrorCodes.MissingParameter,
              $"this shape requires '{name}': #RRGGBB or #RRGGBBAA."));

  /// <summary>A present value read as a colour. A colour outside the accepted spelling is refused
  ///     with the spelling the caller may use, never coerced into black.</summary>
  private static Result<SceneColor?> ColorOf(JsonElement value, string name) =>
      value.ValueKind != JsonValueKind.String
          ? Result.Failure<SceneColor?>(new DomainError(ToolErrorCodes.InvalidParameterType,
              $"'{name}' must be a string like \"#RRGGBB\", but got {value.ValueKind}."))
          : SceneColor.Parse(value.GetString()!).Map(color => (SceneColor?)color);
}
