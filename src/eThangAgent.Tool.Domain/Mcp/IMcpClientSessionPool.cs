namespace eThangAgent.ToolDomain.Mcp;

// CA1034: the nested outcome cases ARE the seam's contract (the ComputerCommand named decision).
#pragma warning disable CA1034

/// <summary>The connect seam (issue #104): creates one client session for an approved
///     server config. The ACL owns the transport choice (stdio spawn or HTTP) and the
///     containment details; the domain owns the pool policy. Connect failures are
///     values, never exceptions.</summary>
public interface IMcpClientSessionPool
{
  /// <summary>Connects to one configured server and returns its session, or a typed
  ///     failure. The pool (McpServerAccess) decides when to call this - lazily,
  ///     on first dispatch to the server.</summary>
  Task<McpConnectResult> ConnectAsync(McpServerConfig server, CancellationToken ct = default);
}

/// <summary>The connect outcome.</summary>
public abstract record McpConnectResult
{
  /// <summary>The server is connected; the session is ready for calls.</summary>
  /// <param name="Session">The connected client session.</param>
  public sealed record Success(IMcpClientSession Session) : McpConnectResult;

  /// <summary>The connect failed; the message explains why and lands in the status view.</summary>
  /// <param name="Code">The canonical error code (McpConnectFailed).</param>
  /// <param name="Message">The failure message.</param>
  public sealed record Failure(string Code, string Message) : McpConnectResult;
}
#pragma warning restore CA1034
