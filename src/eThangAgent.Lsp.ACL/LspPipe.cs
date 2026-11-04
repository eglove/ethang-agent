using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace eThangAgent.Lsp.ACL;

public static class LspProtocol
{
  public const int ProtocolVersion = 1;
  public const int MaxFrameBytes = 4 * 1024 * 1024;
}

public sealed class LspPipeClient : IDisposable
{
  private readonly NamedPipeClientStream _pipe;
  private readonly SemaphoreSlim _writeLock = new(1, 1);

  private LspPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

  public string WorkspaceId { get; private set; } = string.Empty;
  public int ProtocolVersion { get; private set; }

  public static async Task<LspPipeClient> ConnectAsync(string pipeName, string workspaceId, string appInstanceId, CancellationToken ct = default)
  {
    NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(ct).ConfigureAwait(false);
    LspPipeClient client = new(pipe);
    try
    {
      await client.HandshakeAsync(workspaceId, appInstanceId, ct).ConfigureAwait(false);
      return client;
    }
    catch
    {
      client.Dispose();
      throw;
    }
  }

  private async Task HandshakeAsync(string workspaceId, string appInstanceId, CancellationToken ct)
  {
    JsonObject hello = new()
    {
      ["id"] = 1,
      ["method"] = "hello",
      ["params"] = new JsonObject
      {
        ["protocolVersion"] = LspProtocol.ProtocolVersion,
        ["workspaceId"] = workspaceId,
        ["appInstanceId"] = appInstanceId
      }
    };
    await WriteLineAsync(hello.ToJsonString(), ct).ConfigureAwait(false);
    string line = await ReadLineAsync(ct).ConfigureAwait(false);
    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement root = doc.RootElement;
    if (root.TryGetProperty("error", out JsonElement err))
    {
      string code = err.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "unknown" : "unknown";
      if (code == "workspace-mismatch") throw new LspWorkspaceMismatchException("workspace-mismatch");
      throw new InvalidOperationException($"hello error: {code}");
    }
    JsonElement result = root.GetProperty("result");
    WorkspaceId = result.GetProperty("workspaceId").GetString() ?? string.Empty;
    ProtocolVersion = result.GetProperty("protocolVersion").GetInt32();
  }

  private async Task WriteLineAsync(string json, CancellationToken ct)
  {
    byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      await _pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
      await _pipe.FlushAsync(ct).ConfigureAwait(false);
    }
    finally
    {
      _ = _writeLock.Release();
    }
  }

  private async Task<string> ReadLineAsync(CancellationToken ct)
  {
    List<byte> lineBytes = [];
    byte[] buffer = new byte[8192];
    while (true)
    {
      int read = await _pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
      if (read == 0) throw new IOException("daemon closed during hello");
      for (int i = 0; i < read; i++)
      {
        if (buffer[i] == (byte)'\n')
        {
          return Encoding.UTF8.GetString([.. lineBytes]);
        }
        lineBytes.Add(buffer[i]);
        if (lineBytes.Count > LspProtocol.MaxFrameBytes) throw new IOException("frame too large");
      }
    }
  }

  public void Dispose()
  {
    _writeLock.Dispose();
    _pipe.Dispose();
  }
}

public sealed class LspDaemonServer : IDisposable
{
  private readonly string _pipeName;
  private readonly string _workspaceId;
  private readonly Func<Task> _onConnected;
  private readonly string _daemonInstanceId = Guid.NewGuid().ToString("N");
  private NamedPipeServerStream? _server;

  public LspDaemonServer(string pipeName, string workspaceId, Func<Task> onConnected)
  {
    _pipeName = pipeName;
    _workspaceId = workspaceId;
    _onConnected = onConnected;
  }

  public string DaemonInstanceId => _daemonInstanceId;

  public async Task ServeOnceAsync(CancellationToken ct = default)
  {
    _server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    await _server.WaitForConnectionAsync(ct).ConfigureAwait(false);
    string line = await ReadLineAsync(_server, ct).ConfigureAwait(false);
    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement p = doc.RootElement.GetProperty("params");
    string clientWs = p.GetProperty("workspaceId").GetString() ?? string.Empty;
    if (!string.Equals(clientWs, _workspaceId, StringComparison.Ordinal))
    {
      JsonObject reply = new() { ["id"] = 1, ["error"] = new JsonObject { ["code"] = "workspace-mismatch", ["message"] = "workspace mismatch" } };
      await WriteLineAsync(_server, reply.ToJsonString(), ct).ConfigureAwait(false);
      return;
    }
    JsonObject ack = new()
    {
      ["id"] = 1,
      ["result"] = new JsonObject
      {
        ["protocolVersion"] = LspProtocol.ProtocolVersion,
        ["workspaceId"] = _workspaceId,
        ["daemonPid"] = Environment.ProcessId,
        ["daemonInstanceId"] = _daemonInstanceId
      }
    };
    await WriteLineAsync(_server, ack.ToJsonString(), ct).ConfigureAwait(false);
    await _onConnected().ConfigureAwait(false);
  }

  private static async Task<string> ReadLineAsync(NamedPipeServerStream server, CancellationToken ct)
  {
    List<byte> lineBytes = [];
    byte[] buffer = new byte[8192];
    while (true)
    {
      int read = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
      if (read == 0) throw new IOException("client closed");
      for (int i = 0; i < read; i++)
      {
        if (buffer[i] == (byte)'\n')
        {
          return Encoding.UTF8.GetString([.. lineBytes]);
        }
        lineBytes.Add(buffer[i]);
        if (lineBytes.Count > LspProtocol.MaxFrameBytes) throw new IOException("frame too large");
      }
    }
  }

  private static async Task WriteLineAsync(NamedPipeServerStream server, string json, CancellationToken ct)
  {
    byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
    await server.WriteAsync(bytes, ct).ConfigureAwait(false);
    await server.FlushAsync(ct).ConfigureAwait(false);
  }

  public void Dispose() => _server?.Dispose();
}
