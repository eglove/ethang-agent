using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace eThangAgent.ComputerUse.Host;

/// <summary>Owns the broker's single UIA3 automation object. FlaUI's automation is
///     thread-affine COM: every element it produces is a proxy that must be reached
///     from the thread that created it. The broker therefore keeps one instance for
///     the process lifetime and reaches it only from the STA walk worker, which is
///     the thread that created it.</summary>
public sealed class Uia3Session : IDisposable
{
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
        return _automation ??= new UIA3Automation();
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
