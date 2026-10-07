using System.Text;
using System.Text.Json;
using eThangAgent.ToolDomain.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace eThangAgent.Mcp.ACL;

/// <summary>One connected server's client session (issue #104): the seam's
///     IMcpClientSession over the SDK's McpClient. Tool listing maps the server's
///     tool metadata to the domain's McpToolInfo; calls forward the arguments JSON
///     verbatim and render the result's text content blocks.</summary>
public sealed class SdkMcpClientSession(McpClient client) : IMcpClientSession
{
  private readonly McpClient _client = client ?? throw new ArgumentNullException(nameof(client));

  /// <inheritdoc />
  public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
  {
    IList<McpClientTool> tools = await _client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
    return [.. tools.Select(t => new McpToolInfo(t.Name, t.Description,
        t.ProtocolTool.Annotations?.ReadOnlyHint, t.ProtocolTool.Annotations?.DestructiveHint))];
  }

  /// <inheritdoc />
  public async Task<McpToolCallResult> CallToolAsync(
      string tool, string argumentsJson, CancellationToken ct = default)
  {
    Dictionary<string, object?> arguments = ParseArguments(argumentsJson);
    CallToolResult result = await _client
        .CallToolAsync(tool, arguments, cancellationToken: ct).ConfigureAwait(false);
    return new McpToolCallResult(Render(result), result.IsError == true);
  }

  private static Dictionary<string, object?> ParseArguments(string argumentsJson)
  {
    using JsonDocument doc = JsonDocument.Parse(argumentsJson);
    Dictionary<string, object?> parsed = [];
    foreach (JsonProperty property in doc.RootElement.EnumerateObject())
    {
      parsed[property.Name] = property.Value.Clone();
    }

    return parsed;
  }

  /// <summary>Renders the result's content blocks as the model-facing text: text
  ///     blocks verbatim (joined with newlines), other block kinds as typed
  ///     placeholders so the content is never silently dropped.</summary>
  private static string Render(CallToolResult result)
  {
    if (result.Content.Count == 0)
    {
      return string.Empty;
    }

    StringBuilder rendered = new();
    foreach (ContentBlock block in result.Content)
    {
      if (rendered.Length > 0)
      {
        _ = rendered.Append('\n');
      }

      _ = rendered.Append(block switch
      {
        TextContentBlock text => text.Text,
        ImageContentBlock => "[image content withheld: MCP tool returned an image]",
        AudioContentBlock => "[audio content withheld: MCP tool returned audio]",
        _ => "[" + block.Type + " content]",
      });
    }

    return rendered.ToString();
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() => await _client.DisposeAsync().ConfigureAwait(false);
}
