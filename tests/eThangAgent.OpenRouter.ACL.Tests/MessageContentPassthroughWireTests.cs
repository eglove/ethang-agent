using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class MessageContentPassthroughWireTests
{
  [Fact]
  public async Task SendAsync_MessageContent_ReachesTheWireUnmodified()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(/*lang=json,strict*/ "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                  Encoding.UTF8, "application/json")
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));
    DateTimeOffset sentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);
    Message[] messages =
    [
      new(Role.System, "sys notice", sentAt),
      new(Role.User, "hello", sentAt),
      new(Role.Assistant, "", sentAt,
          [new ToolCall("call-1", "read", /*lang=json,strict*/ "{\"path\":\"a.txt\"}")]),
      new(Role.Tool, "file contents", sentAt, ToolCallId: "call-1"),
    ];
    ModelConfig config = ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!;

    Result<ModelResponse> result = await provider.SendAsync(
        config, new ModelRequest(messages, SystemPrompt: "you are exec-guide"),
        TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.NotNull(capturedBody);
    using JsonDocument doc = JsonDocument.Parse(capturedBody);
    JsonElement.ArrayEnumerator sent = doc.RootElement.GetProperty("messages").EnumerateArray();
    Dictionary<string, string> byRole = [];
    foreach (JsonElement m in sent)
    {
      byRole[m.GetProperty("role").GetString()!] = m.GetProperty("content").GetString()!;
    }
    Assert.Equal("hello", byRole["user"]);
    Assert.Equal("sys notice", byRole["system"]);
    Assert.Equal("file contents", byRole["tool"]);
    Assert.Equal("", byRole["assistant"]);
  }

  [Fact]
  public async Task SendAsync_PerRequestSystemPrompt_IsSentAlongsideSystemMessages()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(/*lang=json,strict*/ "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                  Encoding.UTF8, "application/json")
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));
    Message[] messages = [new(Role.System, "sys notice", new DateTimeOffset(2026, 1, 15, 8, 30, 5, TimeSpan.Zero))];
    ModelConfig config = ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!;

    _ = await provider.SendAsync(config,
        new ModelRequest(messages, SystemPrompt: "you are exec-guide"),
        TestContext.Current.CancellationToken);

    Assert.NotNull(capturedBody);
    using JsonDocument doc = JsonDocument.Parse(capturedBody);
    JsonElement.ArrayEnumerator sent = doc.RootElement.GetProperty("messages").EnumerateArray();
    List<string> systemContents = [];
    foreach (JsonElement m in sent)
    {
      if (m.GetProperty("role").GetString() == "system")
      {
        systemContents.Add(m.GetProperty("content").GetString()!);
      }
    }
    Assert.Equal(2, systemContents.Count);
    Assert.Contains(systemContents, c => c == "sys notice");
    Assert.Contains(systemContents, c => c == "you are exec-guide");
  }
}
