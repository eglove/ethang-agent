using System.Collections.Frozen;
using System.Text.Json;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>The ACL adapter (task 18): implements the Tool Domain's
///     <see cref="IComputerAccess"/> over the supervised broker process. Commands
///     translate to broker methods (list_applications, list_windows, capture_app,
///     click, scroll, drag, type_text, press_key, hold_key, element_focus,
///     element_set_value, element_perform_action, element_select_text, element_press,
///     paste, stop_computer_control); the controller lease is taken lazily on the
///     first call that needs one and released by <see cref="ComputerCommand.Stop"/>.
///     Element targets resolve fail-closed through the <see cref="ObservationLedger"/>
///     (ELEMENT_UNAVAILABLE when the index is absent from the latest observation,
///     STALE_STATE when the window changed since the model last looked); coordinate
///     targets resolve through the <see cref="FrameRegistry"/> against the newest
///     delivered frame (STALE_STATE on any miss) and travel to the broker as resolved
///     global screen points. The CancellationToken flows into the pipe client.</summary>
public sealed class BrokerComputerAccess(BrokerSupervisor supervisor, bool ownsSupervisor = false) : IComputerAccess, IAsyncDisposable
{
  private readonly BrokerSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
  private readonly ObservationLedger _ledger = new();
  private readonly FrameRegistry _frames = new();
  private readonly bool _ownsSupervisor = ownsSupervisor;
  private bool _hasLease;

  /// <summary>Per-command ReturnState readers, keyed by the sealed case's runtime
  ///     type: the dispatch-table form of the flat switch chain.</summary>
  private static readonly FrozenDictionary<Type, Func<ComputerCommand, string?>> ReturnStateReaders =
    new Dictionary<Type, Func<ComputerCommand, string?>>
    {
      [typeof(ComputerCommand.Click)] = static c => ((ComputerCommand.Click)c).ReturnState,
      [typeof(ComputerCommand.Drag)] = static c => ((ComputerCommand.Drag)c).ReturnState,
      [typeof(ComputerCommand.Scroll)] = static c => ((ComputerCommand.Scroll)c).ReturnState,
      [typeof(ComputerCommand.TypeText)] = static c => ((ComputerCommand.TypeText)c).ReturnState,
      [typeof(ComputerCommand.SetValue)] = static c => ((ComputerCommand.SetValue)c).ReturnState,
      [typeof(ComputerCommand.SelectText)] = static c => ((ComputerCommand.SelectText)c).ReturnState,
      [typeof(ComputerCommand.Key)] = static c => ((ComputerCommand.Key)c).ReturnState,
      [typeof(ComputerCommand.Paste)] = static c => ((ComputerCommand.Paste)c).ReturnState,
      [typeof(ComputerCommand.PerformAction)] = static c => ((ComputerCommand.PerformAction)c).ReturnState,
    }.ToFrozenDictionary();

  /// <summary>Per-command app-reference readers, keyed by the sealed case's runtime
  ///     type: the dispatch-table form of the flat switch chain.</summary>
  private static readonly FrozenDictionary<Type, Func<ComputerCommand, ComputerAppRef?>> AppReaders =
    new Dictionary<Type, Func<ComputerCommand, ComputerAppRef?>>
    {
      [typeof(ComputerCommand.Click)] = static c => ((ComputerCommand.Click)c).App,
      [typeof(ComputerCommand.Drag)] = static c => ((ComputerCommand.Drag)c).App,
      [typeof(ComputerCommand.Scroll)] = static c => ((ComputerCommand.Scroll)c).App,
      [typeof(ComputerCommand.TypeText)] = static c => ((ComputerCommand.TypeText)c).App,
      [typeof(ComputerCommand.SetValue)] = static c => ((ComputerCommand.SetValue)c).App,
      [typeof(ComputerCommand.SelectText)] = static c => ((ComputerCommand.SelectText)c).App,
      [typeof(ComputerCommand.Key)] = static c => ((ComputerCommand.Key)c).App,
      [typeof(ComputerCommand.Paste)] = static c => ((ComputerCommand.Paste)c).App,
      [typeof(ComputerCommand.PerformAction)] = static c => ((ComputerCommand.PerformAction)c).App,
    }.ToFrozenDictionary();

  /// <summary>Per-command broker-method selectors, keyed by the sealed case's runtime
  ///     type: the dispatch-table form of the flat switch chain. The Key entry keeps
  ///     its hold/press derivation (hold_seconds &gt; 0 selects hold_key).</summary>
  private static readonly FrozenDictionary<Type, Func<ComputerCommand, string>> MethodSelectors =
    new Dictionary<Type, Func<ComputerCommand, string>>
    {
      [typeof(ComputerCommand.Click)] = static _ => "click",
      [typeof(ComputerCommand.Drag)] = static _ => "drag",
      [typeof(ComputerCommand.Scroll)] = static _ => "scroll",
      [typeof(ComputerCommand.TypeText)] = static _ => "type_text",
      [typeof(ComputerCommand.SetValue)] = static _ => "element_set_value",
      [typeof(ComputerCommand.SelectText)] = static _ => "element_select_text",
      [typeof(ComputerCommand.Key)] = static c => MethodForKey(((ComputerCommand.Key)c).HoldSeconds),
      [typeof(ComputerCommand.Paste)] = static _ => "paste",
      [typeof(ComputerCommand.PerformAction)] = static _ => "element_perform_action",
    }.ToFrozenDictionary();

  /// <summary>Per-command parameter builders, keyed by the sealed case's runtime type:
  ///     the dispatch-table form of the flat switch chain. Commands outside the input
  ///     path (never dispatched through ActionAsync) build an empty map.</summary>
  private static readonly FrozenDictionary<Type, Func<ComputerCommand, Dictionary<string, object?>>> ParameterBuilders =
    new Dictionary<Type, Func<ComputerCommand, Dictionary<string, object?>>>
    {
      [typeof(ComputerCommand.Click)] = static c => ParameterMapForClick((ComputerCommand.Click)c),
      [typeof(ComputerCommand.Drag)] = static c => ParameterMapForDrag((ComputerCommand.Drag)c),
      [typeof(ComputerCommand.Scroll)] = static c => ParameterMapForScroll((ComputerCommand.Scroll)c),
      [typeof(ComputerCommand.TypeText)] = static c => ParameterMapForTypeText((ComputerCommand.TypeText)c),
      [typeof(ComputerCommand.SetValue)] = static c => ParameterMapForSetValue((ComputerCommand.SetValue)c),
      [typeof(ComputerCommand.SelectText)] = static c => ParameterMapForSelectText((ComputerCommand.SelectText)c),
      [typeof(ComputerCommand.Key)] = static c => ParameterMapForKey((ComputerCommand.Key)c),
      [typeof(ComputerCommand.Paste)] = static c => ParameterMapForPaste((ComputerCommand.Paste)c),
      [typeof(ComputerCommand.PerformAction)] = static c => ParameterMapForPerformAction((ComputerCommand.PerformAction)c),
    }.ToFrozenDictionary();

  /// <inheritdoc />
  public async Task<ComputerOutcome> ExecuteAsync(ComputerCommand command, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    return command switch
    {
      ComputerCommand.ListApps => await RequestAsync("list_applications", null, ct).ConfigureAwait(false),
      ComputerCommand.ListWindows list => await RequestAsync("list_windows",
          JsonSerializer.SerializeToElement(new { app_ref = AppRef(list.App) }, BrokerWire.Options), ct)
        .ConfigureAwait(false),
      ComputerCommand.Observe observe => await ObserveAsync(observe, ct).ConfigureAwait(false),
      ComputerCommand.Stop => await StopAsync(ct).ConfigureAwait(false),
      _ => await ActionAsync(command, ct).ConfigureAwait(false),
    };
  }

  private async Task<ComputerOutcome> ObserveAsync(ComputerCommand.Observe command, CancellationToken ct)
  {
    JsonElement parameters = JsonSerializer.SerializeToElement(new
    {
      app_ref = AppRef(command.App),
      include_screenshot = command.IncludeScreenshot,
      snapshot_mode = command.DisableDiffing ? "force_full" : "auto",
    }, BrokerWire.Options);
    return await RequestAsync("capture_app", parameters, ct).ConfigureAwait(false);
  }

  private async Task<ComputerOutcome> StopAsync(CancellationToken ct)
  {
    if (!_hasLease)
    {
      // Nothing held: an honest no-op receipt (nothing was dispatched).
      return new ComputerOutcome.Receipt(ActionSent: false, BrokerActionReceipt.Accepted, null, null);
    }

    ComputerOutcome outcome = await RequestAsync("stop_computer_control", null, ct).ConfigureAwait(false);
    if (outcome is ComputerOutcome.Receipt)
    {
      _hasLease = false;
    }

    return outcome;
  }

  /// <summary>The input-path commands: resolve the target BEFORE any wire round trip
  ///     (a stale element or a dead frame never dispatches input), take the lease,
  ///     then dispatch the method with its parameters.</summary>
  private async Task<ComputerOutcome> ActionAsync(ComputerCommand command, CancellationToken ct)
  {
    (ComputerOutcome.Failure? failure, FrameCoordinateResult.Hit? primaryHit, FrameCoordinateResult.Hit? toHit) =
      ResolveTargets(command);
    if (failure is not null)
    {
      return failure;
    }

    ComputerOutcome? leaseFailure = await EnsureLeaseAsync(ct).ConfigureAwait(false);
    if (leaseFailure is not null)
    {
      return leaseFailure;
    }

    JsonElement parameters = ParametersFor(command, primaryHit, toHit);
    ComputerOutcome outcome = await RequestAsync(MethodFor(command), parameters, ct).ConfigureAwait(false);

    // I9: return_state=compact|full drives a post-action re-observe so the option is
    // never silently dropped. Failures of the re-observe ride the Receipt's TreeText slot.
    if (ReturnStateOf(command) is { } returnState && returnState != "none" && outcome is ComputerOutcome.Receipt receipt)
    {
      ComputerAppRef? app = AppOf(command);
      if (app is not null)
      {
        ComputerOutcome postState = await ObserveAsync(
            new ComputerCommand.Observe(app, IncludeScreenshot: false, DisableDiffing: false), ct).ConfigureAwait(false);
        if (postState is ComputerOutcome.Observation postObservation)
        {
          outcome = receipt with { TreeText = postObservation.TreeText };
        }
      }
    }

    return outcome;
  }

  private static string? ReturnStateOf(ComputerCommand command) =>
    ReturnStateReaders.TryGetValue(command.GetType(), out Func<ComputerCommand, string?>? reader) ? reader(command) : null;

  private static ComputerTarget? TargetOf(ComputerCommand command) => command switch
  {
    ComputerCommand.Click c => c.Target,
    ComputerCommand.Scroll s => s.Target,
    ComputerCommand.SetValue v => v.Target,
    ComputerCommand.SelectText s => s.Target,
    ComputerCommand.PerformAction a => a.Target,
    ComputerCommand.Drag d => d.From,
    ComputerCommand.TypeText t => t.Target,
    ComputerCommand.Paste p => p.Target,
    _ => null,
  };

  /// <summary>Resolves every target the command carries (drag: from AND to) in ONE pass;
  ///     the returned hits travel to ParametersFor so coordinates resolve exactly once (I11).
  ///     Null failure means every present target resolved.</summary>
  private (ComputerOutcome.Failure? Failure, FrameCoordinateResult.Hit? PrimaryHit, FrameCoordinateResult.Hit? ToHit) ResolveTargets(
      ComputerCommand command)
  {
    ComputerTarget? primary = TargetOf(command);
    ComputerTarget? to = command is ComputerCommand.Drag d ? d.To : null;

    ComputerOutcome.Failure? primaryFailure = ResolvePrimaryTarget(command, primary, out FrameCoordinateResult.Hit? primaryHit);
    if (primaryFailure is not null)
    {
      return (primaryFailure, null, null);
    }

    ComputerOutcome.Failure? toFailure = ResolveDragToTarget(command, to, out FrameCoordinateResult.Hit? toHit);
    return toFailure is not null ? (toFailure, null, null) : (null, primaryHit, toHit);
  }

  /// <summary>Resolves the primary target: the ledger validates an element index
  ///     (fail-closed ELEMENT_UNAVAILABLE/STALE_STATE), the frame registry resolves a
  ///     coordinate against the newest delivered frame. Neither present is a typed
  ///     InvalidApp failure. The resolved coordinate hit travels out through
  ///     <paramref name="hit"/> so coordinates resolve exactly once (I11).</summary>
  private ComputerOutcome.Failure? ResolvePrimaryTarget(
      ComputerCommand command, ComputerTarget? target, out FrameCoordinateResult.Hit? hit)
  {
    hit = null;
    if (target is not { } p)
    {
      return null;
    }

    if (p.ElementIndex is { } index)
    {
      LedgerIndexCheck check = _ledger.ValidateIndex(LedgerKeyFor(AppOf(command)), index);
      return check.Ok
        ? null
        : new ComputerOutcome.Failure(
            check.Error ?? ComputerErrorCodes.ElementUnavailable,
            check.Message ?? "element target does not resolve; observe first.");
    }

    return p.X is { } px && p.Y is { } py
      ? ResolveCoordinateTarget(_frames, px, py, out hit, "the coordinate target does not resolve against a delivered screenshot frame; observe with include_screenshot first.")
      : new ComputerOutcome.Failure(ComputerErrorCodes.InvalidApp, "target carries neither element index nor coordinates.");
  }

  /// <summary>Resolves the drag 'to' target by the same fail-closed rules as the
  ///     primary target (ledger for element indexes, frame registry for coordinates);
  ///     the per-side failure messages name the drag 'to' slot.</summary>
  private ComputerOutcome.Failure? ResolveDragToTarget(
      ComputerCommand command, ComputerTarget? target, out FrameCoordinateResult.Hit? hit)
  {
    hit = null;
    if (target is not { } toTarget)
    {
      return null;
    }

    if (toTarget.ElementIndex is { } toIndex)
    {
      LedgerIndexCheck toCheck = _ledger.ValidateIndex(LedgerKeyFor(AppOf(command)), toIndex);
      return toCheck.Ok
        ? null
        : new ComputerOutcome.Failure(
            toCheck.Error ?? ComputerErrorCodes.ElementUnavailable,
            toCheck.Message ?? "drag 'to' element target does not resolve; observe first.");
    }

    return toTarget.X is { } tx && toTarget.Y is { } ty
      ? ResolveCoordinateTarget(_frames, tx, ty, out hit, "the drag 'to' coordinate does not resolve against a delivered screenshot frame; observe with include_screenshot first.")
      : new ComputerOutcome.Failure(ComputerErrorCodes.InvalidApp, "drag 'to' carries neither element index nor coordinates.");
  }

  /// <summary>Resolves one coordinate pair against the newest delivered frame
  ///     (STALE_STATE on any miss); the hit travels out through <paramref name="hit"/>.</summary>
  private static ComputerOutcome.Failure? ResolveCoordinateTarget(
      FrameRegistry frames, int x, int y, out FrameCoordinateResult.Hit? hit, string failureMessage)
  {
    hit = null;
    if (frames.ResolveForCoordinate(FrameRegistry.LatestToken, x, y) is not FrameCoordinateResult.Hit resolved)
    {
      return new ComputerOutcome.Failure(ComputerErrorCodes.StaleState, failureMessage);
    }

    hit = resolved;
    return null;
  }

  private static ComputerAppRef? AppOf(ComputerCommand command) =>
    AppReaders.TryGetValue(command.GetType(), out Func<ComputerCommand, ComputerAppRef?>? reader) ? reader(command) : null;

  private static string MethodFor(ComputerCommand command) =>
    MethodSelectors.TryGetValue(command.GetType(), out Func<ComputerCommand, string>? selector)
      ? selector(command)
      : throw new InvalidOperationException("unreachable: ActionAsync only sees input commands");

  private static string MethodForKey(double? holdSeconds) => holdSeconds is { } hold && hold > 0 ? "hold_key" : "press_key";

  private static JsonElement ParametersFor(ComputerCommand command,
      FrameCoordinateResult.Hit? primaryHit, FrameCoordinateResult.Hit? toHit)
  {
    Dictionary<string, object?> map = ParameterBuilders.TryGetValue(command.GetType(),
        out Func<ComputerCommand, Dictionary<string, object?>>? build)
      ? build(command)
      : [];

    // The primary target resolves client-side once (I11): the broker receives the
    // resolved global screen point or the observation element index.
    ApplyPrimaryTarget(command, primaryHit, map);

    // C3: drag carries BOTH endpoints - the 'to' target resolves identically.
    ApplyDragToTarget(command, toHit, map);

    if (AppOf(command) is { } app)
    {
      map["app_ref"] = AppRef(app);
    }

    return JsonSerializer.SerializeToElement(map, BrokerWire.Options);
  }

  private static void ApplyPrimaryTarget(ComputerCommand command, FrameCoordinateResult.Hit? primaryHit,
      Dictionary<string, object?> map)
  {
    if (TargetOf(command) is not { } primaryTarget)
    {
      return;
    }

    if (primaryTarget.ElementIndex is { } index)
    {
      map["element"] = index;
    }
    else if (primaryHit is { } hit)
    {
      map["x"] = (int)hit.Resolution.X;
      map["y"] = (int)hit.Resolution.Y;
    }
  }

  private static void ApplyDragToTarget(ComputerCommand command, FrameCoordinateResult.Hit? toHit,
      Dictionary<string, object?> map)
  {
    if (command is not ComputerCommand.Drag drag)
    {
      return;
    }

    if (drag.To.ElementIndex is { } toIndex)
    {
      map["to_element"] = toIndex;
    }
    else if (toHit is { } toPoint)
    {
      map["to_x"] = (int)toPoint.Resolution.X;
      map["to_y"] = (int)toPoint.Resolution.Y;
    }
  }

  private static Dictionary<string, object?> ParameterMapForClick(ComputerCommand.Click c) => new()
  {
    ["mouse_button"] = c.MouseButton,
    ["click_count"] = c.ClickCount,
    ["modifiers"] = c.Modifiers,
    ["strategy"] = c.Strategy,
  };

  private static Dictionary<string, object?> ParameterMapForDrag(ComputerCommand.Drag d) => new() { ["modifiers"] = d.Modifiers };

  private static Dictionary<string, object?> ParameterMapForScroll(ComputerCommand.Scroll s) => new()
  {
    ["scroll_direction"] = s.ScrollDirection,
    ["scroll_amount"] = s.ScrollAmount,
    ["strategy"] = s.Strategy,
  };

  private static Dictionary<string, object?> ParameterMapForTypeText(ComputerCommand.TypeText t) =>
    new() { ["text"] = t.Text, ["strategy"] = t.Strategy };

  private static Dictionary<string, object?> ParameterMapForSetValue(ComputerCommand.SetValue v) =>
    new() { ["value"] = v.Value, ["strategy"] = v.Strategy };

  private static Dictionary<string, object?> ParameterMapForSelectText(ComputerCommand.SelectText s) => new()
  {
    ["text"] = s.Text,
    ["prefix"] = s.Prefix,
    ["suffix"] = s.Suffix,
    ["selection_type"] = s.SelectionType,
  };

  private static Dictionary<string, object?> ParameterMapForKey(ComputerCommand.Key k) => new()
  {
    ["key"] = k.Text,
    ["repeat"] = k.Repeat,
    ["hold_seconds"] = k.HoldSeconds,
    ["strategy"] = k.Strategy,
  };

  private static Dictionary<string, object?> ParameterMapForPaste(ComputerCommand.Paste p) =>
    new() { ["text"] = p.Text, ["format"] = p.Format };

  private static Dictionary<string, object?> ParameterMapForPerformAction(ComputerCommand.PerformAction a) =>
    new() { ["action_name"] = a.Action };

  private Task<ComputerOutcome?> EnsureLeaseAsync(CancellationToken ct) =>
    _hasLease ? Task.FromResult<ComputerOutcome?>(null) : TakeOverAsync(ct);

  private async Task<ComputerOutcome?> TakeOverAsync(CancellationToken ct)
  {
    ComputerOutcome outcome = await RequestAsync("controller_takeover", null, ct).ConfigureAwait(false);
    if (outcome is ComputerOutcome.Failure failure)
    {
      return failure;
    }

    _hasLease = true;
    return null;
  }

  /// <summary>One broker round trip: errors map through BrokerErrorMapper, action
  ///     receipts survive verbatim, and capture_app results parse into an
  ///     Observation (tree text via TreeTextRenderer, ledger + frame registration,
  ///     screenshot re-encoded through ScreenshotEncoder). List results render as an
  ///     Observation whose tree text IS the JSON rows (they carry no element table).</summary>
  private async Task<ComputerOutcome> RequestAsync(string method, JsonElement? parameters, CancellationToken ct)
  {
    BrokerReply reply;
    try
    {
      reply = await _supervisor.RequestEnvelopeAsync(method, parameters, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      // Cancellation honesty: the caller stopped waiting, so delivery is UNKNOWN. A typed
      // retryable TIMEOUT names the cancellation - never a silent clean result.
      return new ComputerOutcome.Failure(ComputerErrorCodes.Timeout,
          "the call was cancelled before the broker replied; whether the action landed is unknown.");
    }

    catch (BrokerEnvelopeException envelope)
    {
      // C4: transport failures arrive as typed envelope exceptions. HELPER_UNAVAILABLE carries
      // the retry hint; the budget code maps to TIMEOUT. Exceptions never escape the adapter.
      string code = envelope.Code == ComputerErrorCodes.Timeout
        ? ComputerErrorCodes.Timeout
        : ComputerErrorCodes.HelperUnavailable;
      string hint = code == ComputerErrorCodes.Timeout
        ? "the broker call exceeded its budget; whether the action landed is unknown."
        : "the broker is unavailable; the pipe may be restarting - retry the request.";
      return new ComputerOutcome.Failure(code, envelope.Message + " " + hint);
    }
    if (reply.Error is { } error)
    {
      return BrokerErrorMapper.Map(error.Code, error.Message);
    }

    if (method == "capture_app")
    {
      return CaptureToObservation(reply);
    }

    if (BrokerActionReceipt.From(reply) is { } receipt)
    {
      return new ComputerOutcome.Receipt(receipt.ActionSent, receipt.DispatchStatus, receipt.EffectEvidence, null);
    }

    if (reply.Result is { } result)
    {
      // list_applications / list_windows / controller results: no element table exists,
      // so the observation's tree text carries the JSON rows verbatim.
      return new ComputerOutcome.Observation(result.GetRawText(), "s-list", [], null, null, null);
    }

    return new ComputerOutcome.Failure(ComputerErrorCodes.Internal, "broker returned an unrecognized result shape.");
  }

  /// <summary>Parses one capture_app reply into the model-facing Observation: tree text
  ///     rendered through the renderer, the state registered in the ledger, a delivered
  ///     actionable raster registered as a coordinate frame, and the screenshot
  ///     re-encoded for the model (withheld when the broker marked it blank).</summary>
  internal ComputerOutcome CaptureToObservation(BrokerReply reply)
  {
    if (CaptureAppResult.From(reply) is not { } capture)
    {
      // I10: an unparseable capture is a typed retryable failure - never an empty success.
      return new ComputerOutcome.Failure(
          ComputerErrorCodes.StaleState,
          "the broker's capture could not be parsed; observe again.");
    }

    bool treeShown = capture.SnapshotMode != "no_change";
    _lastObservedPid = capture.App.Pid;
    _lastObservedWindowId = capture.Window.WindowId;
    string stateId = _ledger.Record(

        new LedgerKey(capture.App.Pid, capture.Window.WindowId),
        new LedgerWindow(capture.Window.Title, capture.Window.Bounds[0], capture.Window.Bounds[1],
            capture.Window.Bounds[2], capture.Window.Bounds[3]),
        capture.Elements, treeShownToModel: treeShown, screenshotOnly: false);
    _ = stateId;

    RegisterScreenshot(capture, out ToolResultImage? screenshot, out string? withheld, out ComputerFrameRef? frame);

    string treeText = TreeTextRenderer.Render(capture);
    IReadOnlyList<ComputerElement> elements = [.. capture.Elements.Select(e => new ComputerElement(
        e.Index, e.Role, e.Kind, e.Title, e.Value, e.Bounds, e.Enabled, e.Editable, e.Actions,
        e.Focused, e.Selected, e.Pressable, e.HasMenu, e.ChildrenTotal, e.ChildrenShown, e.ChildrenOffset))];
    return new ComputerOutcome.Observation(treeText, stateId, elements, frame, withheld, screenshot);
  }

  /// <summary>Re-encodes a delivered raster as the observation's screenshot frame: a
  ///     blank raster is withheld with its reason, a delivered raster re-encodes through
  ///     ScreenshotEncoder and registers as a coordinate frame over the window bounds.</summary>
  private void RegisterScreenshot(CaptureAppResult capture, out ToolResultImage? screenshot,
      out string? withheld, out ComputerFrameRef? frame)
  {
    screenshot = null;
    withheld = null;
    frame = null;
    if (capture.Screenshot is not { } shot)
    {
      return;
    }

    if (shot.Blank)
    {
      withheld = "the window's raster is uniform (no actionable content)";
      return;
    }

    byte[] png = Convert.FromBase64String(shot.Data);
    DeliveredScreenshot delivered = ScreenshotEncoder.Encode(png);
    screenshot = new ToolResultImage("image/jpeg", delivered.Base64);
    ScreenshotWindowRect rect = new(capture.Window.Bounds[0], capture.Window.Bounds[1],
        capture.Window.Bounds[2], capture.Window.Bounds[3]);
    frame = _frames.Add(delivered.Width, delivered.Height, rect, actionable: true);
  }

  /// <summary>M14: for name/aumid refs the broker resolves the pid; the ledger key derives
  ///     from the pid of the LAST observed capture (fail-closed stays: no observation means
  ///     pid 0, and ValidateIndex answers ELEMENT_UNAVAILABLE). WindowId resolution: the
  ///     ledger records captures under the OBSERVED window id (the broker's window id is
  ///     never 0), so a ref without a WindowId of its own resolves to the last observed
  ///     window of that pid; a pid that was never observed stays (pid, 0) — fail-closed,
  ///     observe first.</summary>
  private LedgerKey LedgerKeyFor(ComputerAppRef? app)
  {
    // M14: explicit pid wins; name/aumid refs fall back to the last observed capture pid.
    int pid = app is { } reference && reference.Pid is { } explicitPid
        ? explicitPid
        : _lastObservedPid ?? 0;
    // Named decision (IDE0046): the guard sequence names each resolution step; a
    // conditional-expression nest would hide which branch answers which ref shape.
#pragma warning disable IDE0046
    if (pid == 0)
    {
      return new LedgerKey(0, 0);
    }

    if (app is { } r && r.WindowId is { } windowId)
    {
      return new LedgerKey(pid, windowId);
    }

    return pid == _lastObservedPid && _lastObservedWindowId is { } observed
        ? new LedgerKey(pid, observed)
        : new LedgerKey(pid, 0);
#pragma warning restore IDE0046
  }

  private int? _lastObservedPid;
  private int? _lastObservedWindowId;

  private static object AppRef(ComputerAppRef app) => new
  {
    name = app.Name,
    pid = app.Pid,
    aumid = app.Aumid,
    window_id = app.WindowId,
  };

  /// <summary>Disposes the owned supervisor when this access owns it. When the caller
  ///     owns the supervisor (composition shares one broker per workspace), disposal is
  ///     the caller's concern and this is a no-op.</summary>
  public async ValueTask DisposeAsync()
  {
    if (_ownsSupervisor)
    {
      await _supervisor.DisposeAsync().ConfigureAwait(false);
    }
  }
}
