using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace eThangAgent.Desktop.Tests;

internal sealed partial class MockOpenRouterServer : IDisposable
{
  private readonly HttpListener _listener = new();
  private readonly CancellationTokenSource _cts = new();
  private readonly Queue<string> _scriptedResponses = new();
  private readonly Dictionary<string, Queue<string>> _modelScripts = [];
  private readonly List<string> _requestBodies = [];

  public IReadOnlyList<string> RequestBodies => _requestBodies;

  public Uri BaseUrl { get; private set; } = null!;

  /// <summary>Body of the most recent chat/completions request, for asserting what the CLI sent.</summary>
  public string? LastChatRequestBody { get; private set; }

  public void Start()
  {
    int port = GetFreePort();
    // devskim: ignore DS162092 - E2E mock provider server must bind to loopback
    BaseUrl = new Uri($"http://127.0.0.1:{port}/");
    _listener.Prefixes.Add(BaseUrl.AbsoluteUri);
    _listener.Start();
    _ = Task.Run(LoopAsync);
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
    ArgumentNullException.ThrowIfNull(responseJsons);
    if (string.IsNullOrWhiteSpace(model))
    {
      throw new ArgumentException("model is required.", nameof(model));
    }

    if (!_modelScripts.TryGetValue(model, out Queue<string>? queue))
    {
      queue = _modelScripts[model] = new Queue<string>();
    }

    foreach (string response in responseJsons)
    {
      queue.Enqueue(response);
    }

    return this;
  }

  /// <summary>Extracts the top-level "model" field from a chat request body,
  ///     or null when the body is not an object or carries no string model.</summary>
  public static string? TryGetRequestModel(string requestBody)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(requestBody);
      return doc.RootElement.ValueKind == JsonValueKind.Object
          && doc.RootElement.TryGetProperty("model", out JsonElement model)
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
  [GeneratedRegex(@"(?:\[agent\]\s+)?id=([0-9a-fA-F-]{36})")]
  private static partial Regex AgentIdAnnotationRegex();

  /// <summary>Extracts the guid from the MOST RECENT tool-role message whose content carries
  ///     an agent-id annotation, or null when no tool message matches. The request body is
  ///     decoded first — raw JSON escapes quotes and would corrupt the match.</summary>
  public static Guid? TryGetMostRecentAgentId(string requestBody)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(requestBody);
      if (doc.RootElement.ValueKind is not JsonValueKind.Object
          || !doc.RootElement.TryGetProperty("messages", out JsonElement messages))
      {
        return null;
      }

      Guid? last = null;
      foreach (JsonElement message in messages.EnumerateArray())
      {
        if (message.TryGetProperty("role", out JsonElement role)
            && role.GetString() == "tool"
            && message.TryGetProperty("content", out JsonElement content)
            && content.GetString() is { } text)
        {
          Match match = AgentIdAnnotationRegex().Match(text);
          if (match.Success)
          {
            last = Guid.Parse(match.Groups[1].Value);
          }
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
    string? model = TryGetRequestModel(requestBody);
    body = model is not null
        && _modelScripts.TryGetValue(model, out Queue<string>? scripted)
        && scripted.Count > 0
      ? scripted.Dequeue()
      : _scriptedResponses.Count > 0 ? _scriptedResponses.Dequeue() : /*lang=json,strict*/ """{"choices":[{"message":{"content":"pineapple"}}]}""";

    return SubstituteChildId(body, requestBody);
  }

  /// <summary>Replaces every {{child_id}} occurrence in a scripted response with the most
  ///     recent agent id observed in the request's tool messages. A script demanding
  ///     substitution with no observed id is a broken test script: refused loudly as a 500,
  ///     never served as-is where the failure would surface far from its cause.</summary>
  private static string SubstituteChildId(string scriptedBody, string requestBody)
  {
    return !scriptedBody.Contains(ChildIdPlaceholder, StringComparison.Ordinal)
      ? scriptedBody
      : TryGetMostRecentAgentId(requestBody) is { } childId
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
      try
      {
        ctx = await _listener.GetContextAsync().ConfigureAwait(false);
      }
      catch (HttpListenerException)
      {
        break;
      }
      catch (ObjectDisposedException)
      {
        break;
      }

      if (ctx.Request.Url!.AbsolutePath == "/api/v1/chat/completions")
      {
        using StreamReader reader = new(ctx.Request.InputStream);
        string requestBody = await reader.ReadToEndAsync().ConfigureAwait(false);
        LastChatRequestBody = requestBody;
        _requestBodies.Add(requestBody);

        try
        {
          string scriptedBody = NextScriptedBody(requestBody);
          ctx.Response.StatusCode = 200;
          if (RequestWantsStream(requestBody))
          {
            // The agent always requests SSE; serving canned completions as
            // multi-chunk streams exercises real client-side chunk assembly.
            ctx.Response.ContentType = "text/event-stream";
            byte[] bytes = Encoding.UTF8.GetBytes(ToSse(scriptedBody));
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
          }
          else
          {
            ctx.Response.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(scriptedBody);
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
          }
        }
        catch (InvalidOperationException ex)
        {
          byte[] bytes = Encoding.UTF8.GetBytes(ex.Message);
          ctx.Response.StatusCode = 500;
          ctx.Response.ContentType = "text/plain";
          await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
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
      using JsonDocument doc = JsonDocument.Parse(requestBody);
      return doc.RootElement.ValueKind == JsonValueKind.Object
          && doc.RootElement.TryGetProperty("stream", out JsonElement stream)
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
    using JsonDocument doc = JsonDocument.Parse(completionBody);
    JsonElement message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
    StringBuilder sse = new();
    if (message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
    {
      string text = content.GetString() ?? "";
      int cut = text.Length / 2;
      if (cut > 0)
      {
        Chunk(sse, new { choices = new[] { new { delta = new { content = text[..cut] } } } });
      }

      if (text.Length - cut > 0)
      {
        Chunk(sse, new { choices = new[] { new { delta = new { content = text[cut..] } } } });
      }
    }
    if (message.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Array)
    {
      List<object> deltas = [];
      int index = 0;
      foreach (JsonElement call in calls.EnumerateArray())
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
    _ = sse.Append("data: [DONE]\n\n");
    return sse.ToString();
  }

  private static void Chunk(StringBuilder sse, object payload) =>
      sse.Append("data: ").Append(JsonSerializer.Serialize(payload)).Append("\n\n");

  private static int GetFreePort()
  {
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  public void Dispose()
  {
    _cts.Cancel();
    _cts.Dispose();
    _listener.Stop();
    _listener.Close();
  }
}
