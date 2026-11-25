using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace eThangAgent.ComputerUse.Host;

/// <summary>Owns the broker's single UIA3 automation object. One instance serves the
///     process lifetime, and its connection and transaction timeouts are the bound on
///     every UIA call the broker makes: a provider that stops answering fails its call
///     instead of hanging the pipe request that triggered it.</summary>
public sealed class Uia3Session : IDisposable
{
  /// <summary>How long a UIA call waits for a provider to answer before failing.</summary>
  public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

  /// <summary>The bound on one UIA transaction. A walk is many transactions, so this is
  ///     the per-call ceiling rather than a whole-walk deadline.</summary>
  public static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(20);

  private readonly Lock _gate = new();
  private UIA3Automation? _automation;
  private bool _disposed;

  /// <summary>The live automation object, created on first use. Throws
  ///     <see cref="ObjectDisposedException"/> once the session is disposed.</summary>
  public UIA3Automation Automation
  {
    get
    {
      lock (_gate)
      {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _automation ??= new UIA3Automation
        {
          ConnectionTimeout = ConnectionTimeout,
          TransactionTimeout = TransactionTimeout,
        };
      }
    }
  }

  /// <summary>Wraps a window handle as a FlaUI element. A handle that no longer
  ///     resolves is the caller's typed failure, so this returns null rather than
  ///     throwing.</summary>
  public AutomationElement? FromHandle(nint window)
  {
    if (window == 0)
    {
      return null;
    }

    try
    {
      return Automation.FromHandle(window);
    }
    catch (FlaUI.Core.Exceptions.FlaUIException)
    {
      return null;
    }
  }

  public void Dispose()
  {
    UIA3Automation? automation;
    lock (_gate)
    {
      if (_disposed)
      {
        return;
      }

      _disposed = true;
      automation = _automation;
      _automation = null;
    }

    automation?.Dispose();
  }
}
