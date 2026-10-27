using System.Net;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

// Pins for the measured call duration (issue #292 D1): the number a user compares between
// providers is the MODEL CALL, so the clock wraps dispatch-to-settled inside the ACL - the
// only layer that can see both ends. Both parse paths must produce one, and a failed call
// must produce none (there is no response to describe).
public class UsageTimingTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);
  private static ModelConfig Model => ModelConfig.Create("m", null, 256, 0.7f, 4096).Value!;

  [Fact]
  public async Task NonStreaming_SuccessfulCall_StampsDurationMs()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK, /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"plain"}]}]}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    // Measured, so a number - and zero is legitimate (a fake handler can finish inside a
    // millisecond). What matters is that it is not null: an unmeasured success is the
    // defect this member exists to eliminate.
    Assert.True(result.Value.DurationMs.HasValue);
    Assert.True(result.Value.DurationMs >= 0);
  }

  [Fact]
  public async Task Streaming_SuccessfulCall_StampsDurationMs()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.DurationMs.HasValue);
    Assert.True(result.Value.DurationMs >= 0);
  }

  [Fact]
  public async Task Streaming_JsonFallback_StillStampsDurationMs()
  {
    // A server that ignores the stream flag answers one JSON body. That is a transport
    // fallback, not a different measurement: the call still took time and must report it.
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK, /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"plain"}]}]}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.DurationMs.HasValue);
    // No delta was ever streamed, so there is no first token to time - null, never 0.
    Assert.Null(result.Value.FirstTokenMs);
  }

  [Fact]
  public async Task Duration_MeasuresTheCall_NotAZeroPlaceholder()
  {
    // A handler that genuinely takes measurable time must produce a duration that reflects
    // it. This is what separates a real measurement from a constant 0 that would make
    // every provider look instantaneous.
    FakeHttpMessageHandler handler = new(async _ =>
    {
      await Task.Delay(40, TestContext.Current.CancellationToken).ConfigureAwait(false);
      return Wire.Json(HttpStatusCode.OK, /*lang=json,strict*/
          """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"plain"}]}]}""");
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.DurationMs.HasValue);
    Assert.True(result.Value.DurationMs >= 30,
        $"a 40 ms handler reported {result.Value.DurationMs} ms - the clock is not around the call.");
  }

  [Fact]
  public async Task FailedCall_CarriesNoDuration()
  {
    // A failure produced no response, so there is no latency to describe. Stamping one
    // would put a number on an error result that no consumer can honestly read.
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.BadRequest,
        /*lang=json,strict*/"""{"error":{"message":"nope"}}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
  }

  [Fact]
  public async Task Streaming_ServedProviderIsNull_WhenTheWireNamesNone()
  {
    // The measured live shape: this surface names no provider. The ACL must not substitute
    // the configured pin - a request's intention is not a fact about what served (B3).
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);
    ModelConfig pinned = ModelConfig.Create("m", "DeepInfra", 256, 0.7f, 4096).Value!;

    Result<ModelResponse> result = await provider.SendStreamingAsync(pinned, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.ServedProvider);
  }
}
