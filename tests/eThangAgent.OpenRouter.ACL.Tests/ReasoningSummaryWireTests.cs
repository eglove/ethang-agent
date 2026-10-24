using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The reasoning-summary request knob (issue #264): the provider's own
///     display separation. Every test asserts the request BODY, because the whole
///     point of the knob is what it does and does not put on the wire.</summary>
public class ReasoningSummaryWireTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

  private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
  {
    Content = new StringContent(OkJson, Encoding.UTF8, "application/json")
  };

  // The expected JSON literal is deliberate: the handler's canned response body.
#pragma warning disable JSON002
  private const string OkJson = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";

  /// <summary>Sends one request through a capturing handler and returns the parsed
  ///     body. The handler answers with a fixed success so the test never depends on
  ///     the provider's response shape.</summary>
  private static async Task<JsonDocument> SendAsync(ModelConfig config, ModelRequest request)
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(config, request, TestContext.Current.CancellationToken).ConfigureAwait(false);
    return JsonDocument.Parse(capturedBody!);
  }

  private static ModelConfig ConfigWithSummary(ReasoningSummaryLevel? summary) =>
      ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096,
          providerSettings: OpenRouterRequestSettings.Serialize(
              new OpenRouterRequestSettings { ReasoningSummary = summary })).Value!;

  [Theory]
  [InlineData(ReasoningSummaryLevel.Auto, "auto")]
  [InlineData(ReasoningSummaryLevel.Concise, "concise")]
  [InlineData(ReasoningSummaryLevel.Detailed, "detailed")]
  public async Task Summary_MapsToWireVocabulary(ReasoningSummaryLevel summary, string wire)
  {
    using JsonDocument doc = await SendAsync(ConfigWithSummary(summary), new ModelRequest([UserMsg("hi")]));

    Assert.Equal(wire, doc.RootElement.GetProperty("reasoning").GetProperty("summary").GetString());
    // Summary-only: the effort member is absent, never defaulted.
    Assert.False(doc.RootElement.GetProperty("reasoning").TryGetProperty("effort", out _));
  }

  [Fact]
  public async Task Summary_Unset_SendsNoReasoningObject()
  {
    using JsonDocument doc = await SendAsync(ConfigWithSummary(null), new ModelRequest([UserMsg("hi")]));

    Assert.False(doc.RootElement.TryGetProperty("reasoning", out _));
  }

  [Fact]
  public async Task EffortAndSummary_BothSet_EmitOneWellFormedReasoningObject()
  {
    ModelConfig config = ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096,
        ReasoningEffort.High,
        providerSettings: OpenRouterRequestSettings.Serialize(
            new OpenRouterRequestSettings { ReasoningSummary = ReasoningSummaryLevel.Concise })).Value!;

    using JsonDocument doc = await SendAsync(config, new ModelRequest([UserMsg("hi")]));

    JsonElement reasoning = doc.RootElement.GetProperty("reasoning");
    Assert.Equal("high", reasoning.GetProperty("effort").GetString());
    Assert.Equal("concise", reasoning.GetProperty("summary").GetString());
  }

  [Fact]
  public async Task EffortOnly_StillEmitsReasoningObjectWithoutSummary()
  {
    ModelConfig config = ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096, ReasoningEffort.Low).Value!;

    using JsonDocument doc = await SendAsync(config, new ModelRequest([UserMsg("hi")]));

    JsonElement reasoning = doc.RootElement.GetProperty("reasoning");
    Assert.Equal("low", reasoning.GetProperty("effort").GetString());
    Assert.False(reasoning.TryGetProperty("summary", out _));
  }

  /// <summary>The display/context independence pin the parent issue asks for: the
  ///     summary setting changes the request's reasoning object and NOT the input
  ///     array. The two channels are provably separate.</summary>
  [Fact]
  public async Task Summary_LeavesTheInputArrayByteIdentical()
  {
    ModelRequest request = new([UserMsg("hello there")]);

    string withoutInput;
    using (JsonDocument doc = await SendAsync(ConfigWithSummary(null), request))
    {
      withoutInput = doc.RootElement.GetProperty("input").GetRawText();
    }

    string withInput;
    using (JsonDocument doc = await SendAsync(ConfigWithSummary(ReasoningSummaryLevel.Detailed), request))
    {
      withInput = doc.RootElement.GetProperty("input").GetRawText();
    }

    Assert.Equal(withoutInput, withInput);
  }

#pragma warning restore JSON002
}
