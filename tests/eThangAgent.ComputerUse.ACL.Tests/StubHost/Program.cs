using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// Stub broker host for supervision tests: serves authenticate + hello, echoes a canned
// reply per request; a request whose method is 'crash' kills the process without replying.
// The auth token arrives via ETHANG_COMPUTER_USE_TOKEN (as the real broker receives it).
if (args.Length < 1)
{
    return 2;
}

using NamedPipeServerStream server = new(args[0], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await server.WaitForConnectionAsync();
string auth = await ReadLineAsync(server);
bool authorized;
using (JsonDocument d = JsonDocument.Parse(auth))
{
    string token = d.RootElement.GetProperty("params").GetProperty("token").GetString() ?? "";
    authorized = token == Environment.GetEnvironmentVariable("ETHANG_COMPUTER_USE_TOKEN");
}

await WriteAsync(server, "{" + Q("id") + ":0," + Q("result") + ":" + (authorized
    ? "{" + Q("ok") + ":true}"
    : "{" + Q("error") + ":{" + Q("code") + ":" + Q("not_authorized") + "," + Q("message") + ":" + Q("bad token") + "}}") + "}");
if (!authorized)
{
    return 3;
}

string hello = await ReadLineAsync(server);
int helloId = GetId(hello);
await WriteAsync(server, Reply(helloId, "{" + Q("protocol") + ":1," + Q("platform") + ":" + Q("windows") + "}"));
while (true)
{
    string line = await ReadLineAsync(server);
    if (line.Contains("crash", StringComparison.Ordinal))
    {
        return 9;
    }

    if (line.Contains("deny", StringComparison.Ordinal))
    {
        await WriteAsync(server, Reply(GetId(line), "{" + Q("action_sent") + ":false," + Q("dispatch_status") + ":" + Q("possibly_sent") + "," + Q("effect_evidence") + ":" + Q("unchanged") + "}"));
        continue;
    }

    await WriteAsync(server, Reply(GetId(line), "{" + Q("action_sent") + ":true," + Q("dispatch_status") + ":" + Q("accepted") + "}"));
}

static string Q(string name) => "\"" + name + "\"";

static string Reply(int id, string resultJson) => "{" + Q("id") + ":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + Q("result") + ":" + resultJson + "}";

static int GetId(string frame)
{
    using JsonDocument d = JsonDocument.Parse(frame);
    return d.RootElement.GetProperty("id").GetInt32();
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
