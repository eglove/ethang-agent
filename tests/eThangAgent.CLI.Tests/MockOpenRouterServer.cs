using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace eThangAgent.CLI.Tests;

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
        BaseUrl = $"http://127.0.0.1:{port}";
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
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.Close();
        }
    }

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
