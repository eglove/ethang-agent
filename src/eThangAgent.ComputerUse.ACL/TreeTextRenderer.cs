using System.Globalization;
using System.Text;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>Renders the capture_app element table into the model-facing tree text
///     (spec 3.2): page header, per-element rows on the fixed flag order, the
///     delta/no_change summaries, and the priority trim with sparse indices.
///     Pure functions - no broker, no I/O.</summary>
public static class TreeTextRenderer
{
  private const int DefaultTrimCap = 1500;
  private const int IndentCap = 24;
  private const int MaxValueLength = 60;
  private const char Ellipsis = '\u2026';

  /// <summary>Renders one capture_app result. Delta results (base_state_id present)
  ///     render changed rows only behind the ax_snapshot line; no_change results
  ///     render the single summary sentence; full results render the tree trimmed
  ///     to maxElements. PRODUCTION ALWAYS CALLS WITH THE DEFAULT: 1500 is the
  ///     brief's cap and production code must never pass another value - the
  ///     parameter is a TEST-ONLY knob so fixtures can exercise the trim on
  ///     small tables.</summary>
  public static string Render(CaptureAppResult capture, int maxElements = DefaultTrimCap,
      int? deliveredWidth = null, int? deliveredHeight = null)
  {
    ArgumentNullException.ThrowIfNull(capture);
    return capture.SnapshotMode switch
    {
      "delta" => RenderDelta(capture, deliveredWidth, deliveredHeight),
      "no_change" => RenderNoChange(capture, deliveredWidth, deliveredHeight),
      "full" => RenderFull(capture, maxElements, deliveredWidth, deliveredHeight),
      _ => throw new InvalidOperationException(
          $"unknown snapshot_mode '{capture.SnapshotMode}': BrokerProtocol.CaptureAppResult.From fails closed on modes outside {{full, delta, no_change}}, so this is a programmer error"),
    };
  }

  private static string RenderFull(CaptureAppResult capture, int maxElements,
      int? deliveredWidth, int? deliveredHeight)
  {
    int shown;
    IReadOnlyList<int> order;
    (shown, order) = Trim(capture.Elements, maxElements);
    StringBuilder sb = new();
    AppendHeader(sb, capture, deliveredWidth, deliveredHeight);
    _ = sb.Append(CultureInfo.InvariantCulture, $"elements ({shown}):\n");
    foreach (int index in order)
    {
      AppendRow(sb, capture.Elements[index], indent: Depth(capture.Elements, index));
    }

    if (shown < capture.Elements.Count)
    {
      _ = sb.Append(CultureInfo.InvariantCulture,
          $"note: {shown} of {capture.Elements.Count} elements shown (selected by priority, ancestors kept) - indices are sparse; {capture.Elements.Count - shown} hidden.\n");
    }

    AppendWindowNote(sb, capture);
    return sb.ToString().TrimEnd('\n');
  }

  /// <summary>The paging disclosure: what this capture showed, what exists beyond it,
  ///     and the offset that reaches the rest. A model that cannot see the whole tree must
  ///     be told the tree continues and how to ask for it - silence here reads as a
  ///     complete picture and produces confident action on a partial one.</summary>
  private static void AppendWindowNote(StringBuilder sb, CaptureAppResult capture)
  {
    int total = capture.ElementTotal;
    int offset = capture.ElementOffset;
    int shown = capture.Elements.Count;
    bool more = offset + shown < total;
    if (!capture.ElementTruncated && !more && offset == 0)
    {
      return;
    }

    string totalText = capture.ElementTruncated
      ? string.Create(CultureInfo.InvariantCulture, $"at least {total}")
      : total.ToString(CultureInfo.InvariantCulture);
    string range = shown == 0
      ? string.Create(CultureInfo.InvariantCulture, $"no elements at offset {offset}")
      : string.Create(CultureInfo.InvariantCulture, $"elements {offset}-{offset + shown - 1} of {totalText}");
    _ = sb.Append(CultureInfo.InvariantCulture, $"paging: showing {range}.");
    if (more)
    {
      _ = sb.Append(CultureInfo.InvariantCulture,
          $" The tree continues: observe again with offset={offset + shown} to see the next page.");
    }

    if (offset > 0)
    {
      _ = sb.Append(CultureInfo.InvariantCulture,
          $" Earlier pages: observe with offset=0.");
    }

    if (capture.ElementTruncated)
    {
      _ = sb.Append(" The tree is deeper than the walk's depth cap, so the total is a floor and elements below that depth are not reachable by paging.");
    }

    _ = sb.Append('\n');
  }

  private static string RenderDelta(CaptureAppResult capture,
      int? deliveredWidth, int? deliveredHeight)
  {
    StringBuilder sb = new();
    AppendHeader(sb, capture, deliveredWidth, deliveredHeight);
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"ax_snapshot: mode=delta base_state_id={capture.BaseStateId} elements={capture.Elements.Count}\n");
    _ = sb.Append(CultureInfo.InvariantCulture, $"elements ({capture.Elements.Count}):\n");
    foreach (CaptureAppElement element in capture.Elements)
    {
      AppendRow(sb, element, indent: Depth(capture.Elements, element.Index), changed: true);
    }

    _ = sb.Append(CultureInfo.InvariantCulture,
        $"Unchanged rows are omitted: no element was added or removed, so element order and every index are identical to {capture.BaseStateId} and remain valid under state_id {capture.StateId}.");
    return sb.ToString();
  }

  private static string RenderNoChange(CaptureAppResult capture,
      int? deliveredWidth, int? deliveredHeight)
  {
    StringBuilder sb = new();
    AppendHeader(sb, capture, deliveredWidth, deliveredHeight);
    _ = sb.Append("No material accessibility change since ")
        .Append(capture.BaseStateId)
        .Append(": the accessibility element list, its order, and every index are identical. ")
        .Append("A page that visibly changed while its accessibility tree did not (a canvas, a cross-origin frame, a dialog that closed without a tree event) looks exactly like this - ")
        .Append("re-observe with force=true to re-scan and get the full tree, or take the next action if the tree is what you needed.");
    return sb.ToString();
  }

  private static void AppendHeader(StringBuilder sb, CaptureAppResult capture,
      int? deliveredWidth, int? deliveredHeight)
  {
    _ = sb.Append("state_id ").Append(capture.StateId).Append('\n');
    // The brief pins the app line to aumid|exe only; when neither is known the
    // placeholder '-' stands in rather than leaking the display Name.
    string app = capture.App.Aumid ?? capture.App.Exe ?? "-";
    _ = sb.Append("app: ").Append(app).Append(" pid=")
        .Append(CultureInfo.InvariantCulture, $"{capture.App.Pid} {Quote(capture.App.Name)}\n");
    CaptureAppWindow w = capture.Window;
    _ = sb.Append("window: ").Append(Quote(w.Title)).Append(" id=")
        .Append(CultureInfo.InvariantCulture, $"{w.WindowId} bounds=[{w.Bounds[0]},{w.Bounds[1]},{w.Bounds[2]},{w.Bounds[3]}]\n");
    AppendScreenshotScale(sb, capture, w, deliveredWidth, deliveredHeight);
    if (w.SurfaceKind != "window" || w.SurfaceLifecycle != "stable")
    {
      _ = sb.Append("surface: kind=").Append(w.SurfaceKind).Append(" lifecycle=").Append(w.SurfaceLifecycle).Append('\n');
    }
  }

  /// <summary>The screenshot-to-screen mapping, stated whenever a screenshot accompanies
  ///     the observation. The model receives a DOWNSCALED raster, so a coordinate read off
  ///     the image is NOT a screen coordinate; without the factor the model has no way to
  ///     know what it is looking at and every coordinate click lands somewhere else. The
  ///     factor is the window's width divided by the DELIVERED raster's, which is exact for
  ///     the uniform downscale the encoder applies. The broker's own raster dimensions are
  ///     the wrong numerator: they describe an image the model never sees, and using them
  ///     reports a factor of 1 while the delivered image is shrunk.</summary>
  private static void AppendScreenshotScale(StringBuilder sb, CaptureAppResult capture,
      CaptureAppWindow window, int? deliveredWidth, int? deliveredHeight)
  {
    if (capture.Screenshot is not { } shot || window.Bounds.Length < 4 || window.Bounds[2] <= 0)
    {
      return;
    }

    int width = deliveredWidth ?? shot.Width;
    int height = deliveredHeight ?? shot.Height;
    if (width <= 0 || height <= 0)
    {
      return;
    }

    double factor = window.Bounds[2] / (double)width;
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"screenshot: {width}x{height} pixels for a {window.Bounds[2]}x{window.Bounds[3]} window; multiply a screenshot coordinate by {factor:0.###} to get the screen coordinate a coordinate target expects.\n");
  }

  private static void AppendRow(StringBuilder sb, CaptureAppElement e, int indent, bool changed = false)
  {
    if (changed)
    {
      _ = sb.Append('~');
    }

    _ = sb.Append(' ', 1 + Math.Min(indent, IndentCap));
    _ = sb.Append('[').Append(e.Index.ToString(CultureInfo.InvariantCulture)).Append("] ");
    _ = sb.Append(e.Role);
    if (HasText(e.Title))
    {
      _ = sb.Append(' ').Append(Quote(Flatten(e.Title)));
    }

    if (HasText(e.Value))
    {
      _ = sb.Append(" = ").Append(Quote(Truncate(Flatten(e.Value))));
    }

    List<string> flags = FlagsOf(e);
    if (flags.Count > 0)
    {
      _ = sb.Append(" (").Append(string.Join(" ", flags)).Append(')');
    }

    List<string> actions = [.. e.Actions.Where(a => !IsDefaultAction(a))];
    if (actions.Count > 0)
    {
      _ = sb.Append(" actions=[").Append(string.Join(",", actions)).Append(']');
    }

    _ = sb.Append('\n');
  }

  /// <summary>The row's state flags in the fixed brief order: pressable, editable,
  ///     has_menu, focused, selected, default_action, disabled.</summary>
  private static List<string> FlagsOf(CaptureAppElement e)
  {
    List<string> flags = [];
    if (e.Pressable)
    {
      flags.Add("pressable");
    }

    if (e.Editable)
    {
      flags.Add("editable");
    }

    if (e.HasMenu)
    {
      flags.Add("has_menu");
    }

    if (e.Focused)
    {
      flags.Add("focused");
    }

    if (e.Selected)
    {
      flags.Add("selected");
    }

    if (IsDefaultAction(e))
    {
      flags.Add("default_action");
    }

    if (!e.Enabled)
    {
      flags.Add("disabled");
    }

    return flags;
  }

  /// <summary>Trim to the cap by priority score, re-add ancestors of kept rows,
  ///     then restore index order. Score weights are the brief's ranking.</summary>
  private static (int Shown, IReadOnlyList<int> Order) Trim(IReadOnlyList<CaptureAppElement> elements, int cap)
  {
    if (elements.Count <= cap)
    {
      IReadOnlyList<int> all = [.. Enumerable.Range(0, elements.Count)];
      return (elements.Count, all);
    }

    List<(int Index, int Score)> ranked = [];
    for (int i = 0; i < elements.Count; i++)
    {
      ranked.Add((i, Score(elements[i])));
    }

    List<int> kept = [.. ranked.OrderByDescending(r => r.Score).ThenBy(r => r.Index).Take(cap).Select(r => r.Index)];
    HashSet<int> keep = [.. kept];
    foreach (int ancestor in Ancestors(elements, kept))
    {
      _ = keep.Add(ancestor);
    }

    IReadOnlyList<int> order = [.. keep.OrderBy(i => i)];
    return (order.Count, order);
  }

  /// <summary>Ancestors of the kept rows: for every kept element, every other
  ///     element whose bounds strictly contain it (same peer rule as Depth).</summary>
  private static IEnumerable<int> Ancestors(IReadOnlyList<CaptureAppElement> elements, List<int> kept)
  {
    foreach (int i in kept)
    {
      for (int j = 0; j < elements.Count; j++)
      {
        if (j == i || !Contains(elements[j].Bounds, elements[i].Bounds))
        {
          continue;
        }

        if (Area(elements[j].Bounds) > Area(elements[i].Bounds)
            || (Area(elements[j].Bounds) == Area(elements[i].Bounds) && j < i))
        {
          yield return j;
        }
      }
    }
  }

  /// <summary>The brief's priority ranking, verbatim: focused +220, selected +240,
  ///     default_action +240, pressable+enabled +200, leaf control +100, editable
  ///     +120, has title +60, has_menu +30, has value +20, containers -120,
  ///     zero-area -400. Weights ride the rule table; Score sums the matching rules.
  ///     </summary>
  private static readonly (Func<CaptureAppElement, bool> Matches, int Weight)[] ScoreRules =
  [
    (static e => e.Focused, 220),
    (static e => e.Selected, 240),
    (static e => IsDefaultAction(e), 240),
    (static e => e.Pressable && e.Enabled, 200),
    (static e => IsLeaf(e), 100),
    (static e => e.Editable, 120),
    (static e => HasText(e.Title), 60),
    (static e => e.HasMenu, 30),
    (static e => HasText(e.Value), 20),
    (static e => IsContainer(e), -120),
    (static e => IsZeroArea(e), -400),
  ];

  private static int Score(CaptureAppElement e)
  {
    int score = 0;
    foreach ((Func<CaptureAppElement, bool> matches, int weight) in ScoreRules)
    {
      if (matches(e))
      {
        score += weight;
      }
    }

    return score;
  }

  /// <summary>leaf control: a non-container with no children of its own in the
  ///     delivered table.</summary>
  private static bool IsLeaf(CaptureAppElement e) => !IsContainer(e) && !HasChildren(e);

  private static bool IsContainer(CaptureAppElement e) => e.Kind is "pane" or "group" or "window";


  /// <summary>Depth derivation over a parent-free wire table: one more than the
  ///     number of strictly smaller CONTAINING bounds; equal-area boxes are peers
  ///     (lower index is the ancestor). O(n^2) per render - fine at the 1500 cap.</summary>
  private static int Depth(IReadOnlyList<CaptureAppElement> elements, int target)
  {
    int targetAt = -1;
    for (int i = 0; i < elements.Count; i++)
    {
      if (elements[i].Index == target)
      {
        targetAt = i;
        break;
      }
    }

    if (targetAt < 0)
    {
      return 0;
    }

    CaptureAppElement e = elements[targetAt];
    int depth = 0;
    for (int i = 0; i < elements.Count; i++)
    {
      if (i == targetAt || !Contains(elements[i].Bounds, e.Bounds))
      {
        continue;
      }

      if (Area(elements[i].Bounds) > Area(e.Bounds)
          || (Area(elements[i].Bounds) == Area(e.Bounds) && i < targetAt))
      {
        depth++;
      }
    }

    return depth;
  }

  private static bool Contains(int[] outer, int[] inner) => outer[0] <= inner[0] && outer[1] <= inner[1]
    && outer[0] + outer[2] >= inner[0] + inner[2] && outer[1] + outer[3] >= inner[1] + inner[3];

  private static long Area(int[] b) => (long)b[2] * b[3];

  /// <summary>The element's default press: the ubiquitous press action the broker
  ///     already filtered from actions[]. Recovered as press or invoke.
  ///     </summary>
  private static bool IsDefaultAction(CaptureAppElement e) => e.Actions.Any(IsUbiquitousPress);

  private static bool IsDefaultAction(string action) => IsUbiquitousPress(action);

  private static bool IsUbiquitousPress(string action) =>
    string.Equals(action, "press", StringComparison.OrdinalIgnoreCase)
    || string.Equals(action, "invoke", StringComparison.OrdinalIgnoreCase);

  private static bool HasChildren(CaptureAppElement e) => (e.ChildrenTotal ?? 0) > 0;

  private static bool HasText(string? s) => !string.IsNullOrEmpty(s);

  private static bool IsZeroArea(CaptureAppElement e) => e.Bounds[2] <= 0 || e.Bounds[3] <= 0;

  /// <summary>Newlines flatten to spaces so one element stays one line.</summary>
  private static string Flatten(string? s) => (s ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

  private static string Truncate(string value) => value.Length <= MaxValueLength
    ? value
    : value[..MaxValueLength] + Ellipsis;

  private static string Quote(string? s) => "\"" + (s ?? string.Empty) + "\"";
}
