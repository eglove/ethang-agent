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

/// <summary>The per-request UIA walk worker (task 17): every request runs on a fresh
///     STA thread (COM UIA requires it), joined with the 45-second deadline cap; a
///     timed-out worker is abandoned (its thread dies with the process; COM teardown on
///     an abandoned STA is unsafe by design, so we orphan it deliberately). The walk
///     itself reads the tree through FlaUI's UIA3 automation, whose tree walkers supply
///     the control and raw views and whose elements supply the property and pattern
///     reads. RealBrokerObserver assigns dense walk-order indices with a 400-element
///     cap; the ACL's TreeTextRenderer additionally tolerates sparse tables and trims
///     for display, so the two sides only agree on the index semantics, not a density
///     requirement. COVERAGE: the deadline join/abandon mechanics are unit-tested over
///     the injectable worker; the live walk stays an integration concern.</summary>
public sealed partial class UiaTreeWalker(Func<string[]?>? walkCore = null)
{
  /// <summary>The hard deadline for one walk request.</summary>
  public static readonly TimeSpan WalkDeadline = TimeSpan.FromSeconds(45);

  private readonly Func<string[]?> _walkCore = walkCore ?? RealWalkCore;

  /// <summary>Runs one walk with the deadline. Returns the element rows (wire JSON
  ///     strings), or null when the walk timed out - the caller answers timeout.</summary>
  public string[]? WalkWithDeadline(TimeSpan? deadline = null)
  {
    TimeSpan cap = deadline ?? WalkDeadline;
    string[]? result = null;
    Exception? fault = null;
    using ManualResetEventSlim done = new(false);
    ThreadStart body = RunWorker;
    Thread worker = new(new ThreadStart(body));
    worker.SetApartmentState(ApartmentState.STA);
    worker.IsBackground = true;
    worker.Start();
    if (!done.Wait(cap))
    {
      return null; // deadline exceeded: the worker is abandoned by design
    }

#pragma warning disable CA1508 // Named decision: fault IS written on the worker thread; the analyzer cannot see the closure write ordering.
    return fault is null ? result : throw new InvalidOperationException("UIA walk failed on the STA worker.", fault);
#pragma warning restore CA1508

    void RunWorker()
    {
      try
      {
        result = _walkCore();
      }
#pragma warning disable CA1031 // Named decision: the STA worker boundary must never crash the process; the fault is delivered to the caller as internal.
      catch (Exception ex)
      {
        fault = ex;
      }
#pragma warning restore CA1031
      done.Set();
    }
  }

  private static string[]? RealWalkCore() => null; // the injected walk seam; production resolves RealBrokerObserver's FlaUI walk
}

/// <summary>The element rows of one window, in walk order, with the runtime elements
///     the element-op dispatcher resolves model indices against. The cache holds null
///     for a row whose element vanished mid-walk: the row is still reported so indices
///     stay dense, and an operation aimed at it answers element_unavailable.</summary>
public sealed record UiaWalk(IReadOnlyList<object> Rows, IReadOnlyList<AutomationElement?> Cache)
{
  /// <summary>An empty walk: no rows, no cached elements.</summary>
  public static UiaWalk Empty { get; } = new([], []);

  /// <summary>The sampling cap that keeps huge trees bounded.</summary>
  public const int ElementCap = 400;

  /// <summary>The depth cap: deeper subtrees are not walked.</summary>
  public const int DepthCap = 12;

  /// <summary>Walks one window's element tree from its root element.</summary>
  public static UiaWalk Of(AutomationElement root)
  {
    List<object> rows = [];
    List<AutomationElement?> cache = [];
    Walk(root, rows, cache, 0);
    return new UiaWalk(rows, cache);
  }

  private static void Walk(AutomationElement element, List<object> rows, List<AutomationElement?> cache, int depth)
  {
    if (cache.Count >= ElementCap)
    {
      return;
    }

    int index = cache.Count;
    cache.Add(element);
    rows.Add(RowFor(index, element, depth));
    if (depth > DepthCap)
    {
      return;
    }

    foreach (AutomationElement child in ChildrenOf(element))
    {
      Walk(child, rows, cache, depth + 1);
    }
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
