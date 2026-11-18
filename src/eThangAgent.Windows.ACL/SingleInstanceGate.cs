using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace eThangAgent.Windows.ACL;

/// <summary>The text a signalling process sent to the process that owns the name.</summary>
/// <param name="payload">The payload the signaller passed.</param>
public sealed class SignalledEventArgs(string payload) : EventArgs
{
  /// <summary>The payload the signaller passed.</summary>
  public string Payload { get; } = payload;
}

/// <summary>Ownership of one name across processes, with a one-way message channel to the
///     owner.
///     <para>A MUTEX ALONE IS NOT ENOUGH: it answers 'is this name taken' but carries no
///     payload, so a second launch could not tell the first which window to raise. A PIPE
///     ALONE IS NOT ENOUGH: two processes racing to create one can both succeed in turn, so
///     'the pipe existed' is not ownership. The mutex decides who owns the name and the pipe
///     carries what the owner needs to hear.</para>
///     <para>Ownership survives a crash: a mutex abandoned by a dead process is acquired by
///     the next caller rather than blocking it forever, which is the behavior a launcher
///     needs after a hard kill.</para></summary>
public sealed class SingleInstanceGate : IDisposable
{
  /// <summary>The names this process currently owns.
  ///     <para>THE MUTEX CANNOT ANSWER THIS BY ITSELF: a mutex is reentrant for the thread
  ///     that already holds it, so a second acquisition on the same thread succeeds and one
  ///     process would believe it owns a name twice. The set answers within the process; the
  ///     mutex answers across processes.</para></summary>
  private static readonly HashSet<string> Owned = [];

  private static readonly Lock OwnedLock = new();

  private readonly Mutex _mutex;
  private readonly string _name;
  private readonly string _pipeName;
  private readonly CancellationTokenSource _cts = new();
  private readonly Task _server;
  private int _disposed;

  /// <summary>Raised with the payload a signalling process sent. Delivered on a background
  ///     thread; a UI consumer marshals it to its own thread.</summary>
  public event EventHandler<SignalledEventArgs>? Signalled;

  private SingleInstanceGate(Mutex mutex, string name, string pipeName)
  {
    _mutex = mutex;
    _name = name;
    _pipeName = pipeName;
    _server = Task.Run(() => ServeAsync(_cts.Token), _cts.Token);
  }

  /// <summary>The pipe name a signaller connects to for <paramref name="name"/>.</summary>
  /// <param name="name">The instance name.</param>
  public static string PipeNameFor(string name) => name + ".signal";

  /// <summary>Takes ownership of <paramref name="name"/>, or returns null when another
  ///     process already owns it.</summary>
  /// <param name="name">The instance name; two callers using one name contend.</param>
  public static SingleInstanceGate? TryAcquire(string name)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    lock (OwnedLock)
    {
      if (!Owned.Add(name))
      {
        return null;
      }
    }

    Mutex mutex = new(initiallyOwned: false, name);
    bool owned;
    try
    {
      owned = mutex.WaitOne(0);
    }
    catch (AbandonedMutexException)
    {
      // The previous owner died without releasing: the name is free and now ours.
      owned = true;
    }

    if (!owned)
    {
      mutex.Dispose();
      lock (OwnedLock)
      {
        _ = Owned.Remove(name);
      }

      return null;
    }

    return new SingleInstanceGate(mutex, name, PipeNameFor(name));
  }

  /// <summary>Sends <paramref name="payload"/> to whichever process owns
  ///     <paramref name="name"/>. Returns false when no process answers, which is the
  ///     ordinary case when nothing holds the name.</summary>
  /// <param name="name">The instance name.</param>
  /// <param name="payload">The text the owner receives.</param>
  /// <param name="timeout">How long to wait for the owner to accept the connection.</param>
  public static bool TrySignal(string name, string payload, TimeSpan timeout)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentNullException.ThrowIfNull(payload);
    // The owner recreates its pipe between connections, so a signaller can arrive in the
    // gap. A short retry window covers that without making a genuinely absent owner wait.
    DateTime deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
      try
      {
        using NamedPipeClientStream client = new(".", PipeNameFor(name),
            PipeDirection.Out, PipeOptions.None);
        client.Connect((int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds));
        byte[] bytes = Encoding.UTF8.GetBytes(payload + "\n");
        client.Write(bytes, 0, bytes.Length);
        client.Flush();
        return true;
      }
      catch (TimeoutException)
      {
        return false;
      }
      catch (IOException)
      {
        // The owner is between connections; try again inside the window.
      }
    }

    return false;
  }

  /// <summary>The name a workspace's instance takes: derived only from the normalized root,
  ///     so two processes opening one directory contend and two directories never do.
  /// </summary>
  /// <param name="normalizedRoot">The workspace's normalized directory path.</param>
  public static string NameFor(string normalizedRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRoot);
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
        normalizedRoot.ToUpperInvariant()));
    return "eThangAgent.ws." + Convert.ToHexString(hash)[..16];
  }

  private async Task ServeAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      try
      {
        // Named decision (CA2007): the server's lifetime is the loop's, and its disposal
        // must not be deferred to a continuation.
#pragma warning disable CA2007
        await using NamedPipeServerStream server = new(_pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
        using StreamReader reader = new(server, Encoding.UTF8);
        string? payload = await reader.ReadLineAsync(ct).ConfigureAwait(false);
#pragma warning restore CA2007
        if (!string.IsNullOrEmpty(payload))
        {
          Signalled?.Invoke(this, new SignalledEventArgs(payload));
        }
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (IOException)
      {
        // A connection that broke mid-read is not fatal: the next launch may still signal.
      }
    }
  }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
    {
      return;
    }

    lock (OwnedLock)
    {
      _ = Owned.Remove(_name);
    }

    _cts.Cancel();
    _cts.Dispose();
    try
    {
      _mutex.ReleaseMutex();
    }
    catch (ApplicationException)
    {
      // Not owned on this thread: the release is best effort, and the process exit
      // releases it anyway.
    }

    _mutex.Dispose();
    // The server task observes the cancelled token; awaiting it here would block a
    // teardown that must not wait on a pipe.
    _ = _server.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
  }
}
