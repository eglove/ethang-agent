using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// A mock provider's listener teardown and per-request handlers are best-effort (CA1031).
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>One model the mock provider's catalog lists, with the context window the catalog
///     advertises for it.</summary>
/// <param name="Id">The model id.</param>
/// <param name="ContextLength">The advertised context window.</param>
internal readonly record struct MockModel(string Id, int ContextLength)
{
  /// <summary>A model advertised with a window wide enough for any test turn.</summary>
  public static MockModel Of(string id) => new(id, 32_768);
}

/// <summary>What the mock provider answers one responses request with.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The response body.</param>
internal readonly record struct MockAnswer(int Status, string Body)
{
  /// <summary>A successful answer carrying <paramref name="body"/>.</summary>
  public static MockAnswer Ok(string body) => new((int)HttpStatusCode.OK, body);

  /// <summary>A refusal: the provider's own error, served with its own status.</summary>
  public static MockAnswer Refuse(int status, string body) => new(status, body);
}

/// <summary>A local OpenAI-compatible provider for a composed run: a catalog naming the
///     models a test needs, optional per-model endpoint payloads (and the provider's
///     zero-retention eligible list), and a responses endpoint that either answers the
///     default completion or the answer a test scripts.
///     <para>ONE DOUBLE FOR EVERY COMPOSITION RIG. The catalog, the endpoint crawl, and the
///     responses surface are one provider, so a second copy beside this one would let the two
///     disagree about what the real API promises. Each rig passes only the models and the
///     answers its subject needs.</para>
///     <para>EVERY RESPONSES REQUEST IS RECORDED, in arrival order, so a test can assert what
///     the session actually sent rather than what it meant to send.</para></summary>
/// <param name="models">The models the catalog lists.</param>
internal sealed class MockProviderServer(params MockModel[] models) : IDisposable
{
  /// <summary>The one modality set both the catalog and a rollout need: this harness sends
  ///      text and checks no capability, so a single list keeps the payload honest.</summary>
  private static readonly string[] TextOnly = ["text"];

  /// <summary>The one supported-parameter list every catalog row declares.</summary>
  private static readonly string[] ToolParameters = ["tools"];

  private readonly HttpListener _listener = new();
  private readonly CancellationTokenSource _cts = new();
  private readonly Lock _gate = new();
  private readonly List<string> _responseBodies = [];
  private readonly Dictionary<string, string> _endpoints = [with(StringComparer.Ordinal)];
  private readonly string _catalogJson = CatalogJson(models);

  public Uri BaseUrl { get; private set; } = null!;

  /// <summary>The assistant text the default completion carries.</summary>
  public string CompletionText { get; set; } = "ok";

  /// <summary>The model the default completion names as having served it, or null to name
  ///      none (the ordinary unreported answer).</summary>
  public string? ServedModelId { get; set; }

  /// <summary>Answers one responses request from its body, or null to serve the default
  ///      completion. A test scripts this when one turn must be served differently from the
  ///      next.</summary>
  public Func<string, MockAnswer>? Responder { get; set; }

  /// <summary>The provider's zero-retention eligible list, or null to answer 404, which
  ///      leaves the list unconsulted rather than empty.</summary>
  public string? ZeroDataRetentionBody { get; set; }

  /// <summary>Every responses request body the server received, in arrival order.</summary>
  public IReadOnlyList<string> ResponseBodies
  {
    get
    {
      lock (_gate)
      {
        return [.. _responseBodies];
      }
    }
  }

  /// <summary>How many responses requests the server received.</summary>
  public int ResponseRequestCount
  {
    get
    {
      lock (_gate)
      {
        return _responseBodies.Count;
      }
    }
  }

  /// <summary>Serves <paramref name="json"/> as one model's endpoint payload. A model with no
  ///      payload answers 404, which the catalog reads as the model-level row.</summary>
  public MockProviderServer ServesEndpoints(string modelId, string json)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    ArgumentNullException.ThrowIfNull(json);
    lock (_gate)
    {
      _endpoints[modelId] = json;
    }

    return this;
  }

  public void Start()
  {
    using TcpListener probe = new(IPAddress.Loopback, 0);
    probe.Start();
    int port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    BaseUrl = new Uri($"http://127.0.0.1:{port}/");
    _listener.Prefixes.Add(BaseUrl.AbsoluteUri);
    _listener.Start();
    _ = Task.Run(LoopAsync, _cts.Token);
  }

  /// <summary>A Responses-API completion body, optionally naming the model that served it.
  ///      <para>Usage IS PART OF THE BODY because a turn records its spend from it: without one
  ///      the session has nothing to report, and a test could not observe which model ran.</para>
  ///      </summary>
  public static string Completion(string? servedModelId, string text = "ok",
      int inputTokens = 11, int outputTokens = 5)
      => JsonSerializer.Serialize(new
      {
        model = servedModelId,
        status = "completed",
        output = new object[]
        {
          new
          {
            type = "message",
            role = "assistant",
            content = new object[] { new { type = "output_text", text } },
          },
        },
        usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
      });

  /// <summary>One model's endpoint payload in the live object envelope: a single described
  ///      endpoint whose <paramref name="tag"/> is what the retention list joins on.</summary>
  public static string Endpoints(string tag, int contextLength = 128_000)
      => JsonSerializer.Serialize(new
      {
        data = new
        {
          endpoints = new object[]
          {
            new
            {
              provider_name = ProviderNameOf(tag),
              tag,
              context_length = contextLength,
              pricing = new { prompt = "0.000002", completion = "0.000004" },
            },
          },
        },
      });

  /// <summary>The provider's zero-retention eligible list naming the endpoints the stated tags
  ///      describe, in the payload shape the catalog joins on.</summary>
  public static string ZeroDataRetention(params (string ModelId, string Tag)[] endpoints)
      => JsonSerializer.Serialize(new
      {
        data = endpoints.Select(endpoint => new
        {
          model_id = endpoint.ModelId,
          provider_name = ProviderNameOf(endpoint.Tag),
          tag = endpoint.Tag,
        }).ToArray(),
      });

  /// <summary>The provider name an endpoint tag belongs to: the tag's first segment. The
  ///      catalog keys an endpoint on (model, provider name, tag), so both payloads derive it
  ///      the same way.</summary>
  public static string ProviderNameOf(string tag)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(tag);
    int slash = tag.IndexOf('/', StringComparison.Ordinal);
    return slash > 0 ? tag[..slash] : tag;
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
      catch (Exception)
      {
        break;
      }

      await HandleAsync(ctx).ConfigureAwait(false);
    }
  }

  /// <summary>Routes one request to the payload its path addresses.</summary>
  private async Task HandleAsync(HttpListenerContext ctx)
  {
    string path = ctx.Request.Url!.AbsolutePath;
    if (path.EndsWith("/endpoints/zdr", StringComparison.Ordinal))
    {
      await ServeIfConfiguredAsync(ctx, ZeroDataRetentionBody).ConfigureAwait(false);
      return;
    }

    if (path.EndsWith("/endpoints", StringComparison.Ordinal))
    {
      string? endpoints;
      lock (_gate)
      {
        _ = _endpoints.TryGetValue(ModelIdOf(path), out endpoints);
      }

      await ServeIfConfiguredAsync(ctx, endpoints).ConfigureAwait(false);
      return;
    }

    if (path.EndsWith("/models", StringComparison.Ordinal))
    {
      await WriteAsync(ctx, (int)HttpStatusCode.OK, _catalogJson).ConfigureAwait(false);
      return;
    }

    if (path.EndsWith("/responses", StringComparison.Ordinal))
    {
      await ServeResponsesAsync(ctx).ConfigureAwait(false);
      return;
    }

    ctx.Response.StatusCode = 404;
    ctx.Response.Close();
  }

  /// <summary>Records one responses request and answers it: the scripted answer when the test
  ///      supplied one, the default completion otherwise.</summary>
  private async Task ServeResponsesAsync(HttpListenerContext ctx)
  {
    string body;
    using (StreamReader reader = new(ctx.Request.InputStream, Encoding.UTF8))
    {
      body = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
    }

    lock (_gate)
    {
      _responseBodies.Add(body);
    }

    MockAnswer answer = Responder is { } responder
        ? responder(body)
        : MockAnswer.Ok(Completion(ServedModelId, CompletionText));
    await WriteAsync(ctx, answer.Status, answer.Body).ConfigureAwait(false);
  }

  private async Task ServeIfConfiguredAsync(HttpListenerContext ctx, string? body)
  {
    if (body is null)
    {
      ctx.Response.StatusCode = 404;
      ctx.Response.Close();
      return;
    }

    await WriteAsync(ctx, (int)HttpStatusCode.OK, body).ConfigureAwait(false);
  }

  private async Task WriteAsync(HttpListenerContext ctx, int status, string body)
  {
    byte[] bytes = Encoding.UTF8.GetBytes(body);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
    ctx.Response.Close();
  }

  /// <summary>The model id an endpoint path addresses: what sits between the models segment
  ///      and the trailing endpoints segment. A path in neither shape keeps its whole path, so a
  ///      routing mistake surfaces as a 404 rather than as a payload served to the wrong
  ///      model.</summary>
  private static string ModelIdOf(string path)
  {
    const string Prefix = "/api/v1/models/";
    const string Suffix = "/endpoints";
    return path.StartsWith(Prefix, StringComparison.Ordinal)
        && path.EndsWith(Suffix, StringComparison.Ordinal)
            ? path[Prefix.Length..^Suffix.Length]
            : path;
  }

  private static string CatalogJson(IReadOnlyList<MockModel> models)
      => JsonSerializer.Serialize(new
      {
        data = models.Select(model => new
        {
          id = model.Id,
          context_length = model.ContextLength,
          pricing = new { prompt = "0.000001", completion = "0.000002" },
          architecture = new { input_modalities = TextOnly, output_modalities = TextOnly },
          supported_parameters = ToolParameters,
          top_provider = new { context_length = model.ContextLength, max_completion_tokens = 8192 },
        }).ToArray(),
      });

  public void Dispose()
  {
    _cts.Cancel();
    _cts.Dispose();
    _listener.Stop();
    _listener.Close();
  }
}
