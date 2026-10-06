using System.Text.Json;
using eThangAgent.ToolDomain.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace eThangAgent.Mcp.ACL;

/// <summary>The SDK-backed connect seam (issue #104): builds the transport for one
///     configured server and creates the client session over it. stdio servers spawn
///     with environment inheritance OFF (B3): the child sees the SDK default set plus
///     the server's configured env - never a parent credential variable uninvited.
///     HTTP servers carry static headers; OAuth is a later increment (#110).</summary>
public sealed class SdkMcpClientSessionFactory : IMcpClientSessionPool
{
  /// <summary>Builds the stdio transport options for one server config: command,
  ///     args, name, and the environment allowlist (defaults + configured, never the
  ///     parent process env). Internal for tests; the shape is the ACL's contract.</summary>
  internal static StdioClientTransportOptions BuildStdioOptions(McpServerConfig server)
  {
    StdioClientTransportOptions options = new()
    {
      Command = server.CommandOrUrl,
      Name = server.Name,
      InheritEnvironmentVariables = false,
    };

    if (server.ArgsJson.Length > 2)
    {
      using JsonDocument args = JsonDocument.Parse(server.ArgsJson);
      List<string> parsed = [];
      foreach (JsonElement item in args.RootElement.EnumerateArray())
      {
        parsed.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
      }

      options.Arguments = parsed;
    }

    Dictionary<string, string?> env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
    if (server.EnvJson.Length > 2)
    {
      using JsonDocument configured = JsonDocument.Parse(server.EnvJson);
      foreach (JsonProperty property in configured.RootElement.EnumerateObject())
      {
        env[property.Name] = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString()
            : property.Value.GetRawText();
      }
    }

    options.EnvironmentVariables = env;
    return options;
  }

  /// <summary>Builds the Streamable-HTTP transport options for one server config:
  ///     the endpoint plus the static headers from the row. OAuth is a later
  ///     increment (issue #110); ClientOAuthOptions stays unset.</summary>
  internal static HttpClientTransportOptions BuildHttpOptions(McpServerConfig server)
  {
    HttpClientTransportOptions options = new()
    {
      Endpoint = new Uri(server.CommandOrUrl),
      Name = server.Name,
      TransportMode = HttpTransportMode.StreamableHttp,
    };

    if (server.HeadersJson.Length > 2)
    {
      using JsonDocument headers = JsonDocument.Parse(server.HeadersJson);
      Dictionary<string, string> parsed = [];
      foreach (JsonProperty property in headers.RootElement.EnumerateObject())
      {
        parsed[property.Name] = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString() ?? string.Empty
            : property.Value.GetRawText();
      }

      options.AdditionalHeaders = parsed;
    }

    return options;
  }

  /// <summary>Creates the transport for one server config: stdio spawn or Streamable
  ///     HTTP. Internal for tests.</summary>
  internal static IClientTransport CreateTransport(McpServerConfig server) =>
      server.Transport == McpTransport.Stdio
          ? new StdioClientTransport(BuildStdioOptions(server))
          : new HttpClientTransport(BuildHttpOptions(server));

  /// <inheritdoc />
  public Task<McpConnectResult> ConnectAsync(McpServerConfig server, CancellationToken ct = default) =>
      ConnectAsync(server, ct, transportOverride: null);

  /// <summary>Connects with an optional transport override (the in-memory test hook:
  ///     tests swap the stdio/HTTP transport for a StreamClientTransport over duplex
  ///     streams without any process spawn).</summary>
  internal static async Task<McpConnectResult> ConnectAsync(
      McpServerConfig server, CancellationToken ct, Stream? transportOverride = null)
#pragma warning disable CA1031, CA2000 // named decisions: transport faults are values; session ownership transfers to the pool
  {
    IClientTransport transport = transportOverride is null
        ? CreateTransport(server)
        : new StreamClientTransport(transportOverride, transportOverride);
    try
    {
      McpClient client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
      SdkMcpClientSession session = new(client);
      return new McpConnectResult.Success(session);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      // Named decision (CA1031): every transport failure (spawn failure, refused
      // HTTP, protocol error) is a connect failure value, never an exception
      // escaping to the pool.
      return new McpConnectResult.Failure("McpConnectFailed", ex.Message);
    }
#pragma warning restore CA1031, CA2000 // named decisions: transport faults are values; session ownership transfers to the pool
  }
}
