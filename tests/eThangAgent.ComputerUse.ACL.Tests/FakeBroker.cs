using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>An in-process fake broker over a real NamedPipeServerStream (no broker process
///     in unit tests): serves authenticate + hello, then runs one scenario.
///     Modes: ok | auth | version | echo | oversize | drop.</summary>
internal sealed class FakeBroker : IAsyncDisposable
{
  private readonly NamedPipeServerStream _server;
  private readonly Task _loopTask;
  private readonly string _mode;
  private readonly CancellationTokenSource _cts = new();

  private FakeBroker(NamedPipeServerStream server, string pipeName, string mode)
  {
    _server = server;
    PipeName = pipeName;
    _mode = mode;
    _loopTask = Task.Run(async () => await RunAsync().ConfigureAwait(true), CancellationToken.None);
  }

  public string PipeName { get; }

  /// <summary>Creates the server pipe and starts serving. The first client connect wins.</summary>
  public static FakeBroker Start(string mode)
  {
    string pipeName = "ethang-test-" + Guid.NewGuid().ToString("N");
    NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    return new FakeBroker(server, pipeName, mode);
  }

  public Task<NdjsonPipeClient> ConnectClientAsync()
    => NdjsonPipeClient.ConnectAsync(PipeName, "test-token", 1, "windows", ct: TestContext.Current.CancellationToken);

  private async Task RunAsync()
  {
    try
    {
      await _server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(true);
      string auth = await ReadLineAsync(_cts.Token).ConfigureAwait(true);
      ReceivedAuthId = ExtractId(auth);
      ReceivedAuth = ExtractToken(auth);
      if (_mode == "auth")
      {
        await WriteLineAsync(Frame(0, JsonValue(new { ok = false, code = "not_authorized", message = "bad token" })), _cts.Token).ConfigureAwait(true);
        return;
      }

      await WriteLineAsync(Frame(0, JsonValue(new { ok = true })), _cts.Token).ConfigureAwait(true);

      string hello = await ReadLineAsync(_cts.Token).ConfigureAwait(true);
      int helloId = ExtractId(hello);
      if (_mode == "version")
      {
        await WriteLineAsync(JsonEncoder.Reply(helloId, JsonValue(new { protocol = 999, platform = "linux" })), _cts.Token).ConfigureAwait(true);
        return;
      }

      await WriteLineAsync(JsonEncoder.Reply(helloId, JsonValue(new { protocol = 1, platform = "windows" })), _cts.Token).ConfigureAwait(true);

      if (_mode == "drop")
      {
        if (_server.IsConnected)
        {
          _server.Disconnect();
        }

        return;
      }

      while (!_cts.IsCancellationRequested)
      {
        string line = await ReadLineAsync(_cts.Token).ConfigureAwait(true);
        int id = ExtractId(line);
        if (_mode == "oversize")
        {
          await WriteLineAsync(JsonEncoder.Reply(id, JsonValue(new { big = new string('x', (65 * 1024 * 1024) + 100) })), _cts.Token).ConfigureAwait(true);
        }
        else
        {
          await WriteLineAsync(JsonEncoder.Reply(id, JsonValue(new { echo = true })), _cts.Token).ConfigureAwait(true);
        }
      }
    }
    catch (OperationCanceledException)
    {
      // disposed mid-run: the scenario is over
    }
    catch (IOException)
    {
      // client hung up - fine for drop and oversize scenarios
    }
  }

  public int ReceivedAuthId { get; private set; } = -1;
  public string ReceivedAuth { get; private set; } = "";

  private static int ExtractId(string frame)
  {
    using JsonDocument doc = JsonDocument.Parse(frame);
    return doc.RootElement.GetProperty("id").GetInt32();
  }

  private static string ExtractToken(string frame)
  {
    using JsonDocument doc = JsonDocument.Parse(frame);
    return doc.RootElement.GetProperty("params").GetProperty("token").GetString() ?? "";
  }

  private async Task<string> ReadLineAsync(CancellationToken ct)
  {
    StringBuilder sb = new();
    byte[] one = new byte[1];
    while (true)
    {
      int n = await _server.ReadAsync(one, ct).ConfigureAwait(true);
      if (n == 0)
      {
        throw new IOException("fake broker: pipe closed by client");
      }

      if (one[0] == (byte)'\n')
      {
        return sb.ToString();
      }

      _ = sb.Append((char)one[0]);
    }
  }

  private async Task WriteLineAsync(string text, CancellationToken ct)
  {
    byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
    await _server.WriteAsync(bytes, ct).ConfigureAwait(true);
    await _server.FlushAsync(ct).ConfigureAwait(true);
  }

  private static string Frame(int id, string resultJson) => "{\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"result\":" + resultJson + "}";

  private static string JsonValue(object value) => JsonSerializer.Serialize(value);

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    await _cts.CancelAsync().ConfigureAwait(true);
    try
    {
      if (_server.IsConnected)
      {
        _server.Disconnect();
      }
    }
    catch (IOException)
    {
      // already gone
    }

    await _server.DisposeAsync().ConfigureAwait(true);
    try
    {
      await _loopTask.ConfigureAwait(true);
    }
    catch (IOException)
    {
      // already recorded by RunAsync's own guard
    }

    _cts.Dispose();
  }
}

/// <summary>Builds reply frames with exact JSON shapes for the fake broker.</summary>
internal static class JsonEncoder
{
  public static string Reply(int id, string resultJson)
    => "{" + "\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"result\":" + resultJson + "}";
}
