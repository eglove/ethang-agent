using System.Net;
using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Zai.ACL.Tests;

/// <summary>z.ai sends only the sampling knobs its OpenAI-compatible chat API documents
///     (top_p, frequency_penalty, presence_penalty) and never the ones without a documented
///     analogue (spec AD2/AD6). Assertions run against the RAW serialized request body.</summary>
public class ZaiModelProviderKnobsTests
{
  private static readonly Uri BaseUrl = new("https://zai.test");
  private static ZaiConfiguration Config => new("test-key", BaseUrl);

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

  private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
  {
    Content = new StringContent(
        /*lang=json,strict*/
        """{"choices":[{"message":{"content":"ok"}}]}""", Encoding.UTF8, "application/json")
  };

  private static async Task<string> CaptureBodyAsync(ModelConfig config)
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Ok();
    });
    ZaiModelProvider provider = new(new HttpClient(handler), Config);

    _ = await provider.SendAsync(config, new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken).ConfigureAwait(false);
    return capturedBody ?? throw new InvalidOperationException("request body was not captured");
  }

  [Fact]
  public async Task Body_SendsApplicableKnobs()
  {
    ModelConfig config = ModelConfig.Create("glm-5.3", null, 64, 0.7f, 1_000_000,
        topP: 0.9f, frequencyPenalty: 0.25f, presencePenalty: -1.5f).Value!;

    string body = await CaptureBodyAsync(config);

    Assert.Contains("\"top_p\":0.9", body, StringComparison.Ordinal);
    Assert.Contains("\"frequency_penalty\":0.25", body, StringComparison.Ordinal);
    Assert.Contains("\"presence_penalty\":-1.5", body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Body_NeverSendsNonApplicableKnobs()
  {
    ModelConfig config = ModelConfig.Create("glm-5.3", null, 64, 0.7f, 1_000_000,
        topK: 40, repetitionPenalty: 1.5f, minP: 0.05f, topA: 0.9f, seed: 42,
        verbosity: VerbosityLevel.High, parallelToolCalls: false,
        providerSettings: /*lang=json,strict*/ "{\"x\":1}").Value!;

    string body = await CaptureBodyAsync(config);

    Assert.DoesNotContain("\"top_k\"", body, StringComparison.Ordinal);
    Assert.DoesNotContain("\"repetition_penalty\"", body, StringComparison.Ordinal);
    Assert.DoesNotContain("\"min_p\"", body, StringComparison.Ordinal);
    Assert.DoesNotContain("\"top_a\"", body, StringComparison.Ordinal);
    Assert.DoesNotContain("\"seed\"", body, StringComparison.Ordinal);
    Assert.DoesNotContain("\"verbosity\"", body, StringComparison.Ordinal);
    Assert.DoesNotContain("\"parallel_tool_calls\"", body, StringComparison.Ordinal);
    Assert.DoesNotContain("\"provider_settings\"", body, StringComparison.Ordinal);
    Assert.DoesNotContain("\"providerSettings\"", body, StringComparison.Ordinal);
  }
}
