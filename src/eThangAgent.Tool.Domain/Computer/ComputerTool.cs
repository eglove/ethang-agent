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
    "locate_image", "read_text",
  ];

  /// <inheritdoc />
  public ToolDefinition Definition { get; } = new(
      "computer",
      BuildDescription(),
      [
        new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
        new ToolParameter("action", ToolParameterType.Text,
            "Exactly one of list_apps, list_windows, observe, click, drag, scroll, type, set_value, select_text, key, paste, perform_action, stop (case-sensitive)."),
        new ToolParameter("app_ref", ToolParameterType.ObjectValue,
            "Object selecting the app: exactly one of name (string), pid (integer), or aumid (string), optionally plus window_id (integer). Required for list_windows and observe; optional elsewhere. The bare-string shorthand is not accepted."),
        new ToolParameter("target", ToolParameterType.ObjectValue,
            "{type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}. Required for click, scroll, set_value, select_text, perform_action; optional for type and paste."),
        new ToolParameter("include_screenshot", ToolParameterType.Flag,
            "Optional, observe only (default false): capture a screenshot of the window."),
        new ToolParameter("disable_diffing", ToolParameterType.Flag,
            "Optional, observe only (default false): skip tree diffing and return the full tree."),
        new ToolParameter("force", ToolParameterType.Flag,
            "Optional, observe only (default false): always re-scan and return the full tree, whatever the previous observation was. " +
            "Same effect as disable_diffing; use it when a delta or no-change answer looks wrong."),
        new ToolParameter("offset", ToolParameterType.WholeNumber,
            "Optional, observe only (default 0): the element index to start this page at. The tree text's paging line names the offset that reaches the next page."),
        new ToolParameter("limit", ToolParameterType.WholeNumber,
            "Optional, observe only (default 400, max 2000): how many elements this page carries. A larger tree is paged, never silently cut."),
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
        new ToolParameter("from_target", ToolParameterType.ObjectValue,
            "Required, drag only: the drag start target, same shape as target."),
        new ToolParameter("to", ToolParameterType.ObjectValue,
            "Required, drag only: the drag end target, same shape as target."),
        new ToolParameter("scroll_direction", ToolParameterType.Text,
            "Required, scroll only: up | down | left | right (case-sensitive)."),
        new ToolParameter("scroll_amount", ToolParameterType.WholeNumber,
            "Required, scroll only: integer 0..100 (scroll pages). Never clamped."),
        new ToolParameter("text", ToolParameterType.Text,
            "Required for type, select_text, key, and paste: the characters to type, the text to select, a key name or X-keysym chord, or the paste content. Non-empty. " +
            "Key names (case-insensitive): a-z, 0-9, F1-F24, Return, Enter, Tab, Escape, Esc, Backspace, Delete, Del, Insert, Home, End, PageUp, Prior, PageDown, Next, Up, Down, Left, Right, Space. " +
            "Modifiers join with '+' and come first: ctrl, control, alt, shift, win, super, meta (e.g. ctrl+shift+Tab, alt+F4). One key per call; repeat and hold_seconds repeat it."),
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
        new ToolParameter("template_path", ToolParameterType.Text,
            "Required, locate_image only: the image file to find inside the window's raster. The template needs visual variation (a shape, icon, or text); a flat patch is refused."),
        new ToolParameter("min_score", ToolParameterType.WholeNumber,
            "Optional, locate_image only: the match threshold 0..1 (default 0.8). Never clamped."),
        new ToolParameter("region", ToolParameterType.ObjectValue,
            "Optional, observe and read_text: {x, y, width, height} in DELIVERED-RASTER pixels. " +
            "observe crops the screenshot to that rect and resamples it up to fill the delivery ceiling, so text too small to read at whole-window scale arrives legible; " +
            "read_text restricts the OCR to that area. Reported bounds stay in the full raster's space."),
      ],
      [ToolTimeout.ParameterName, "action"]);

  /// <summary>The description IS the contract: every action, key, range, output
  ///     annotation line, error code, and retry hint appears verbatim so the model
  ///     never guesses what it is looking at.</summary>
  private static string BuildDescription()
  {
    StringBuilder d = new();
    _ = d.Append("Control the desktop: list running apps, list or observe an app's windows,")
        .Append(" click, drag, scroll, type, set values, select text, press keys, paste, invoke element actions, locate an image, read text from a raster, or stop computer control.")
        .Append(" timeoutSeconds is mandatory. action is exactly one of ")
        .Append(string.Join(", ", ActionNames))
        .Append(" (case-sensitive).")
        .Append(" app_ref is an object with exactly one of name (string), pid (integer; pid must be positive), or aumid (string), optionally plus window_id (integer; window_id must be positive);")
        .Append(" it is required for list_windows and observe. target is an object: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>};")
        .Append(" it is required for click, scroll, set_value, select_text, and perform_action, optional for type and paste.")
        .Append(" Observe accepts include_screenshot, disable_diffing, force, offset, limit, and region (force always re-scans and returns the full tree); stop accepts reason; drag takes from_target and to; perform_action takes action_name;")
        .Append(" select_text takes prefix, suffix, and selection_type; key takes repeat and hold_seconds; paste takes format; click takes mouse_button, click_count, and modifiers.")
        .Append(" locate_image takes template_path (an image file to find inside the window's raster) and min_score (0..1, default 0.8);")
        .Append(" read_text takes an optional region {x, y, width, height} in raster pixels.")
        .Append(" These two are the vision path for surfaces that expose pixels instead of an accessibility tree - a remote desktop, a video frame, a drawing:")
        .Append(" they render one line per finding carrying x, y, width, height, centre, and score (matches) or confidence (words), all in RASTER pixels - the same space a coordinate target accepts, so a reported centre is usable as-is.")
        .Append(" A template with no visual variation is refused rather than matched everywhere.")
        .Append(" Every input action naming an app_ref brings that window to the foreground first, automatically: injection reaches whatever window holds focus, so the tool focuses the target rather than requiring the caller to ask.")
        .Append(" Uploading a file through a native Open dialog: observe the dialog, set_value the 'File name:' edit to the absolute path (the receipt is verified against what the element reads back),")
        .Append(" then click the Open button by element index. Both steps are UIA operations on the dialog itself, so neither depends on which window holds focus; type and Return do, and are the fallback only when the edit exposes no Value pattern.")
        .Append(" Ranges, never clamped: scroll_amount 0..100, click_count >= 1, repeat 1..100, hold_seconds 0..30.")
        .Append(" Membership: mouse_button left|right|middle; modifiers plus-joined ctrl|alt|shift|win; strategy auto|a11y|event;")
        .Append(" return_state compact|full|none; selection_type text|cursor_before|cursor_after; paste format text|md|html; scroll_direction up|down|left|right.")
        .Append(" Key names are case-insensitive: a-z, 0-9, F1-F24, punctuation, Return, Enter, Tab, Escape, Esc, Backspace, Delete, Del, Insert, Home, End, PageUp, Prior, PageDown, Next, Up, Down, Left, Right, Space;")
        .Append(" punctuation resolves through the active keyboard layout, so '.' is the period key and '!' carries the shift it needs;")
        .Append(" modifiers ctrl|control|alt|shift|win|super|meta join with '+' and come first (ctrl+shift+Tab). One key per call.")
        .Append(" Observe shows one page of the element tree: 400 elements by default, starting at offset. When elements exist beyond the page, the tree text ends with a `paging:` line naming the total and the offset that reaches the next page - so a large tree is walked page by page and never silently cut. A tree deeper than the walk's depth cap says so in that line, and elements below that depth are not reachable by paging.")
        .Append(" Output: observe renders the element tree text as content; with include_screenshot true the screenshot is attached as an image when the model has image input,")
        .Append(" otherwise the line `[computer] screenshot withheld: model has no image input` is appended (the capture is still requested).")
        .Append(" A screenshot is downscaled so its longest edge is at most 1280 pixels, and the tree header's `coordinates:` line states the contract exactly:")
        .Append(" a coordinate target takes DELIVERED-RASTER pixels - the pixel you read off the image, never multiplied by anything. When the text in a region is too small to read, observe again with region={x,y,width,height} to get that area zoomed.")
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
      ComputerOutcome.VisionResult seen => RenderVision(v, seen),
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

  /// <summary>A vision result: one line per finding, each carrying the raster-pixel box
  ///     the model needs to address it. A locate_image with no match says so explicitly
  ///     rather than rendering an empty success, because "not found" is the answer.
  ///     </summary>
  private static ToolResult RenderVision(ComputerToolInput v, ComputerOutcome.VisionResult result)
  {
    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"[computer] ok {ActionName(v.Action)} raster={result.RasterWidth}x{result.RasterHeight}");
    if (v.Action == ComputerAction.LocateImage)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $" matches={result.Matches.Count}");
      foreach (VisionMatch match in result.Matches)
      {
        _ = sb.Append(CultureInfo.InvariantCulture,
            $"\nmatch x={match.Bounds.X} y={match.Bounds.Y} width={match.Bounds.Width} height={match.Bounds.Height} centre=({match.Bounds.Centre.X},{match.Bounds.Centre.Y}) score={match.Score:0.###}");
      }

      if (result.Matches.Count == 0)
      {
        _ = sb.Append("\n[computer] no match at or above the threshold: the template is not in this raster, or the threshold is too high.");
      }
    }
    else
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $" words={result.Words.Count}");
      foreach (VisionWord word in result.Words)
      {
        _ = sb.Append(CultureInfo.InvariantCulture,
            $"\nword \"{word.Text}\" x={word.Bounds.X} y={word.Bounds.Y} width={word.Bounds.Width} height={word.Bounds.Height} centre=({word.Bounds.Centre.X},{word.Bounds.Centre.Y}) confidence={word.Confidence:0.##}");
      }

      if (result.Words.Count == 0)
      {
        _ = sb.Append("\n[computer] no text was recognised in this raster or region.");
      }
    }

    _ = sb.Append("\n[computer] bounds are raster pixels: convert to a screen coordinate with the factor the observe header states.");
    return new ToolResult(sb.ToString(), false);
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

  private static string ActionName(ComputerAction action) => ActionNames[(int)action];

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
