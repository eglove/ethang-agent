using System.Net;
using System.Text;

namespace eThangAgent.Desktop.Tests;

/// <summary>Unit checks for the mock server's {{child_id}} substitution: before serving any
///     scripted response, the most recent agent-id annotation in the request's tool messages
///     replaces every placeholder occurrence — child ids are runtime Guids no static script
///     can predict.</summary>
public class MockOpenRouterServerTests
{
  private const string NewestId = "12345678-1234-1234-1234-123456789abc";
  private const string OlderId = "11111111-1111-1111-1111-111111111111";

  [Fact]
  public async Task Serving_ReplacesEveryPlaceholder_WithMostRecentAgentId()
  {
    using MockOpenRouterServer mock = new();
    mock.Start();
    _ = mock.Returns(ExecToolCall("call_1", Program(
        "agent.status @{ id = '{{child_id}}' }; agent.result @{ id = '{{child_id}}' }")));

    // Two tool messages: the most recent one wins; both gutter forms are recognized.
    string requestBody = ChatRequest(
        Message("system", "sys"),
        Message("tool", $"id={OlderId} status=running"),
        Message("tool", $"[agent] id={NewestId} status=completed"));

    string served = await PostChatAsync(mock, requestBody);

    Assert.Contains(NewestId, served, StringComparison.Ordinal);
    Assert.DoesNotContain("{{child_id}}", served, StringComparison.Ordinal);
    Assert.DoesNotContain(OlderId, served, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Serving_ScriptDemandingSubstitution_WithoutAnyAgentId_IsRefused()
  {
    using MockOpenRouterServer mock = new();
    mock.Start();
    _ = mock.Returns(ExecToolCall("call_1", Program("agent.status @{ id = '{{child_id}}' }")));

    string requestBody = ChatRequest(Message("user", "no tool messages here"));

    HttpResponseMessage response = await PostAsync(mock, requestBody);

    Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    string error = await response.Content.ReadAsStringAsync();
    Assert.Contains("{{child_id}}", error, StringComparison.Ordinal);
  }

  private static string Program(string text) =>
      System.Text.Json.JsonSerializer.Serialize(new { program = text });

  private static string ExecToolCall(string id, string arguments) =>
      System.Text.Json.JsonSerializer.Serialize(new
      {
        choices = new[]
          {
                new
                {
                    message = new
                    {
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new { id, type = "function", function = new { name = "exec", arguments } }
                        }
                    }
                }
          }
      });

  private static string ChatRequest(params object[] messages) =>
      System.Text.Json.JsonSerializer.Serialize(new { model = "stealth/ox-alpha", messages });

  private static object Message(string role, string content) => new { role, content };

  private static async Task<string> PostChatAsync(MockOpenRouterServer mock, string body)
  {
    HttpResponseMessage response = await PostAsync(mock, body).ConfigureAwait(true);
    _ = response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync().ConfigureAwait(true);
  }

  private static async Task<HttpResponseMessage> PostAsync(MockOpenRouterServer mock, string body)
  {
    using HttpClient client = new();
    using StringContent content = new(body, Encoding.UTF8, "application/json");
    return await client.PostAsync(new Uri(mock.BaseUrl, "api/v1/chat/completions"), content)
        .ConfigureAwait(false);
  }
}
