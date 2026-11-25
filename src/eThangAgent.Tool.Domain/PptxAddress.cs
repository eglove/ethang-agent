using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The address kinds of the pinned pptx address grammar.</summary>
public enum PptxAddressKind
{
  /// <summary>One slide: s&lt;N&gt;.</summary>
  Slide,

  /// <summary>One shape in a slide's shape tree: s&lt;N&gt;.sh&lt;M&gt;, with a further
  ///     .sh&lt;K&gt; segment for each level of group nesting.</summary>
  Shape,

  /// <summary>One table cell: s&lt;N&gt;.sh&lt;M&gt;.t&lt;R&gt;.c&lt;C&gt;.</summary>
  TableCell,

  /// <summary>One slide's speaker notes: s&lt;N&gt;.notes.</summary>
  Notes,

  /// <summary>One animation effect: s&lt;N&gt;.anim&lt;K&gt;.</summary>
  Animation,
}

/// <summary>One parsed address of the pinned pptx grammar: s&lt;N&gt; (slide);
///     s&lt;N&gt;.sh&lt;M&gt; (shape) with a further .sh&lt;K&gt; for each level of group
///     nesting; s&lt;N&gt;.sh&lt;M&gt;.t&lt;R&gt;.c&lt;C&gt; (table cell); s&lt;N&gt;.notes
///     (speaker notes); s&lt;N&gt;.anim&lt;K&gt; (animation effect). Every index is 1-based.
///     A chart is a shape, never a separate form. <see cref="Render"/> is the canonical
///     text: it is what a read prints, and parsing it back yields an equal address.
///     </summary>
public sealed record PptxAddress
{
  /// <summary>Programmer-error guard: the members must describe an address of
  ///     <paramref name="kind"/> - a 1-based slide, a non-empty shape path of 1-based
  ///     indices for a shape or table cell, 1-based cell coordinates, a 1-based effect
  ///     index, and an empty shape path for every other kind. Model input reaches this
  ///     type through <see cref="Parse"/>, which never throws.</summary>
  /// <param name="kind">The address kind.</param>
  /// <param name="slide">The 1-based slide number.</param>
  /// <param name="shapePath">The 1-based shape path, one entry per level of group nesting.</param>
  /// <param name="row">The 1-based table row, for a table-cell address.</param>
  /// <param name="col">The 1-based table column, for a table-cell address.</param>
  /// <param name="effectIndex">The 1-based animation effect index, for an animation address.</param>
  public PptxAddress(PptxAddressKind kind, int slide, IReadOnlyList<int> shapePath, int row, int col, int effectIndex)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(slide, 1);
    ArgumentNullException.ThrowIfNull(shapePath);
    if (kind is PptxAddressKind.Shape or PptxAddressKind.TableCell)
    {
      if (shapePath.Count == 0)
      {
        throw new ArgumentException($"A {kind} address needs a non-empty shape path.", nameof(shapePath));
      }

      if (shapePath.Any(i => i < 1))
      {
        throw new ArgumentException("Every shape path index is 1-based.", nameof(shapePath));
      }
    }
    else if (shapePath.Count != 0)
    {
      throw new ArgumentException($"A {kind} address carries no shape path.", nameof(shapePath));
    }

    if (kind == PptxAddressKind.TableCell)
    {
      ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
      ArgumentOutOfRangeException.ThrowIfLessThan(col, 1);
    }

    if (kind == PptxAddressKind.Animation)
    {
      ArgumentOutOfRangeException.ThrowIfLessThan(effectIndex, 1);
    }

    Kind = kind;
    Slide = slide;
    ShapePath = [.. shapePath];
    Row = row;
    Col = col;
    EffectIndex = effectIndex;
  }

  /// <summary>The address kind.</summary>
  public PptxAddressKind Kind { get; }

  /// <summary>The 1-based slide number.</summary>
  public int Slide { get; }

  /// <summary>The 1-based shape path, one entry per level of group nesting; empty for
  ///     every kind other than shape and table cell.</summary>
  public IReadOnlyList<int> ShapePath { get; }

  /// <summary>The 1-based table row; 0 for every kind other than table cell.</summary>
  public int Row { get; }

  /// <summary>The 1-based table column; 0 for every kind other than table cell.</summary>
  public int Col { get; }

  /// <summary>The 1-based animation effect index; 0 for every kind other than animation.
  ///     </summary>
  public int EffectIndex { get; }

  /// <summary>The canonical text of this address. Parsing it back yields an equal address.
  ///     </summary>
  public string Render() => Kind switch
  {
    PptxAddressKind.Slide => $"s{Slide}",
    PptxAddressKind.Notes => $"s{Slide}.notes",
    PptxAddressKind.Animation => $"s{Slide}.anim{EffectIndex}",
    PptxAddressKind.Shape => ShapeText(),
    PptxAddressKind.TableCell => $"{ShapeText()}.t{Row}.c{Col}",
    _ => throw new InvalidOperationException($"Unhandled address kind {Kind}."),
  };

  /// <summary>Builds a slide address.</summary>
  /// <param name="slide">The 1-based slide number.</param>
  public static PptxAddress ForSlide(int slide) =>
      new(PptxAddressKind.Slide, slide, [], 0, 0, 0);

  /// <summary>Builds a shape address from its 1-based shape path.</summary>
  /// <param name="slide">The 1-based slide number.</param>
  /// <param name="shapePath">The 1-based shape path, one entry per level of group nesting.</param>
  public static PptxAddress ForShape(int slide, IReadOnlyList<int> shapePath) =>
      new(PptxAddressKind.Shape, slide, shapePath, 0, 0, 0);

  /// <summary>Builds a table-cell address from the table's shape path and the 1-based cell
  ///     coordinates.</summary>
  /// <param name="slide">The 1-based slide number.</param>
  /// <param name="shapePath">The table shape's 1-based shape path.</param>
  /// <param name="row">The 1-based row.</param>
  /// <param name="col">The 1-based column.</param>
  public static PptxAddress ForTableCell(int slide, IReadOnlyList<int> shapePath, int row, int col) =>
      new(PptxAddressKind.TableCell, slide, shapePath, row, col, 0);

  /// <summary>Builds a speaker-notes address.</summary>
  /// <param name="slide">The 1-based slide number.</param>
  public static PptxAddress ForNotes(int slide) =>
      new(PptxAddressKind.Notes, slide, [], 0, 0, 0);

  /// <summary>Builds an animation-effect address from its 1-based playback index.</summary>
  /// <param name="slide">The 1-based slide number.</param>
  /// <param name="effectIndex">The 1-based effect index.</param>
  public static PptxAddress ForAnimation(int slide, int effectIndex) =>
      new(PptxAddressKind.Animation, slide, [], 0, 0, effectIndex);

  /// <summary>Parses an address against the pinned grammar. Nothing is trimmed (the text
  ///     must already be exact), the grammar is case-sensitive, and every index parses
  ///     with int.TryParse under the invariant culture and must be at least 1. Any
  ///     violation is a typed InvalidAddress failure - an error value, never an exception.
  ///     </summary>
  /// <param name="raw">The address text.</param>
  public static Result<PptxAddress> Parse(string? raw)
  {
    if (string.IsNullOrEmpty(raw))
    {
      return Fail(raw, "address text is empty");
    }

    string[] segments = raw.Split('.');
    if (segments.Any(s => s.Length == 0))
    {
      return Fail(raw, "empty segment");
    }

    if (segments[0][0] != 's')
    {
      return Fail(raw, null);
    }

    Result<int> slide = Index(segments[0][1..]);
    return slide.IsSuccess ? ParseTail(segments, slide.Value, raw) : Fail(raw, null);
  }

  private static Result<PptxAddress> ParseTail(string[] segments, int slide, string raw)
  {
    return segments.Length == 1
        ? Result.Success(ForSlide(slide))
        : ParseSecondSegment(segments, slide, raw);
  }

  private static Result<PptxAddress> ParseSecondSegment(string[] segments, int slide, string raw)
  {
    string second = segments[1];
    return second == "notes" ? ParseNotes(segments, slide, raw) : ParseAfterNotes(segments, slide, second, raw);
  }

  private static Result<PptxAddress> ParseNotes(string[] segments, int slide, string raw) =>
      segments.Length == 2 ? Result.Success(ForNotes(slide)) : Fail(raw, null);

  private static Result<PptxAddress> ParseAfterNotes(string[] segments, int slide, string second, string raw) =>
      second.StartsWith("anim", StringComparison.Ordinal)
          ? ParseAnimation(segments, slide, second, raw)
          : ParseShapeTail(segments, slide, raw);
  private static Result<PptxAddress> ParseAnimation(string[] segments, int slide, string second, string raw)
  {
    Result<int> effect = Index(second[4..]);
    return effect.IsSuccess && segments.Length == 2
        ? Result.Success(ForAnimation(slide, effect.Value))
        : Fail(raw, null);
  }

  private static Result<PptxAddress> ParseShapeTail(string[] segments, int slide, string raw)
  {
    Result<List<int>> path = ParseShapePath(segments);
    if (!path.IsSuccess)
    {
      return Fail(raw, null);
    }

    int at = 1 + path.Value.Count;
    return at == segments.Length
        ? Result.Success(ForShape(slide, path.Value))
        : ParseCellTail(segments, at, slide, path.Value, raw);
  }

  private static Result<PptxAddress> ParseCellTail(string[] segments, int at, int slide, List<int> path, string raw) =>
      segments.Length - at == 2 && segments[at][0] == 't' && segments[at + 1][0] == 'c'
          ? ParseCell(segments, at, slide, path, raw)
          : Fail(raw, null);

  private static Result<PptxAddress> ParseCell(string[] segments, int at, int slide, List<int> path, string raw)
  {
    Result<int> row = Index(segments[at][1..]);
    Result<int> col = Index(segments[at + 1][1..]);
    return row.IsSuccess && col.IsSuccess
        ? Result.Success(ForTableCell(slide, path, row.Value, col.Value))
        : Fail(raw, null);
  }

  private static Result<List<int>> ParseShapePath(string[] segments)
  {
    List<int> path = [];
    int at = 1;
    while (at < segments.Length && segments[at].StartsWith("sh", StringComparison.Ordinal))
    {
      Result<int> index = Index(segments[at][2..]);
      if (!index.IsSuccess)
      {
        return Result.Failure<List<int>>(index.Error);
      }

      path.Add(index.Value);
      at++;
    }

    return path.Count == 0
        ? Result.Failure<List<int>>(new DomainError(PptxErrorCodes.InvalidAddress, GrammarReminder))
        : Result.Success(path);
  }
  private static Result<int> Index(string text) =>
      int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 1
          ? Result.Success(value)
          : Result.Failure<int>(new DomainError(PptxErrorCodes.InvalidAddress, GrammarReminder));

  private static Result<PptxAddress> Fail(string? raw, string? reason)
  {
    string detail = reason is null ? $"address '{raw}'" : $"address '{raw}': {reason}";
    return Result.Failure<PptxAddress>(new DomainError(
        PptxErrorCodes.InvalidAddress, $"Invalid pptx address - {detail}. {GrammarReminder}"));
  }

  private string ShapeText() =>
      $"s{Slide}" + string.Concat(ShapePath.Select(i => $".sh{i}"));

  private const string GrammarReminder =
      "Use s<N> (slide), s<N>.sh<M> (shape, with a further .sh<K> per level of group nesting), " +
      "s<N>.sh<M>.t<R>.c<C> (table cell), s<N>.notes (speaker notes) or s<N>.anim<K> " +
      "(animation effect); all indices 1-based.";
}
