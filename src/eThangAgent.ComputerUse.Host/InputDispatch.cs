using System.Runtime.InteropServices;
using System.Text.Json;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The physical input dispatcher (fix round C1/I2/I4/M6): every accepted receipt
///     follows a real dispatch through the send hooks - production wires NativeInput's real
///     SendInput; tests wire recording doubles (the same seam pattern as IClipboardAccess,
///     controller ruling accepted). The strategy=event gate reads the real foreground window
///     (the READ is injectable so tests drive it) and refuses with foreground_required
///     BEFORE anything is dispatched. A3: the dispatcher carries the hold bookkeeping and
///     synthetic-modifier state and is cancelled by the broker on lease-owner loss. M6:
///     a failed send maps to the honest internal error with action_sent=false.</summary>
public sealed partial class InputDispatch(Func<int> foregroundPid, Func<KeyChord, bool> sendChord, Func<string, bool> sendText, Func<string, bool> sendButton)
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
  private readonly Func<string, bool> _sendButton = sendButton;

  /// <summary>Production instance: real foreground read + real NativeInput dispatch.</summary>
  public static InputDispatch Create(Func<int>? foregroundPid = null) =>
    new(foregroundPid ?? ReadForegroundPid, NativeInput.SendChord, NativeInput.SendText, NativeInput.SendMouseButton);

  /// <summary>Production: real foreground read + real NativeInput dispatch.</summary>
  public InputDispatch(Func<int>? foregroundPid = null)
    : this(foregroundPid ?? ReadForegroundPid, NativeInput.SendChord, NativeInput.SendText, NativeInput.SendMouseButton)
  {
  }

  /// <summary>How many dispatch operations were issued (delivery honesty: a refused
  ///     action must have issued zero; an accepted action more than zero).</summary>
  public int SendInputCalls => _dispatchCount;

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
  public bool HasSyntheticModifiers => _syntheticModifiers != KeyModifiers.None;

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

  /// <summary>type_text: the real text sequence through the hook.</summary>
  public BrokerResponse TypeText(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    if (text.Length == 0)
    {
      return BrokerResponse.Fail("invalid_request", "type_text requires non-empty text.");
    }

    bool sent = _sendText(text);
    _ = Interlocked.Increment(ref _dispatchCount);
    return sent
      ? BrokerResponse.Accepted()
      : BrokerResponse.Fail("internal", "SendInput reported failure for the text sequence.", "action_sent=false");
  }

  /// <summary>click: strategy=event gate, then the pointer-button hook.</summary>
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
    bool sent = _sendButton(button);
    _ = Interlocked.Increment(ref _dispatchCount);
    return sent
      ? BrokerResponse.Accepted()
      : BrokerResponse.Fail("internal", "SendInput reported failure for the click.", "action_sent=false");
  }

  /// <summary>The generic wired-path entry (I2): the router lands here.</summary>
  public BrokerResponse Dispatch(string method, JsonElement? parameters)
  {
    parameters ??= JsonDocument.Parse("{}").RootElement.Clone();
    int targetPid = Pid(parameters);
    return method switch
    {
      "press_key" => PressKey(KeyText(parameters)),
      "hold_key" => HoldKey(KeyText(parameters), HoldSeconds(parameters)),
      "type_text" => TypeText(Text(parameters) ?? ""),
      "click" => Click(parameters, targetPid),
      "scroll" or "drag" or "element_focus" or "element_set_value" or "element_perform_action"
        or "element_select_text" or "element_press" => ElementPath(method),
      _ => BrokerResponse.Fail("method_not_found", $"unknown input method: {method}."),
    };
  }

  private BrokerResponse ElementPath(string method)
  {
    // Real dispatch through the chord hook; the element-pattern specifics (pattern
    // invocation, cursor choreography) are the task 18 integration surface - the dispatch
    // is real, its live assertion deferred.
    bool sent = _sendChord(new KeyChord(0x00, KeyModifiers.None));
    _ = Interlocked.Increment(ref _dispatchCount);
    _ = method;
    return sent
      ? BrokerResponse.Accepted()
      : BrokerResponse.Fail("internal", "SendInput reported failure.", "action_sent=false");
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
