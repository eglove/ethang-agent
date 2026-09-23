using System.Runtime.InteropServices;
using System.Text.Json;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The physical input dispatcher (fix round C1/I2/I4/M6, retargeted in fix
///     round 5): every accepted receipt follows a real dispatch through the send hooks -
///     production wires NativeInput's real SendInput; tests wire recording doubles (the
///     same seam pattern as IClipboardAccess, controller ruling accepted). The
///     strategy=event gate reads the real foreground window (the READ is injectable so
///     tests drive it) and refuses with foreground_required BEFORE anything is
///     dispatched. A3: the dispatcher carries the hold bookkeeping and
///     synthetic-modifier state and is cancelled by the broker on lease-owner loss. M6:
///     a failed send maps to the honest internal error with action_sent=false. Fix
///     round 5 (F1/F3/F6/F7): click and scroll ACT AT THEIR TARGET - a coordinate
///     target dispatches a positioned send (move/wheel/button rows at the resolved
///     screen point) and an element target routes through the
///     <see cref="IElementActionSink"/> (UIA invoke/scroll/focus); paste gates on the
///     foreground window like click; type_text with an element target focuses it first.</summary>
public sealed partial class InputDispatch(Func<int> foregroundPid, Func<KeyChord, bool> sendChord, Func<string, bool> sendText, Func<string, bool> sendButton, Func<string, int, int, int, int, bool>? sendDrag = null, Func<string, int, int, bool>? sendMouseButtonAt = null, Func<string, int, int, int, bool>? sendWheelAt = null)
{
  private readonly List<HoldRecord> _activeHolds = [];
  private readonly Lock _holdGate = new();
  private KeyModifiers _syntheticModifiers = KeyModifiers.None;
  private int _dispatchCount;

  /// <summary>The hold bookkeeping record: chord, start, and the key-up deadline.</summary>
  private sealed record HoldRecord(KeyChord Chord, DateTimeOffset StartedAt, TimeSpan Duration);
  private readonly Func<int> _foregroundPid = foregroundPid;
  private readonly Func<KeyChord, bool> _sendChord = sendChord;
  private readonly Func<string, bool> _sendText = sendText;
  private readonly Func<string, int, int, bool> _sendMouseButtonAt = sendMouseButtonAt ?? FallbackButtonAt;
  private readonly Func<string, int, int, int, bool> _sendWheelAt = sendWheelAt ?? NativeInput.SendWheelAt;
  private readonly Func<string, int, int, int, int, bool> _sendDrag = sendDrag ?? NativeInput.SendDrag;
  private int _dragCalls;
  private (int X, int Y) _lastDragFrom;
  private (int X, int Y) _lastDragTo;
  private int _clickCalls;
  private int _scrollCalls;

  /// <summary>How many targeted clicks reached the click sink (fix round 5 wire pins;
  ///     a refused or unresolvable click must have issued zero).</summary>
  public int ClickCalls => _clickCalls;

  /// <summary>The last targeted click's resolved point.</summary>
  public (int X, int Y) LastClickPoint { get; private set; }

  /// <summary>The last targeted click's button.</summary>
  public string LastClickButton { get; private set; } = string.Empty;

  /// <summary>How many wheel scrolls reached the wheel sink (fix round 5 wire pins).</summary>
  public int ScrollCalls => _scrollCalls;

  /// <summary>The last wheel scroll's resolved point.</summary>
  public (int X, int Y) LastScrollPoint { get; private set; }

  /// <summary>The last wheel scroll's direction and page count.</summary>
  public (string Direction, int Pages) LastScrollRequest { get; private set; }

  /// <summary>Legacy fallback when no positioned-button hook is wired: an UNTARGETED
  ///     button send (the pre-fix behavior) is only used by legacy test factories.</summary>
  private static bool FallbackButtonAt(string button, int x, int y)
  {
    _ = (x, y);
    return NativeInput.SendMouseButton(button);
  }

  /// <summary>Production instance: real foreground read + real NativeInput dispatch.</summary>
  public static InputDispatch Create(Func<int>? foregroundPid = null) =>
    new(foregroundPid ?? ReadForegroundPid, NativeInput.SendChord, NativeInput.SendText, NativeInput.SendMouseButton,
      sendDrag: NativeInput.SendDrag, sendMouseButtonAt: NativeInput.SendMouseButtonAt, sendWheelAt: NativeInput.SendWheelAt);

  /// <summary>Production: real foreground read + real NativeInput dispatch.</summary>
  public InputDispatch(Func<int>? foregroundPid = null)
    : this(foregroundPid ?? ReadForegroundPid, NativeInput.SendChord, NativeInput.SendText, NativeInput.SendMouseButton,
      sendDrag: NativeInput.SendDrag, sendMouseButtonAt: NativeInput.SendMouseButtonAt, sendWheelAt: NativeInput.SendWheelAt)
  {
  }

  /// <summary>How many dispatch operations were issued (delivery honesty: a refused
  ///     action must have issued zero; an accepted action more than zero).</summary>
  public int SendInputCalls => _dispatchCount;

  /// <summary>How many drag dispatches reached the drag sink (C3 wire pins; a refused
  ///     or unresolvable drag must have issued zero).</summary>
  public int DragCalls => _dragCalls;

  /// <summary>The last drag's from-point.</summary>
  public (int X, int Y) LastDragFrom => _lastDragFrom;

  /// <summary>The last drag's to-point.</summary>
  public (int X, int Y) LastDragTo => _lastDragTo;

  /// <summary>A3: true while a hold_key is ACTIVE (key down, key-up pending).</summary>
  public bool HasActiveHold
  {
    get
    {
      lock (_holdGate)
      {
        return _activeHolds.Count > 0;
      }
    }
  }

  /// <summary>A3: the synthetic modifier state held across a chord's down/up window.</summary>
  public bool HasSyntheticModifiers
  {
    get
    {
      lock (_holdGate)
      {
        return _syntheticModifiers != KeyModifiers.None;
      }
    }
  }

  /// <summary>A3: owner lost - cancel every ACTIVE hold (immediate key-ups) and clear
  ///     the synthetic modifier state.</summary>
  public void CancelActiveInput()
  {
    HoldRecord[] holds;
    lock (_holdGate)
    {
      holds = [.. _activeHolds];
      _activeHolds.Clear();
    }

    foreach (HoldRecord hold in holds)
    {
      _ = _sendChord(hold.Chord);
    }

    _syntheticModifiers = KeyModifiers.None;
  }

  /// <summary>The strategy=event gate: real foreground read against the target pid.</summary>
  public bool GateAllows(string? strategy, int targetPid) =>
    strategy != "event" || _foregroundPid() == targetPid;

  /// <summary>press_key: chord down+up; M6 honesty on send failure.</summary>
  public BrokerResponse PressKey(string keyText)
  {
    KeyChord chord = KeyChordNormalizer.Parse(keyText);
    _syntheticModifiers = chord.Modifiers;
    bool sent = _sendChord(chord);
    _ = Interlocked.Increment(ref _dispatchCount);
    _syntheticModifiers = KeyModifiers.None;
    return sent
      ? BrokerResponse.Accepted()
      : BrokerResponse.Fail("internal", $"SendInput reported failure for key '{keyText}'.", "action_sent=false");
  }

  /// <summary>hold_key: registered ACTIVE first, key down, key-up after the window
  ///     (skipped when A3 cancelled the record). Honesty: possibly_sent receipt.</summary>
  public BrokerResponse HoldKey(string keyText, double holdSeconds)
  {
    KeyChord chord = KeyChordNormalizer.Parse(keyText);
    HoldRecord record = new(chord, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(holdSeconds));
    lock (_holdGate)
    {
      _activeHolds.Add(record);
    }

    _syntheticModifiers = chord.Modifiers;
    if (!_sendChord(chord))
    {
      lock (_holdGate)
      {
        _ = _activeHolds.Remove(record);
      }

      _syntheticModifiers = KeyModifiers.None;
      return BrokerResponse.Fail("internal", $"SendInput key-down failed for '{keyText}'.", "action_sent=false");
    }

    _ = Interlocked.Increment(ref _dispatchCount);
    _ = Task.Run(async () =>
    {
      await Task.Delay(record.Duration).ConfigureAwait(false);
      bool stillHeld;
      lock (_holdGate)
      {
        stillHeld = _activeHolds.Remove(record);
      }

      if (stillHeld)
      {
        _ = _sendChord(chord);
        _syntheticModifiers = KeyModifiers.None;
      }
    });
    return BrokerResponse.Ok(JsonSerializer.SerializeToElement(new
    {
      action_sent = true,
      dispatch_status = BrokerReceipt.PossiblySent,
      effect_evidence = $"key-down dispatched; key-up due in {holdSeconds}s",
    }));
  }

  /// <summary>type_text (fix round 5, F7): an element target is focused FIRST (the
  ///     element_focus path), then the real text sequence flows through the hook; no
  ///     target types at the current keyboard focus.</summary>
  public BrokerResponse TypeText(string text, JsonElement? parameters = null)
  {
    ArgumentNullException.ThrowIfNull(text);
    if (text.Length == 0)
    {
      return BrokerResponse.Fail("invalid_request", "type_text requires non-empty text.");
    }

    if (parameters is { } p && p.TryGetProperty("element", out JsonElement el)
      && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int element))
    {
      if (_elementOps is IElementActionSink sink)
      {
        BrokerResponse focused = sink.Focus(element);
        if (focused.Error is not null)
        {
          return focused; // the focus failure is the honest answer; no text is typed
        }
      }
      else if (_elementOps is not IElementActionSink)
      {
        // A bounds-only resolver (drag math) cannot focus: the honest refusal.
        return BrokerResponse.Fail("invalid_request",
          $"type_text requires a focusable element target; element {element} has no element surface wired.",
          "action_sent=false");
      }
    }

    bool sent = _sendText(text);
    _ = Interlocked.Increment(ref _dispatchCount);
    return sent
      ? BrokerResponse.Accepted()
      : BrokerResponse.Fail("internal", "SendInput reported failure for the text sequence.", "action_sent=false");
  }

  /// <summary>click (fix round 5, F1): the click acts at ITS TARGET. A coordinate
  ///     target dispatches a positioned button send - the MOVE row to the resolved
  ///     screen point goes BEFORE the button rows (a button acts at the current cursor
  ///     position) - and an element target routes through the element sink (UIA
  ///     invoke). An unresolvable target is a typed invalid_request with nothing
  ///     dispatched; the strategy=event gate and send-failure honesty match the other
  ///     senders.</summary>
  public BrokerResponse Click(JsonElement? parameters, int targetPid)
  {
    string? strategy = Str(parameters, "strategy");
    if (!GateAllows(strategy, targetPid))
    {
      return BrokerResponse.Fail(
        "foreground_required",
        $"strategy=event requires the target app (pid {targetPid}) to be foreground; nothing was dispatched.");
    }

    string button = Str(parameters, "mouse_button") ?? "left";
    _ = sendButton; // legacy seam kept wired for untargeted factories; the targeted path never uses it

    // Element target: route through the element path (F1a) - UIA invoke, never a
    // coordinate click. A missing sink is the unresolvable-target refusal.
    if (parameters is { } p && p.TryGetProperty("element", out JsonElement el)
      && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int element))
    {
      if (_elementOps is IElementActionSink sink)
      {
        BrokerResponse routed = sink.Invoke(element);
        if (routed.Error is null)
        {
          _ = Interlocked.Increment(ref _dispatchCount);
        }

        return routed;
      }

      return BrokerResponse.Fail("invalid_request",
        $"click requires a resolvable target (x/y or element); element {element} has no element surface wired; nothing was dispatched.",
        "action_sent=false");
    }

    // Coordinate target: dispatch the positioned click (F1b) - MOVE row first, then
    // the button rows at that position.
    if (TryReadPoint(parameters, "x", "y", "element", out int x, out int y))
    {
      bool sent = _sendMouseButtonAt(button, x, y);
      _ = Interlocked.Increment(ref _dispatchCount);
      _ = Interlocked.Increment(ref _clickCalls);
      LastClickPoint = (x, y);
      LastClickButton = button;
      return sent
        ? BrokerResponse.Accepted()
        : BrokerResponse.Fail("internal", "SendInput reported failure for the click.", "action_sent=false");
    }

    return BrokerResponse.Fail("invalid_request",
      "click requires a resolvable target (x/y or element); nothing was dispatched.",
      "action_sent=false");
  }

  /// <summary>scroll (fix round 5, F3): vertical wheel injection AT the target. A
  ///     coordinate target positions the cursor (the wheel acts at the current cursor
  ///     position) then injects MOUSEEVENTF_WHEEL rows with mouseData = signed pages *
  ///     WHEEL_DELTA; an element target uses the UIA scroll pattern through the element
  ///     sink (honest failure when the pattern is unavailable); horizontal wheel
  ///     scrolling is an honest typed action_unavailable. Nothing dispatches on any
  ///     refusal.</summary>
  public BrokerResponse Scroll(JsonElement? parameters, int targetPid)
  {
    string? strategy = Str(parameters, "strategy");
    if (!GateAllows(strategy, targetPid))
    {
      return BrokerResponse.Fail("foreground_required",
        $"strategy=event requires the target window (pid {targetPid}) in the foreground; nothing was dispatched.");
    }

    string? directionRaw = Str(parameters, "scroll_direction");
    string direction = directionRaw ?? string.Empty;
    if (!string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase)
      && !string.Equals(direction, "down", StringComparison.OrdinalIgnoreCase)
      && !string.Equals(direction, "left", StringComparison.OrdinalIgnoreCase)
      && !string.Equals(direction, "right", StringComparison.OrdinalIgnoreCase))
    {
      return BrokerResponse.Fail("invalid_request",
        "scroll requires params.scroll_direction (one of up, down, left, right); nothing was dispatched.");
    }

    int pages = parameters is { } pa && pa.TryGetProperty("scroll_amount", out JsonElement amt)
      && amt.ValueKind == JsonValueKind.Number && amt.TryGetInt32(out int amount) ? amount : 1;

    if (direction is "left" or "right")
    {
      // F3: the horizontal wheel (MOUSEEVENTF_HWHEEL) is named honestly instead of
      // mis-mapped onto the vertical wheel.
      return BrokerResponse.Fail("action_unavailable",
        $"horizontal scroll ({direction}) is not available on this broker: the vertical wheel is the only injected axis; nothing was dispatched.");
    }

    // Element target: the UIA scroll pattern through the element sink.
    if (parameters is { } pe && pe.TryGetProperty("element", out JsonElement el)
      && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int element))
    {
      if (_elementOps is IElementActionSink sink)
      {
        BrokerResponse routed = sink.Scroll(element, direction, pages);
        if (routed.Error is null)
        {
          _ = Interlocked.Increment(ref _dispatchCount);
        }

        return routed;
      }

      return BrokerResponse.Fail("invalid_request",
        $"scroll requires a resolvable target (x/y or element); element {element} has no element surface wired; nothing was dispatched.",
        "action_sent=false");
    }

    // Coordinate target: position the cursor, then wheel.
    if (TryReadPoint(parameters, "x", "y", "element", out int x, out int y))
    {
      bool sent = _sendWheelAt(direction, pages, x, y);
      _ = Interlocked.Increment(ref _dispatchCount);
      _ = Interlocked.Increment(ref _scrollCalls);
      LastScrollPoint = (x, y);
      LastScrollRequest = (direction, pages);
      return sent
        ? BrokerResponse.Accepted()
        : BrokerResponse.Fail("internal", "SendInput reported failure for the scroll.", "action_sent=false");
    }

    return BrokerResponse.Fail("invalid_request",
      "scroll requires a resolvable target (x/y or element); nothing was dispatched.",
      "action_sent=false");
  }

  /// <summary>The generic wired-path entry (I2): the router lands here.</summary>
  public BrokerResponse Dispatch(string method, JsonElement? parameters)
  {
    if (parameters is null)
    {
      // R4: a borrowed empty document leaks nothing - dispose it when the scope ends.
      using JsonDocument empty = JsonDocument.Parse("{}");
      return DispatchCore(method, empty.RootElement.Clone());
    }

    return DispatchCore(method, parameters);
  }

  private BrokerResponse DispatchCore(string method, JsonElement? parameters)
  {
    int targetPid = Pid(parameters);
    try
    {
      return method switch
      {
        "press_key" => PressKey(KeyText(parameters)),
        "hold_key" => HoldKey(KeyText(parameters), HoldSeconds(parameters)),
        "type_text" => TypeText(Text(parameters) ?? "", parameters),
        "click" => Click(parameters, targetPid),
        "drag" => Drag(parameters, targetPid),
        "scroll" => Scroll(parameters, targetPid),
        "element_focus" or "element_set_value" or "element_perform_action"
          or "element_select_text" or "element_press" => ElementPath(method, parameters),
        _ => BrokerResponse.Fail("method_not_found", $"unknown input method: {method}."),
      };
    }
    catch (KeyChordException ex)
    {
      // R4: a missing/garbled chord is typed invalid_request feedback - never a crash.
      return BrokerResponse.Fail("invalid_request", ex.Message);
    }
  }

  /// <summary>The element resolver + real UIA ops (R1): set at broker composition from
  ///     the observer's last-walk cache; null keeps the legacy pulse (tests).</summary>
  private IElementBoundsResolver? _elementOps;

  public void SetElementOps(IElementBoundsResolver ops) => _elementOps = ops;

  /// <summary>C3: drag dispatch. Coordinate targets dispatch the real pointer sequence
  ///     (button down, interpolated absolute moves, button up); element targets resolve the
  ///     element's bounds center through the UIA cache and dispatch the same sequence. Only a
  ///     genuinely unresolvable target yields the honest failure. The strategy=event gate and
  ///     the send-failure honesty match the other senders.</summary>
  public BrokerResponse Drag(JsonElement? parameters, int targetPid)
  {
    string? strategy = parameters is { } p && p.TryGetProperty("strategy", out JsonElement st) && st.ValueKind == JsonValueKind.String
      ? st.GetString()
      : null;
    if (!GateAllows(strategy, targetPid))
    {
      return BrokerResponse.Fail("foreground_required",
        $"strategy=event requires the target window (pid {targetPid}) in the foreground; got pid {_foregroundPid()}.",
        "action_sent=false");
    }

    string button = parameters is { } pb && pb.TryGetProperty("button", out JsonElement bt) && bt.ValueKind == JsonValueKind.String
      ? bt.GetString()!
      : "left";

    bool fromOk = TryReadPoint(parameters, "x", "y", "element", out int fromX, out int fromY);
    bool toOk = TryReadPoint(parameters, "to_x", "to_y", "to_element", out int toX, out int toY);
    if (!fromOk || !toOk)
    {
      return BrokerResponse.Fail("invalid_request",
        "drag requires a resolvable from (x/y or element) and to (to_x/to_y or to_element); nothing was dispatched.",
        "action_sent=false");
    }

    bool sent = _sendDrag(button, fromX, fromY, toX, toY);
    _ = Interlocked.Increment(ref _dispatchCount);
    _ = Interlocked.Increment(ref _dragCalls);
    _lastDragFrom = (fromX, fromY);
    _lastDragTo = (toX, toY);
    return sent
      ? BrokerResponse.Accepted()
      : BrokerResponse.Fail("internal", "SendInput reported failure for the drag sequence.", "action_sent=false");
  }

  /// <summary>Reads a screen point pair for drag endpoints; an element index resolves the
  ///     bounds center through the wired element ops (UIA BoundingRectangle cache).</summary>
  private bool TryReadPoint(JsonElement? parameters, string xName, string yName, string elementName, out int x, out int y)
  {
    x = 0;
    y = 0;
    if (parameters is { } p
      && p.TryGetProperty(xName, out JsonElement xEl) && xEl.ValueKind == JsonValueKind.Number && xEl.TryGetInt32(out x)
      && p.TryGetProperty(yName, out JsonElement yEl) && yEl.ValueKind == JsonValueKind.Number && yEl.TryGetInt32(out y))
    {
      return true;
    }

    if (parameters is { } p2
      && p2.TryGetProperty(elementName, out JsonElement eEl) && eEl.ValueKind == JsonValueKind.Number && eEl.TryGetInt32(out int index))
    {
      if (_elementOps is { } ops && ops.TryResolveBoundsCenter(index, out x, out y))
      {
        return true;
      }

      x = 0;
      y = 0;
      return false;
    }

    return false;
  }
  private BrokerResponse ElementPath(string method, JsonElement? parameters)
  {
    if (_elementOps is UiaElementOps ops)
    {
      return DispatchElementOps(ops, method, parameters);
    }

    if (_elementOps is IElementActionSink sink)
    {
      return DispatchElementSink(sink, method, parameters);
    }

    // No element surface wired (unit-test doubles): the dispatch is the real chord
    // pulse the skeleton used - never a faked receipt.
    bool sent = _sendChord(new KeyChord(0x00, KeyModifiers.None));
    _ = Interlocked.Increment(ref _dispatchCount);
    return sent
      ? BrokerResponse.Accepted()
      : BrokerResponse.Fail("internal", "SendInput reported failure.", "action_sent=false");
  }

  /// <summary>Element methods over the action-sink seam (fix round 5): the sink answers
  ///     each method honestly; unknown methods stay typed refusals.</summary>
  private static BrokerResponse DispatchElementSink(IElementActionSink sink, string method, JsonElement? parameters)
  {
    int element = parameters is { } p && p.TryGetProperty("element", out JsonElement el)
      && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int index) ? index : -1;
#pragma warning disable IDE0046 // Named decision: the missing-element guard names the refusal; the ternary form hides it.
    if (element < 0)
    {
      return BrokerResponse.Fail("invalid_request",
        $"{method} requires params.element (integer index from the latest observation).");
    }
#pragma warning restore IDE0046

    return method switch
    {
      "element_focus" => sink.Focus(element),
      "element_press" or "element_perform_action" or "click" => sink.Invoke(element),
      "scroll" => sink.Scroll(element, ScrollDirection(parameters), ScrollPages(parameters)),
      "element_set_value" or "element_select_text" => BrokerResponse.Fail("action_unavailable",
        $"{method} is not available over the wired element surface; nothing ran."),
      _ => BrokerResponse.Fail("unimplemented", $"{method} has no element operation."),
    };
  }

  private static string ScrollDirection(JsonElement? parameters)
  {
    string? direction = Str(parameters, "scroll_direction");
    return direction is "up" or "down" or "left" or "right" ? direction
      : throw new KeyChordException("scroll requires params.scroll_direction (one of up, down, left, right).");
  }

  private static int ScrollPages(JsonElement? parameters) =>
    parameters is { } p && p.TryGetProperty("scroll_amount", out JsonElement el)
      && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int pages) ? pages : 1;

  private static BrokerResponse DispatchElementOps(UiaElementOps ops, string method, JsonElement? parameters)
  {
    int element = parameters is { } p && p.TryGetProperty("element", out JsonElement el)
      && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int index) ? index : -1;
    if (element < 0)
    {
      return BrokerResponse.Fail("invalid_request",
        $"{method} requires params.element (integer index from the latest observation).");
#pragma warning disable IDE0046 // Named decision: the guard names the refusal once; the ternary form below reads worse.
    }

    return ElementOp(ops, method, element, parameters);
#pragma warning restore IDE0046
  }

  private static BrokerResponse ElementOp(UiaElementOps ops, string method, int element, JsonElement? parameters)
  {
    return method switch
    {
      "element_focus" => ops.Focus(element),
      "element_press" => ops.PerformAction(element, "press"),
      "element_perform_action" => Str(parameters, "action_name") is { } action
        ? ops.PerformAction(element, action)
        : BrokerResponse.Fail("invalid_request", "element_perform_action requires params.action_name (string)."),
      "element_set_value" => Str(parameters, "value") is { } value
        ? ops.SetValue(element, value)
        : BrokerResponse.Fail("invalid_request", "element_set_value requires params.value (string)."),
      "element_select_text" => ops.SelectText(element),
      _ => BrokerResponse.Fail("unimplemented", $"{method} has no element operation."),
    };
  }
  private static int Pid(JsonElement? parameters) =>
    parameters is { } p && p.TryGetProperty("app_ref", out JsonElement appRef) && appRef.ValueKind == JsonValueKind.Object
      && appRef.TryGetProperty("pid", out JsonElement pidEl) && pidEl.ValueKind == JsonValueKind.Number
      && pidEl.TryGetInt32(out int pid)
        ? pid
        : -1;


  private static string KeyText(JsonElement? parameters) =>
    parameters is { } p && p.TryGetProperty("key", out JsonElement k) && k.ValueKind == JsonValueKind.String
      ? k.GetString()!
      : throw new KeyChordException("key chord parse failed: params.key is required.");


  private static double HoldSeconds(JsonElement? parameters) =>
    parameters is { } p && p.TryGetProperty("hold_seconds", out JsonElement h)
      && h.ValueKind == JsonValueKind.Number && h.TryGetDouble(out double v) ? v : 0;

  private static string? Text(JsonElement? parameters) =>
    parameters is { } p && p.TryGetProperty("text", out JsonElement tx) && tx.ValueKind == JsonValueKind.String
      ? tx.GetString() : null;

  private static string? Str(JsonElement? parameters, string name) =>
    parameters is { } p && p.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
      ? el.GetString() : null;

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [LibraryImport("user32.dll")]
  private static partial nint GetForegroundWindow();

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [LibraryImport("user32.dll")]
  private static partial uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);

  private static int ReadForegroundPid()
  {
    nint window = GetForegroundWindow();
    _ = GetWindowThreadProcessId(window, out int pid);
    return pid;
  }
};

/// <summary>Receipt vocabulary (spec 7): accepted only after a real dispatch; possibly_sent
///     only for real write ambiguity (hold_key key-down landed, key-up pending).</summary>
internal static class BrokerReceipt
{
  public const string Accepted = "accepted";
  public const string PossiblySent = "possibly_sent";
};
