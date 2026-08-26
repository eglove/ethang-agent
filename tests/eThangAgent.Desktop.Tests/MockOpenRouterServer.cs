using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace eThangAgent.Desktop.Tests;

public sealed class MockOpenRouterServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<string> _scriptedResponses = new();
    private readonly Dictionary<string, Queue<string>> _modelScripts = new();
    private Task? _loop;

    public List<string> RequestBodies { get; } = new();

    public string BaseUrl { get; private set; } = "";

    /// <summary>Body of the most recent chat/completions request, for asserting what the CLI sent.</summary>
    public string? LastChatRequestBody { get; private set; }

    public void Start()
    {
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}"; // devskim: ignore DS162092 - E2E mock provider server must bind to loopback
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public MockOpenRouterServer Returns(string responseJson)
    {
        _scriptedResponses.Enqueue(responseJson);
        return this;
    }

    /// <summary>Scripts turns for a specific request model: when a chat request body's
    ///     top-level "model" field matches, turns are served from that model's queue
    ///     (first call => first response) instead of the default script. Lets one mock
    ///     server play both parent and child in a nested-spawn session.</summary>
    public MockOpenRouterServer ReturnsForModel(string model, params string[] responseJsons)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("model is required.", nameof(model));
        if (!_modelScripts.TryGetValue(model, out var queue))
            queue = _modelScripts[model] = new Queue<string>();
        foreach (var response in responseJsons)
            queue.Enqueue(response);
        return this;
    }

    /// <summary>Extracts the top-level "model" field from a chat request body,
    ///     or null when the body is not an object or carries no string model.</summary>
    public static string? TryGetRequestModel(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Placeholder replaced with the most recently observed child-agent id before a
    ///     scripted response is served: child ids are runtime Guids no static script can
    ///     predict, so scripts reference them only through this placeholder.</summary>
    public const string ChildIdPlaceholder = "{{child_id}}";

    /// <summary>Agent-id annotation inside a tool message. The async contract renders
    ///     'id=&lt;guid&gt; status=…' lines (spawn/status results); the legacy '[agent] '
    ///     gutter prefix is accepted so canned bodies in either shape substitute.</summary>
    private static readonly Regex AgentIdAnnotation =
        new(@"(?:\[agent\]\s+)?id=([0-9a-fA-F-]{36})", RegexOptions.Compiled);

    /// <summary>Extracts the guid from the MOST RECENT tool-role message whose content carries
    ///     an agent-id annotation, or null when no tool message matches. The request body is
    ///     decoded first — raw JSON escapes quotes and would corrupt the match.</summary>
    public static Guid? TryGetMostRecentAgentId(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.ValueKind is not JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("messages", out var messages))
                return null;

            Guid? last = null;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("role", out var role)
                    && role.GetString() == "tool"
                    && message.TryGetProperty("content", out var content)
                    && content.GetString() is { } text)
                {
                    var match = AgentIdAnnotation.Match(text);
                    if (match.Success)
                        last = Guid.Parse(match.Groups[1].Value);
                }
            }
            return last;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Picks the next scripted response for a chat request — the request model's
    ///     queue, then the default script, then the pineapple fallback — and applies
    ///     {{child_id}} substitution before the body is served.</summary>
    private string NextScriptedBody(string requestBody)
    {
        string body;
        var model = TryGetRequestModel(requestBody);
        if (model is not null
            && _modelScripts.TryGetValue(model, out var scripted)
            && scripted.Count > 0)
            body = scripted.Dequeue();
        else if (_scriptedResponses.Count > 0)
            body = _scriptedResponses.Dequeue();
        else
            body = """{"choices":[{"message":{"content":"pineapple"}}]}""";
        return SubstituteChildId(body, requestBody);
    }

    /// <summary>Replaces every {{child_id}} occurrence in a scripted response with the most
    ///     recent agent id observed in the request's tool messages. A script demanding
    ///     substitution with no observed id is a broken test script: refused loudly as a 500,
    ///     never served as-is where the failure would surface far from its cause.</summary>
    private static string SubstituteChildId(string scriptedBody, string requestBody)
    {
        if (!scriptedBody.Contains(ChildIdPlaceholder, StringComparison.Ordinal))
            return scriptedBody;

        return TryGetMostRecentAgentId(requestBody) is { } childId
            ? scriptedBody.Replace(ChildIdPlaceholder, childId.ToString("D"), StringComparison.Ordinal)
            : throw new InvalidOperationException(
                $"Scripted response contains '{ChildIdPlaceholder}' but no tool message " +
                "in the request carries an agent id ('id=<guid>').");
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            if (ctx.Request.Url!.AbsolutePath == "/api/v1/chat/completions")
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                var requestBody = await reader.ReadToEndAsync();
                LastChatRequestBody = requestBody;
                RequestBodies.Add(requestBody);

                try
                {
                    var scriptedBody = NextScriptedBody(requestBody);
                    ctx.Response.StatusCode = 200;
                    if (RequestWantsStream(requestBody))
                    {
                        // The agent always requests SSE; serving canned completions as
                        // multi-chunk streams exercises real client-side chunk assembly.
                        ctx.Response.ContentType = "text/event-stream";
                        var bytes = Encoding.UTF8.GetBytes(ToSse(scriptedBody));
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                    }
                    else
                    {
                        ctx.Response.ContentType = "application/json";
                        var bytes = Encoding.UTF8.GetBytes(scriptedBody);
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    var bytes = Encoding.UTF8.GetBytes(ex.Message);
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "text/plain";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.Close();
        }
    }

    private static bool RequestWantsStream(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("stream", out var stream)
                && stream.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Converts a canned non-streaming completion body into an equivalent SSE exchange:
    ///     content split across two delta chunks (proving client-side chunk assembly),
    ///     tool_calls served whole in one delta, terminated by [DONE].</summary>
    private static string ToSse(string completionBody)
    {
        using var doc = JsonDocument.Parse(completionBody);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var sse = new StringBuilder();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? "";
            var cut = text.Length / 2;
            if (cut > 0)
                Chunk(sse, new { choices = new[] { new { delta = new { content = text[..cut] } } } });
            if (text.Length - cut > 0)
                Chunk(sse, new { choices = new[] { new { delta = new { content = text[cut..] } } } });
        }
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            var deltas = new List<object>();
            var index = 0;
            foreach (var call in calls.EnumerateArray())
            {
                deltas.Add(new
                {
                    index,
                    id = call.GetProperty("id").GetString(),
                    type = "function",
                    function = new
                    {
                        name = call.GetProperty("function").GetProperty("name").GetString(),
                        arguments = call.GetProperty("function").GetProperty("arguments").GetString()
                    }
                });
                index++;
            }
            Chunk(sse, new { choices = new[] { new { delta = new { tool_calls = deltas.ToArray() } } } });
        }
        sse.Append("data: [DONE]\n\n");
        return sse.ToString();
    }

    private static void Chunk(StringBuilder sse, object payload) =>
        sse.Append("data: ").Append(JsonSerializer.Serialize(payload)).Append("\n\n");

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
    }
}
