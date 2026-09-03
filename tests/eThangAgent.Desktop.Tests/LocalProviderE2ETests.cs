using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Local.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// The local-provider end-to-end pin (Task 11): the REAL session factory opens a
/// session against a running mock OpenAI-compatible local server, the shell builds
/// its tab through the production open path (factory delegate + OpenAgentAsync, not
/// a prebuilt detour), one streamed prompt lands the mock's assistant content in the
/// transcript, the model picker's catalog loader lists the server's own lineup, and
/// the effort command is gated OFF on this tab. The mock serves the catalog and
/// context-probe routes (LM Studio batch tier) exactly the way the Composition.Tests
/// mock does — the probe is what carries an advertised context window past the
/// catalog floor.
/// </summary>
[Collection("Desktop E2E")]
public class LocalProviderE2ETests
{
  [Fact]
  public async Task LocalSession_FullPipeline_Transcript_Catalog_EffortGate()
  {
    using MockLocalServer server = new MockLocalServer()
        .WithModels(/*lang=json,strict*/ """{"data":[{"id":"first-model","context_length":8192},{"id":"second-model"}]}""")
        .WithLmStudio(/*lang=json,strict*/ """{"data":[{"id":"first-model","context_length":8192}]}""")
        .WithChatCompletions(/*lang=json,strict*/ """{"choices":[{"message":{"role":"assistant","content":"hello from local"}}]}""");
    server.Start();

    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-local-e2e-{Guid.NewGuid():N}", "test.db");
    AppDatabase database = new(dbPath);
    AgentSession? session = null;
    try
    {
      AgentSettings settings = new(
          new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
          new ZaiSettings(null, new Uri("https://zai.test")),
          new SubAgentOptions(null, 2),
          Local: new LocalSettings(server.BaseUrl.AbsoluteUri, ApiKey: null));
      AgentSessionFactory factory = new(settings, database);

      // No live Avalonia dispatcher exists in headless tests, so the production
      // UI-thread marshaling sink would wedge the turn inside DrainUntilIdleAsync
      // (the same reasoning as the E2E harness): a direct-apply sink keeps events
      // on the turn's own thread.
      MainViewModel? shellRef = null;
      Func<UiStreamEvent, Task> sink = new(evt => (shellRef?.Tabs[0].ViewModel ??
          throw new InvalidOperationException("local E2E sink fired before the tab was initialized"))
          .ApplyUiStreamEventAsync(evt));
      Task<Result<AgentSession>> CreateSession(string root, string provider) =>
          factory.CreateAsync(root, provider, new NeverClarifyChannel());
      MainViewModel shell = new(
          CreateSession,
          new MainViewModelOptions { UiStreamSink = sink });
      shellRef = shell;

      // The production open path: the shell creates the session through the same
      // factory delegate a real open uses, so the local bootstrap resolution and
      // container wiring are exercised exactly as a user open exercises them.
      Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(
          Directory.GetCurrentDirectory(), Providers.Local).ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      AgentSessionViewModel vm = shell.Tabs[0].ViewModel;
      session = shell.Tabs[0].Container;

      // Task 9's bootstrap: the session opened on the server's OWN first listed
      // model, never a static pseudo-model — and the status bar says so.
      Assert.Equal("first-model", session.ModelId);
      Assert.Equal(Providers.Local, session.ProviderName);
      Assert.Equal(Providers.DisplayName(Providers.Local), vm.Status.Provider);

      // One streamed turn: the assistant content lands in the transcript.
      await vm.RunTurnAsync("say hi").ConfigureAwait(true);

      static string EntryText(TranscriptEntry e) => e switch
      {
        AssistantTextEntry a => a.Text,
        NoticeEntry n => "[notice] " + n.Text,
        ReasoningEntry r => "[reasoning] " + r.Text,
        UserMessageEntry u => "[user] " + u.Text,
        _ => e.GetType().Name,
      };
      string diagDump = $"wire={server.ChatRequestPaths.Count} lastModel={server.LastChatModel ?? "(none)"} entries=[" +
          string.Join(", ", vm.Transcript.Entries.Select(e => e.GetType().Name + ":" + EntryText(e))) + "]";
      List<AssistantTextEntry> assistant = [.. vm.Transcript.Entries.OfType<AssistantTextEntry>()];
      Assert.True(assistant.Count > 0, diagDump);
      Assert.Equal("hello from local", string.Join("", assistant.Select(a => a.Text)));

      // The chat went over the local wire naming the bootstrap model.
      Assert.NotEmpty(server.ChatRequestPaths);
      Assert.All(server.ChatRequestPaths, p => Assert.Equal("/chat/completions", p));
      Assert.Equal("first-model", server.LastChatModel);

      // The model picker's catalog loader lists the mock server's own lineup, in
      // listing order, stamped with the local serving-provider name.
      Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>>? loader = shell.SelectedTabCatalogLoader;
      Assert.NotNull(loader);
      Result<IReadOnlyList<ModelProviderEntry>> catalog = await loader(CancellationToken.None).ConfigureAwait(true);
      Assert.True(catalog.IsSuccess, catalog.Error?.Message);
      Assert.Equal(["first-model", "second-model"], [.. catalog.Value.Select(e => e.ModelId)]);
      Assert.All(catalog.Value, e => Assert.Equal(LocalModelCatalog.ProviderName, e.ProviderName));

      // Task 10's effort gate: reasoning effort is never sent to local servers, so
      // the command is not executable on this tab and the shell entry point is a no-op.
      Assert.True(shell.IsLocalTab);
      Assert.False(shell.ChooseEffortCommand.CanExecute(null));
      int raiseCount = 0;
      shell.EffortPickerRequested += (_, _) => raiseCount++;
      shell.RequestChooseEffort();
      Assert.Equal(0, raiseCount);
    }
    finally
    {
      // Named decision (CA1031): teardown is best effort — disposal and temp-file
      // cleanup never mask the test's own outcome.
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        if (session is not null)
        {
          await session.Services.DisposeAsync().ConfigureAwait(true);
        }
      }
      catch { /* best effort */ }

      // Connection pooling keeps the file open after the stores are done with it.
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      try
      {
        Directory.Delete(Path.GetDirectoryName(dbPath)!, true);
      }
      catch { /* best effort */ }
#pragma warning restore CA1031
    }
  }

  private sealed class NeverClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<string>(
            new DomainError("Cancelled", "no clarify expected in this E2E scenario")));
  }
}

/// <summary>Minimal OpenAI-compatible local-server mock for the desktop E2E — the
///     HttpListener skeleton of MockOpenRouterServer (and the Composition.Tests
///     MockLocalServer) shrunk to the routes the local provider consumes:
///     /v1/models serves the lineup, /api/v0/models the LM Studio batch context
///     probe (the tier that carries a context_length), /chat/completions the canned
///     completion converted to a two-chunk SSE exchange whenever the request
///     carries stream:true (the streamed path the transcript renders), plain JSON
///     otherwise (the documented transport fallback).</summary>
internal sealed class MockLocalServer : IDisposable
{
  private readonly HttpListener _listener = new();
  private readonly CancellationTokenSource _cts = new();
  private string _modelsJson = /*lang=json,strict*/ """{"data":[]}""";
  private string _lmStudioJson = /*lang=json,strict*/ """{"data":[]}""";
  private string _chatJson = /*lang=json,strict*/ """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""";

  public Uri BaseUrl { get; private set; } = null!;

  /// <summary>The "model" field of the most recent chat request — asserts the turn
  ///     named the server's own bootstrap model.</summary>
  public string? LastChatModel { get; private set; }

  public IReadOnlyList<string> ChatRequestPaths => _chatRequestPaths;
  private readonly List<string> _chatRequestPaths = [];

  /// <summary>A free loopback port with nothing listening on it.</summary>
  public static int FreePort()
  {
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  public MockLocalServer WithModels(string modelsJson)
  {
    _modelsJson = modelsJson;
    return this;
  }

  public MockLocalServer WithLmStudio(string lmStudioJson)
  {
    _lmStudioJson = lmStudioJson;
    return this;
  }

  public MockLocalServer WithChatCompletions(string chatJson)
  {
    _chatJson = chatJson;
    return this;
  }

  public void Start()
  {
    // devskim: ignore DS162092 - E2E mock provider server must bind to loopback
    BaseUrl = new Uri($"http://127.0.0.1:{FreePort()}/");
    _listener.Prefixes.Add(BaseUrl.AbsoluteUri);
    _listener.Start();
    _ = Task.Run(LoopAsync, _cts.Token);
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

      string path = ctx.Request.Url!.AbsolutePath;
      using StreamReader reader = new(ctx.Request.InputStream);
      string requestBody = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
      if (path == "/chat/completions")
      {
        LastChatModel = TryGetRequestModel(requestBody);
        _chatRequestPaths.Add(path);
      }

      bool wantsStream = path == "/chat/completions" && RequestWantsStream(requestBody);
      string resolved = wantsStream ? ToSse(_chatJson) : _chatJson;
      string? body = path switch
      {
        "/v1/models" => _modelsJson,
        "/api/v0/models" => _lmStudioJson,
        "/chat/completions" => resolved,
        _ => null,
      };
      if (body is null)
      {
        ctx.Response.StatusCode = 404;
      }
      else
      {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = wantsStream ? "text/event-stream" : "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
      }

      ctx.Response.Close();
    }
  }

  /// <summary>Extracts the top-level "model" field from a chat request body,
  ///     or null when the body is not an object or carries no string model.</summary>
  private static string? TryGetRequestModel(string requestBody)
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

  /// <summary>Converts the canned non-streaming completion body into an equivalent
  ///     SSE exchange — content split across two delta chunks (exercising client-side
  ///     chunk assembly through the shared wire core), terminated by [DONE]. The
  ///     local provider requests SSE like every other streaming provider.</summary>
  private static string ToSse(string completionBody)
  {
    using JsonDocument doc = JsonDocument.Parse(completionBody);
    string content = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
        .GetProperty("content").GetString() ?? "";
    StringBuilder sse = new();
    int cut = content.Length / 2;
    if (cut > 0)
    {
      Chunk(sse, new { choices = new[] { new { delta = new { content = content[..cut] } } } });
    }

    if (content.Length - cut > 0)
    {
      Chunk(sse, new { choices = new[] { new { delta = new { content = content[cut..] } } } });
    }

    _ = sse.Append("data: [DONE]\n\n");
    return sse.ToString();
  }

  private static void Chunk(StringBuilder sse, object payload) =>
      sse.Append("data: ").Append(JsonSerializer.Serialize(payload)).Append("\n\n");

  public void Dispose()
  {
    _cts.Cancel();
    _cts.Dispose();
    _listener.Stop();
    _listener.Close();
  }
}
