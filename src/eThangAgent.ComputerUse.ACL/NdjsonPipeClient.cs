using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>The NDJSON named-pipe client (task 12): connects to the broker pipe, runs the
///     authenticate + hello handshake, then serves strictly-validated requests with
///     monotonic ids from 1. One JSON object per newline-terminated line; frames above
///     BrokerProtocolConstants.MaxFrameBytes are rejected and the connection is closed.
///     Replies match requests by id; concurrent requests each complete on their own reply.
///     Thread-safe: requests may be issued concurrently; the wire serializes them.</summary>
public sealed class NdjsonPipeClient : IAsyncDisposable
{
  private readonly NamedPipeClientStream _pipe;
  private readonly SemaphoreSlim _writeLock = new(1, 1);
  private readonly SemaphoreSlim _stateLock = new(1, 1);
  private readonly Dictionary<int, TaskCompletionSource<BrokerReply>> _pending = [];
  private readonly CancellationTokenSource _readerCts = new();
  private int _nextId;
  private bool _disposed;

  private NdjsonPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

  /// <summary>Connects to the named pipe and runs the handshake: authenticate with the
  ///     token (fixed id 0, expects ok:true), then hello with the protocol version (expects
  ///     the same protocol and platform windows). Not-ready connects go through the
  ///     caller-supplied NotReadyPolicy. Auth failure throws BrokerAuthException; a hello
  ///     mismatch throws BrokerVersionException - never retried.</summary>
  public static async Task<NdjsonPipeClient> ConnectAsync(
      string pipeName,
      string token,
      int protocolVersion,
      string platform,
      NotReadyPolicy? notReady = null,
      CancellationToken ct = default)
  {
    NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    NotReadyPolicy policy = notReady ?? new NotReadyPolicy(TaskDelayer.Instance);
    // Attempt 1 is immediate; the schedule applies to the retries that follow.
    int attempt = 0;
    while (true)
    {
      try
      {
        // A server that never listens leaves ConnectAsync waiting forever; bound each
        // attempt so the not-ready schedule, not the pipe poll, drives the timing.
        await pipe.ConnectAsync(500, ct).ConfigureAwait(false);
        break;
      }
      catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException or TimeoutException)
      {
        if (!await policy.WaitBeforeRetryAsync(attempt + 1, ct).ConfigureAwait(false))
        {
          await pipe.DisposeAsync().ConfigureAwait(false);
          throw new BrokerTimeoutException("broker pipe never became ready within the not-ready schedule.", ex);
        }

        attempt++;
      }
    }

    NdjsonPipeClient client = new(pipe);
    try
    {
      await client.HandshakeAsync(token, protocolVersion, platform, ct).ConfigureAwait(false);
      client._nextId = 1; // handshake consumed ids 0 and 1; requests continue from 2
      _ = Task.Run(client.ReadLoopAsync, client._readerCts.Token);
      return client;
    }
    catch (Exception)
    {
      await client.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }


  private async Task HandshakeAsync(string token, int protocolVersion, string platform, CancellationToken ct)
  {
    // authenticate: fixed id 0, expects {ok: true}.
    BrokerRequest auth = BrokerRequest.Authenticate(token);
    BrokerReply authReply = await ExchangeAsync(auth, ct).ConfigureAwait(false);
    if (authReply.Error is not null || authReply.Result?.ValueKind != JsonValueKind.Object
        || !authReply.Result.Value.TryGetProperty("ok", out JsonElement okEl)
        || okEl.ValueKind != JsonValueKind.True)
    {
      string message = authReply.Error is null ? "authenticate returned ok != true." : authReply.Error.Message;
      throw new BrokerAuthException(message);
    }

    // hello: protocol version exchange; mismatch is fatal and never retried.
    BrokerRequest hello = new(1, "hello", JsonSerializer.SerializeToElement(new HelloParams(protocolVersion, platform), BrokerRequest.WireOptions));
    BrokerReply helloReply = await ExchangeAsync(hello, ct).ConfigureAwait(false);
    if (helloReply.Error is not null)
    {
      throw new BrokerVersionException(helloReply.Error.Message);
    }

    JsonElement? result = helloReply.Result;
    if (result?.ValueKind != JsonValueKind.Object)
    {
      throw new BrokerVersionException("hello returned no result object.");
    }

    // Fail closed: BOTH keys are mandatory. A hello without them is a version mismatch.
    if (!result.Value.TryGetProperty("protocol", out JsonElement protoEl)
        || protoEl.ValueKind != JsonValueKind.Number || !protoEl.TryGetInt32(out int brokerVersion))
    {
      throw new BrokerVersionException("hello result is missing the protocol version.");
    }

    if (brokerVersion != protocolVersion)
    {
      throw new BrokerVersionException($"protocol version mismatch: client {protocolVersion}, broker {brokerVersion}.");
    }

    if (!result.Value.TryGetProperty("platform", out JsonElement platEl) || platEl.ValueKind != JsonValueKind.String)
    {
      throw new BrokerVersionException("hello result is missing the platform.");
    }

    string? brokerPlatform = platEl.GetString();
    if (brokerPlatform is null || !string.Equals(brokerPlatform, platform, StringComparison.OrdinalIgnoreCase))
    {
      throw new BrokerVersionException($"platform mismatch: client {platform}, broker {brokerPlatform ?? "null"}.");
    }
  }

  /// <summary>Sends one request frame and awaits the reply with the matching id.</summary>
  public async Task<BrokerReply> RequestAsync(string method, JsonElement? parameters, CancellationToken ct = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    int id = Interlocked.Increment(ref _nextId);
    BrokerRequest request = BrokerRequest.Create(id, method, parameters);
    TaskCompletionSource<BrokerReply> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await _stateLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      _pending[id] = completion;
    }
    finally
    {
      _ = _stateLock.Release();
    }

    try
    {
      await WriteFrameAsync(request.ToJson(), ct).ConfigureAwait(false);
    }
    catch (Exception)
    {
      await DiscardPendingAsync(id).ConfigureAwait(false);
      throw;
    }

    try
    {
      return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    catch (Exception)
    {
      await DiscardPendingAsync(id).ConfigureAwait(false);
      throw;
    }
  }

  private async Task<BrokerReply> ExchangeAsync(BrokerRequest request, CancellationToken ct)
  {
    await WriteFrameAsync(request.ToJson(), ct).ConfigureAwait(false);
    // Handshake frames are sequential; read the next line directly.
    string line = await ReadLineAsync(ct).ConfigureAwait(false);
    return BrokerReply.Parse(line) ?? throw new BrokerProtocolException("handshake reply was not a well-formed reply frame.");
  }

  private async Task WriteFrameAsync(string json, CancellationToken ct)
  {
    byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      if (bytes.Length > BrokerProtocolConstants.MaxFrameBytes)
      {
        throw new BrokerProtocolException($"frame of {bytes.Length} bytes exceeds the {BrokerProtocolConstants.MaxFrameBytes}-byte ceiling; connection closed.");
      }

      await _pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
      await _pipe.FlushAsync(ct).ConfigureAwait(false);
    }
    finally
    {
      _ = _writeLock.Release();
    }
  }

  /// <summary>The reader loop: newline-delimited frames, oversize rejection (connection
  ///     closed), id-matched completion of pending requests. Oversize or malformed frames
  ///     fault every pending request and end the loop - the wire is never trusted again.</summary>
  private async Task ReadLoopAsync()
  {
    CancellationToken ct = _readerCts.Token;
    List<byte> lineBytes = [];
    byte[] buffer = new byte[8192];
    try
    {
      while (!ct.IsCancellationRequested)
      {
        int read = await _pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read == 0)
        {
          break; // broker closed the pipe
        }

        for (int i = 0; i < read; i++)
        {
          if (buffer[i] == (byte)'\n')
          {
            // Newline is ASCII 0x0A and never appears inside a multi-byte UTF-8 sequence or an
            // unescaped JSON string, so byte-splitting is safe; decode the full line as UTF-8.
            string frame = Encoding.UTF8.GetString([.. lineBytes]);
            lineBytes.Clear();
            await HandleLineAsync(frame).ConfigureAwait(false);
          }
          else
          {
            lineBytes.Add(buffer[i]);
            if (lineBytes.Count > BrokerProtocolConstants.MaxFrameBytes)
            {
              throw new BrokerProtocolException($"frame exceeds the {BrokerProtocolConstants.MaxFrameBytes}-byte ceiling; closing connection.");
            }
          }
        }
      }
    }
    catch (OperationCanceledException)
    {
      // disposed - normal shutdown
    }
#pragma warning disable CA1031 // Named decision: the reader loop is a boundary; any fault there must fault pending requests, never crash the loop thread.
    catch (Exception ex)
    {
      await FaultAllPendingAsync(new BrokerProtocolException(ex.Message)).ConfigureAwait(false);
      return;
    }
#pragma warning restore CA1031

    await FaultAllPendingAsync(new BrokerConnectionClosedException()).ConfigureAwait(false);
  }

  private async Task HandleLineAsync(string frame)
  {
    if (frame.Length > BrokerProtocolConstants.MaxFrameBytes)
    {
      throw new BrokerProtocolException($"frame exceeds the {BrokerProtocolConstants.MaxFrameBytes}-byte ceiling; closing connection.");
    }

    BrokerReply reply = BrokerReply.Parse(frame)
      ?? throw new BrokerProtocolException("malformed reply frame.");

    TaskCompletionSource<BrokerReply>? completion = null;
    await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    try
    {
      if (_pending.Remove(reply.Id, out TaskCompletionSource<BrokerReply>? found))
      {
        completion = found;
      }
    }
    finally
    {
      _ = _stateLock.Release();
    }

    _ = completion?.TrySetResult(reply);
  }

  private async Task DiscardPendingAsync(int id)
  {
    await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    try
    {
      _ = _pending.Remove(id);
    }
    finally
    {
      _ = _stateLock.Release();
    }
  }

  private async Task FaultAllPendingAsync(Exception error)
  {
    TaskCompletionSource<BrokerReply>[] completions;
    await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    try
    {
      completions = [.. _pending.Values];
      _pending.Clear();
    }
    finally
    {
      _ = _stateLock.Release();
    }

    foreach (TaskCompletionSource<BrokerReply> completion in completions)
    {
      _ = completion.TrySetException(error);
    }
  }

  // Retained for handshake-time direct reads if needed in future; the current handshake
  // reads via the reader loop only after it starts, so direct reads use this helper.
  private async Task<string> ReadLineAsync(CancellationToken ct)
  {
    List<byte> lineBytes = [];
    byte[] buffer = new byte[1];
    while (true)
    {
      int read = await _pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
      if (read == 0)
      {
        throw new BrokerConnectionClosedException();
      }

      if (buffer[0] == (byte)'\n')
      {
        return Encoding.UTF8.GetString([.. lineBytes]);
      }

      lineBytes.Add(buffer[0]);
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    await _readerCts.CancelAsync().ConfigureAwait(false);
    await FaultAllPendingAsync(new BrokerConnectionClosedException()).ConfigureAwait(false);
    await _pipe.DisposeAsync().ConfigureAwait(false);
    _writeLock.Dispose();
    _stateLock.Dispose();
    _readerCts.Dispose();
    GC.SuppressFinalize(this);
  }
}

/// <summary>Authenticate failed: the token was rejected. Never retried.</summary>
public sealed class BrokerAuthException : Exception
{
  public BrokerAuthException() : this("authenticate failed.") { }
  public BrokerAuthException(string message) : base(message) { }
  public BrokerAuthException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Protocol version or platform mismatch at hello. Never retried.</summary>
public sealed class BrokerVersionException : Exception
{
  public BrokerVersionException() : this("hello protocol/platform mismatch.") { }
  public BrokerVersionException(string message) : base(message) { }
  public BrokerVersionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The broker closed the pipe or the frame stream ended.</summary>
public sealed class BrokerConnectionClosedException : Exception
{
  public BrokerConnectionClosedException() : base("broker connection closed.") { }
  public BrokerConnectionClosedException(string message) : base(message) { }
  public BrokerConnectionClosedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The peer violated the protocol: malformed or oversize frame.</summary>
public sealed class BrokerProtocolException : Exception
{
  public BrokerProtocolException() : this("protocol violation.") { }
  public BrokerProtocolException(string message) : base(message) { }
  public BrokerProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The broker pipe never became ready within the not-ready schedule.</summary>
public sealed class BrokerTimeoutException : Exception
{
  public BrokerTimeoutException() : this("broker pipe never became ready.") { }
  public BrokerTimeoutException(string message) : base(message) { }
  public BrokerTimeoutException(string message, Exception innerException) : base(message, innerException) { }
}
