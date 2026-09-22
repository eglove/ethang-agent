using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The broker's NDJSON serve loop. Spec 4 arbitrates CONCURRENT controllers, so the
///     loop serves MULTIPLE concurrent pipe connections (C6): each accepted connection gets its
///     own serve task and connection id; the PipeServer arbitrates the single controller lease
///     across them (first takeover wins, rivals get controller_busy with the owner id). newline-delimited request frames in, exactly one reply frame out
///     per request; UTF-8 both ways; frames above the ceiling are refused and the
///     connection closes (the wire is never trusted past a framing violation). The
///     loop assigns each connection a monotonically increasing connection id, which
///     Dispatch uses for authentication state and lease ownership; when a connection
///     ends (EOF, write failure, oversize), DropConnection releases the lease if that
///     connection owned it - the A3 auto-expiry.</summary>
public static class PipeServeLoop
{
  /// <summary>Serves ONE connection end-to-end on the given server stream: waits for
  ///     the client, runs the request loop until the pipe drops, then returns. The
  ///     caller owns the stream's lifetime.</summary>
  public static async Task ServeOnceAsync(NamedPipeServerStream server, PipeServer broker)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentNullException.ThrowIfNull(broker);
    await server.WaitForConnectionAsync().ConfigureAwait(false);
    int connectionId = broker.NextConnectionId();
    try
    {
      await RunConnectionAsync(server, broker, connectionId).ConfigureAwait(false);
    }
    finally
    {
      broker.DropConnection(connectionId);
    }
  }

  /// <summary>C6: the production multi-connection serve loop. The FACTORY mints a fresh
  ///     NamedPipeServerStream per accepted connection (a pipe instance serves exactly one
  ///     client); the accept loop keeps creating instances until cancelled. Every connection
  ///     runs on its own task with its own connection id; the single controller lease in the
  ///     shared PipeServer arbitrates across all of them.</summary>
  public static async Task ServeConcurrentAsync(
      Func<NamedPipeServerStream> connectionStreamFactory,
      PipeServer broker,
      CancellationToken acceptLoopCancellationToken)
  {
    ArgumentNullException.ThrowIfNull(connectionStreamFactory);
    ArgumentNullException.ThrowIfNull(broker);
    List<Task> connectionTasks = [];
    try
    {
      while (!acceptLoopCancellationToken.IsCancellationRequested)
      {
        NamedPipeServerStream stream = connectionStreamFactory();
        await stream.WaitForConnectionAsync(acceptLoopCancellationToken).ConfigureAwait(false);
        int connectionId = broker.NextConnectionId();
        connectionTasks.Add(Task.Run(async () =>
        {
          try
          {
            await RunConnectionAsync(stream, broker, connectionId).ConfigureAwait(false);
          }
          catch (IOException)
          {
            // The client dropped mid-frame; the lease auto-expires via DropConnection.
          }
          finally
          {
            broker.DropConnection(connectionId);
          }
        }, acceptLoopCancellationToken));
      }
    }
    catch (OperationCanceledException)
    {
      // The accept loop was cancelled; existing connections drain via their own tasks.
    }

    await Task.WhenAll(connectionTasks).ConfigureAwait(false);
  }
  private static async Task RunConnectionAsync(NamedPipeServerStream server, PipeServer broker, int connectionId)
  {
    List<byte> lineBytes = [];
    byte[] buffer = new byte[8192];
    while (true)
    {
      int read = await server.ReadAsync(buffer).ConfigureAwait(false);
      if (read == 0)
      {
        return; // client hung up
      }

      for (int i = 0; i < read; i++)
      {
        if (buffer[i] == (byte)'\n')
        {
          // Newline is ASCII 0x0A, never part of a multi-byte UTF-8 sequence or an
          // unescaped JSON string, so byte splitting is safe; the whole line decodes as UTF-8.
          string frame = Encoding.UTF8.GetString([.. lineBytes]);
          lineBytes.Clear();
          bool keepGoing = await HandleFrameAsync(server, broker, connectionId, frame).ConfigureAwait(false);
          if (!keepGoing)
          {
            return;
          }
        }
        else
        {
          lineBytes.Add(buffer[i]);
          if (lineBytes.Count > BrokerConfig.MaxFrameBytes)
          {
            // A framing violation means the wire is not trustworthy; answer once, then close.
            await WriteErrorAsync(server, BrokerResponse.Fail("invalid_request", $"frame exceeds the {BrokerConfig.MaxFrameBytes}-byte ceiling; closing.")).ConfigureAwait(false);
            return;
          }
        }
      }
    }
  }

  /// <summary>Dispatches one decoded frame and writes exactly one reply. False means
  ///     the connection must close (fatal framing). A reply whose payload would breach
  ///     the ceiling is refused: the error answers once and the connection closes -
  ///     delivery-state honesty beats a corrupted stream.</summary>
  private static async Task<bool> HandleFrameAsync(NamedPipeServerStream server, PipeServer broker, int connectionId, string frame)
  {
    if (frame.Length > BrokerConfig.MaxFrameBytes)
    {
      await WriteErrorAsync(server, BrokerResponse.Fail("invalid_request", $"frame exceeds the {BrokerConfig.MaxFrameBytes}-byte ceiling; closing.")).ConfigureAwait(false);
      return false;
    }

    DispatchedReply dispatched = broker.DispatchRaw(frame, connectionId);
    string reply = FrameFor(dispatched.Response, dispatched.RequestId);
    if (Encoding.UTF8.GetByteCount(reply) > BrokerConfig.MaxFrameBytes)
    {
      await WriteErrorAsync(server, BrokerResponse.Fail("internal", "reply exceeds the frame ceiling; closing.")).ConfigureAwait(false);
      return false;
    }

    await WriteLineAsync(server, reply).ConfigureAwait(false);
    return true;
  }

  /// <summary>Renders one BrokerResponse as a reply frame: {id, result} or
  ///     {id, error:{code, message, details}}. A response with neither payload nor
  ///     code (an observer that returned nothing) renders as unimplemented - the wire
  ///     never carries an empty reply.</summary>
  internal static string FrameFor(BrokerResponse response, int id = -1)
  {
    string idJson = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    if (response.Error is { } error)
    {
      // The details value renders FLAT inside the error object (standing ruling):
      // "details":"<string>" - never a nested {details:...} object the client cannot parse.
      string details = error.Details is null
        ? string.Empty
        : "," + Quote("details") + ":" + JsonSerializer.Serialize(error.Details);
      return "{" + Quote("id") + ":" + idJson + "," + Quote("error") + ":{" + Quote("code") + ":" + JsonSerializer.Serialize(error.Code)
        + "," + Quote("message") + ":" + JsonSerializer.Serialize(error.Message) + details + "}}";
    }

    if (response.Result is { } result
        && Encoding.UTF8.GetByteCount(result.GetRawText()) > BrokerConfig.MaxFrameBytes)
    {
      // Refuse to render an oversized payload: the wire is never corrupted by a
      // too-big observation. The internal error answers once and the loop closes.
      return FrameFor(BrokerResponse.Fail("internal", "reply exceeds the frame ceiling; closing."), id);
    }

    return response.Result is not { }
      ? FrameFor(BrokerResponse.Fail("unimplemented", "method is not implemented on this broker."), id)
      : "{" + Quote("id") + ":" + idJson + "," + Quote("result") + ":" + response.Result.Value.GetRawText() + "}";
  }

  private static async Task WriteErrorAsync(NamedPipeServerStream server, BrokerResponse response)
  {
    try
    {
      await WriteLineAsync(server, FrameFor(response)).ConfigureAwait(false);
    }
    catch (IOException)
    {
      // The pipe died while we were reporting its death; nothing left to do.
    }
  }

  private static async Task WriteLineAsync(NamedPipeServerStream server, string text)
  {
    byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
    await server.WriteAsync(bytes).ConfigureAwait(false);
    await server.FlushAsync().ConfigureAwait(false);
  }

  private static string Quote(string name) => "\"" + name + "\"";
};
