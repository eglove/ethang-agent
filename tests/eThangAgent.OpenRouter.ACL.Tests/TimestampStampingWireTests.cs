using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class TimestampStampingWireTests
{
  private const string StampPrefix = "[2026-01-15 08:30:05Z] ";

  [Fact]
  public async Task SendAsync_EveryMessageRole_IsSentWithTheUtcStampPrefix()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                  Encoding.UTF8, "application/json")
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));
    DateTimeOffset stamp = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);
    Message[] messages =
    [
      new(Role.System, "sys notice", stamp),
      new(Role.User, "hello", stamp),
      new(Role.Assistant, "", stamp,
          [new ToolCall("call-1", "read", "{\"path\":\"a.txt\"}")]),
      new(Role.Tool, "file contents", stamp, ToolCallId: "call-1"),
    ];
    ModelConfig config = ModelConfig.Create("m", null, 100, 0.5f).Value!;

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
    Assert.Equal(StampPrefix + "hello", byRole["user"]);
    Assert.Equal(StampPrefix + "sys notice", byRole["system"]);
    Assert.Equal(StampPrefix + "file contents", byRole["tool"]);
    Assert.Equal(StampPrefix, byRole["assistant"]);
  }

  [Fact]
  public async Task SendAsync_PerRequestSystemPrompt_IsNotStampedWhileSystemMessagesAre()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                  Encoding.UTF8, "application/json")
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));
    Message[] messages = [new(Role.System, "sys notice", new DateTimeOffset(2026, 1, 15, 8, 30, 5, TimeSpan.Zero))];
    ModelConfig config = ModelConfig.Create("m", null, 100, 0.5f).Value!;

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
    Assert.Contains(systemContents, c => c == "[2026-01-15 08:30:05Z] sys notice");
    Assert.Contains(systemContents, c => c == "you are exec-guide");
  }
}
