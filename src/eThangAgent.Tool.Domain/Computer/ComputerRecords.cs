#pragma warning disable CA1034 // Named decision: the command and outcome switches ARE the seam's contract (the spec's closed method set); public nested cases keep the case names as declared, namespace-qualified at use sites.
#pragma warning disable CA1819 // Bounds is a wire row cell (int[4] x,y,w,h), copied verbatim into tree rendering; a defensive copy per read would allocate per rendered element.
namespace eThangAgent.ToolDomain;

/// <summary>Wire-faithful Computer Use records (spec 1.2): the commands the tool
///     builds from strictly parsed input, the outcomes the ACL returns, and the
///     element/frame rows the observation ledger carries. Field names mirror the
///     model-facing keys so the mapping stays mechanical.
///     <para>Validation rules are construction gates — an invalid app reference or
///     target is a programmer error and throws <see cref="ArgumentException"/>;
///     user input never reaches these records unparsed.</para></summary>
public abstract record ComputerCommand
{
  /// <summary>Lists running applications.</summary>
  public sealed record ListApps() : ComputerCommand;

  /// <summary>Lists an application's windows.</summary>
  public sealed record ListWindows(ComputerAppRef App) : ComputerCommand;

  /// <summary>Observes one window: element tree, optional screenshot.</summary>
  public sealed record Observe(ComputerAppRef App, bool IncludeScreenshot, bool DisableDiffing) : ComputerCommand;

  /// <summary>Clicks a target.</summary>
  public sealed record Click(ComputerTarget Target, ComputerAppRef? App, string MouseButton = "left",
      int ClickCount = 1, string Modifiers = "", string Strategy = "auto", string ReturnState = "none") : ComputerCommand;

  /// <summary>Drags from one target to another.</summary>
  public sealed record Drag(ComputerTarget From, ComputerTarget To, ComputerAppRef? App,
      string Modifiers = "", string ReturnState = "none") : ComputerCommand;

  /// <summary>Scrolls at a target.</summary>
  public sealed record Scroll(ComputerTarget Target, string ScrollDirection, int ScrollAmount, ComputerAppRef? App,
      string Strategy = "auto", string ReturnState = "none") : ComputerCommand;

  /// <summary>Types text, optionally into a focused element target.</summary>
  public sealed record TypeText(string Text, ComputerTarget? Target, ComputerAppRef? App,
      string Strategy = "auto", string ReturnState = "none") : ComputerCommand;

  /// <summary>Sets an element's value directly over UIA.</summary>
  public sealed record SetValue(ComputerTarget Target, string Value, ComputerAppRef? App,
      string Strategy = "auto", string ReturnState = "none") : ComputerCommand;

  /// <summary>Selects text inside an editable element.</summary>
  public sealed record SelectText(ComputerTarget Target, string Text, string? Prefix, string? Suffix,
      string SelectionType, ComputerAppRef? App, string ReturnState = "none") : ComputerCommand;

  /// <summary>Presses a key chord; repeat and hold drive hold_key semantics. The
  ///     two nullable members precede the optional strings because C# requires
  ///     required-before-optional (brief name ruling: keep names, reorder).</summary>
  public sealed record Key(string Text, int? Repeat, double? HoldSeconds, ComputerAppRef? App,
      string Strategy = "auto", string ReturnState = "none") : ComputerCommand;

  /// <summary>Pastes via a broker-owned atomic clipboard borrow.</summary>
  public sealed record Paste(string Text, ComputerTarget? Target, ComputerAppRef? App,
      string Format = "text", string ReturnState = "none") : ComputerCommand;

  /// <summary>Invokes a named action advertised on an element; membership is checked
  ///     at execution time by the ACL/broker, not here.</summary>
  public sealed record PerformAction(ComputerTarget Target, string Action, ComputerAppRef? App,
      string ReturnState = "none") : ComputerCommand;

  /// <summary>Stops computer control and releases the controller lease.</summary>
#pragma warning disable CA1716 // Named decision: 'Stop' is the model-facing action name (the spec's closed action set); the VB-only collision is accepted to keep the wire name.
  public sealed record Stop(string? Reason) : ComputerCommand;
#pragma warning restore CA1716
}

/// <summary>Application reference: exactly one of Name, Pid, or Aumid selects the
///     app; WindowId optionally narrows to one window. Construction enforces
///     exactly-one (ArgumentException otherwise); callers go through the
///     By-name/pid/aumid factories.</summary>
public sealed record ComputerAppRef
{
  public ComputerAppRef(string? name, int? pid, string? aumid, int? windowId)
  {
    int given = (name is not null ? 1 : 0) + (pid is not null ? 1 : 0) + (aumid is not null ? 1 : 0);
    if (given != 1)
    {
      throw new ArgumentException($"Exactly one of name, pid, or aumid must be given; got {given}.", nameof(name));
    }

    if (name is not (null or { Length: > 0 }))
    {
      throw new ArgumentException("App name must be non-empty when given.", nameof(name));
    }

    if (aumid is not (null or { Length: > 0 }))
    {
      throw new ArgumentException("App AUMID must be non-empty when given.", nameof(aumid));
    }

    if (pid is not (null or > 0))
    {
      throw new ArgumentException("App pid must be positive when given.", nameof(pid));
    }

    if (windowId is not (null or > 0))
    {
      throw new ArgumentException("Window id must be positive when given.", nameof(windowId));
    }

    Name = name;
    Pid = pid;
    Aumid = aumid;
    WindowId = windowId;
  }

  /// <summary>Reference by exact application name.</summary>
  public static ComputerAppRef ByName(string name, int? WindowId = null) => new(name, null, null, WindowId);

  /// <summary>Reference by process id.</summary>
  public static ComputerAppRef ByPid(int pid, int? WindowId = null) => new(null, pid, null, WindowId);

  /// <summary>Reference by AUMID.</summary>
  public static ComputerAppRef ByAumid(string aumid, int? WindowId = null) => new(null, null, aumid, WindowId);

  public string? Name { get; }
  public int? Pid { get; }
  public string? Aumid { get; }
  public int? WindowId { get; }
}

/// <summary>Interaction target: either an element index from the latest
///     observation of the referenced app, or a pixel coordinate inside a
///     registered frame (resolved by the ACL's frame registry). Index and
///     coordinates are non-negative; violations throw ArgumentException.
///     ElementIndex is set for element targets, X/Y for coordinates — a
///     construction-gate discriminated union like <see cref="ComputerAppRef"/>.
///     </summary>
public sealed record ComputerTarget
{
  private ComputerTarget(int? elementIndex, int? x, int? y)
  {
    ElementIndex = elementIndex;
    X = x;
    Y = y;
  }

  /// <summary>Element target by observation index.</summary>
  public static ComputerTarget Element(int index)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(index);

    return new ComputerTarget(index, null, null);
  }

  /// <summary>Coordinate target inside a registered frame.</summary>
  public static ComputerTarget Coordinate(int x, int y)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(x);

    ArgumentOutOfRangeException.ThrowIfNegative(y);

    return new ComputerTarget(null, x, y);
  }

  public int? ElementIndex { get; }
  public int? X { get; }
  public int? Y { get; }
}

/// <summary>One observed accessibility element: a sparse index addressing the
///     app's latest observation, the element's role and kind, title/value,
///     bounds [x, y, width, height], state flags, the actions it advertises
///     (ubiquitous default presses filtered by the renderer), and sampled
///     container counts. Mirrors the capture_app element table field-for-field.
///     </summary>
public sealed record ComputerElement(
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
    int? ChildrenOffset);

/// <summary>Reference to a registered screenshot frame: the delivered raster's
///     id and pixel size. Coordinate targets resolve through the registry.
///     </summary>
public sealed record ComputerFrameRef(string FrameId, int Width, int Height);

/// <summary>The outcome of one IComputerAccess call: an observation, an action
///     receipt, or a typed failure carried as a value — the seam returns errors,
///     it never throws domain errors.</summary>
public abstract record ComputerOutcome
{
  /// <summary>An observe result: the rendered tree text, the ledger state id, the
  ///     full element table, an optional frame reference for coordinate targets,
  ///     an optional withheld-screenshot reason (the ACL already rendered it into
  ///     the tree text), and an optional screenshot re-encoded for the model.
  ///     </summary>
  public sealed record Observation(
      string TreeText,
      string StateId,
      IReadOnlyList<ComputerElement> Elements,
      ComputerFrameRef? Frame,
      string? WithheldReason,
      ToolResultImage? Screenshot) : ComputerOutcome;

  /// <summary>An action result: whether the action was actually sent, the dispatch
  ///     status (accepted | possibly_sent — delivery-state honesty), an optional
  ///     effect-evidence marker, and an optional post-action tree when the command
  ///     requested state.</summary>
  public sealed record Receipt(
      bool ActionSent,
      string DispatchStatus,
      string? EffectEvidence,
      string? TreeText) : ComputerOutcome;

  /// <summary>A typed failure value: one of the ComputerErrorCodes surface codes
  ///     plus a human-readable message. The tool renders the standard
  ///     'Error [Code]: message' block plus the retry hint line.</summary>
  public sealed record Failure(string Code, string Message) : ComputerOutcome;
}
