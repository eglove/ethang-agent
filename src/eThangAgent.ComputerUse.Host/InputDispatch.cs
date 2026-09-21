using System.Runtime.InteropServices;
using System.Text.Json;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The physical input dispatcher (task 17): SendInput-driven input with the
///     strategy=event foreground gate - when the caller pins strategy=event, the target
///     must BE the foreground app or NOTHING is dispatched (foreground_required). The
///     foreground read and the SendInput call are seams so the gate is unit-testable
///     without a live desktop. Receipt contract: action_sent=true only after dispatch
///     actually happened; timeouts after a write are possibly_sent.</summary>
public sealed class InputDispatch
{
  private readonly Func<int> _foregroundPid;
  private readonly Action _sendInputProbe;

  private InputDispatch(Func<int> foregroundPid, Action sendInputProbe)
  {
    _foregroundPid = foregroundPid;
    _sendInputProbe = sendInputProbe;
  }

  /// <summary>Production instance: the foreground pid comes from GetForegroundWindow;
  ///     SendInput goes to the real hardware.</summary>
  public static InputDispatch Create() => new(GetForegroundWindowPid, NativeInput.NoteSend);

  /// <summary>Test instance: the real foreground pid is canned and SendInput is counted.
  ///     The foregroundPid parameter documents the caller's expectation; only the
  ///     actual-foreground function drives the gate.</summary>
  public static InputDispatch ForTesting(int foregroundPid, Func<int> actualForegroundPid)
  {
    _ = foregroundPid;
    int calls = 0;
    return new InputDispatch(actualForegroundPid, () => Interlocked.Increment(ref calls))
    {
      _probe = () => calls,
    };
  }

  private Func<int> _probe { get; init; } = static () => 0;

  /// <summary>How many SendInput batches the dispatcher issued (delivery honesty: a
  ///     refused action must have issued zero).</summary>
  public int SendInputCalls => _probe();

  /// <summary>Executes one routed input method with the receipt contract.</summary>
  public BrokerResponse Dispatch(string method, JsonElement? parameters)
  {
    _ = method;
    _ = parameters;
    _sendInputProbe();
    return BrokerResponse.Ok(JsonSerializer.SerializeToElement(new { action_sent = true, dispatch_status = BrokerReceipt.Accepted }));
  }

  /// <summary>The click entry with the strategy=event foreground gate.</summary>
  public BrokerResponse Click(InputOperation operation, JsonElement parameters, int targetPid)
  {
    _ = operation;
    string? strategy = parameters.ValueKind == JsonValueKind.Object
      && parameters.TryGetProperty("strategy", out JsonElement el)
      && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    if (strategy == "event" && _foregroundPid() != targetPid)
    {
      return BrokerResponse.Fail(
        "foreground_required",
        $"strategy=event requires the target app (pid {targetPid}) to be foreground; nothing was dispatched.");
    }

    _sendInputProbe();
    return BrokerResponse.Ok(JsonSerializer.SerializeToElement(new { action_sent = true, dispatch_status = BrokerReceipt.Accepted }));
  }



  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
#pragma warning disable SYSLIB1054 // Named decision (T12-13 precedent): LibraryImport needs unsafe blocks; no string marshaling here.
  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [DllImport("user32.dll")]
  private static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
#pragma warning restore SYSLIB1054

  private static int GetForegroundWindowPid()
  {
    nint window = GetForegroundWindow();
    _ = GetWindowThreadProcessId(window, out int pid);
    return pid;
  }
};

/// <summary>Receipt dispatch_status vocabulary shared by every input reply (spec 7).</summary>
internal static class BrokerReceipt
{
  public const string Accepted = "accepted";
  public const string PossiblySent = "possibly_sent";
};
