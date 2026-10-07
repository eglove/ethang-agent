using System.Diagnostics;
using System.Text;
using System.Text.Json;
using eThangAgent.ToolDomain.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace eThangAgent.Mcp.ACL;

/// <summary>One CONTAINED stdio server session (issue #105): the harness spawns the
///     server process itself - the SDK's StdioClientTransport offers no process-started
///     hook, so a kill-on-close Job Object could never attach to its spawn - with the
///     allowlisted environment (SDK defaults plus the server's configured entries,
///     never the parent process env), attaches the job, pumps stderr into a bounded
///     tail, and hands the process streams to the SDK's StreamClientTransport. Dispose
///     disposes the client (closing the streams), kills the process tree, and closes
///     the job (reaping any survivor). The kernel path (B2): the harness dying closes
///     the job handle and the OS reaps the server.</summary>
public sealed class ContainedStdioSession : IMcpClientSession
{
  private readonly McpClient _client;
  private readonly Process _process;
  private readonly nint _job;
  private readonly StderrTail _stderr;
  private readonly Lock _gate = new();
  private bool _reaped;

  internal ContainedStdioSession(McpClient client, Process process, nint job, StderrTail stderr)
  {
    _client = client;
    _process = process;
    _job = job;
    _stderr = stderr;
  }

  /// <summary>The spawned server's OS process id, or null once it has exited (the id
  ///     is meaningless after exit; tests use it to observe the reaping).</summary>
  public int? ServerProcessId => _process.HasExited ? null : _process.Id;

  /// <summary>Whether the server process has exited (the pool's reconnect trigger;
  ///     the IMcpClientSession default is false, this is the real probe).</summary>
  public bool HasExited => _process.HasExited;

  /// <summary>The bounded stderr tail (the last captured lines, oldest first).</summary>
  public string StderrTail => _stderr.Snapshot();

  /// <inheritdoc />
  public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
  {
    IList<McpClientTool> tools = await _client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
    return [.. tools.Select(t => new McpToolInfo(t.Name, t.Description,
        t.ProtocolTool.Annotations?.ReadOnlyHint, t.ProtocolTool.Annotations?.DestructiveHint))];
  }

  /// <inheritdoc />
  public async Task<McpToolCallResult> CallToolAsync(
      string tool, string argumentsJson, CancellationToken ct = default)
  {
    Dictionary<string, object?> arguments = ParseArguments(argumentsJson);
    CallToolResult result = await _client
        .CallToolAsync(tool, arguments, cancellationToken: ct).ConfigureAwait(false);
    return new McpToolCallResult(Render(result), result.IsError == true);
  }

  /// <summary>Waits for the server process to exit (the crashed-session test's
  ///     observation point; the pool never waits - it polls HasExited).</summary>
  public Task WaitForExitAsync(CancellationToken ct = default) => _process.WaitForExitAsync(ct);

  /// <summary>Test seam: the contained Process object (the reaping tests hold it,
  ///     whose HasExited/WaitForExitAsync stay valid after the process dies).</summary>
  internal Process TestProcess() => _process;

  /// <summary>Test seam (the Job Object's kernel path): closes ONLY the job handle -
  ///     a kill-on-close job reaps the server. Proves B2 is the job's work, not the
  ///     explicit kill's.</summary>
  internal void TestCloseJob()
  {
    lock (_gate)
    {
      if (!_reaped)
      {
        _ = JobObject.Close(_job);
      }
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    try
    {
      await _client.DisposeAsync().ConfigureAwait(false);
    }
    catch (ObjectDisposedException)
    {
      // Named decision: a crashed server already tore the transport down.
    }

    lock (_gate)
    {
      if (!_reaped)
      {
        _reaped = true;
        try
        {
          if (!_process.HasExited)
          {
            _process.Kill(entireProcessTree: true);
          }
        }
        catch (InvalidOperationException)
        {
          // already exited - best-effort kill per the contract
        }
#pragma warning disable CA1031 // named decision: a reaped process cannot be killed twice; dispose is best-effort by contract
        catch (Exception)
        {
          // already exited or already reaped
        }
#pragma warning restore CA1031

        _ = JobObject.Close(_job);
      }
    }

    _process.Dispose();
  }

  private static Dictionary<string, object?> ParseArguments(string argumentsJson)
  {
    using JsonDocument doc = JsonDocument.Parse(argumentsJson);
    Dictionary<string, object?> parsed = [];
    foreach (JsonProperty property in doc.RootElement.EnumerateObject())
    {
      parsed[property.Name] = property.Value.Clone();
    }

    return parsed;
  }

  /// <summary>Renders the result's content blocks as the model-facing text: text
  ///     blocks verbatim (joined with newlines), other block kinds as typed
  ///     placeholders so the content is never silently dropped.</summary>
  private static string Render(CallToolResult result)
  {
    if (result.Content.Count == 0)
    {
      return string.Empty;
    }

    StringBuilder rendered = new();
    foreach (ContentBlock block in result.Content)
    {
      if (rendered.Length > 0)
      {
        _ = rendered.Append('\n');
      }

      _ = rendered.Append(block switch
      {
        TextContentBlock text => text.Text,
        ImageContentBlock => "[image content withheld: MCP tool returned an image]",
        AudioContentBlock => "[audio content withheld: MCP tool returned audio]",
        _ => "[" + block.Type + " content]",
      });
    }

    return rendered.ToString();
  }
}

/// <summary>The bounded stderr ring: the last N lines of a server's stderr, kept for
///     diagnosis without polluting tool results or tripping error heuristics (B3).</summary>
public sealed class StderrTail(int capacity = 50)
{
  private readonly Queue<string> _lines = new(capacity);
  private readonly Lock _gate = new();
  private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

  /// <summary>Appends one stderr line; the oldest line drops once the ring is full.</summary>
  public void Append(string line)
  {
    lock (_gate)
    {
      while (_lines.Count >= _capacity)
      {
        _ = _lines.Dequeue();
      }

      _lines.Enqueue(line);
    }
  }

  /// <summary>The captured lines joined with newlines (empty when none were).</summary>
  public string Snapshot()
  {
    lock (_gate)
    {
      return string.Join(Environment.NewLine, _lines);
    }
  }
}
