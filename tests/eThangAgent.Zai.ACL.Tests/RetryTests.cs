using System.Net;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Zai.ACL.Tests;

// Transient z.ai failures (429/408/5xx, transport errors, timeouts) are retried
// with exponential backoff; permanent failures (4xx) fail immediately. Streaming
// requests are only retried while nothing has been emitted to a callback.
public class RetryTests
{
  private static readonly Uri BaseUrl = new("https://zai.test");
  private static ModelConfig Model => ModelConfig.Create("glm-5.3", null, 256, 0.7f).Value!;

  private static ZaiConfiguration Config(RetryPolicy? policy = null) =>
      new("test-key", BaseUrl) { Retry = policy ?? new RetryPolicy(4) };

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

  private sealed class Recorder
  {
    internal int _calls;
    public List<TimeSpan> Delays { get; } = [];
    public Func<int, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK);
    public Func<int, Task<HttpResponseMessage>>? RespondAsync { get; set; }
    public Exception? Throw { get; set; }

    public HttpClient Client()
    {
      FakeHttpMessageHandler handler = new(_ =>
      {
        _calls++;
        return Throw is not null ? throw Throw : RespondAsync is not null ? RespondAsync(_calls) : Task.FromResult(Respond(_calls));
      });
      HttpClient client = new(handler);
      return client;
    }
  }

  private static ZaiModelProvider Provider(ZaiConfiguration config, Recorder rec) =>
      new(rec.Client(), config,
          delay: (t, _) =>
          {
            rec.Delays.Add(t);
            return Task.CompletedTask;
          },
          jitter: () => 0.0);

  private static HttpResponseMessage Status(HttpStatusCode code, TimeSpan? retryAfter = null)
  {
    HttpResponseMessage response = new(code);
    if (retryAfter is { } ra)
    {
      response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
    }

    return response;
  }

  private static HttpResponseMessage JsonOk() =>
      new(HttpStatusCode.OK)
      {
        Content = new StringContent(
                                   /*lang=json,strict*/
                                   """{"choices":[{"message":{"content":"ok"}}]}""",
              System.Text.Encoding.UTF8, "application/json")
      };

  // ---- non-streaming ----

  [Fact]
  public async Task Transient500_IsRetried_ThenSucceeds()
  {
    Recorder rec = new() { Respond = call => call switch { 1 or 2 => Status(HttpStatusCode.InternalServerError), _ => JsonOk() } };
    ZaiModelProvider provider = Provider(Config(new RetryPolicy(4)), rec);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Equal(3, rec._calls);
  }

  [Fact]
  public async Task RateLimit429_IsRetried_ThenSucceeds()
  {
    Recorder rec = new() { Respond = call => call == 1 ? Status(HttpStatusCode.TooManyRequests) : JsonOk() };
    ZaiModelProvider provider = Provider(Config(), rec);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Equal(2, rec._calls);
  }

  [Fact]
  public async Task ClientError400_IsNotRetried()
  {
    Recorder rec = new() { Respond = _ => Status(HttpStatusCode.BadRequest) };
    ZaiModelProvider provider = Provider(Config(), rec);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]));

    Assert.False(result.IsSuccess);
    Assert.Equal(1, rec._calls);
  }

  [Fact]
  public async Task Unauthorized401_IsNotRetried()
  {
    Recorder rec = new() { Respond = _ => Status(HttpStatusCode.Unauthorized) };
    ZaiModelProvider provider = Provider(Config(), rec);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]));

    Assert.False(result.IsSuccess);
    Assert.Equal(1, rec._calls);
  }

  [Fact]
  public async Task Retries_StopAtMaxAttempts_AndReturnLastFailure()
  {
    Recorder rec = new() { Respond = _ => Status(HttpStatusCode.InternalServerError) };
    ZaiModelProvider provider = Provider(Config(new RetryPolicy(3)), rec);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]));

    Assert.False(result.IsSuccess);
    Assert.Equal(3, rec._calls);
    Assert.Equal("ProviderError", result.Error!.Code);
  }

  [Fact]
  public async Task TransportError_IsRetried_ThenSucceeds()
  {
    Recorder rec = new() { RespondAsync = call => call == 1 ? throw new HttpRequestException("boom") : Task.FromResult(JsonOk()) };
    ZaiModelProvider provider = Provider(Config(), rec);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Equal(2, rec._calls);
  }

  [Fact]
  public async Task Timeout_IsRetried_ThenSucceeds()
  {
    Recorder rec = new() { RespondAsync = call => call == 1 ? throw new TaskCanceledException() : Task.FromResult(JsonOk()) };
    ZaiModelProvider provider = Provider(Config(), rec);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Equal(2, rec._calls);
  }

  [Fact]
  public async Task Backoff_Doubles_PerAttempt()
  {
    Recorder rec = new() { Respond = _ => Status(HttpStatusCode.InternalServerError) };
    ZaiModelProvider provider = Provider(Config(new RetryPolicy(3)), rec);

    _ = await provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]));

    Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)], rec.Delays);
  }

  [Fact]
  public async Task RetryAfter_Header_Wins_OverBackoff()
  {
    Recorder rec = new()
    {
      Respond = call => call == 1 ? Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2)) : JsonOk(),
    };
    ZaiModelProvider provider = Provider(Config(), rec);

    _ = await provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]));

    Assert.Equal([TimeSpan.FromSeconds(2)], rec.Delays);
  }

  // ---- streaming ----

  [Fact]
  public async Task Streaming_TransientStatus_BeforeDeltas_IsRetried()
  {
    Recorder rec = new()
    {
      RespondAsync = call => Task.FromResult(call == 1
          ? Status(HttpStatusCode.InternalServerError)
          : SseOk("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n")),
    };
    ZaiModelProvider provider = Provider(Config(), rec);
    List<string> deltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), deltas.Add);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, rec._calls);
    Assert.Equal(["ok"], deltas);
  }

  [Fact]
  public async Task Streaming_Failure_AfterDeltas_IsNotRetried()
  {
    int calls = 0;
    FakeHttpMessageHandler handler = new(_ =>
    {
      calls++;
      return Task.FromResult(SseThenThrow());
    });
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config());

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), _ => { });

    Assert.False(result.IsSuccess);
    Assert.Equal(1, calls);
  }

  private static HttpResponseMessage SseOk(string raw) =>
      new(HttpStatusCode.OK) { Content = new StringContent(raw, System.Text.Encoding.UTF8, "text/event-stream") };

  /// <summary>Event-stream response over a stream that delivers exactly one full delta
  ///     frame and then dies, simulating deltas reaching the caller before a lost
  ///     connection. A real lazy stream is required: HttpClient buffers custom
  ///     HttpContent bodies before they reach the provider.</summary>
  private static HttpResponseMessage SseThenThrow()
  {
    StreamContent content = new(new DyingSseStream(System.Text.Encoding.UTF8.GetBytes(
        "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")));
    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
  }

  /// <summary>Serves the prefix frame once; every later read loses the connection.</summary>
  private sealed class DyingSseStream(byte[] prefix) : Stream
  {
    private bool _prefixDelivered;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
      int n = ReadCore(buffer.AsSpan(offset, count));
      return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ValueTask.FromResult(ReadCore(buffer.Span));

    private int ReadCore(Span<byte> target)
    {
      if (_prefixDelivered)
      {
        throw new IOException("connection lost mid-stream");
      }

      _prefixDelivered = true;
      int n = Math.Min(target.Length, prefix.Length);
      prefix.AsSpan(0, n).CopyTo(target);
      return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
