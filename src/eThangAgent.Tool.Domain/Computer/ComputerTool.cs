using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The 'computer' tool (task-11): the model's single door to desktop
///     automation. Input is parsed strictly by <see cref="ComputerToolInput"/>,
///     mapped onto the task-9 <see cref="ComputerCommand"/> records, executed
///     through <see cref="IComputerAccess"/>, and rendered on the fixed output
///     contract: observation trees verbatim, one ok line per action, screenshots
///     attached only when the model can see them, unchanged-effect advisories, and
///     typed failure blocks carrying the seam's retry hint. The seam never throws
///     domain errors - failures arrive as <see cref="ComputerOutcome.Failure"/>
///     values and are rendered, not caught.</summary>
public sealed class ComputerTool(IComputerAccess access, IImageInputCapability vision) : ITool
{
  private readonly IComputerAccess _access = access ?? throw new ArgumentNullException(nameof(access));
  private readonly IImageInputCapability _vision = vision ?? throw new ArgumentNullException(nameof(vision));

  /// <summary>The wire name of every admitted action, in advertisement order.</summary>
  private static readonly string[] ActionNames =
  [
    "list_apps", "list_windows", "observe", "click", "drag", "scroll", "type",
    "set_value", "select_text", "key", "paste", "perform_action", "stop",
  ];

  /// <inheritdoc />
  public ToolDefinition Definition { get; } = new(
      "computer",
      BuildDescription(),
      [
        new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
        new ToolParameter("action", ToolParameterType.Text,
            "Exactly one of list_apps, list_windows, observe, click, drag, scroll, type, set_value, select_text, key, paste, perform_action, stop (case-sensitive)."),
        new ToolParameter("app_ref", ToolParameterType.Text,
            "Object selecting the app: exactly one of name (string), pid (integer), or aumid (string), optionally plus window_id (integer). Required for list_windows and observe; optional elsewhere. The bare-string shorthand is not accepted."),
        new ToolParameter("target", ToolParameterType.Text,
            "{type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}. Required for click, scroll, set_value, select_text, perform_action; optional for type and paste."),
        new ToolParameter("include_screenshot", ToolParameterType.Flag,
            "Optional, observe only (default false): capture a screenshot of the window."),
        new ToolParameter("disable_diffing", ToolParameterType.Flag,
            "Optional, observe only (default false): skip tree diffing and return the full tree."),
        new ToolParameter("mouse_button", ToolParameterType.Text,
            "Optional, click only: left | right | middle (default left)."),
        new ToolParameter("click_count", ToolParameterType.WholeNumber,
            "Optional, click only: integer >= 1 (default 1). Never clamped."),
        new ToolParameter("modifiers", ToolParameterType.Text,
            "Optional, click and drag: plus-joined ctrl|alt|shift|win tokens, e.g. ctrl+shift (default none)."),
        new ToolParameter("strategy", ToolParameterType.Text,
            "Optional: auto | a11y | event (default auto). Accepted on click, scroll, type, set_value, and key."),
        new ToolParameter("return_state", ToolParameterType.Text,
            "Optional: compact | full | none (default none) - request the post-action element tree. Accepted on every action except list_apps, list_windows, observe, and stop."),
        new ToolParameter("from_target", ToolParameterType.Text,
            "Required, drag only: the drag start target, same shape as target."),
        new ToolParameter("to", ToolParameterType.Text,
            "Required, drag only: the drag end target, same shape as target."),
        new ToolParameter("scroll_direction", ToolParameterType.Text,
            "Required, scroll only: up | down | left | right (case-sensitive)."),
        new ToolParameter("scroll_amount", ToolParameterType.WholeNumber,
            "Required, scroll only: integer 0..100 (scroll pages). Never clamped."),
        new ToolParameter("text", ToolParameterType.Text,
            "Required for type, select_text, key, and paste: the characters to type, the text to select, a key name (a, Return, Tab, Up) or X-keysym chord (Control_L+a), or the paste content. Non-empty."),
        new ToolParameter("value", ToolParameterType.Text,
            "Required, set_value only: the replacement value, non-empty."),
        new ToolParameter("prefix", ToolParameterType.Text,
            "Optional, select_text only: extend the selection forward by this text."),
        new ToolParameter("suffix", ToolParameterType.Text,
            "Optional, select_text only: extend the selection backward by this text."),
        new ToolParameter("selection_type", ToolParameterType.Text,
            "Optional, select_text only: text | cursor_before | cursor_after (default text)."),
        new ToolParameter("repeat", ToolParameterType.WholeNumber,
            "Optional, key only: integer 1..100 (default 1). Never clamped."),
        new ToolParameter("hold_seconds", ToolParameterType.WholeNumber,
            "Optional, key only: number 0..30 (default none). Never clamped."),
        new ToolParameter("format", ToolParameterType.Text,
            "Optional, paste only: text | md | html (default text)."),
        new ToolParameter("action_name", ToolParameterType.Text,
            "Required, perform_action only: the element action to invoke, non-empty; must be one the target element advertises (checked at execution time)."),
        new ToolParameter("reason", ToolParameterType.Text,
            "Optional, stop only: why computer control is being released."),
      ],
      [ToolTimeout.ParameterName, "action"]);

  /// <summary>The description IS the contract: every action, key, range, output
  ///     annotation line, error code, and retry hint appears verbatim so the model
  ///     never guesses what it is looking at.</summary>
  private static string BuildDescription()
  {
    StringBuilder d = new();
    _ = d.Append("Control the desktop: list running apps, list or observe an app's windows,")
        .Append(" click, drag, scroll, type, set values, select text, press keys, paste, invoke element actions, or stop computer control.")
        .Append(" timeoutSeconds is mandatory. action is exactly one of ")
        .Append(string.Join(", ", ActionNames))
        .Append(" (case-sensitive).")
        .Append(" app_ref is an object with exactly one of name (string), pid (integer; pid must be positive), or aumid (string), optionally plus window_id (integer; window_id must be positive);")
        .Append(" it is required for list_windows and observe. target is an object: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>};")
        .Append(" it is required for click, scroll, set_value, select_text, and perform_action, optional for type and paste.")
        .Append(" Observe accepts include_screenshot and disable_diffing; stop accepts reason; drag takes from_target and to; perform_action takes action_name;")
        .Append(" select_text takes prefix, suffix, and selection_type; key takes repeat and hold_seconds; paste takes format; click takes mouse_button, click_count, and modifiers.")
        .Append(" Ranges, never clamped: scroll_amount 0..100, click_count >= 1, repeat 1..100, hold_seconds 0..30.")
        .Append(" Membership: mouse_button left|right|middle; modifiers plus-joined ctrl|alt|shift|win; strategy auto|a11y|event;")
        .Append(" return_state compact|full|none; selection_type text|cursor_before|cursor_after; paste format text|md|html; scroll_direction up|down|left|right.")
        .Append(" Output: observe renders the element tree text as content; with include_screenshot true the screenshot is attached as an image when the model has image input,")
        .Append(" otherwise the line `[computer] screenshot withheld: model has no image input` is appended (the capture is still requested).")
        .Append(" Every other action renders one line `[computer] ok <action> action_sent=<true|false> dispatch=<accepted|possibly_sent>`;")
        .Append(" when the command requested a post-action state and the receipt carries a tree, that tree is appended;")
        .Append(" when a click, drag, or scroll reports effect_evidence=unchanged, the line `[computer] effect_evidence=unchanged - the app state is byte-identical; do not repeat the same coordinate; prefer an element index or keyboard` is appended.")
        .Append(" Failures render `Error [Code]: <message>` plus a `retry=<hint>` line where hint is reobserve (ELEMENT_UNAVAILABLE, STALE_STATE - the desktop moved, observe first),")
        .Append(" never (CONTROLLER_BUSY, CONTROL_STOPPED, NOT_SETTABLE, NOT_SELECTABLE, ACTION_UNAVAILABLE, VERSION_MISMATCH - repeating cannot help), or retry (everything else).")
        .Append(" Error codes: APP_NOT_FOUND, AMBIGUOUS_APP, ELEMENT_UNAVAILABLE, STALE_STATE, NOT_SETTABLE, NOT_SELECTABLE, ACTION_UNAVAILABLE, FOREGROUND_REQUIRED,")
        .Append(" CONTROLLER_BUSY, CONTROL_STOPPED, HELPER_UNAVAILABLE, VERSION_MISMATCH, TIMEOUT, INVALID_APP, LAUNCH_FAILED, INTERNAL. Parser errors use Error [Code]: too.");
    return d.ToString();
  }

  /// <inheritdoc />
  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ComputerToolInput> parsed = ComputerToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => RunAsync(parsed.Value, token), ct);
  }

  private async Task<ToolResult> RunAsync(ComputerToolInput v, CancellationToken ct)
  {
    ComputerOutcome outcome = await _access.ExecuteAsync(v.ToCommand(), ct).ConfigureAwait(false);
    return outcome switch
    {
      ComputerOutcome.Observation observation => RenderObservation(v, observation),
      ComputerOutcome.Receipt receipt => RenderReceipt(v, receipt),
      ComputerOutcome.Failure failure => RenderFailure(failure),
      _ => throw new InvalidOperationException("unreachable: outcome is a validated record family"),
    };
  }

  /// <summary>Observe: the tree text verbatim; the screenshot attaches only when the
  ///     model has image input, and a requested-but-unseeable capture appends the
  ///     withhold notice. The capture is requested from the seam regardless.</summary>
  private ToolResult RenderObservation(ComputerToolInput v, ComputerOutcome.Observation observation)
  {
    bool requested = v.Action == ComputerAction.Observe && v.IncludeScreenshot == true;
    string content = observation.TreeText;
    if (requested && !_vision.AcceptsImages)
    {
      content += "\n[computer] screenshot withheld: model has no image input";
    }

    IReadOnlyList<ToolResultImage>? images =
        observation.Screenshot is ToolResultImage shot && _vision.AcceptsImages ? [shot] : null;
    return new ToolResult(content, false, Images: images);
  }

  /// <summary>A seam failure: the standard error block plus the code's retry hint
  ///     line - the model reads the hint verbatim to plan its next move.</summary>
  private static ToolResult RenderFailure(ComputerOutcome.Failure failure) => new(
      $"Error [{failure.Code}]: {failure.Message}\nretry={ComputerErrorCodes.RetryHint(failure.Code)}", true);

  /// <summary>Actions: one ok line, then the unchanged-effect advisory for pointer
  ///     actions, then the post-action tree when one was requested and delivered.</summary>
  private static ToolResult RenderReceipt(ComputerToolInput v, ComputerOutcome.Receipt receipt)
  {
    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture,
          $"[computer] ok {ActionName(v.Action)} action_sent={(receipt.ActionSent ? "true" : "false")} dispatch={receipt.DispatchStatus}");

    if (receipt.EffectEvidence == "unchanged" && IsPointer(v.Action))
    {
      _ = sb.Append("\n[computer] effect_evidence=unchanged - the app state is byte-identical; do not repeat the same coordinate; prefer an element index or keyboard");
    }

    if (RequestedPostState(v) && receipt.TreeText is not null)
    {
      _ = sb.Append('\n').Append(receipt.TreeText);
    }

    return new ToolResult(sb.ToString(), false);
  }

  private static bool IsPointer(ComputerAction action) => action is ComputerAction.Click or ComputerAction.Drag or ComputerAction.Scroll;

  private static bool RequestedPostState(ComputerToolInput v) => v.ReturnState is not (null or "none");

  private static string ActionName(ComputerAction action) => action switch
  {
    ComputerAction.ListApps => "list_apps",
    ComputerAction.ListWindows => "list_windows",
    ComputerAction.Observe => "observe",
    ComputerAction.Click => "click",
    ComputerAction.Drag => "drag",
    ComputerAction.Scroll => "scroll",
    ComputerAction.Type => "type",
    ComputerAction.SetValue => "set_value",
    ComputerAction.SelectText => "select_text",
    ComputerAction.Key => "key",
    ComputerAction.Paste => "paste",
    ComputerAction.PerformAction => "perform_action",
    ComputerAction.Stop => "stop",
    _ => throw new InvalidOperationException("unreachable: action is a validated enum value"),
  };

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
