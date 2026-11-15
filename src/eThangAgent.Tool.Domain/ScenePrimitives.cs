using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>An opaque colour, in the only spelling the scene document accepts: #RRGGBB, or
///     #RRGGBBAA when the caller wants translucency. Parsed once at construction so a colour
///     that cannot be drawn is a typed refusal at the boundary rather than a silent black
///     rectangle deep inside the renderer.</summary>
public sealed record SceneColor
{
  private SceneColor(byte red, byte green, byte blue, byte alpha)
  {
    Red = red;
    Green = green;
    Blue = blue;
    Alpha = alpha;
  }

  /// <summary>The red channel, 0-255.</summary>
  public byte Red { get; }

  /// <summary>The green channel, 0-255.</summary>
  public byte Green { get; }

  /// <summary>The blue channel, 0-255.</summary>
  public byte Blue { get; }

  /// <summary>The alpha channel, 0-255. 255 (opaque) when the caller did not state one.</summary>
  public byte Alpha { get; }

  /// <summary>Parses #RRGGBB or #RRGGBBAA. Anything else is a typed refusal naming the
  ///     accepted spellings - the caller is writing image directives, so a named colour or a
  ///     bare "red" would be a value the renderer silently reinterpreted.</summary>
  public static Result<SceneColor> Parse(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return Result.Failure<SceneColor>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "A colour must be a non-empty #RRGGBB or #RRGGBBAA string."));
    }

    string hex = text.Trim().TrimStart('#');
    if (hex.Length is not (6 or 8) || !hex.All(Uri.IsHexDigit))
    {
      return Result.Failure<SceneColor>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{text}' is not a colour. Use #RRGGBB, or #RRGGBBAA for translucency."));
    }

    byte Channel(int index) => byte.Parse(hex.AsSpan(index, 2), NumberStyles.HexNumber,
        CultureInfo.InvariantCulture);

    return Result.Success(new SceneColor(
        Channel(0), Channel(2), Channel(4), hex.Length == 8 ? Channel(6) : (byte)255));
  }
}

/// <summary>A point in the scene's pixel space, origin at the top-left.</summary>
/// <param name="X">Horizontal position.</param>
/// <param name="Y">Vertical position.</param>
public readonly record struct ScenePoint(float X, float Y);

/// <summary>A rectangle in the scene's pixel space, expressed as position plus extent so a
///     negative extent is a refusal rather than a rectangle the renderer would have to
///     normalise behind the caller's back.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Extent to the right; must be >= 0.</param>
/// <param name="Height">Extent downward; must be >= 0.</param>
public readonly record struct SceneRect(float X, float Y, float Width, float Height);

/// <summary>A stroke: how wide, and in what colour. A width of 0 is a refusal - a stroke the
///     caller asked for that draws nothing is a mistake to report, not a line to omit.</summary>
/// <param name="Width">Stroke width in pixels; must be > 0.</param>
/// <param name="Color">Stroke colour.</param>
public sealed record SceneStroke(float Width, SceneColor Color);
