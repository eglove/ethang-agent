using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class MessageContentPassthroughWireTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static async Task<(string Body, Result<ModelResponse> Result)> SendAsync(
      Message[] messages, string? systemPrompt = null)
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, new OpenRouterConfiguration("test-key", BaseUrl));
    ModelConfig config = ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!;

    Result<ModelResponse> result = await provider.SendAsync(
        config, new ModelRequest(messages, SystemPrompt: systemPrompt),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    return (capturedBody!, result);
  }

  [Fact]
  public async Task SendAsync_MessageContent_ReachesTheWireUnmodified()
  {
    DateTimeOffset sentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);
    Message[] messages =
    [
      new(Role.System, "sys notice", sentAt),
      new(Role.User, "hello", sentAt),
      new(Role.Assistant, "", sentAt,
          [new ToolCall("call-1", "read", /*lang=json,strict*/ "{\"path\":\"a.txt\"}")]),
      new(Role.Tool, "file contents", sentAt, ToolCallId: "call-1"),
    ];

    (string body, Result<ModelResponse> result) = await SendAsync(messages, "you are exec-guide").ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(body);
    // The input item array: system/user history ride as message items, the textless
    // assistant shell contributes only its function_call item, and the tool result
    // is a function_call_output paired by call_id. (Raw-text equality is off the
    // table here: JsonContent escapes quotes inside argument strings as \u0022.)
    JsonElement input = doc.RootElement.GetProperty("input");
    Assert.Equal(4, input.GetArrayLength());

    JsonElement system = input[0];
    Assert.Equal(("message", "system"), (system.GetProperty("type").GetString(), system.GetProperty("role").GetString()));
    Assert.Equal("sys notice", system.GetProperty("content")[0].GetProperty("text").GetString());

    JsonElement user = input[1];
    Assert.Equal(("message", "user"), (user.GetProperty("type").GetString(), user.GetProperty("role").GetString()));
    Assert.Equal("hello", user.GetProperty("content")[0].GetProperty("text").GetString());

    JsonElement functionCall = input[2];
    Assert.Equal("function_call", functionCall.GetProperty("type").GetString());
    Assert.Equal("call-1", functionCall.GetProperty("call_id").GetString());
    Assert.Equal("read", functionCall.GetProperty("name").GetString());
    Assert.Equal(/*lang=json,strict*/ "{\"path\":\"a.txt\"}", functionCall.GetProperty("arguments").GetString());

    JsonElement functionOutput = input[3];
    Assert.Equal("function_call_output", functionOutput.GetProperty("type").GetString());
    Assert.Equal("call-1", functionOutput.GetProperty("call_id").GetString());
    Assert.Equal("file contents", functionOutput.GetProperty("output").GetString());
  }

  [Fact]
  public async Task SendAsync_PerRequestSystemPrompt_RidesInstructionsWhileSystemHistoryStaysInInput()
  {
    Message[] messages = [new(Role.System, "sys notice", new DateTimeOffset(2026, 1, 15, 8, 30, 5, TimeSpan.Zero))];

    (string body, Result<ModelResponse> result) = await SendAsync(messages, "you are exec-guide").ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal("you are exec-guide", doc.RootElement.GetProperty("instructions").GetString());
    JsonElement input = doc.RootElement.GetProperty("input");
    _ = Assert.Single(input.EnumerateArray());
    JsonElement systemItem = input[0];
    Assert.Equal("message", systemItem.GetProperty("type").GetString());
    Assert.Equal("system", systemItem.GetProperty("role").GetString());
    Assert.Equal("sys notice", systemItem.GetProperty("content")[0].GetProperty("text").GetString());
  }
}
