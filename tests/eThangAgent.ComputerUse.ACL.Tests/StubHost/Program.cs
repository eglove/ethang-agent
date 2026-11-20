using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using StubBroker;

// Stub broker host for supervision tests. It models the REAL broker's controller lease:
// one owner per connection, input refused with controller_busy unless the calling
// connection owns it, and controller_stop/stop_computer_control releasing it. A request
// whose method is 'crash' kills the process without replying.
// The auth token arrives via ETHANG_COMPUTER_USE_TOKEN (as the real broker receives it).
if (args.Length < 1)
{
    return 2;
}

using NamedPipeServerStream server = new(args[0], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await server.WaitForConnectionAsync();
if (!await AuthenticateAsync(server))
{
    return 3;
}

await HandshakeAsync(server);
LeaseState lease = new();
while (true)
{
    string line = await ReadLineAsync(server);
    if (line.Contains("crash", StringComparison.Ordinal))
    {
        return 9;
    }

    await HandleAsync(server, lease, line);
}

static async Task<bool> AuthenticateAsync(NamedPipeServerStream server)
{
    string auth = await ReadLineAsync(server);
    bool authorized;
    using (JsonDocument d = JsonDocument.Parse(auth))
    {
        string token = d.RootElement.GetProperty("params").GetProperty("token").GetString() ?? string.Empty;
        authorized = token == Environment.GetEnvironmentVariable("ETHANG_COMPUTER_USE_TOKEN");
    }

    await WriteAsync(server, authorized
        ? Wire.Reply(0, new { ok = true })
        : Wire.Fail(0, "not_authorized", "bad token"));
    return authorized;
}

static async Task HandshakeAsync(NamedPipeServerStream server)
{
    int helloId = Wire.GetId(await ReadLineAsync(server));
    await WriteAsync(server, Wire.Reply(helloId, new { protocol = 1, platform = "windows" }));
}

static async Task HandleAsync(NamedPipeServerStream server, LeaseState lease, string line)
{
    using JsonDocument frame = JsonDocument.Parse(line);
    int id = frame.RootElement.GetProperty("id").GetInt32();
    string method = frame.RootElement.GetProperty("method").GetString() ?? string.Empty;
    await WriteAsync(server, lease.Route(id, method));
}

static async Task<string> ReadLineAsync(NamedPipeServerStream server)
{
    StringBuilder sb = new();
    byte[] one = new byte[1];
    while (true)
    {
        int n = await server.ReadAsync(one);
        if (n == 0)
        {
            throw new IOException("closed");
        }

        if (one[0] == (byte)'\n')
        {
            return sb.ToString();
        }

        _ = sb.Append((char)one[0]);
    }
}

static async Task WriteAsync(NamedPipeServerStream server, string text)
{
    byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
    await server.WriteAsync(bytes);
}
