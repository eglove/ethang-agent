using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;

namespace eThangAgent.ComputerUse.Host;

/// <summary>One walked element row (task 17): the spec 3.1 wire fields. Index is
///     assigned at walk order; bounds is the screen-rect int[4] {x,y,w,h}; actions are
///     pattern-derived (Invoke => press/invoke); container sampling caps children and
///     records children_total/children_shown/children_offset.</summary>
#pragma warning disable CA1819 // Named decision: bounds is the wire cell int[4] {x,y,w,h} copied verbatim; a copy per row allocates for nothing.
public sealed record UiaElementRow(
  int Index,
  string Role,
  string Kind,
  string? Title,
  string? Value,
  int[] Bounds,
  bool Enabled,
  bool Editable,
  IReadOnlyList<string> Actions,
  bool Focused,
  bool Selected,
  bool Pressable,
  bool HasMenu,
  int? ChildrenTotal,
  int? ChildrenShown,
  int? ChildrenOffset)
{
  /// <summary>The container-sampling cap: above this many children the walker records
  ///     totals and samples evenly instead of walking every child.</summary>
  public const int ContainerChildCap = 100;
#pragma warning restore CA1819
}

/// <summary>The element rows of one window, in walk order, with the runtime elements
///     the element-op dispatcher resolves model indices against. The cache holds null
///     for a row whose element vanished mid-walk: the row is still reported so indices
///     stay dense, and an operation aimed at it answers element_unavailable.
///     <para>The walk is COMPLETE: every element within the depth cap is collected, and
///     the caller selects the window it shows. Truncating here instead would leave the
///     caller unable to say how much it withheld, and unable to reach what it cut.</para></summary>
public sealed record UiaWalk(IReadOnlyList<object> Rows, IReadOnlyList<AutomationElement?> Cache)
{
  /// <summary>An empty walk: no rows, no cached elements.</summary>
  public static UiaWalk Empty { get; } = new([], []);

  /// <summary>True when the depth cap cut the tree short, so <see cref="Rows"/> is a
  ///     floor on the element count rather than the whole tree.</summary>
  public bool Truncated { get; init; }

  /// <summary>The depth cap: deeper subtrees are not walked, because a tree that deep is
  ///     layout scaffolding rather than content.</summary>
  public const int DepthCap = 12;

  /// <summary>Walks one window's element tree from its root element.</summary>
  public static UiaWalk Of(AutomationElement root)
  {
    List<object> rows = [];
    List<AutomationElement?> cache = [];
    bool truncated = Walk(root, rows, cache, 0);
    return new UiaWalk(rows, cache) { Truncated = truncated };
  }

  /// <summary>Walks one element and its subtree; true when the depth cap stopped it short
  ///     of the whole subtree, which the caller reports rather than hides.</summary>
  private static bool Walk(AutomationElement element, List<object> rows, List<AutomationElement?> cache, int depth)
  {
    int index = cache.Count;
    cache.Add(element);
    rows.Add(RowFor(index, element, depth));
    if (depth >= DepthCap)
    {
      // A node at the cap may still have children; the tree below it is not collected.
      return ChildrenOf(element).Count > 0;
    }

    bool truncated = false;
    foreach (AutomationElement child in ChildrenOf(element))
    {
      truncated |= Walk(child, rows, cache, depth + 1);
    }

    return truncated;
  }

  /// <summary>The children to descend into. The control view stops at a frame's document
  ///     node, so a cross-origin iframe - an application form embedded in a host page, the
  ///     common shape of a job-application portal - contributes one opaque Document row and
  ///     nothing inside it is reachable, addressable, or scrollable. A frame or document
  ///     node is therefore descended through the raw view, which is where the hosted content
  ///     lives; every other element keeps the control view so the tree stays free of the raw
  ///     view's layout noise.</summary>
  private static List<AutomationElement> ChildrenOf(AutomationElement element) =>
    IsFrameNode(element) ? RawChildren(element) : ControlChildren(element);

  /// <summary>The control-view children: what an ordinary element exposes to a screen
  ///     reader.</summary>
  private static List<AutomationElement> ControlChildren(AutomationElement element)
  {
    try
    {
      return [.. element.FindAllChildren()];
    }
    catch (ElementNotAvailableException)
    {
      return [];
    }
  }

  /// <summary>The raw-view children of a frame node. A frame's hosted document is reachable
  ///     only through the raw view, and the raw view also carries the frame's own chrome, so
  ///     the walk keeps only the nodes that can hold content. A frame whose raw view yields
  ///     nothing falls back to its control children rather than contributing no subtree.</summary>
  private static List<AutomationElement> RawChildren(AutomationElement element)
  {
    List<AutomationElement> children = [];
    try
    {
      ITreeWalker walker = element.Automation.TreeWalkerFactory.GetRawViewWalker();
      for (AutomationElement? child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
      {
        children.Add(child);
      }
    }
    catch (ElementNotAvailableException)
    {
      return ControlChildren(element);
    }

    return children.Count > 0 ? children : ControlChildren(element);
  }

  /// <summary>True when the element hosts a separate document whose content the control
  ///     view does not expose: a frame pane or a document node.</summary>
  private static bool IsFrameNode(AutomationElement element)
  {
    try
    {
      return element.ControlType is ControlType.Pane or ControlType.Document;
    }
    catch (ElementNotAvailableException)
    {
      return false;
    }
  }

  private static object RowFor(int index, AutomationElement element, int depth)
  {
    try
    {
      return RowFromLiveElement(index, element, depth);
    }
    catch (ElementNotAvailableException)
    {
      return RowForUnavailableElement(index);
    }
  }

  private static object RowFromLiveElement(int index, AutomationElement element, int depth)
  {
    Rectangle bounds = element.BoundingRectangle;
    string role = element.ControlType.ToString();
    string name = element.Name ?? string.Empty;
    bool pressable = element.Patterns.Invoke.IsSupported;
    bool toggleable = element.Patterns.Toggle.IsSupported;
    string[] actions = ActionsFor(pressable, toggleable);
    bool valueBacked = element.Patterns.Value.IsSupported;
    string? value = ElementValue(element, valueBacked);
    bool selected = element.Patterns.SelectionItem.IsSupported;
    bool hasMenu = element.ControlType == ControlType.Menu;
    return new
    {
      index,
      role,
      kind = depth == 0 ? "window" : "control",
      title = name,
      value,
      bounds = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height },
      enabled = element.IsEnabled,
      editable = valueBacked,
      actions,
      focused = element.Properties.HasKeyboardFocus.ValueOrDefault,
      selected,
      pressable = pressable || toggleable,
      has_menu = hasMenu,
      children_total = (int?)null,
      children_shown = (int?)null,
      children_offset = (int?)null,
    };
  }

  /// <summary>The action list from the pressable/toggleable pattern flags.</summary>
  private static string[] ActionsFor(bool pressable, bool toggleable)
  {
    return (pressable, toggleable) switch
    {
      (true, true) => ["press", "toggle"],
      (true, false) => ["press"],
      (false, true) => ["toggle"],
      _ => [],
    };
  }

  /// <summary>Reads the Value pattern's current value; a value that vanishes
  ///     mid-read (the element died between the pattern check and the read) is null.</summary>
  private static string? ElementValue(AutomationElement element, bool valueBacked)
  {
    if (!valueBacked || !element.Patterns.Value.TryGetPattern(out FlaUI.Core.Patterns.IValuePattern? pattern))
    {
      return null;
    }

    try
    {
      return pattern.Value.ValueOrDefault;
    }
    catch (ElementNotAvailableException)
    {
      return null;
    }
  }

  private static object RowForUnavailableElement(int index)
  {
    return new
    {
      index,
      role = "unknown",
      kind = "control",
      title = string.Empty,
      value = (string?)null,
      bounds = new[] { 0, 0, 0, 0 },
      enabled = false,
      editable = false,
      actions = Array.Empty<string>(),
      focused = false,
      selected = false,
      pressable = false,
      has_menu = false,
      children_total = (int?)null,
      children_shown = (int?)null,
      children_offset = (int?)null,
    };
  }
}
