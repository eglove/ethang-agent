using System.Collections.Frozen;
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strictly parsed arguments for the 'computer' tool (task-10 contract):
///     a required action switch (exactly one of thirteen names, case-sensitive -
///     absent or unknown fails InvalidAction), per-action required and optional
///     keys, the app_ref / target sub-objects, and every range checked with NO
///     clamping - an out-of-range value is a typed error, never a silent snap.
///     <para>The element action of 'perform_action' travels under the key
///     'action_name': the tool's flat JSON object already uses 'action' for the
///     action switch, and duplicate keys are not valid JSON.</para></summary>
public sealed record ComputerToolInput(
    ComputerAction Action,
    ComputerAppRef? AppRef,
    bool? IncludeScreenshot,
    bool? DisableDiffing,
    ComputerTarget? Target,
    ComputerTarget? From,
    ComputerTarget? To,
    int? ScrollAmount,
    string? ScrollDirection,
    string? Text,
    string? Value,
    string? Prefix,
    string? Suffix,
    string? SelectionType,
    int? Repeat,
    double? HoldSeconds,
    string? Format,
    string? ActionName,
    string? MouseButton,
    int? ClickCount,
    string? Modifiers,
    string? Strategy,
    string? ReturnState,
    string? Reason)
{

  /// <summary>Parses raw JSON arguments into validated input on the strict
  ///     tool-parser contract: unknown keys, wrong kinds, missing required keys,
  ///     and out-of-range values are typed errors; nothing is coerced.</summary>
  public static Result<ComputerToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    Result<ComputerAction> action = ParseAction(baseParse.Value);
    if (!action.IsSuccess)
    {
      return Fail(action.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(baseParse.Value, Allowed(action.Value));
    return unknown is not null
      ? Fail(unknown)
      : ParseFields(action.Value, baseParse.Value);
  }

  /// <summary>Reads and validates the required 'action' switch: present, a JSON
  ///     string, and one of the thirteen admitted names (case-sensitive).</summary>
  private static Result<ComputerAction> ParseAction(JsonElement json)
    => json.TryGetProperty("action", out JsonElement actionEl) ? TypedAction(actionEl) : MissingAction();

  private static Result<ComputerAction> TypedAction(JsonElement actionEl)
    => actionEl.ValueKind == JsonValueKind.String ? MapAction(actionEl.GetString()!) : NotAString(actionEl);

  private static Result<ComputerAction> MissingAction() => Result.Failure<ComputerAction>(new DomainError("InvalidAction",
      "'action' is required: exactly one of list_apps, list_windows, observe, click, drag, scroll, type, set_value, select_text, key, paste, perform_action, or stop (case-sensitive)."));

  private static Result<ComputerAction> NotAString(JsonElement actionEl) => Result.Failure<ComputerAction>(
      new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));

  /// <summary>Maps the action name onto the enum; an unknown name is an
  ///     InvalidAction typed error.</summary>
  private static Result<ComputerAction> MapAction(string actionText) =>
    ActionMap.TryGetValue(actionText, out ComputerAction action)
        ? Result.Success(action)
        : Result.Failure<ComputerAction>(new DomainError("InvalidAction",
            $"'action' must be exactly one of list_apps, list_windows, observe, click, drag, scroll, type, set_value, select_text, key, paste, perform_action, or stop (case-sensitive; got '{actionText}')."));

  private static readonly FrozenDictionary<string, ComputerAction> ActionMap = new Dictionary<string, ComputerAction>(StringComparer.Ordinal)
  {
    ["list_apps"] = ComputerAction.ListApps,
    ["list_windows"] = ComputerAction.ListWindows,
    ["observe"] = ComputerAction.Observe,
    ["click"] = ComputerAction.Click,
    ["drag"] = ComputerAction.Drag,
    ["scroll"] = ComputerAction.Scroll,
    ["type"] = ComputerAction.Type,
    ["set_value"] = ComputerAction.SetValue,
    ["select_text"] = ComputerAction.SelectText,
    ["key"] = ComputerAction.Key,
    ["paste"] = ComputerAction.Paste,
    ["perform_action"] = ComputerAction.PerformAction,
    ["stop"] = ComputerAction.Stop,
  }.ToFrozenDictionary();

  /// <summary>The allowed argument keys per action (timeoutSeconds belongs to
  ///     every tool call). The list is echoed verbatim on UnknownParameter errors.
  ///     </summary>
  private static string[] Allowed(ComputerAction action) => AllowedMap[action];

  private static readonly FrozenDictionary<ComputerAction, string[]> AllowedMap = new Dictionary<ComputerAction, string[]>
  {
    [ComputerAction.ListApps] = ["action", ToolTimeout.ParameterName],
    [ComputerAction.ListWindows] = ["action", ToolTimeout.ParameterName, "app_ref"],
    [ComputerAction.Observe] = ["action", ToolTimeout.ParameterName, "app_ref", "include_screenshot", "disable_diffing"],
    [ComputerAction.Click] = ["action", ToolTimeout.ParameterName, "target", "app_ref", "mouse_button", "click_count", "modifiers", "strategy", "return_state"],
    [ComputerAction.Drag] = ["action", ToolTimeout.ParameterName, "from_target", "to", "app_ref", "modifiers", "return_state"],
    [ComputerAction.Scroll] = ["action", ToolTimeout.ParameterName, "target", "scroll_direction", "scroll_amount", "app_ref", "strategy", "return_state"],
    [ComputerAction.Type] = ["action", ToolTimeout.ParameterName, "text", "target", "app_ref", "strategy", "return_state"],
    [ComputerAction.SetValue] = ["action", ToolTimeout.ParameterName, "target", "value", "app_ref", "strategy", "return_state"],
    [ComputerAction.SelectText] = ["action", ToolTimeout.ParameterName, "target", "text", "prefix", "suffix", "selection_type", "app_ref", "return_state"],
    [ComputerAction.Key] = ["action", ToolTimeout.ParameterName, "text", "repeat", "hold_seconds", "app_ref", "strategy", "return_state"],
    [ComputerAction.Paste] = ["action", ToolTimeout.ParameterName, "text", "format", "target", "app_ref", "return_state"],
    [ComputerAction.PerformAction] = ["action", ToolTimeout.ParameterName, "target", "action_name", "app_ref", "return_state"],
    [ComputerAction.Stop] = ["action", ToolTimeout.ParameterName, "reason"],
  }.ToFrozenDictionary();

  /// <summary>Builds the seam command, applying the documented defaults for
  ///     every absent optional key. Only a successful parse reaches this.</summary>
  public ComputerCommand ToCommand() => CommandBuilders[Action](this);

  private static readonly FrozenDictionary<ComputerAction, Func<ComputerToolInput, ComputerCommand>> CommandBuilders
      = new Dictionary<ComputerAction, Func<ComputerToolInput, ComputerCommand>>
      {
        [ComputerAction.ListApps] = _ => new ComputerCommand.ListApps(),
        [ComputerAction.ListWindows] = v => new ComputerCommand.ListWindows(v.AppRef!),
        [ComputerAction.Observe] = v => new ComputerCommand.Observe(v.AppRef!, v.IncludeScreenshot ?? false, v.DisableDiffing ?? false),
        [ComputerAction.Click] = v => new ComputerCommand.Click(v.Target!, v.AppRef, v.MouseButton ?? "left", v.ClickCount ?? 1, v.Modifiers ?? "", v.Strategy ?? "auto", v.ReturnState ?? "none"),
        [ComputerAction.Drag] = v => new ComputerCommand.Drag(v.From!, v.To!, v.AppRef, v.Modifiers ?? "", v.ReturnState ?? "none"),
        [ComputerAction.Scroll] = v => new ComputerCommand.Scroll(v.Target!, v.ScrollDirection!, v.ScrollAmount!.Value, v.AppRef, v.Strategy ?? "auto", v.ReturnState ?? "none"),
        [ComputerAction.Type] = v => new ComputerCommand.TypeText(v.Text!, v.Target, v.AppRef, v.Strategy ?? "auto", v.ReturnState ?? "none"),
        [ComputerAction.SetValue] = v => new ComputerCommand.SetValue(v.Target!, v.Value!, v.AppRef, v.Strategy ?? "auto", v.ReturnState ?? "none"),
        [ComputerAction.SelectText] = v => new ComputerCommand.SelectText(v.Target!, v.Text!, v.Prefix, v.Suffix, v.SelectionType ?? "text", v.AppRef, v.ReturnState ?? "none"),
        [ComputerAction.Key] = v => new ComputerCommand.Key(v.Text!, v.Repeat, v.HoldSeconds, v.AppRef, v.Strategy ?? "auto", v.ReturnState ?? "none"),
        [ComputerAction.Paste] = v => new ComputerCommand.Paste(v.Text!, v.Target, v.AppRef, v.Format ?? "text", v.ReturnState ?? "none"),
        [ComputerAction.PerformAction] = v => new ComputerCommand.PerformAction(v.Target!, v.ActionName!, v.AppRef, v.ReturnState ?? "none"),
        [ComputerAction.Stop] = v => new ComputerCommand.Stop(v.Reason),
      }.ToFrozenDictionary();

  /// <summary>Parses the per-action fields against the already-validated action.
  ///     Sub-objects parse first (they carry their own typed errors), then the
  ///     optional scalars, then the per-action required and range checks.</summary>
  private static Result<ComputerToolInput> ParseFields(ComputerAction action, JsonElement json)
  {
    ParsedFields f = new();
    DomainError? error = ParseSubObjects(json, f)
        ?? ParseScalars(json, f)
        ?? ParseStrings(json, f);
    if (error is not null)
    {
      return Fail(error);
    }

    ComputerToolInput input = new(action, f._appRef, f._includeScreenshot, f._disableDiffing,
        f._target, f._from, f._to, f._scrollAmount, f._scrollDirection, f._text, f._value,
        f._prefix, f._suffix, f._selectionType, f._repeat, f._holdSeconds,
        f._format, f._actionName, f._mouseButton, f._clickCount, f._modifiers,
        f._strategy, f._returnState, f._reason);

    return Validate(action, input);
  }

  // Mutable carrier for the per-action optional fields.
  private sealed class ParsedFields
  {
    public ComputerAppRef? _appRef { get; set; }
    public ComputerTarget? _target { get; set; }
    public ComputerTarget? _from { get; set; }
    public ComputerTarget? _to { get; set; }
    public bool? _includeScreenshot { get; set; }
    public bool? _disableDiffing { get; set; }
    public int? _scrollAmount { get; set; }
    public int? _clickCount { get; set; }
    public int? _repeat { get; set; }
    public double? _holdSeconds { get; set; }
    public string? _scrollDirection { get; set; }
    public string? _text { get; set; }
    public string? _value { get; set; }
    public string? _actionName { get; set; }
    public string? _reason { get; set; }
    public string? _prefix { get; set; }
    public string? _suffix { get; set; }
    public string? _selectionType { get; set; }
    public string? _format { get; set; }
    public string? _mouseButton { get; set; }
    public string? _modifiers { get; set; }
    public string? _strategy { get; set; }
    public string? _returnState { get; set; }
  }

  // Sub-objects: app_ref and the three target shapes.
  private static DomainError? ParseSubObjects(JsonElement json, ParsedFields f)
  {
    Result<ComputerAppRef?> appRef = ParseAppRefField(json);
    if (!appRef.IsSuccess)
    {
      return appRef.Error;
    }
    f._appRef = appRef.Value;

    Result<ComputerTarget?> target = ParseTargetField(json, "target");
    if (!target.IsSuccess)
    {
      return target.Error;
    }
    f._target = target.Value;

    Result<ComputerTarget?> from = ParseTargetField(json, "from_target");
    if (!from.IsSuccess)
    {
      return from.Error;
    }
    f._from = from.Value;

    Result<ComputerTarget?> to = ParseTargetField(json, "to");
    if (!to.IsSuccess)
    {
      return to.Error;
    }
    f._to = to.Value;

    return null;
  }

  // Scalar optionals: booleans, integers, and the fractional-seconds field.
  private static DomainError? ParseScalars(JsonElement json, ParsedFields f)
  {
    Result<bool?> includeScreenshot = ToolArguments.OptionalBool(json, "include_screenshot");
    if (!includeScreenshot.IsSuccess)
    {
      return includeScreenshot.Error;
    }
    f._includeScreenshot = includeScreenshot.Value;

    Result<bool?> disableDiffing = ToolArguments.OptionalBool(json, "disable_diffing");
    if (!disableDiffing.IsSuccess)
    {
      return disableDiffing.Error;
    }
    f._disableDiffing = disableDiffing.Value;

    Result<int?> scrollAmount = ToolArguments.OptionalInt(json, "scroll_amount");
    if (!scrollAmount.IsSuccess)
    {
      return scrollAmount.Error;
    }
    f._scrollAmount = scrollAmount.Value;

    Result<int?> clickCount = ToolArguments.OptionalInt(json, "click_count");
    if (!clickCount.IsSuccess)
    {
      return clickCount.Error;
    }
    f._clickCount = clickCount.Value;

    Result<int?> repeat = ToolArguments.OptionalInt(json, "repeat");
    if (!repeat.IsSuccess)
    {
      return repeat.Error;
    }
    f._repeat = repeat.Value;

    Result<double?> holdSeconds = OptionalSeconds(json, "hold_seconds");
    if (!holdSeconds.IsSuccess)
    {
      return holdSeconds.Error;
    }
    f._holdSeconds = holdSeconds.Value;

    return null;
  }

  // String and enum-by-name optionals: two halves, each within the complexity gate.
  private static DomainError? ParseStrings(JsonElement json, ParsedFields f)
    => ParseStringsFirst(json, f) ?? ParseStringsSecond(json, f);

  private static DomainError? ParseStringsFirst(JsonElement json, ParsedFields f)
  {
    Result<string?> scrollDirection = OptionalName(json, "scroll_direction");
    if (!scrollDirection.IsSuccess)
    {
      return scrollDirection.Error;
    }
    f._scrollDirection = scrollDirection.Value;

    Result<string?> text = ToolArguments.OptionalString(json, "text");
    if (!text.IsSuccess)
    {
      return text.Error;
    }
    f._text = text.Value;

    Result<string?> value = ToolArguments.OptionalString(json, "value");
    if (!value.IsSuccess)
    {
      return value.Error;
    }
    f._value = value.Value;

    Result<string?> actionName = ToolArguments.OptionalString(json, "action_name");
    if (!actionName.IsSuccess)
    {
      return actionName.Error;
    }
    f._actionName = actionName.Value;

    Result<string?> reason = ToolArguments.OptionalString(json, "reason");
    if (!reason.IsSuccess)
    {
      return reason.Error;
    }
    f._reason = reason.Value;

    return null;
  }

  private static DomainError? ParseStringsSecond(JsonElement json, ParsedFields f)
  {

    Result<string?> prefix = ToolArguments.OptionalString(json, "prefix");
    if (!prefix.IsSuccess)
    {
      return prefix.Error;
    }
    f._prefix = prefix.Value;

    Result<string?> suffix = ToolArguments.OptionalString(json, "suffix");
    if (!suffix.IsSuccess)
    {
      return suffix.Error;
    }
    f._suffix = suffix.Value;

    Result<string?> selectionType = OptionalName(json, "selection_type");
    if (!selectionType.IsSuccess)
    {
      return selectionType.Error;
    }
    f._selectionType = selectionType.Value;

    Result<string?> format = OptionalName(json, "format");
    if (!format.IsSuccess)
    {
      return format.Error;
    }
    f._format = format.Value;

    Result<string?> mouseButton = OptionalName(json, "mouse_button");
    if (!mouseButton.IsSuccess)
    {
      return mouseButton.Error;
    }
    f._mouseButton = mouseButton.Value;

    Result<string?> modifiers = ToolArguments.OptionalString(json, "modifiers");
    if (!modifiers.IsSuccess)
    {
      return modifiers.Error;
    }
    f._modifiers = modifiers.Value;

    Result<string?> strategy = OptionalName(json, "strategy");
    if (!strategy.IsSuccess)
    {
      return strategy.Error;
    }
    f._strategy = strategy.Value;

    Result<string?> returnState = OptionalName(json, "return_state");
    if (!returnState.IsSuccess)
    {
      return returnState.Error;
    }
    f._returnState = returnState.Value;

    return null;
  }

  /// <summary>Optional app_ref: null when absent; the sub-object's typed error
  ///     when present but malformed.</summary>
  private static Result<ComputerAppRef?> ParseAppRefField(JsonElement json)
  {
    if (!Has(json, "app_ref"))
    {
      return Result.Success<ComputerAppRef?>(null);
    }

    Result<ComputerAppRef> parsed = ParseAppRef(json);
    return parsed.IsSuccess
      ? Result.Success<ComputerAppRef?>(parsed.Value)
      : Result.Failure<ComputerAppRef?>(parsed.Error);
  }

  /// <summary>Optional target-shaped sub-object: null when absent; the
  ///     sub-object's typed error when present but malformed.</summary>
  private static Result<ComputerTarget?> ParseTargetField(JsonElement json, string key)
  {
    if (!Has(json, key))
    {
      return Result.Success<ComputerTarget?>(null);
    }

    Result<ComputerTarget> parsed = ParseTarget(json, key);
    return parsed.IsSuccess
      ? Result.Success<ComputerTarget?>(parsed.Value)
      : Result.Failure<ComputerTarget?>(parsed.Error);
  }

  /// <summary>Per-action required-key, membership, and range validation. Runs
  ///     after every field parsed cleanly, so messages name one problem at a time.
  ///     Ranges come from the tool contract: scroll 0..100, repeat 1..100, hold
  ///     seconds 0..30, click count >= 1 - violations are typed errors, never
  ///     clamped.</summary>
  private static Result<ComputerToolInput> Validate(ComputerAction action, ComputerToolInput input) =>
    Validators[action](input);

  private static readonly FrozenDictionary<ComputerAction, Func<ComputerToolInput, Result<ComputerToolInput>>> Validators
      = new Dictionary<ComputerAction, Func<ComputerToolInput, Result<ComputerToolInput>>>
      {
        [ComputerAction.ListApps] = _ => Result.Success(_),
        [ComputerAction.Stop] = _ => Result.Success(_),
        [ComputerAction.ListWindows] = ValidateListWindows,
        [ComputerAction.Observe] = ValidateObserve,
        [ComputerAction.Click] = ValidateClickAction,
        [ComputerAction.Drag] = ValidateDrag,
        [ComputerAction.Scroll] = ValidateScroll,
        [ComputerAction.Type] = ValidateType,
        [ComputerAction.SetValue] = ValidateSetValue,
        [ComputerAction.SelectText] = ValidateSelectText,
        [ComputerAction.Key] = ValidateKey,
        [ComputerAction.Paste] = ValidatePaste,
        [ComputerAction.PerformAction] = ValidatePerformAction,
      }.ToFrozenDictionary();

  private static Result<ComputerToolInput> ValidateListWindows(ComputerToolInput input) => Required(input, input.AppRef is not null, "app_ref",
      "The 'list_windows' action requires app_ref: an object with exactly one of name (string), pid (integer), or aumid (string), optionally plus window_id (integer).");

  private static Result<ComputerToolInput> ValidateObserve(ComputerToolInput input) => Required(input, input.AppRef is not null, "app_ref",
      "The 'observe' action requires app_ref: an object with exactly one of name (string), pid (integer), or aumid (string), optionally plus window_id (integer).");

  private static Result<ComputerToolInput> ValidateClickAction(ComputerToolInput input) => Required(input, input.Target is not null, "target",
      "The 'click' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
      .Bind(_ => ValidateClick(input));

  private static Result<ComputerToolInput> ValidateDrag(ComputerToolInput input) => Required(input, input.From is not null, "from_target",
      "The 'drag' action requires from_target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
      .Bind(_ => Required(input, input.To is not null, "to",
      "The 'drag' action requires to: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}."))
      .Bind(_ => ValidateModifiers(input))
      .Bind(_ => ReturnStateCheck(input));

  private static Result<ComputerToolInput> ValidateScroll(ComputerToolInput input) => Required(input, input.Target is not null, "target",
      "The 'scroll' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
      .Bind(_ => Required(input, input.ScrollDirection is not null, "scroll_direction",
      "The 'scroll' action requires scroll_direction: exactly one of up, down, left, or right (case-sensitive)."))
      .Bind(_ => Check(input, input.ScrollDirection is null or "up" or "down" or "left" or "right",
          ToolErrorCodes.InvalidParameterValue, $"'scroll_direction' must be exactly one of up, down, left, right (case-sensitive), but got '{input.ScrollDirection}'."))
      .Bind(_ => Required(input, input.ScrollAmount is not null, "scroll_amount",
      "The 'scroll' action requires scroll_amount: an integer 0..100 (scroll pages)."))
      .Bind(_ => Check(input, input.ScrollAmount is null or (>= 0 and <= 100),
          ToolErrorCodes.InvalidParameterValue, $"'scroll_amount' must be 0..100 (got {input.ScrollAmount}); values are never clamped."))
      .Bind(_ => DispatchChecks(input));

  private static Result<ComputerToolInput> ValidateType(ComputerToolInput input) => Required(input, input.Text is not null, "text",
      "The 'type' action requires text: the characters to type, non-empty.")
      .Bind(_ => Check(input, input.Text == null || input.Text.Length > 0,
          ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."))
      .Bind(_ => DispatchChecks(input));

  private static Result<ComputerToolInput> ValidateSetValue(ComputerToolInput input) => Required(input, input.Target is not null, "target",
      "The 'set_value' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
      .Bind(_ => Required(input, input.Value is not null, "value",
      "The 'set_value' action requires value: the replacement value, non-empty."))
      .Bind(_ => Check(input, input.Value == null || input.Value.Length > 0,
          ToolErrorCodes.InvalidParameterValue, "'value' must be non-empty."))
      .Bind(_ => DispatchChecks(input));

  private static Result<ComputerToolInput> ValidateSelectText(ComputerToolInput input) => Required(input, input.Target is not null, "target",
      "The 'select_text' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
      .Bind(_ => Required(input, input.Text is not null, "text",
      "The 'select_text' action requires text: the text to select, non-empty."))
      .Bind(_ => Check(input, input.Text == null || input.Text.Length > 0,
          ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."))
      .Bind(_ => Check(input, input.SelectionType is null or "text" or "cursor_before" or "cursor_after",
          ToolErrorCodes.InvalidParameterValue, $"'selection_type' must be exactly one of text, cursor_before, cursor_after (case-sensitive), but got '{input.SelectionType}'."))
      .Bind(_ => ReturnStateCheck(input));

  private static Result<ComputerToolInput> ValidateKey(ComputerToolInput input) => Required(input, input.Text is not null, "text",
      "The 'key' action requires text: a key name (a, Return, Tab, Up) or X-keysym chord (Control_L+a).")
      .Bind(_ => Check(input, input.Text == null || input.Text.Length > 0,
          ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."))
      .Bind(_ => Check(input, input.Repeat is null or (>= 1 and <= 100),
          ToolErrorCodes.InvalidParameterValue, $"'repeat' must be 1..100 (got {input.Repeat}); values are never clamped."))
      .Bind(_ => Check(input, input.HoldSeconds is null or (>= 0 and <= 30),
          ToolErrorCodes.InvalidParameterValue, $"'hold_seconds' must be 0..30 whole seconds (got {input.HoldSeconds}); values are never clamped or coerced."))
      .Bind(_ => DispatchChecks(input));

  private static Result<ComputerToolInput> ValidatePaste(ComputerToolInput input) => Required(input, input.Text is not null, "text",
      "The 'paste' action requires text: the content to place on the clipboard and paste, non-empty.")
      .Bind(_ => Check(input, input.Text == null || input.Text.Length > 0,
          ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."))
      .Bind(_ => Check(input, input.Format is null or "text" or "md" or "html",
          ToolErrorCodes.InvalidParameterValue, $"'format' must be exactly one of text, md, html (case-sensitive), but got '{input.Format}'."))
      .Bind(_ => ReturnStateCheck(input));

  private static Result<ComputerToolInput> ValidatePerformAction(ComputerToolInput input) => Required(input, input.Target is not null, "target",
      "The 'perform_action' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
      .Bind(_ => Required(input, input.ActionName is not null, "action_name",
      "The 'perform_action' action requires action_name: the element action to invoke (non-empty; must be one the element advertises)."))
      .Bind(_ => Check(input, input.ActionName == null || input.ActionName.Length > 0,
          ToolErrorCodes.InvalidParameterValue, "'action_name' must be non-empty."))
      .Bind(_ => ReturnStateCheck(input));

  /// <summary>Fails with MissingParameter naming the absent key when the
  ///     condition does not hold; succeeds otherwise.</summary>
  private static Result<ComputerToolInput> Required(ComputerToolInput input, bool present, string name, string requirement) => present
      ? Result.Success(input)
      : ToolArguments.Missing<ComputerToolInput>(name, requirement);

  /// <summary>Click-specific membership and range validation after the required
  ///     target check.</summary>
  private static Result<ComputerToolInput> ValidateClick(ComputerToolInput input)
  {
    Result<ComputerToolInput> clickCount = Check(input, input.ClickCount is null or >= 1,
        ToolErrorCodes.InvalidParameterValue, $"'click_count' must be >= 1 (got {input.ClickCount}); values are never clamped.");
    if (!clickCount.IsSuccess)
    {
      return clickCount;
    }

    Result<ComputerToolInput> mouseButton = Check(input, input.MouseButton is null or "left" or "right" or "middle",
        ToolErrorCodes.InvalidParameterValue, $"'mouse_button' must be exactly one of left, right, middle (case-sensitive), but got '{input.MouseButton}'.");
    return !mouseButton.IsSuccess ? mouseButton : ValidateModifiers(input)
        .Bind(_ => DispatchChecks(input));
  }

  /// <summary>Strategy membership: exactly auto | a11y | event, absent allowed. Shared
  ///     by every strategy-carrying action.</summary>
  private static Result<ComputerToolInput> StrategyCheck(ComputerToolInput input) =>
    Check(input, input.Strategy is null or "auto" or "a11y" or "event",
        ToolErrorCodes.InvalidParameterValue, $"'strategy' must be exactly one of auto, a11y, event (case-sensitive), but got '{input.Strategy}'.");

  /// <summary>Return_state membership: exactly compact | full | none, absent allowed.
  ///     Shared by every post-state-carrying action.</summary>
  private static Result<ComputerToolInput> ReturnStateCheck(ComputerToolInput input) =>
    Check(input, input.ReturnState is null or "compact" or "full" or "none",
        ToolErrorCodes.InvalidParameterValue, $"'return_state' must be exactly one of compact, full, none (case-sensitive), but got '{input.ReturnState}'.");

  /// <summary>Both post-state and dispatch knobs every pointer/keyboard action may carry.</summary>
  private static Result<ComputerToolInput> DispatchChecks(ComputerToolInput input) =>
    StrategyCheck(input).Bind(_ => ReturnStateCheck(input));

  /// <summary>Modifiers are plus-joined ctrl|alt|shift|win tokens, empty allowed.</summary>
  private static Result<ComputerToolInput> ValidateModifiers(ComputerToolInput input)
  {
    if (input.Modifiers is null or "")
    {
      return Result.Success(input);
    }

    string[] tokens = input.Modifiers.Split('+');
    bool ok = tokens is [_, ..] && tokens.All(t => t is "ctrl" or "alt" or "shift" or "win");
    return ok
      ? Result.Success(input)
      : Result.Failure<ComputerToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'modifiers' must be plus-joined ctrl, alt, shift, or win tokens (case-sensitive), but got '{input.Modifiers}'."));
  }

  /// <summary>Typed Check helper: condition holds succeeds, otherwise fails with
  ///     the given code and message. The success payload is discarded by the
  ///     Validate chain, which rebinds the input.</summary>
  private static Result<ComputerToolInput> Check(ComputerToolInput input, bool valid, string code, string message) => valid
      ? Result.Success(input)
      : Result.Failure<ComputerToolInput>(new DomainError(code, message));

  private static bool Has(JsonElement json, string name) => json.TryGetProperty(name, out _);

  /// <summary>Optional enum-by-name string: null when absent; a typed error when
  ///     present but not a string (membership stays with the caller, which owns
  ///     the per-action allowed set).</summary>
  private static Result<string?> OptionalName(JsonElement json, string name) =>
    !Has(json, name) ? Result.Success<string?>(null) : ToolArguments.OptionalString(json, name);

  /// <summary>Optional whole-second count: null when absent; InvalidParameterType
  ///     when present but not a JSON number, InvalidParameterValue when the number is
  ///     fractional - whole seconds are demanded, never coerced. Range checks stay
  ///     with the caller.</summary>
  private static Result<double?> OptionalSeconds(JsonElement json, string name)
  {
    if (!json.TryGetProperty(name, out JsonElement el))
    {
      return Result.Success<double?>(null);
    }

    if (el.ValueKind != JsonValueKind.Number)
    {
      return Result.Failure<double?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{name}' must be a number, but got {el.ValueKind}."));
    }

    Result<double?> whole = el.TryGetInt32(out int seconds)
      ? Result.Success<double?>(seconds)
      : Result.Failure<double?>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{name}' must be a whole number of seconds (got {el.GetDouble()}); fractional values are never coerced."));
    return whole;
  }

  /// <summary>App_ref: object with EXACTLY one of name / pid / aumid, optionally
  ///     plus window_id. The bare-string shorthand is deliberately not offered;
  ///     a non-object app_ref fails InvalidParameterType, a wrong-shape object
  ///     fails InvalidParameterValue.</summary>
  private static Result<ComputerAppRef> ParseAppRef(JsonElement json)
  {
    JsonElement appRef = json.GetProperty("app_ref");
    if (appRef.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<ComputerAppRef>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'app_ref' must be an object with exactly one of name, pid, or aumid, but got {appRef.ValueKind}. The bare-string shorthand is not offered."));
    }

    DomainError? unknownAppRefKey = ToolArguments.RejectUnknownParameters(appRef, "name", "pid", "aumid", "window_id");
    if (unknownAppRefKey is not null)
    {
      return Result.Failure<ComputerAppRef>(unknownAppRefKey);
    }

    bool hasName = Has(appRef, "name");
    bool hasPid = Has(appRef, "pid");
    bool hasAumid = Has(appRef, "aumid");
    int selectors = (hasName ? 1 : 0) + (hasPid ? 1 : 0) + (hasAumid ? 1 : 0);
    if (selectors != 1)
    {
      return Result.Failure<ComputerAppRef>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'app_ref' must carry exactly one of name, pid, or aumid; got {selectors}."));
    }

    Result<int?> windowId = ParseWindowId(appRef);
    return !windowId.IsSuccess
        ? Result.Failure<ComputerAppRef>(windowId.Error)
        : SelectSelector(appRef, hasPid, hasName, windowId.Value);
  }

  private static bool hasWindow(JsonElement appRef) => Has(appRef, "window_id");

  /// <summary>Optional window_id: null when absent; a typed error when present
  ///     but not a positive integer.</summary>
  private static Result<int?> ParseWindowId(JsonElement appRef)
  {
    if (!hasWindow(appRef))
    {
      return Result.Success<int?>(null);
    }

    Result<int?> parsed = ToolArguments.OptionalInt(appRef, "window_id");
    return !parsed.IsSuccess
        ? Result.Failure<int?>(parsed.Error)
        : PositiveWindowId(parsed.Value);
  }

  private static Result<int?> PositiveWindowId(int? value) => value is int positive && positive > 0
      ? Result.Success<int?>(positive)
      : Result.Failure<int?>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'window_id' must be a positive integer."));

  /// <summary>Parses the single selector key (validated upstream) and delegates
  ///     to the record factories; their ArgumentExceptions re-surface as
  ///     InvalidParameterValue values.</summary>
  private static Result<ComputerAppRef> SelectSelector(
      JsonElement appRef, bool hasPid, bool hasName, int? windowId)
  {
    if (hasPid)
    {
      JsonElement el = appRef.GetProperty("pid");
      if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int pid))
      {
        return Result.Failure<ComputerAppRef>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'pid' must be an integer, but got {el.ValueKind}."));
      }

      try
      {
        return Result.Success(ComputerAppRef.ByPid(pid, windowId));
      }
      catch (ArgumentException ex)
      {
        return Result.Failure<ComputerAppRef>(new DomainError(ToolErrorCodes.InvalidParameterValue, ex.Message));
      }
    }

    if (hasName)
    {
      JsonElement el = appRef.GetProperty("name");
      if (el.ValueKind != JsonValueKind.String)
      {
        return Result.Failure<ComputerAppRef>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'name' must be a string, but got {el.ValueKind}."));
      }

      try
      {
        return Result.Success(ComputerAppRef.ByName(el.GetString()!, windowId));
      }
      catch (ArgumentException ex)
      {
        return Result.Failure<ComputerAppRef>(new DomainError(ToolErrorCodes.InvalidParameterValue, ex.Message));
      }
    }

    JsonElement aumid = appRef.GetProperty("aumid");
    if (aumid.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<ComputerAppRef>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'aumid' must be a string, but got {aumid.ValueKind}."));
    }

    try
    {
      return Result.Success(ComputerAppRef.ByAumid(aumid.GetString()!, windowId));
    }
    catch (ArgumentException ex)
    {
      return Result.Failure<ComputerAppRef>(new DomainError(ToolErrorCodes.InvalidParameterValue, ex.Message));
    }
  }

  /// <summary>Target: object with type=element + integer index >= 0, or
  ///     type=coordinate + integer x,y >= 0. Wrong shape fails
  ///     InvalidParameterValue; wrong JSON kinds fail InvalidParameterType.</summary>
  private static Result<ComputerTarget> ParseTarget(JsonElement json, string key)
  {
    JsonElement target = json.GetProperty(key);
    if (target.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<ComputerTarget>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{key}' must be an object with type element or coordinate, but got {target.ValueKind}."));
    }

    if (!Has(target, "type"))
    {
      return Result.Failure<ComputerTarget>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{key}' must carry type: exactly 'element' or 'coordinate' (case-sensitive)."));
    }

    JsonElement typeEl = target.GetProperty("type");
    if (typeEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<ComputerTarget>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'type' must be a string, but got {typeEl.ValueKind}."));
    }

    string type = typeEl.GetString()!;
    return type switch
    {
      "element" => ElementTarget(target, key),
      "coordinate" => CoordinateTarget(target, key),
      _ => Result.Failure<ComputerTarget>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{key}.type' must be exactly 'element' or 'coordinate' (case-sensitive), but got '{type}'.")),
    };
  }

  private static Result<ComputerTarget> ElementTarget(JsonElement target, string key)
  {
    DomainError? unknownKey = ToolArguments.RejectUnknownParameters(target, "type", "index");
    if (unknownKey is not null)
    {
      return Result.Failure<ComputerTarget>(unknownKey);
    }

    if (!Has(target, "index"))
    {
      return Result.Failure<ComputerTarget>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{key}' with type element requires index: an integer >= 0."));
    }

    Result<int> index = ToolArguments.RequireInt(target, "index", "The element index addresses the app's latest observation.");
    if (!index.IsSuccess)
    {
      return Result.Failure<ComputerTarget>(index.Error);
    }

    Result<ComputerTarget> element = index.Value < 0
      ? Result.Failure<ComputerTarget>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'index' must be >= 0 (got {index.Value})."))
      : Result.Success(ComputerTarget.Element(index.Value));
    return element;
  }

  private static Result<ComputerTarget> CoordinateTarget(JsonElement target, string key)
  {
    DomainError? unknownKey = ToolArguments.RejectUnknownParameters(target, "type", "x", "y");
    if (unknownKey is not null)
    {
      return Result.Failure<ComputerTarget>(unknownKey);
    }

    if (!Has(target, "x") || !Has(target, "y"))
    {
      return Result.Failure<ComputerTarget>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{key}' with type coordinate requires both x and y: integers >= 0."));
    }

    Result<int> x = ToolArguments.RequireInt(target, "x", "The coordinate x addresses the registered frame's raster.");
    if (!x.IsSuccess)
    {
      return Result.Failure<ComputerTarget>(x.Error);
    }

    Result<int> y = ToolArguments.RequireInt(target, "y", "The coordinate y addresses the registered frame's raster.");
    if (!y.IsSuccess)
    {
      return Result.Failure<ComputerTarget>(y.Error);
    }

    Result<ComputerTarget> coordinate = x.Value < 0 || y.Value < 0
      ? Result.Failure<ComputerTarget>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"coordinates must be >= 0 (got x={x.Value}, y={y.Value})."))
      : Result.Success(ComputerTarget.Coordinate(x.Value, y.Value));
    return coordinate;
  }

  private static Result<ComputerToolInput> Fail(DomainError error) => Result.Failure<ComputerToolInput>(error);
}

/// <summary>The admitted 'computer' actions.</summary>
public enum ComputerAction
{
  ListApps,
  ListWindows,
  Observe,
  Click,
  Drag,
  Scroll,
  Type,
  SetValue,
  SelectText,
  Key,
  Paste,
  PerformAction,
  Stop,
}

