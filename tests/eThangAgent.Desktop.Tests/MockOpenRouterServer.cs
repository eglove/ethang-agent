using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace eThangAgent.Desktop.Tests;

internal sealed partial class MockOpenRouterServer(string responsesPath = "/api/v1/responses") : IDisposable
{
  private readonly HttpListener _listener = new();
  private readonly CancellationTokenSource _cts = new();
  private readonly Queue<string> _scriptedResponses = new();
  private readonly Dictionary<string, Queue<string>> _modelScripts = [];
  private readonly List<string> _requestBodies = [];
  private readonly string _responsesPath = responsesPath;
  private readonly List<string> _responsesRequestPaths = [];
  private string? _catalogResponse;
  private int _port;

  public IReadOnlyList<string> RequestBodies => _requestBodies;

  public Uri BaseUrl { get; private set; } = null!;

  /// <summary>Body of the most recent /api/v1/responses request, for asserting what the CLI sent.</summary>
  public string? LastChatRequestBody { get; private set; }

  /// <summary>Absolute path of every responses request the server served.</summary>
  public IReadOnlyList<string> ChatRequestPaths => _responsesRequestPaths;

  public void Start()
  {
    // devskim: ignore DS162092 - E2E mock provider server must bind to loopback
    StartListenerOnFreePort("http://127.0.0.1:");
    BaseUrl = new Uri($"http://127.0.0.1:{_port}/");
    _ = Task.Run(LoopAsync, _cts.Token);
  }

  public MockOpenRouterServer Returns(string responseJson)
  {
    _scriptedResponses.Enqueue(responseJson);
    return this;
  }

  /// <summary>Scripts turns for a specific request model: when a responses request body's
  ///     top-level "model" field matches, turns are served from that model's queue
  ///     (first call => first response) instead of the default script. Lets one mock
  ///     server play both parent and child in a nested-spawn session.</summary>
  /// <summary>Scripts the /api/v1/models catalog response. When unset, the endpoint
  ///     serves an empty data array.</summary>
  public MockOpenRouterServer ReturnsCatalog(string catalogJson)
  {
    ArgumentNullException.ThrowIfNull(catalogJson);
    _catalogResponse = catalogJson;
    return this;
  }

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

  /// <summary>Extracts the top-level "model" field from a responses request body,
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

  /// <summary>Agent-id annotation inside a tool result. The async contract renders
  ///     'id=&lt;guid&gt; status=…' lines (spawn/status results); the legacy '[agent] '
  ///     gutter prefix is accepted so canned bodies in either shape substitute.</summary>
  [GeneratedRegex(@"(?:\[agent\]\s+)?id=([0-9a-fA-F-]{36})")]
  private static partial Regex AgentIdAnnotationRegex();

  /// <summary>Extracts the guid from the MOST RECENT function_call_output input item whose
  ///     output carries an agent-id annotation, or null when no tool result matches. The
  ///     output is either a flat string or an input-part array (input_text parts carry the
  ///     text when the result rode images); the request body is decoded first — raw JSON
  ///     escapes quotes and would corrupt the match.</summary>
  public static Guid? TryGetMostRecentAgentId(string requestBody)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(requestBody);
      if (doc.RootElement.ValueKind is not JsonValueKind.Object
          || !doc.RootElement.TryGetProperty("input", out JsonElement input)
          || input.ValueKind is not JsonValueKind.Array)
      {
        return null;
      }

      Guid? last = null;
      foreach (JsonElement item in input.EnumerateArray())
      {
        if (!IsToolOutputItem(item, out JsonElement output))
        {
          continue;
        }

        foreach (string text in OutputTexts(output))
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

  private static bool IsToolOutputItem(JsonElement item, out JsonElement output)
  {
    output = default;
    return item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("type", out JsonElement type)
        && type.ValueKind == JsonValueKind.String
        && type.GetString() == "function_call_output"
        && item.TryGetProperty("output", out output);
  }

  /// <summary>The decoded text fragments of one function_call_output's output value:
  ///     the value itself when it is a string, each input_text part's text when it is
  ///     an input-part array.</summary>
  private static List<string> OutputTexts(JsonElement output)
  {
    if (output.ValueKind == JsonValueKind.String)
    {
      return [output.GetString() ?? ""];
    }

    if (output.ValueKind != JsonValueKind.Array)
    {
      return [];
    }

    List<string> texts = [];
    foreach (JsonElement part in output.EnumerateArray())
    {
      if (part.ValueKind == JsonValueKind.Object
          && part.TryGetProperty("type", out JsonElement type)
          && type.ValueKind == JsonValueKind.String
          && type.GetString() == "input_text"
          && part.TryGetProperty("text", out JsonElement text)
          && text.ValueKind == JsonValueKind.String)
      {
        texts.Add(text.GetString()!);
      }
    }

    return texts;
  }

  /// <summary>Picks the next scripted response for a responses request — the request model's
  ///     queue, then the default script, then the pineapple fallback — and applies
  ///     {{child_id}} substitution before the body is served.</summary>
  private string NextScriptedBody(string requestBody)
  {
    string? model = TryGetRequestModel(requestBody);
    string body = model is not null
        && _modelScripts.TryGetValue(model, out Queue<string>? scripted)
        && scripted.Count > 0
        ? scripted.Dequeue()
        : NextDefaultScriptedBody();
    return SubstituteChildId(body, requestBody);
  }

  private string NextDefaultScriptedBody()
  {
    return _scriptedResponses.Count > 0
        ? _scriptedResponses.Dequeue()
        : /*lang=json,strict*/ """{"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"pineapple"}]}],"status":"completed"}""";
  }

  /// <summary>Replaces every {{child_id}} occurrence in a scripted response with the most
  ///     recent agent id observed in the request's tool results. A script demanding
  ///     substitution with no observed id is a broken test script: refused loudly as a 500,
  ///     never served as-is where the failure would surface far from its cause.</summary>
  private static string SubstituteChildId(string scriptedBody, string requestBody)
  {
    return !scriptedBody.Contains(ChildIdPlaceholder, StringComparison.Ordinal)
        ? scriptedBody
        : Substitute(scriptedBody, requestBody);
  }

  private static string Substitute(string scriptedBody, string requestBody)
  {
    return TryGetMostRecentAgentId(requestBody) is { } childId
        ? scriptedBody.Replace(ChildIdPlaceholder, childId.ToString("D"), StringComparison.Ordinal)
        : throw new InvalidOperationException(
            $"Scripted response contains '{ChildIdPlaceholder}' but no tool result " +
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

      if (ctx.Request.Url!.AbsolutePath == "/api/v1/models")
      {
        string body = _catalogResponse ?? /*lang=json,strict*/ """{"data":[]}""";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
      }
      else if (ctx.Request.Url.AbsolutePath == _responsesPath)
      {
        using StreamReader reader = new(ctx.Request.InputStream);
        string requestBody = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
        LastChatRequestBody = requestBody;
        _requestBodies.Add(requestBody);
        _responsesRequestPaths.Add(ctx.Request.Url.AbsolutePath);

        try
        {
          string scriptedBody = NextScriptedBody(requestBody);
          ctx.Response.StatusCode = 200;
          if (RequestWantsStream(requestBody))
          {
            // The agent always requests SSE; serving canned responses as
            // multi-chunk streams exercises real client-side chunk assembly.
            ctx.Response.ContentType = "text/event-stream";
            byte[] bytes = Encoding.UTF8.GetBytes(ToSse(scriptedBody));
            await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
          }
          else
          {
            ctx.Response.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(scriptedBody);
            await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
          }
        }
        catch (InvalidOperationException ex)
        {
          byte[] bytes = Encoding.UTF8.GetBytes(ex.Message);
          ctx.Response.StatusCode = 500;
          ctx.Response.ContentType = "text/plain";
          await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
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

  /// <summary>Converts a canned non-streaming responses body into the equivalent SSE event
  ///     stream: response.created, message text split across two output_text.delta chunks
  ///     (proving client-side chunk assembly), each function_call item announced by an
  ///     output_item.added event with its arguments served whole in an arguments.delta
  ///     (plus the authoritative .done), terminated by a response.completed event carrying
  ///     the full response (usage and status ride it) and [DONE].</summary>
  private static string ToSse(string responseBody)
  {
    using JsonDocument doc = JsonDocument.Parse(responseBody);
    StringBuilder sse = new();
    _ = sse.Append("""data: {"type":"response.created"}""").Append("\n\n");
    if (doc.RootElement.TryGetProperty("output", out JsonElement output)
        && output.ValueKind == JsonValueKind.Array)
    {
      int outputIndex = 0;
      foreach (JsonElement item in output.EnumerateArray())
      {
        string type = item.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()! : "";
        if (type == "message"
            && item.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.Array)
        {
          foreach (JsonElement part in content.EnumerateArray())
          {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("type", out JsonElement partType)
                && partType.ValueKind == JsonValueKind.String
                && partType.GetString() == "output_text"
                && part.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String)
            {
              EmitTextDeltas(sse, text.GetString() ?? "");
            }
          }
        }
        else if (type == "function_call")
        {
          EmitFunctionCall(sse, outputIndex, item);
        }

        outputIndex++;
      }
    }

    // The terminal response.completed event carries the whole canned body: usage
    // and status ride it exactly as the live endpoint frames them.
    Chunk(sse, new { type = "response.completed", response = JsonSerializer.Deserialize<JsonElement>(responseBody) });
    _ = sse.Append("data: [DONE]\n\n");
    return sse.ToString();
  }

  /// <summary>One message text split across two output_text.delta frames.</summary>
  private static void EmitTextDeltas(StringBuilder sse, string text)
  {
    int cut = text.Length / 2;
    if (cut > 0)
    {
      Chunk(sse, new { type = "response.output_text.delta", delta = text[..cut] });
    }

    if (text.Length - cut > 0)
    {
      Chunk(sse, new { type = "response.output_text.delta", delta = text[cut..] });
    }
  }

  /// <summary>One function_call item: the output_item.added event carries the complete
  ///     call_id and name, the arguments ride the delta (and the authoritative done)
  ///     events — all keyed by the item's output_index, as the stream core requires.</summary>
  private static void EmitFunctionCall(StringBuilder sse, int outputIndex, JsonElement item)
  {
    string callId = item.TryGetProperty("call_id", out JsonElement id) && id.ValueKind == JsonValueKind.String
        ? id.GetString() ?? "" : "";
    string name = item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
        ? n.GetString() ?? "" : "";
    string arguments = item.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.String
        ? args.GetString() ?? "" : "";
    Chunk(sse, new
    {
      type = "response.output_item.added",
      output_index = outputIndex,
      item = new { type = "function_call", call_id = callId, name },
    });
    if (arguments.Length > 0)
    {
      Chunk(sse, new { type = "response.function_call_arguments.delta", output_index = outputIndex, delta = arguments });
      Chunk(sse, new { type = "response.function_call_arguments.done", output_index = outputIndex, arguments });
    }
  }

  private static void Chunk(StringBuilder sse, object payload) =>
      sse.Append("data: ").Append(JsonSerializer.Serialize(payload)).Append("\n\n");

  private static int GetFreePort()
  {
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  /// <summary>Binds the HTTP listener, retrying on a stolen port: the TcpListener probe
  ///     above closes before HttpListener binds, and a concurrently starting mock can
  ///     win that port under a parallel run. Each retry re-probes a fresh port.</summary>
  private void StartListenerOnFreePort(string prefix)
  {
    const int maxAttempts = 5;
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
      int port = GetFreePort();
      _port = port;
      _listener.Prefixes.Clear();
      _listener.Prefixes.Add(prefix + port + "/");
      try
      {
        _listener.Start();
        return;
      }
      catch (HttpListenerException) when (attempt < maxAttempts)
      {
        // the port was stolen between probe and bind - probe a fresh one
      }
    }

    throw new InvalidOperationException("could not bind the mock provider listener; all port attempts collided.");
  }

  public void Dispose()
  {
    _cts.Cancel();
    _cts.Dispose();
    _listener.Stop();
    _listener.Close();
  }
}
