namespace eThangAgent.ComputerUse.Host;

/// <summary>Physical-input serialization (task 16): while ONE physical input operation
///     is in flight, a second input method call is rejected BEFORE dispatch with
///     input_busy - nothing is queued and nothing is dispatched. The client maps the
///     code to TIMEOUT-retryable (controller ruling) with action_sent=false, so the
///     rejection must happen strictly before any SendInput side effect.</summary>
internal sealed class InputSerializer
{
  private readonly Lock _gate = new();
  private InputOperation? _inFlight;

  /// <summary>True while one input operation holds the gate.</summary>
  public bool IsBusy
  {
    get
    {
      _gate.Enter();
      try
      {
        return _inFlight is not null;
      }
      finally
      {
        _gate.Exit();
      }
    }
  }

  /// <summary>The method name of the in-flight operation, or null when idle. Exposed so
  ///     the input_busy details can name what the caller must wait for.</summary>
  public string? CurrentMethod
  {
    get
    {
      _gate.Enter();
      try
      {
        return _inFlight?.Method;
      }
      finally
      {
        _gate.Exit();
      }
    }
  }

  /// <summary>Begins an input operation: non-null ownership token when the gate was
  ///     free, null (nothing dispatched, nothing queued) when one is already in flight.</summary>
  public InputOperation? TryBegin(string method)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(method);
    _gate.Enter();
    try
    {
      if (_inFlight is not null)
      {
        return null;
      }

      InputOperation operation = new(this, method);
      _inFlight = operation;
      return operation;
    }
    finally
    {
      _gate.Exit();
    }
  }

  internal void End(InputOperation operation)
  {
    _gate.Enter();
    try
    {
      if (ReferenceEquals(_inFlight, operation))
      {
        _inFlight = null;
      }
    }
    finally
    {
      _gate.Exit();
    }
  }
};

/// <summary>Ownership token for one in-flight input operation: End (or Dispose)
///     releases the gate. Idempotent - a double End never frees someone else's slot.</summary>
public sealed class InputOperation : IDisposable
{
  private readonly InputSerializer _owner;
  private int _ended;

  internal InputOperation(InputSerializer owner, string method)
  {
    _owner = owner;
    Method = method;
  }

  public string Method { get; }

  public void End()
  {
    if (Interlocked.Exchange(ref _ended, 1) == 0)
    {
      _owner.End(this);
    }
  }

  /// <inheritdoc />
  public void Dispose() => End();
};
