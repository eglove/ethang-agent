namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The OAuth credentials stored for one HTTP MCP server (issue #110's carrier;
///     the table exists from V15 so that issue needs no migration). Replacing is
///     whole-row: SaveTokensAsync overwrites both tokens and the expiry together.</summary>
/// <param name="AccessToken">The current access token.</param>
/// <param name="RefreshToken">The refresh token, when the server issued one.</param>
/// <param name="ExpiresAt">When the access token expires, when known.</param>
public sealed record McpOAuthTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt);
