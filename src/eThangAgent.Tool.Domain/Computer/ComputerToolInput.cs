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

    JsonElement json = baseParse.Value;
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Fail(new DomainError("InvalidAction",
          "'action' is required: exactly one of list_apps, list_windows, observe, click, drag, scroll, type, set_value, select_text, key, paste, perform_action, or stop (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    Result<ComputerAction> action = actionText switch
    {
      "list_apps" => Result.Success(ComputerAction.ListApps),
      "list_windows" => Result.Success(ComputerAction.ListWindows),
      "observe" => Result.Success(ComputerAction.Observe),
      "click" => Result.Success(ComputerAction.Click),
      "drag" => Result.Success(ComputerAction.Drag),
      "scroll" => Result.Success(ComputerAction.Scroll),
      "type" => Result.Success(ComputerAction.Type),
      "set_value" => Result.Success(ComputerAction.SetValue),
      "select_text" => Result.Success(ComputerAction.SelectText),
      "key" => Result.Success(ComputerAction.Key),
      "paste" => Result.Success(ComputerAction.Paste),
      "perform_action" => Result.Success(ComputerAction.PerformAction),
      "stop" => Result.Success(ComputerAction.Stop),
      _ => Result.Failure<ComputerAction>(new DomainError("InvalidAction",
          $"'action' must be exactly one of list_apps, list_windows, observe, click, drag, scroll, type, set_value, select_text, key, paste, perform_action, or stop (case-sensitive; got '{actionText}').")),
    };
    if (!action.IsSuccess)
    {
      return Fail(action.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, Allowed(action.Value));
    return unknown is not null
      ? Fail(unknown)
      : ParseFields(action.Value, json);
  }

  /// <summary>The allowed argument keys per action (timeoutSeconds belongs to
  ///     every tool call). The list is echoed verbatim on UnknownParameter errors.
  ///     </summary>
  private static string[] Allowed(ComputerAction action) => action switch
  {
    ComputerAction.ListApps => ["action", ToolTimeout.ParameterName],
    ComputerAction.ListWindows => ["action", ToolTimeout.ParameterName, "app_ref"],
    ComputerAction.Observe => ["action", ToolTimeout.ParameterName, "app_ref", "include_screenshot", "disable_diffing"],
    ComputerAction.Click => ["action", ToolTimeout.ParameterName, "target", "app_ref", "mouse_button", "click_count", "modifiers", "strategy", "return_state"],
    ComputerAction.Drag => ["action", ToolTimeout.ParameterName, "from_target", "to", "app_ref", "modifiers", "return_state"],
    ComputerAction.Scroll => ["action", ToolTimeout.ParameterName, "target", "scroll_direction", "scroll_amount", "app_ref", "strategy", "return_state"],
    ComputerAction.Type => ["action", ToolTimeout.ParameterName, "text", "target", "app_ref", "strategy", "return_state"],
    ComputerAction.SetValue => ["action", ToolTimeout.ParameterName, "target", "value", "app_ref", "strategy", "return_state"],
    ComputerAction.SelectText => ["action", ToolTimeout.ParameterName, "target", "text", "prefix", "suffix", "selection_type", "app_ref", "return_state"],
    ComputerAction.Key => ["action", ToolTimeout.ParameterName, "text", "repeat", "hold_seconds", "app_ref", "strategy", "return_state"],
    ComputerAction.Paste => ["action", ToolTimeout.ParameterName, "text", "format", "target", "app_ref", "return_state"],
    ComputerAction.PerformAction => ["action", ToolTimeout.ParameterName, "target", "action_name", "app_ref", "return_state"],
    ComputerAction.Stop => ["action", ToolTimeout.ParameterName, "reason"],
    _ => throw new InvalidOperationException("unreachable: action is a validated enum value"),
  };

  /// <summary>Builds the seam command, applying the documented defaults for
  ///     every absent optional key. Only a successful parse reaches this.</summary>
  public ComputerCommand ToCommand() => Action switch
  {
    ComputerAction.ListApps => new ComputerCommand.ListApps(),
    ComputerAction.ListWindows => new ComputerCommand.ListWindows(AppRef!),
    ComputerAction.Observe => new ComputerCommand.Observe(AppRef!, IncludeScreenshot ?? false, DisableDiffing ?? false),
    ComputerAction.Click => new ComputerCommand.Click(Target!, AppRef, MouseButton ?? "left", ClickCount ?? 1, Modifiers ?? "", Strategy ?? "auto", ReturnState ?? "none"),
    ComputerAction.Drag => new ComputerCommand.Drag(From!, To!, AppRef, Modifiers ?? "", ReturnState ?? "none"),
    ComputerAction.Scroll => new ComputerCommand.Scroll(Target!, ScrollDirection!, ScrollAmount!.Value, AppRef, Strategy ?? "auto", ReturnState ?? "none"),
    ComputerAction.Type => new ComputerCommand.TypeText(Text!, Target, AppRef, Strategy ?? "auto", ReturnState ?? "none"),
    ComputerAction.SetValue => new ComputerCommand.SetValue(Target!, Value!, AppRef, Strategy ?? "auto", ReturnState ?? "none"),
    ComputerAction.SelectText => new ComputerCommand.SelectText(Target!, Text!, Prefix, Suffix, SelectionType ?? "text", AppRef, ReturnState ?? "none"),
    ComputerAction.Key => new ComputerCommand.Key(Text!, Repeat, HoldSeconds, AppRef, Strategy ?? "auto", ReturnState ?? "none"),
    ComputerAction.Paste => new ComputerCommand.Paste(Text!, Target, AppRef, Format ?? "text", ReturnState ?? "none"),
    ComputerAction.PerformAction => new ComputerCommand.PerformAction(Target!, ActionName!, AppRef, ReturnState ?? "none"),
    ComputerAction.Stop => new ComputerCommand.Stop(Reason),
    _ => throw new InvalidOperationException("unreachable: action is a validated enum value"),
  };

  /// <summary>Parses the per-action fields against the already-validated action.
  ///     Sub-objects parse first (they carry their own typed errors), then the
  ///     optional scalars, then the per-action required and range checks.</summary>
  private static Result<ComputerToolInput> ParseFields(ComputerAction action, JsonElement json)
  {
    ComputerAppRef? appRef = null;
    if (Has(json, "app_ref"))
    {
      Result<ComputerAppRef> parsed = ParseAppRef(json);
      if (!parsed.IsSuccess)
      {
        return Fail(parsed.Error);
      }

      appRef = parsed.Value;
    }

    ComputerTarget? target = null;
    if (Has(json, "target"))
    {
      Result<ComputerTarget> parsed = ParseTarget(json, "target");
      if (!parsed.IsSuccess)
      {
        return Fail(parsed.Error);
      }

      target = parsed.Value;
    }

    ComputerTarget? from = null;
    if (Has(json, "from_target"))
    {
      Result<ComputerTarget> parsed = ParseTarget(json, "from_target");
      if (!parsed.IsSuccess)
      {
        return Fail(parsed.Error);
      }

      from = parsed.Value;
    }

    ComputerTarget? to = null;
    if (Has(json, "to"))
    {
      Result<ComputerTarget> parsed = ParseTarget(json, "to");
      if (!parsed.IsSuccess)
      {
        return Fail(parsed.Error);
      }

      to = parsed.Value;
    }

    Result<bool?> includeScreenshot = ToolArguments.OptionalBool(json, "include_screenshot");
    if (!includeScreenshot.IsSuccess)
    {
      return Fail(includeScreenshot.Error);
    }

    Result<bool?> disableDiffing = ToolArguments.OptionalBool(json, "disable_diffing");
    if (!disableDiffing.IsSuccess)
    {
      return Fail(disableDiffing.Error);
    }

    Result<int?> scrollAmount = ToolArguments.OptionalInt(json, "scroll_amount");
    if (!scrollAmount.IsSuccess)
    {
      return Fail(scrollAmount.Error);
    }

    Result<int?> clickCount = ToolArguments.OptionalInt(json, "click_count");
    if (!clickCount.IsSuccess)
    {
      return Fail(clickCount.Error);
    }

    Result<int?> repeat = ToolArguments.OptionalInt(json, "repeat");
    if (!repeat.IsSuccess)
    {
      return Fail(repeat.Error);
    }

    Result<string?> scrollDirection = OptionalName(json, "scroll_direction");
    if (!scrollDirection.IsSuccess)
    {
      return Fail(scrollDirection.Error);
    }

    Result<string?> text = ToolArguments.OptionalString(json, "text");
    if (!text.IsSuccess)
    {
      return Fail(text.Error);
    }

    Result<string?> value = ToolArguments.OptionalString(json, "value");
    if (!value.IsSuccess)
    {
      return Fail(value.Error);
    }

    Result<string?> actionName = ToolArguments.OptionalString(json, "action_name");
    if (!actionName.IsSuccess)
    {
      return Fail(actionName.Error);
    }

    Result<string?> reason = ToolArguments.OptionalString(json, "reason");
    if (!reason.IsSuccess)
    {
      return Fail(reason.Error);
    }

    Result<string?> prefix = ToolArguments.OptionalString(json, "prefix");
    if (!prefix.IsSuccess)
    {
      return Fail(prefix.Error);
    }

    Result<string?> suffix = ToolArguments.OptionalString(json, "suffix");
    if (!suffix.IsSuccess)
    {
      return Fail(suffix.Error);
    }

    Result<string?> selectionType = OptionalName(json, "selection_type");
    if (!selectionType.IsSuccess)
    {
      return Fail(selectionType.Error);
    }

    Result<double?> holdSeconds = OptionalSeconds(json, "hold_seconds");
    if (!holdSeconds.IsSuccess)
    {
      return Fail(holdSeconds.Error);
    }

    Result<string?> format = OptionalName(json, "format");
    if (!format.IsSuccess)
    {
      return Fail(format.Error);
    }

    Result<string?> mouseButton = OptionalName(json, "mouse_button");
    if (!mouseButton.IsSuccess)
    {
      return Fail(mouseButton.Error);
    }

    Result<string?> modifiers = ToolArguments.OptionalString(json, "modifiers");
    if (!modifiers.IsSuccess)
    {
      return Fail(modifiers.Error);
    }

    Result<string?> strategy = OptionalName(json, "strategy");
    if (!strategy.IsSuccess)
    {
      return Fail(strategy.Error);
    }

    Result<string?> returnState = OptionalName(json, "return_state");
    if (!returnState.IsSuccess)
    {
      return Fail(returnState.Error);
    }

    ComputerToolInput input = new(action, appRef, includeScreenshot.Value, disableDiffing.Value,
        target, from, to, scrollAmount.Value, scrollDirection.Value, text.Value, value.Value,
        prefix.Value, suffix.Value, selectionType.Value, repeat.Value, holdSeconds.Value,
        format.Value, actionName.Value, mouseButton.Value, clickCount.Value, modifiers.Value,
        strategy.Value, returnState.Value, reason.Value);

    return Validate(action, input);
  }

  /// <summary>Per-action required-key, membership, and range validation. Runs
  ///     after every field parsed cleanly, so messages name one problem at a time.
  ///     Ranges come from the tool contract: scroll 0..100, repeat 1..100, hold
  ///     seconds 0..30, click count >= 1 - violations are typed errors, never
  ///     clamped.</summary>
  private static Result<ComputerToolInput> Validate(ComputerAction action, ComputerToolInput input) => action switch
  {
    ComputerAction.ListApps => Result.Success(input),
    ComputerAction.ListWindows => Required(input, input.AppRef is not null, "app_ref",
        "The 'list_windows' action requires app_ref: an object with exactly one of name (string), pid (integer), or aumid (string), optionally plus window_id (integer)."),
    ComputerAction.Observe => Required(input, input.AppRef is not null, "app_ref",
        "The 'observe' action requires app_ref: an object with exactly one of name (string), pid (integer), or aumid (string), optionally plus window_id (integer)."),
    ComputerAction.Click => Required(input, input.Target is not null, "target",
        "The 'click' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
        .Bind(_ => ValidateClick(input)),
    ComputerAction.Drag => Required(input, input.From is not null, "from_target",
        "The 'drag' action requires from_target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
        .Bind(_ => Required(input, input.To is not null, "to",
        "The 'drag' action requires to: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}."))
        .Bind(_ => ValidateModifiers(input))
        .Bind(_ => ReturnStateCheck(input)),
    ComputerAction.Scroll => Required(input, input.Target is not null, "target",
        "The 'scroll' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
        .Bind(_ => Required(input, input.ScrollDirection is not null, "scroll_direction",
        "The 'scroll' action requires scroll_direction: exactly one of up, down, left, or right (case-sensitive)."))
        .Bind(_ => Check(input, input.ScrollDirection is null or "up" or "down" or "left" or "right",
            ToolErrorCodes.InvalidParameterValue, $"'scroll_direction' must be exactly one of up, down, left, right (case-sensitive), but got '{input.ScrollDirection}'."))
        .Bind(_ => Required(input, input.ScrollAmount is not null, "scroll_amount",
        "The 'scroll' action requires scroll_amount: an integer 0..100 (scroll pages)."))
        .Bind(_ => Check(input, input.ScrollAmount is null or (>= 0 and <= 100),
            ToolErrorCodes.InvalidParameterValue, $"'scroll_amount' must be 0..100 (got {input.ScrollAmount}); values are never clamped."))
        .Bind(_ => DispatchChecks(input)),
    ComputerAction.Type => Required(input, input.Text is not null, "text",
        "The 'type' action requires text: the characters to type, non-empty.")
        .Bind(_ => Check(input, input.Text == null || input.Text.Length > 0,
            ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."))
        .Bind(_ => DispatchChecks(input)),
    ComputerAction.SetValue => Required(input, input.Target is not null, "target",
        "The 'set_value' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
        .Bind(_ => Required(input, input.Value is not null, "value",
        "The 'set_value' action requires value: the replacement value, non-empty."))
        .Bind(_ => Check(input, input.Value == null || input.Value.Length > 0,
            ToolErrorCodes.InvalidParameterValue, "'value' must be non-empty."))
        .Bind(_ => DispatchChecks(input)),
    ComputerAction.SelectText => Required(input, input.Target is not null, "target",
        "The 'select_text' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
        .Bind(_ => Required(input, input.Text is not null, "text",
        "The 'select_text' action requires text: the text to select, non-empty."))
        .Bind(_ => Check(input, input.Text == null || input.Text.Length > 0,
            ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."))
        .Bind(_ => Check(input, input.SelectionType is null or "text" or "cursor_before" or "cursor_after",
            ToolErrorCodes.InvalidParameterValue, $"'selection_type' must be exactly one of text, cursor_before, cursor_after (case-sensitive), but got '{input.SelectionType}'."))
        .Bind(_ => ReturnStateCheck(input)),
    ComputerAction.Key => Required(input, input.Text is not null, "text",
        "The 'key' action requires text: a key name (a, Return, Tab, Up) or X-keysym chord (Control_L+a).")
        .Bind(_ => Check(input, input.Text == null || input.Text.Length > 0,
            ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."))
        .Bind(_ => Check(input, input.Repeat is null or (>= 1 and <= 100),
            ToolErrorCodes.InvalidParameterValue, $"'repeat' must be 1..100 (got {input.Repeat}); values are never clamped."))
        .Bind(_ => Check(input, input.HoldSeconds is null or (>= 0 and <= 30),
            ToolErrorCodes.InvalidParameterValue, $"'hold_seconds' must be 0..30 whole seconds (got {input.HoldSeconds}); values are never clamped or coerced."))
        .Bind(_ => DispatchChecks(input)),
    ComputerAction.Paste => Required(input, input.Text is not null, "text",
        "The 'paste' action requires text: the content to place on the clipboard and paste, non-empty.")
        .Bind(_ => Check(input, input.Text == null || input.Text.Length > 0,
            ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."))
        .Bind(_ => Check(input, input.Format is null or "text" or "md" or "html",
            ToolErrorCodes.InvalidParameterValue, $"'format' must be exactly one of text, md, html (case-sensitive), but got '{input.Format}'."))
        .Bind(_ => ReturnStateCheck(input)),
    ComputerAction.PerformAction => Required(input, input.Target is not null, "target",
        "The 'perform_action' action requires target: {type: element, index: <int >= 0>} or {type: coordinate, x: <int >= 0>, y: <int >= 0>}.")
        .Bind(_ => Required(input, input.ActionName is not null, "action_name",
        "The 'perform_action' action requires action_name: the element action to invoke (non-empty; must be one the element advertises)."))
        .Bind(_ => Check(input, input.ActionName == null || input.ActionName.Length > 0,
            ToolErrorCodes.InvalidParameterValue, "'action_name' must be non-empty."))
        .Bind(_ => ReturnStateCheck(input)),
    ComputerAction.Stop => Result.Success(input),
    _ => throw new InvalidOperationException("unreachable: action is a validated enum value"),
  };

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

    int? windowId = null;
    if (hasWindow(appRef))
    {
      Result<int?> parsed = ToolArguments.OptionalInt(appRef, "window_id");
      if (!parsed.IsSuccess)
      {
        return Result.Failure<ComputerAppRef>(parsed.Error);
      }

      if (parsed.Value is not int positive || positive <= 0)
      {
        return Result.Failure<ComputerAppRef>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            "'window_id' must be a positive integer."));
      }

      windowId = positive;
    }

    return SelectSelector(appRef, hasPid, hasName, windowId);
  }

  private static bool hasWindow(JsonElement appRef) => Has(appRef, "window_id");

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

