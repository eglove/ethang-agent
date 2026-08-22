using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain;

/// <summary>
/// Recall breadth: everything persisted, or exactly one agent session.
/// </summary>
public abstract record SessionScope
{
    public sealed record Global : SessionScope;

    public sealed record Session(AgentId Id) : SessionScope;

    /// <summary>
    /// Parses the wire form strictly — no silent fallbacks. Null or "global"
    /// (case-insensitive) yields <see cref="Global"/>; "session:&lt;guid&gt;" with the
    /// remainder in exact 'D' format yields <see cref="Session"/>; anything else fails
    /// with the raw input echoed and both valid forms named.
    /// </summary>
    public static Result<SessionScope> Parse(string? raw)
    {
        if (raw is null || string.Equals(raw, "global", StringComparison.OrdinalIgnoreCase))
            return Result<SessionScope>.Success(new Global());

        if (raw.StartsWith("session:", StringComparison.Ordinal) &&
            Guid.TryParseExact(raw["session:".Length..], "D", out var id))
            return Result<SessionScope>.Success(new Session(new AgentId(id)));

        return Result<SessionScope>.Failure(new Error(
            "InvalidScope",
            $"Unknown scope '{raw}'. Valid scopes: global | session:<agentId>."));
    }
}
